using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Data.Html;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed class LiveTileNewsItem
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
    }

    public sealed class LiveTileQmsInfo
    {
        public int UnreadCount { get; set; }
        public string Title { get; set; }
    }

    public static class LiveTileService
    {
        private const string NewsRssUrl = "https://4pda.to/feed/";
        private const int MaxTileNewsItems = 5;
        private const int MaxTileQueueNotifications = 4;

        private const string KeyNewsTitle = "LiveTileNewsTitle";
        private const string KeyNewsDescription = "LiveTileNewsDescription";
        private const string KeyNewsCount = "LiveTileNewsCount";
        private const string KeyNewsTitlePrefix = "LiveTileNewsTitle";
        private const string KeyNewsDescriptionPrefix = "LiveTileNewsDescription";
        private const string KeyNewsImagePrefix = "LiveTileNewsImage";
        private const string KeyQmsUnread = "LiveTileQmsUnread";
        private const string KeyQmsTitle = "LiveTileQmsTitle";

        public static void UpdateFromNewsAndQms(IEnumerable<object> newsItems, IEnumerable<object> qmsRoots)
        {
            List<LiveTileNewsItem> news = ConvertNews(newsItems);
            LiveTileQmsInfo qms = qmsRoots == null ? LoadCachedQms() : ConvertQms(qmsRoots);
            SaveCache(news, qms);

            Task ignored = UpdateTileAsync(news, qms);
        }

        public static async Task RefreshQmsOnlyAsync()
        {
            try
            {
                LiveTileQmsInfo qms = await LoadQmsInfoAsync();
                SaveQmsCache(qms);
                UpdateBadge(qms.UnreadCount);
            }
            catch
            {
            }
        }

        public static async Task RefreshTileFromNetworkAsync()
        {
            List<LiveTileNewsItem> news = null;
            LiveTileQmsInfo qms = null;

            try
            {
                news = await LoadNewsAsync();
            }
            catch
            {
                news = LoadCachedNews();
            }

            try
            {
                qms = await LoadQmsInfoAsync();
            }
            catch
            {
                qms = LoadCachedQms();
            }

            SaveCache(news, qms);
            await UpdateTileAsync(news, qms);
        }

        private static List<LiveTileNewsItem> ConvertNews(IEnumerable<object> newsItems)
        {
            List<LiveTileNewsItem> result = new List<LiveTileNewsItem>();

            if (newsItems == null)
                return result;

            foreach (object item in newsItems)
            {
                if (item == null)
                    continue;

                string title = GetStringProperty(item, "Title", "Header", "Name", "Text", "Caption", "NewsTitle");
                string description = GetStringProperty(item, "Description", "Subtitle", "Summary", "Info", "Excerpt", "Content");
                string imageUrl = GetStringProperty(item, "ImageUrl", "ImageUri", "PictureUrl", "ThumbnailUrl", "IconUrl");

                if (String.IsNullOrWhiteSpace(title))
                    title = item.ToString();

                if (String.IsNullOrWhiteSpace(title))
                    continue;

                LiveTileNewsItem tileItem = new LiveTileNewsItem();
                tileItem.Title = CleanText(title);
                tileItem.Description = CleanText(description);
                tileItem.ImageUrl = NormalizeUrl(imageUrl);

                result.Add(tileItem);

                if (result.Count >= MaxTileNewsItems)
                    break;
            }

            return result;
        }

        private static LiveTileQmsInfo ConvertQms(IEnumerable<object> qmsRoots)
        {
            LiveTileQmsInfo info = new LiveTileQmsInfo();
            info.Title = "";

            if (qmsRoots == null)
                return info;

            foreach (object node in qmsRoots)
            {
                if (node == null)
                    continue;

                int unread = GetIntProperty(node, "UnreadCount", "Unread", "NewCount", "UnreadMessagesCount");

                if (unread <= 0)
                    continue;

                info.UnreadCount += unread;

                if (String.IsNullOrWhiteSpace(info.Title))
                    info.Title = GetStringProperty(node, "Title", "ContactNick", "Name", "Nick", "Subtitle");
            }

            return info;
        }

        private static string GetStringProperty(object target, params string[] propertyNames)
        {
            object value = GetFirstPropertyValue(target, propertyNames);

            if (value == null)
                return "";

            return value.ToString();
        }

        private static int GetIntProperty(object target, params string[] propertyNames)
        {
            object value = GetFirstPropertyValue(target, propertyNames);

            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static object GetFirstPropertyValue(object target, params string[] propertyNames)
        {
            if (target == null || propertyNames == null)
                return null;

            for (int i = 0; i < propertyNames.Length; i++)
            {
                object value = GetPropertyValue(target, propertyNames[i]);

                if (value != null)
                    return value;
            }

            return null;
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null || String.IsNullOrWhiteSpace(propertyName))
                return null;

            try
            {
                Type type = target.GetType();

                while (type != null)
                {
                    TypeInfo typeInfo = type.GetTypeInfo();

                    foreach (PropertyInfo property in typeInfo.DeclaredProperties)
                    {
                        if (!String.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!property.CanRead)
                            return null;

                        return property.GetValue(target);
                    }

                    type = typeInfo.BaseType;
                }
            }
            catch
            {
            }

            return null;
        }

        private static async Task<LiveTileQmsInfo> LoadQmsInfoAsync()
        {
            LiveTileQmsInfo info = new LiveTileQmsInfo();
            info.Title = "";

            if (!ForumAuthService.IsAuthorized)
                return info;

            QmsService qmsService = new QmsService();
            List<QmsContact> contacts = await qmsService.GetContactsAsync();

            foreach (QmsContact contact in contacts)
            {
                if (contact == null || contact.UnreadCount <= 0)
                    continue;

                info.UnreadCount += contact.UnreadCount;

                if (String.IsNullOrWhiteSpace(info.Title))
                    info.Title = contact.Title;
            }

            return info;
        }

        private static async Task<List<LiveTileNewsItem>> LoadNewsAsync()
        {
            HttpClient client = CreateHttpClient();
            string rss = await client.GetStringAsync(new Uri(NewsRssUrl, UriKind.Absolute));
            rss = NormalizeXmlText(rss);

            XDocument document = XDocument.Parse(rss);
            List<LiveTileNewsItem> result = new List<LiveTileNewsItem>();

            foreach (XElement item in document.Descendants("item"))
            {
                string title = CleanText(ElementValue(item, "title"));

                if (String.IsNullOrWhiteSpace(title))
                    continue;

                string rawDescription = ElementValue(item, "description");
                string description = CleanText(rawDescription);
                string imageUrl = NormalizeUrl(ExtractRssImageUrl(item, rawDescription));

                LiveTileNewsItem newsItem = new LiveTileNewsItem();
                newsItem.Title = title;
                newsItem.Description = description;
                newsItem.ImageUrl = imageUrl;

                result.Add(newsItem);

                if (result.Count >= MaxTileNewsItems)
                    break;
            }

            return result;
        }

        private static async Task UpdateTileAsync(IList<LiveTileNewsItem> news, LiveTileQmsInfo qms)
        {
            try
            {
                TileUpdater updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.EnableNotificationQueue(true);
                updater.Clear();

                List<LiveTileNewsItem> cleanedNews = NormalizeNewsList(news);

                if (cleanedNews.Count == 0)
                {
                    TileNotification emptyNotification = CreateTextTileNotification("4PDA", "Новости пока не загружены", "Открой приложение для обновления", "");
                    emptyNotification.Tag = "news_empty";
                    updater.Update(emptyNotification);
                }
                else
                {
                    int queueIndex = 0;

                    for (int index = 0; index < cleanedNews.Count && queueIndex + 1 < MaxTileQueueNotifications; index++)
                    {
                        LiveTileNewsItem item = cleanedNews[index];
                        string localImage = await PrepareTileImageAsync(item.ImageUrl, index);

                        TileNotification titleNotification = CreateNewsTitleTileNotification(item, index);
                        titleNotification.Tag = "news_" + queueIndex.ToString();
                        updater.Update(titleNotification);
                        queueIndex++;

                        if (queueIndex >= MaxTileQueueNotifications)
                            break;

                        TileNotification imageNotification = CreateNewsImageTileNotification(item, localImage, index);
                        imageNotification.Tag = "news_" + queueIndex.ToString();
                        updater.Update(imageNotification);
                        queueIndex++;
                    }
                }

                if (qms != null)
                    UpdateBadge(qms.UnreadCount);
                else
                    UpdateBadge(0);
            }
            catch
            {
            }
        }

        private static List<LiveTileNewsItem> NormalizeNewsList(IList<LiveTileNewsItem> news)
        {
            List<LiveTileNewsItem> result = new List<LiveTileNewsItem>();

            if (news == null)
                return result;

            foreach (LiveTileNewsItem item in news)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Title))
                    continue;

                LiveTileNewsItem copy = new LiveTileNewsItem();
                copy.Title = CleanText(item.Title);
                copy.Description = CleanText(item.Description);
                copy.ImageUrl = NormalizeUrl(item.ImageUrl);

                result.Add(copy);

                if (result.Count >= MaxTileNewsItems)
                    break;
            }

            return result;
        }

        private static TileNotification CreateNewsImageTileNotification(LiveTileNewsItem item, string localImage, int index)
        {
            string title = item == null ? "Новости 4PDA" : CleanText(item.Title);
            string image = String.IsNullOrWhiteSpace(localImage) ? "ms-appx:///Assets/Logo.png" : localImage;

            string xml =
                "<tile>" +
                "<visual version=\"2\">" +
                "<binding template=\"TileSquare150x150Image\" fallback=\"TileSquareImage\" branding=\"none\">" +
                "<image id=\"1\" src=\"" + EscapeXml(image) + "\" alt=\"" + EscapeXml(title) + "\"/>" +
                "</binding>" +
                "<binding template=\"TileWide310x150Image\" fallback=\"TileWideImage\" branding=\"none\">" +
                "<image id=\"1\" src=\"" + EscapeXml(image) + "\" alt=\"" + EscapeXml(title) + "\"/>" +
                "</binding>" +
                "</visual>" +
                "</tile>";

            return CreateTileNotificationFromXml(xml);
        }

        private static TileNotification CreateNewsTitleTileNotification(LiveTileNewsItem item, int index)
        {
            string title = item == null ? "Новости 4PDA" : CleanText(item.Title);

            string xml =
                "<tile>" +
                "<visual version=\"2\">" +
                "<binding template=\"TileSquare150x150Text04\" fallback=\"TileSquareText04\" branding=\"none\">" +
                "<text id=\"1\">" + EscapeXml(title) + "</text>" +
                "</binding>" +
                "<binding template=\"TileWide310x150Text03\" fallback=\"TileWideText03\" branding=\"none\">" +
                "<text id=\"1\">" + EscapeXml(title) + "</text>" +
                "</binding>" +
                "</visual>" +
                "</tile>";

            return CreateTileNotificationFromXml(xml);
        }

        private static TileNotification CreateTextTileNotification(string line1, string line2, string line3, string line4)
        {
            string square = FirstNonEmpty(line2, line1, "4PDA");

            string xml =
                "<tile>" +
                "<visual version=\"2\">" +
                "<binding template=\"TileSquare150x150Text04\" fallback=\"TileSquareText04\">" +
                "<text id=\"1\">" + EscapeXml(square) + "</text>" +
                "</binding>" +
                "<binding template=\"TileWide310x150Text03\" fallback=\"TileWideText03\">" +
                "<text id=\"1\">" + EscapeXml(line1) + "</text>" +
                "<text id=\"2\">" + EscapeXml(line2) + "</text>" +
                "<text id=\"3\">" + EscapeXml(line3) + "</text>" +
                "<text id=\"4\">" + EscapeXml(line4) + "</text>" +
                "</binding>" +
                "</visual>" +
                "</tile>";

            return CreateTileNotificationFromXml(xml);
        }

        private static TileNotification CreateTileNotificationFromXml(string xml)
        {
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);

            TileNotification notification = new TileNotification(document);
            notification.ExpirationTime = DateTimeOffset.Now.AddHours(6);
            return notification;
        }

        private static async Task<string> PrepareTileImageAsync(string imageUrl, int index)
        {
            if (String.IsNullOrWhiteSpace(imageUrl))
                return "";

            try
            {
                Uri uri;

                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out uri))
                    return "";

                HttpClient client = CreateHttpClient();
                IBuffer buffer = await client.GetBufferAsync(uri);

                if (buffer == null || buffer.Length == 0)
                    return "";

                string extension = GetImageExtension(uri);
                string fileName = "live_tile_news_" + index.ToString() + extension;

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, buffer);

                return "ms-appdata:///local/" + file.Name;
            }
            catch
            {
                return "";
            }
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient();

            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko");
            }
            catch
            {
            }

            return client;
        }

        private static string GetImageExtension(Uri uri)
        {
            if (uri == null)
                return ".jpg";

            string path = uri.AbsolutePath;

            if (String.IsNullOrWhiteSpace(path))
                return ".jpg";

            path = path.ToLowerInvariant();

            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return ".png";

            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                return ".gif";

            if (path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                return ".jpg";

            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                return ".jpg";

            return ".jpg";
        }

        private static void UpdateBadge(int unreadCount)
        {
            try
            {
                BadgeUpdater updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();

                if (unreadCount <= 0)
                {
                    updater.Clear();
                    return;
                }

                XmlDocument badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                XmlElement badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;

                if (badgeElement != null)
                    badgeElement.SetAttribute("value", Math.Min(unreadCount, 99).ToString());

                BadgeNotification badgeNotification = new BadgeNotification(badgeXml);
                badgeNotification.ExpirationTime = DateTimeOffset.Now.AddHours(6);
                updater.Update(badgeNotification);
            }
            catch
            {
            }
        }

        private static void SaveQmsCache(LiveTileQmsInfo qms)
        {
            try
            {
                if (qms == null)
                    return;

                ApplicationDataContainer values = ApplicationData.Current.LocalSettings;
                values.Values[KeyQmsUnread] = qms.UnreadCount;
                values.Values[KeyQmsTitle] = qms.Title == null ? "" : qms.Title;
            }
            catch
            {
            }
        }

        private static void SaveCache(IList<LiveTileNewsItem> news, LiveTileQmsInfo qms)
        {
            try
            {
                ApplicationDataContainer values = ApplicationData.Current.LocalSettings;
                List<LiveTileNewsItem> cleanedNews = NormalizeNewsList(news);

                values.Values[KeyNewsCount] = cleanedNews.Count;

                for (int i = 0; i < MaxTileNewsItems; i++)
                {
                    if (i < cleanedNews.Count)
                    {
                        LiveTileNewsItem item = cleanedNews[i];

                        values.Values[KeyNewsTitlePrefix + i.ToString()] = item.Title == null ? "" : item.Title;
                        values.Values[KeyNewsDescriptionPrefix + i.ToString()] = item.Description == null ? "" : item.Description;
                        values.Values[KeyNewsImagePrefix + i.ToString()] = item.ImageUrl == null ? "" : item.ImageUrl;
                    }
                    else
                    {
                        values.Values.Remove(KeyNewsTitlePrefix + i.ToString());
                        values.Values.Remove(KeyNewsDescriptionPrefix + i.ToString());
                        values.Values.Remove(KeyNewsImagePrefix + i.ToString());
                    }
                }

                if (cleanedNews.Count > 0)
                {
                    values.Values[KeyNewsTitle] = cleanedNews[0].Title == null ? "" : cleanedNews[0].Title;
                    values.Values[KeyNewsDescription] = cleanedNews[0].Description == null ? "" : cleanedNews[0].Description;
                }

                if (qms != null)
                {
                    values.Values[KeyQmsUnread] = qms.UnreadCount;
                    values.Values[KeyQmsTitle] = qms.Title == null ? "" : qms.Title;
                }
            }
            catch
            {
            }
        }

        private static List<LiveTileNewsItem> LoadCachedNews()
        {
            List<LiveTileNewsItem> result = new List<LiveTileNewsItem>();

            try
            {
                ApplicationDataContainer values = ApplicationData.Current.LocalSettings;
                int count = 0;

                if (values.Values.ContainsKey(KeyNewsCount) && values.Values[KeyNewsCount] != null)
                    count = Convert.ToInt32(values.Values[KeyNewsCount]);

                if (count > MaxTileNewsItems)
                    count = MaxTileNewsItems;

                for (int i = 0; i < count; i++)
                {
                    string titleKey = KeyNewsTitlePrefix + i.ToString();
                    string descriptionKey = KeyNewsDescriptionPrefix + i.ToString();
                    string imageKey = KeyNewsImagePrefix + i.ToString();

                    string title = values.Values.ContainsKey(titleKey) ? values.Values[titleKey] as string : "";
                    string description = values.Values.ContainsKey(descriptionKey) ? values.Values[descriptionKey] as string : "";
                    string imageUrl = values.Values.ContainsKey(imageKey) ? values.Values[imageKey] as string : "";

                    if (!String.IsNullOrWhiteSpace(title))
                    {
                        LiveTileNewsItem item = new LiveTileNewsItem();
                        item.Title = title;
                        item.Description = description;
                        item.ImageUrl = imageUrl;

                        result.Add(item);
                    }
                }

                if (result.Count == 0)
                {
                    string title = values.Values.ContainsKey(KeyNewsTitle) ? values.Values[KeyNewsTitle] as string : "";
                    string description = values.Values.ContainsKey(KeyNewsDescription) ? values.Values[KeyNewsDescription] as string : "";

                    if (!String.IsNullOrWhiteSpace(title))
                    {
                        LiveTileNewsItem item = new LiveTileNewsItem();
                        item.Title = title;
                        item.Description = description;
                        item.ImageUrl = "";

                        result.Add(item);
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static LiveTileQmsInfo LoadCachedQms()
        {
            LiveTileQmsInfo info = new LiveTileQmsInfo();
            info.Title = "";

            try
            {
                ApplicationDataContainer values = ApplicationData.Current.LocalSettings;

                if (values.Values.ContainsKey(KeyQmsUnread) && values.Values[KeyQmsUnread] != null)
                    info.UnreadCount = Convert.ToInt32(values.Values[KeyQmsUnread]);

                if (values.Values.ContainsKey(KeyQmsTitle))
                    info.Title = values.Values[KeyQmsTitle] as string;
            }
            catch
            {
            }

            return info;
        }

        private static string ElementValue(XElement parent, string name)
        {
            XElement element = parent == null ? null : parent.Element(name);
            return element == null ? "" : element.Value;
        }

        private static string ExtractRssImageUrl(XElement item, string descriptionHtml)
        {
            if (item != null)
            {
                foreach (XElement element in item.Descendants())
                {
                    string localName = element.Name.LocalName;

                    if (String.Equals(localName, "content", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(localName, "thumbnail", StringComparison.OrdinalIgnoreCase))
                    {
                        XAttribute url = element.Attribute("url");

                        if (url != null && !String.IsNullOrWhiteSpace(url.Value))
                            return url.Value;
                    }

                    if (String.Equals(localName, "enclosure", StringComparison.OrdinalIgnoreCase))
                    {
                        XAttribute type = element.Attribute("type");
                        XAttribute url = element.Attribute("url");

                        if (url != null && !String.IsNullOrWhiteSpace(url.Value))
                        {
                            if (type == null || type.Value.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0)
                                return url.Value;
                        }
                    }
                }
            }

            Match image = Regex.Match(descriptionHtml == null ? "" : descriptionHtml, @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!image.Success)
                return "";

            string attributes = image.Groups["attrs"].Value;
            string src = GetAttribute(attributes, "src");

            if (String.IsNullOrWhiteSpace(src))
                src = GetAttribute(attributes, "data-src");

            return src;
        }

        private static string GetAttribute(string html, string name)
        {
            if (String.IsNullOrEmpty(html) || String.IsNullOrEmpty(name))
                return "";

            string pattern = name + "\\s*=\\s*(?:([\'\"])(?<value>.*?)\\1|(?<value>[^\\s>]+))";
            Match match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
                return "";

            return HtmlDecode(match.Groups["value"].Value.Trim());
        }

        private static string NormalizeXmlText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            return text.TrimStart('\uFEFF', '\u200B', ' ', '\r', '\n', '\t');
        }

        private static string NormalizeUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            url = HtmlDecode(url).Trim();

            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;

            if (url.StartsWith("/", StringComparison.Ordinal))
                return "https://4pda.to" + url;

            return url;
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string text = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ");

            try
            {
                text = HtmlUtilities.ConvertToText(text);
            }
            catch
            {
            }

            text = HtmlDecode(text);
            text = Regex.Replace(text, "\\s+", " ").Trim();

            return text;
        }

        private static string HtmlDecode(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            try
            {
                return HtmlUtilities.ConvertToText(value);
            }
            catch
            {
                return value.Replace("&amp;", "&")
                    .Replace("&quot;", "\"")
                    .Replace("&#39;", "'")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">");
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
                return "";

            foreach (string value in values)
            {
                if (!String.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static string EscapeXml(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            return value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}