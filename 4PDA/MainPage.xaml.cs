using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Data.Html;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed partial class MainPage : Page
    {
        private const string Host = "4pda.to";
        private const string NewsPageUrl = "https://4pda.to/page/1/";
        private const string NewsRssUrl = "https://4pda.to/feed/";
        private const string ForumIndexUrl = "https://4pda.to/forum/index.php?act=idx";
        private const string ForumSearchUrl = "https://4pda.to/forum/index.php?act=search";
        private const string SlartusForumStructUrl = "https://raw.githubusercontent.com/slartus/4pdaClient-plus/master/forum_struct.json";

        private const string ForumCacheFileName = "forum_tree_grouped_categories_fast_v1.txt";
        private const string NewsCacheFileName = "news_cache_v1.txt";

        private const int NewsImageDecodeWidth = 300;
        private const int MaxNewsImagesToStartImmediately = 4;

        private readonly ObservableCollection<FeedItem> _newsItems = new ObservableCollection<FeedItem>();
        private readonly ObservableCollection<ForumNode> _visibleForumItems = new ObservableCollection<ForumNode>();
        private readonly ObservableCollection<QmsNode> _visibleQmsItems = new ObservableCollection<QmsNode>();

        private readonly List<ForumNode> _forumRoots = new List<ForumNode>();
        private readonly List<QmsNode> _qmsRoots = new List<QmsNode>();

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly QmsService _qmsService = new QmsService();

        private bool _loaded;
        private bool _loading;
        private bool _forumLoadQueued;
        private bool _qmsLoading;
        private bool _qmsLoadQueued;
        private bool _hasForumCache;
        private bool _hasNewsCache;

        private ForumNode _selectedForumNode;


        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            NewsListView.ItemsSource = _newsItems;
            ForumListView.ItemsSource = _visibleForumItems;
            QmsListView.ItemsSource = _visibleQmsItems;

            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; Touch; rv:11.0) like Gecko");
            }
            catch
            {
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            UpdateAccountButton();

            bool openMessages = e.Parameter is string &&
                String.Equals((string)e.Parameter, "messages", StringComparison.OrdinalIgnoreCase);

            if (openMessages && MainPivot != null)
                MainPivot.SelectedIndex = 2;

            if (_loaded)
            {
                if (openMessages)
                    await EnsureQmsLoadedAsync(false);

                return;
            }

            _loaded = true;

            _hasNewsCache = await ApplyNewsCacheAsync();
            LiveTileService.UpdateFromNewsAndQms(_newsItems, null);

            if (!_hasNewsCache && _newsItems.Count == 0)
                await RefreshDataAsync(true, false);
            else
                SetStatus("", false);

            if (MainPivot != null && MainPivot.SelectedIndex == 1)
                await EnsureForumLoadedAsync(false);

            if (MainPivot != null && MainPivot.SelectedIndex == 2)
                await EnsureQmsLoadedAsync(false);
        }

        private void AccountAppBarButton_Click(object sender, RoutedEventArgs e)
        {
            if (ForumAuthService.IsAuthorized)
                Frame.Navigate(typeof(UserPage));
            else
                Frame.Navigate(typeof(LoginPage));
        }

        private async void RefreshAppBarButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainPivot != null && MainPivot.SelectedIndex == 2)
            {
                await EnsureQmsLoadedAsync(true);
                return;
            }

            bool forumSelected = MainPivot != null && MainPivot.SelectedIndex == 1;
            await RefreshDataAsync(!forumSelected, forumSelected);
        }

        private async void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainPivot == null)
                return;

            if (MainPivot.SelectedIndex == 1)
            {
                if (_loading)
                {
                    _forumLoadQueued = true;
                    return;
                }

                await EnsureForumLoadedAsync(false);
                return;
            }

            if (MainPivot.SelectedIndex == 2)
                await EnsureQmsLoadedAsync(false);
        }

        private async Task EnsureForumLoadedAsync(bool forceReload)
        {
            if (_loading)
            {
                _forumLoadQueued = true;
                return;
            }

            if (!forceReload && _forumRoots.Count > 0)
                return;

            if (!forceReload && !_hasForumCache && _forumRoots.Count == 0)
                _hasForumCache = await ApplyForumCacheAsync();

            if (!forceReload && _forumRoots.Count > 0)
                return;

            await RefreshDataAsync(false, true);
        }

        private void UpdateAccountButton()
        {
            if (AccountAppBarButton == null)
                return;

            if (ForumAuthService.IsAuthorized)
            {
                string login = ForumAuthService.CurrentUserLogin;
                AccountAppBarButton.Label = String.IsNullOrWhiteSpace(login) ? "профиль" : login;
            }
            else
            {
                AccountAppBarButton.Label = "аккаунт";
            }
        }

        private async Task RefreshDataAsync(bool loadNews, bool loadForum)
        {
            if (_loading)
                return;

            _loading = true;
            SetLoading(loadNews, loadForum);
            SetStatus("", false);

            try
            {
                var errors = new List<string>();

                Task newsTask = loadNews ? LoadAndApplyNewsAsync(errors) : Task.FromResult<object>(null);
                Task forumTask = loadForum ? LoadAndApplyForumAsync(errors) : Task.FromResult<object>(null);

                await Task.WhenAll(newsTask, forumTask);

                if (errors.Count > 0)
                    SetStatus(String.Join("; ", errors), true);
                else
                    SetStatus("", false);
            }
            finally
            {
                SetLoading(false, false);
                _loading = false;
            }

            if (_forumLoadQueued && MainPivot != null && MainPivot.SelectedIndex == 1)
            {
                _forumLoadQueued = false;
                await EnsureForumLoadedAsync(false);
            }
        }

        private async Task LoadAndApplyNewsAsync(List<string> errors)
        {
            try
            {
                List<FeedItem> news = await LoadNewsAsync();

                ReplaceItems(_newsItems, news);

                _hasNewsCache = news.Count > 0;

                var ignoredImages = LoadNewsImagesAsync(news);
                var ignoredCache = SaveNewsCacheAsync(news);

                LiveTileService.UpdateFromNewsAndQms(_newsItems, null);
            }
            catch (Exception ex)
            {
                if (_newsItems.Count == 0)
                    ReplaceItems(_newsItems, new List<FeedItem>());

                errors.Add(_hasNewsCache || _newsItems.Count > 0
                    ? "Показан кэш новостей. Обновление не удалось: " + ex.Message
                    : "Новости не загружены: " + ex.Message);

                LiveTileService.UpdateFromNewsAndQms(_newsItems, null);
            }
        }

        private async Task LoadAndApplyForumAsync(List<string> errors)
        {
            try
            {
                List<ForumNode> roots = await LoadForumAsync();

                ApplyForumRoots(roots);

                await SaveForumCacheAsync(_forumRoots);
            }
            catch (Exception ex)
            {
                if (_forumRoots.Count == 0)
                {
                    _forumRoots.Clear();
                    _visibleForumItems.Clear();
                }

                errors.Add(_hasForumCache || _forumRoots.Count > 0
                    ? "Показан кэш форума. Обновление не удалось: " + ex.Message
                    : "Форум не загружен: " + ex.Message);
            }
        }

        private void ApplyForumRoots(List<ForumNode> roots)
        {
            roots = CleanForumCategoryTree(roots);

            CollapseForumTree(roots);

            _selectedForumNode = null;

            _forumRoots.Clear();

            if (roots != null)
                _forumRoots.AddRange(roots);

            RebuildVisibleForumItems();
        }

        private static List<ForumNode> CleanForumCategoryTree(List<ForumNode> roots)
        {
            var result = new List<ForumNode>();

            if (roots == null)
                return result;

            foreach (ForumNode root in roots)
            {
                ForumNode cleaned = CleanForumNode(root);
                if (cleaned != null)
                    result.Add(cleaned);
            }

            return result;
        }

        private static ForumNode CleanForumNode(ForumNode node)
        {
            if (node == null)
                return null;

            if (IsForumTreeTrashNode(node))
                return null;

            node.Description = "";

            var cleanChildren = new List<ForumNode>();

            foreach (ForumNode child in node.Children)
            {
                ForumNode cleanedChild = CleanForumNode(child);
                if (cleanedChild != null)
                    cleanChildren.Add(cleanedChild);
            }

            node.Children.Clear();

            foreach (ForumNode child in cleanChildren)
                node.Children.Add(child);

            node.HasForums = node.Children.Count > 0;
            node.HasTopics = node.Children.Count == 0;

            return node;
        }

        private static bool IsForumTreeTrashNode(ForumNode node)
        {
            if (node == null)
                return true;

            string title = node.Title ?? "";
            string url = node.Url ?? "";

            if (String.IsNullOrWhiteSpace(title))
                return true;

            if (url.IndexOf("showtopic=", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (url.IndexOf("showuser=", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (url.IndexOf("findpost", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (url.IndexOf("act=rep", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string lower = title.ToLowerInvariant();

            if (lower.IndexOf("популярные темы", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (lower.IndexOf("последние темы", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (lower.IndexOf("последнее сообщение", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (lower.IndexOf("автор", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (lower.IndexOf("реп:", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (lower.IndexOf("сообщений:", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private void SetLoading(bool newsBusy, bool forumBusy)
        {
            if (RefreshAppBarButton != null)
                RefreshAppBarButton.IsEnabled = !newsBusy && !forumBusy;
        }

        private void SetStatus(string text, bool visible)
        {
            StatusTextBlock.Text = text;
            StatusTextBlock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void ReplaceItems(ObservableCollection<FeedItem> target, IEnumerable<FeedItem> source)
        {
            target.Clear();

            if (source == null)
                return;

            foreach (FeedItem item in source)
                target.Add(item);
        }

        private async Task<List<FeedItem>> LoadNewsAsync()
        {
            string html = await DownloadStringAsync(NewsPageUrl);

            List<FeedItem> result = await Task.Run<List<FeedItem>>(delegate
            {
                return ParseNewsFromHtml(html);
            });

            if (result.Count == 0)
                result = await LoadNewsFromRssAsync();

            return result;
        }

        private async Task<List<FeedItem>> LoadNewsFromRssAsync()
        {
            string rss = await DownloadStringAsync(NewsRssUrl);
            rss = NormalizeRss(rss);

            return await Task.Run<List<FeedItem>>(delegate
            {
                return ParseNewsFromRss(rss);
            });
        }

        private static List<FeedItem> ParseNewsFromRss(string rss)
        {
            var result = new List<FeedItem>();
            XDocument document = XDocument.Parse(rss);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";

            foreach (XElement item in document.Descendants("item"))
            {
                string title = ElementValue(item, "title");
                string descriptionHtml = ElementValue(item, "description");
                string description = CleanText(descriptionHtml);
                string url = ElementValue(item, "link");
                string author = item.Element(dc + "creator") == null ? "" : item.Element(dc + "creator").Value;
                string date = FormatDate(ElementValue(item, "pubDate"));
                string imageUrl = NormalizeUrl(ExtractRssImageUrl(item, descriptionHtml));

                if (String.IsNullOrWhiteSpace(title))
                    continue;

                if (IsBlockedNews(title, description, url))
                    continue;

                result.Add(new FeedItem
                {
                    Title = title,
                    Description = description,
                    Url = NormalizeUrl(url),
                    Info = JoinNonEmpty(" · ", date, author),
                    ImageUrl = imageUrl,
                    ImageVisibility = String.IsNullOrWhiteSpace(imageUrl) ? Visibility.Collapsed : Visibility.Visible
                });
            }

            return result;
        }

        private async Task LoadNewsImagesAsync(IList<FeedItem> items)
        {
            if (items == null || items.Count == 0)
                return;

            try
            {
                await Task.Delay(220);

                int loaded = 0;

                foreach (FeedItem item in items)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.ImageUrl))
                        continue;

                    item.Image = CreateBitmapImage(item.ImageUrl);
                    loaded++;

                    if (loaded >= MaxNewsImagesToStartImmediately)
                        break;

                    await Task.Delay(1);
                }
            }
            catch
            {
            }
        }

        private async Task<List<ForumNode>> LoadForumAsync()
        {
            try
            {
                string json = await DownloadStringAsync(SlartusForumStructUrl);

                List<ForumNode> roots = await Task.Run<List<ForumNode>>(delegate
                {
                    return BuildForumTree(ParseForumJson(json));
                });

                if (roots.Count > 0 && CountForumNodes(roots) > roots.Count)
                    return roots;
            }
            catch
            {
            }

            try
            {
                string html = await DownloadStringAsync(ForumSearchUrl);

                List<ForumNode> roots = await Task.Run<List<ForumNode>>(delegate
                {
                    return ParseForumFromSearch(html);
                });

                if (roots.Count > 0 && !HasBrokenForumTitles(roots))
                    return roots;
            }
            catch
            {
            }

            string indexHtml = await DownloadStringAsync(ForumIndexUrl);

            return await Task.Run<List<ForumNode>>(delegate
            {
                return ParseForumFromHtml(indexHtml);
            });
        }

        private async Task<bool> ApplyNewsCacheAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(NewsCacheFileName);
                string cache = await FileIO.ReadTextAsync(file);

                List<FeedItem> news = await Task.Run<List<FeedItem>>(delegate
                {
                    return ParseNewsCache(cache);
                });

                if (news.Count > 0)
                {
                    ReplaceItems(_newsItems, news);
                    var ignoredImages = LoadNewsImagesAsync(news);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private async Task SaveNewsCacheAsync(List<FeedItem> news)
        {
            try
            {
                if (news == null || news.Count == 0)
                    return;

                string cache = await Task.Run<string>(delegate
                {
                    return CreateNewsCache(news);
                });

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    NewsCacheFileName,
                    CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(file, cache);
            }
            catch
            {
            }
        }

        private async Task PreloadForumCacheAsync()
        {
            if (_hasForumCache || _forumRoots.Count > 0)
                return;

            try
            {
                _hasForumCache = await ApplyForumCacheAsync();
            }
            catch
            {
            }
        }

        private async Task<bool> ApplyForumCacheAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(ForumCacheFileName);
                string cache = await FileIO.ReadTextAsync(file);

                List<ForumNode> roots = await Task.Run<List<ForumNode>>(delegate
                {
                    return ParseForumCache(cache);
                });

                if (roots.Count > 0)
                {
                    ApplyForumRoots(roots);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private async Task SaveForumCacheAsync(List<ForumNode> roots)
        {
            try
            {
                if (roots == null || roots.Count == 0)
                    return;

                string cache = await Task.Run<string>(delegate
                {
                    return CreateForumCache(roots);
                });

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    ForumCacheFileName,
                    CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(file, cache);
            }
            catch
            {
            }
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            return await _httpClient.GetStringAsync(new Uri(url, UriKind.Absolute));
        }

        private static string CreateNewsCache(List<FeedItem> news)
        {
            var builder = new StringBuilder();

            if (news == null)
                return "";

            foreach (FeedItem item in news)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Title))
                    continue;

                builder.Append(EncodeCacheValue(item.Title));
                builder.Append('\t');
                builder.Append(EncodeCacheValue(item.Description));
                builder.Append('\t');
                builder.Append(EncodeCacheValue(item.Url));
                builder.Append('\t');
                builder.Append(EncodeCacheValue(item.Info));
                builder.Append('\t');
                builder.Append(EncodeCacheValue(item.ImageUrl));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static List<FeedItem> ParseNewsCache(string cache)
        {
            var result = new List<FeedItem>();

            if (String.IsNullOrWhiteSpace(cache))
                return result;

            string[] lines = cache.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');

                if (parts.Length < 5)
                    continue;

                string title = DecodeCacheValue(parts[0]);
                string url = DecodeCacheValue(parts[2]);

                if (String.IsNullOrWhiteSpace(title) || String.IsNullOrWhiteSpace(url))
                    continue;

                string imageUrl = DecodeCacheValue(parts[4]);

                result.Add(new FeedItem
                {
                    Title = title,
                    Description = DecodeCacheValue(parts[1]),
                    Url = url,
                    Info = DecodeCacheValue(parts[3]),
                    ImageUrl = imageUrl,
                    ImageVisibility = String.IsNullOrWhiteSpace(imageUrl) ? Visibility.Collapsed : Visibility.Visible
                });
            }

            return result;
        }

        private static string CreateForumCache(List<ForumNode> roots)
        {
            var builder = new StringBuilder();

            if (roots == null)
                return "";

            foreach (ForumNode root in roots)
                AppendForumCacheNode(builder, root, 0);

            return builder.ToString();
        }

        private static void AppendForumCacheNode(StringBuilder builder, ForumNode node, int level)
        {
            if (node == null)
                return;

            builder.Append(level);
            builder.Append('\t');
            builder.Append(EncodeCacheValue(node.Id));
            builder.Append('\t');
            builder.Append(EncodeCacheValue(node.ParentId));
            builder.Append('\t');
            builder.Append(EncodeCacheValue(node.Title));
            builder.Append('\t');
            builder.Append(EncodeCacheValue(node.Description));
            builder.Append('\t');
            builder.Append(EncodeCacheValue(node.Url));
            builder.AppendLine();

            foreach (ForumNode child in node.Children)
                AppendForumCacheNode(builder, child, level + 1);
        }

        private static List<ForumNode> ParseForumCache(string cache)
        {
            var flatItems = new List<ForumNode>();

            if (String.IsNullOrWhiteSpace(cache))
                return flatItems;

            string[] lines = cache.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');

                if (parts.Length < 6)
                    continue;

                int level;
                if (!Int32.TryParse(parts[0], out level))
                    level = 0;

                string id = DecodeCacheValue(parts[1]);
                string parentId = DecodeCacheValue(parts[2]);
                string title = DecodeCacheValue(parts[3]);

                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title))
                    continue;

                flatItems.Add(new ForumNode
                {
                    Id = id,
                    ParentId = parentId,
                    Title = title,
                    Description = DecodeCacheValue(parts[4]),
                    Url = DecodeCacheValue(parts[5]),
                    Level = level,
                    IsExpanded = false
                });
            }

            List<ForumNode> roots = BuildForumTreeFromFlat(flatItems);

            foreach (ForumNode root in roots)
                PrepareForumNode(root, 0);

            MarkForumFlags(roots);

            return roots;
        }

        private static string EncodeCacheValue(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            return Convert.ToBase64String(bytes);
        }

        private static string DecodeCacheValue(string value)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value ?? "");
                return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }
            catch
            {
                return "";
            }
        }

        private static List<FeedItem> ParseNewsFromHtml(string html)
        {
            var result = new List<FeedItem>();

            if (String.IsNullOrWhiteSpace(html))
                return result;

            int index = 0;

            while (result.Count < 40)
            {
                int articleStart = IndexOfIgnoreCase(html, "<article", index);

                if (articleStart < 0)
                    break;

                int startTagEnd = html.IndexOf('>', articleStart);

                if (startTagEnd < 0)
                    break;

                int articleEnd = IndexOfIgnoreCase(html, "</article>", startTagEnd + 1);

                if (articleEnd < 0)
                    articleEnd = Math.Min(html.Length, startTagEnd + 16000);

                string attributes = html.Substring(articleStart + 8, startTagEnd - articleStart - 8);
                string body = html.Substring(startTagEnd + 1, Math.Max(0, articleEnd - startTagEnd - 1));

                index = articleEnd + 10;

                if (attributes.IndexOf("post", StringComparison.OrdinalIgnoreCase) < 0 &&
                    body.IndexOf("itemprop", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                LinkInfo link = ExtractFirstUsefulLink(body);

                if (link == null || String.IsNullOrWhiteSpace(link.Title) || String.IsNullOrWhiteSpace(link.Url))
                    continue;

                string description = ExtractDescription(body);

                if (IsBlockedNews(link.Title, description, link.Url))
                    continue;

                string date = ExtractMetaContent(body, "datePublished");

                if (!String.IsNullOrEmpty(date) && date.Length > 10)
                    date = date.Substring(0, 10);

                string author = ExtractAuthor(body);
                string comments = ExtractComments(body);
                string imageUrl = NormalizeUrl(ExtractNewsImageUrl(attributes, body));

                result.Add(new FeedItem
                {
                    Title = link.Title,
                    Url = NormalizeUrl(link.Url),
                    Description = description,
                    Info = JoinNonEmpty(" · ", date, author, comments),
                    ImageUrl = imageUrl,
                    ImageVisibility = String.IsNullOrWhiteSpace(imageUrl) ? Visibility.Collapsed : Visibility.Visible
                });
            }

            return result;
        }

        private static List<ForumJsonItem> ParseForumJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
                return new List<ForumJsonItem>();

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ForumJsonItem>));
                    var parsed = serializer.ReadObject(stream) as List<ForumJsonItem>;

                    if (parsed != null && parsed.Count > 0)
                    {
                        bool hasParents = false;

                        foreach (ForumJsonItem item in parsed)
                        {
                            if (item != null && !String.IsNullOrWhiteSpace(item.ParentId))
                            {
                                hasParents = true;
                                break;
                            }
                        }

                        if (hasParents)
                            return parsed;
                    }
                }
            }
            catch
            {
            }

            return ParseForumJsonManually(json);
        }

        private static List<ForumJsonItem> ParseForumJsonManually(string json)
        {
            var result = new List<ForumJsonItem>();
            int index = 0;

            while (index < json.Length)
            {
                int start = json.IndexOf('{', index);

                if (start < 0)
                    break;

                int end = FindJsonObjectEnd(json, start);

                if (end <= start)
                    break;

                string obj = json.Substring(start, end - start + 1);

                string id = ExtractJsonStringAny(obj, "id", "forum_id", "forumId", "fid");
                string title = ExtractJsonStringAny(obj, "title", "name");
                string parentId = ExtractJsonStringAny(obj, "parentId", "parent_id", "parentid", "parent", "pid");

                if (!String.IsNullOrWhiteSpace(id) && !String.IsNullOrWhiteSpace(title))
                {
                    result.Add(new ForumJsonItem
                    {
                        Id = id,
                        Title = title,
                        Description = ExtractJsonStringAny(obj, "description", "desc"),
                        ParentId = parentId,
                        IsHasTopics = ExtractJsonBoolAny(obj, "isHasTopics", "hasTopics", "is_has_topics", "topics"),
                        IsHasForums = ExtractJsonBoolAny(obj, "isHasForums", "hasForums", "is_has_forums", "forums")
                    });
                }

                index = end + 1;
            }

            return result;
        }

        private static string ExtractJsonStringAny(string obj, params string[] names)
        {
            if (names == null)
                return "";

            for (int i = 0; i < names.Length; i++)
            {
                string value = ExtractJsonString(obj, names[i]);

                if (!String.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static bool ExtractJsonBoolAny(string obj, params string[] names)
        {
            if (names == null)
                return false;

            for (int i = 0; i < names.Length; i++)
            {
                if (ExtractJsonBool(obj, names[i]))
                    return true;
            }

            return false;
        }

        private static List<ForumNode> BuildForumTree(List<ForumJsonItem> items)
        {
            var roots = new List<ForumNode>();
            var byId = new Dictionary<string, ForumNode>();
            var orderedNodes = new List<ForumNode>();

            if (items == null)
                return roots;

            foreach (ForumJsonItem item in items)
            {
                if (item == null)
                    continue;

                if (String.IsNullOrWhiteSpace(item.Id) || String.IsNullOrWhiteSpace(item.Title))
                    continue;

                string id = item.Id.Trim();

                if (byId.ContainsKey(id))
                    continue;

                var node = new ForumNode();
                node.Id = id;
                node.ParentId = NormalizeForumParentId(item.ParentId);
                node.Title = CleanText(item.Title);
                node.Description = CleanText(item.Description);
                node.Url = "https://" + Host + "/forum/index.php?showforum=" + id;
                node.HasTopics = item.IsHasTopics;
                node.HasForums = item.IsHasForums;
                node.IsExpanded = false;

                byId.Add(id, node);
                orderedNodes.Add(node);
            }

            foreach (ForumNode node in orderedNodes)
            {
                ForumNode parent;

                if (!IsForumRootParentId(node.ParentId) && byId.TryGetValue(node.ParentId, out parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    roots.Add(node);
                }
            }

            foreach (ForumNode root in roots)
                PrepareForumNode(root, 0);

            MarkForumFlags(roots);

            return roots;
        }

        private static string NormalizeForumParentId(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            value = value.Trim();

            if (value == "0" || value == "-1" || value.Equals("null", StringComparison.OrdinalIgnoreCase))
                return "";

            return value;
        }

        private static bool IsForumRootParentId(string value)
        {
            return String.IsNullOrWhiteSpace(NormalizeForumParentId(value));
        }

        private static List<ForumNode> ParseForumFromSearch(string html)
        {
            var flatItems = new List<ForumNode>();
            var usedIds = new HashSet<string>();

            if (String.IsNullOrWhiteSpace(html))
                return flatItems;

            string source = html;
            int selectIndex = 0;

            while (true)
            {
                int selectStart = IndexOfIgnoreCase(html, "<select", selectIndex);

                if (selectStart < 0)
                    break;

                int tagEnd = html.IndexOf('>', selectStart);

                if (tagEnd < 0)
                    break;

                string attrs = html.Substring(selectStart + 7, tagEnd - selectStart - 7);
                string name = GetAttribute(attrs, "name");
                string idAttr = GetAttribute(attrs, "id");

                if (name.IndexOf("forums", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    idAttr.IndexOf("forums", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int selectEnd = IndexOfIgnoreCase(html, "</select>", tagEnd + 1);

                    if (selectEnd > tagEnd)
                        source = html.Substring(tagEnd + 1, selectEnd - tagEnd - 1);

                    break;
                }

                selectIndex = tagEnd + 1;
            }

            int index = 0;

            while (index < source.Length)
            {
                int optionStart = IndexOfIgnoreCase(source, "<option", index);

                if (optionStart < 0)
                    break;

                int tagEnd = source.IndexOf('>', optionStart);

                if (tagEnd < 0)
                    break;

                int optionEnd = IndexOfIgnoreCase(source, "</option>", tagEnd + 1);

                if (optionEnd < 0)
                    optionEnd = Math.Min(source.Length, tagEnd + 500);

                string attrs = source.Substring(optionStart + 7, tagEnd - optionStart - 7);
                string id = GetAttribute(attrs, "value");

                if (!String.IsNullOrWhiteSpace(id) && IsDigits(id) && !usedIds.Contains(id))
                {
                    string raw = source.Substring(tagEnd + 1, Math.Max(0, optionEnd - tagEnd - 1));
                    string rawText = StripTagsFast(raw);
                    int level = CalculateForumLevel(rawText);
                    string title = CleanText(RemoveForumIndent(rawText));

                    if (!String.IsNullOrWhiteSpace(title))
                    {
                        usedIds.Add(id);

                        flatItems.Add(new ForumNode
                        {
                            Id = id,
                            Title = title,
                            Url = "https://" + Host + "/forum/index.php?showforum=" + id,
                            Level = level,
                            IsExpanded = false
                        });
                    }
                }

                index = optionEnd + 9;
            }

            List<ForumNode> roots = BuildForumTreeFromFlat(flatItems);

            foreach (ForumNode root in roots)
                PrepareForumNode(root, 0);

            MarkForumFlags(roots);

            return roots;
        }

        private static List<ForumNode> BuildForumTreeFromFlat(List<ForumNode> flatItems)
        {
            var roots = new List<ForumNode>();
            var levelParents = new List<ForumNode>();

            if (flatItems == null)
                return roots;

            foreach (ForumNode node in flatItems)
            {
                if (node == null)
                    continue;

                if (node.Level < 0)
                    node.Level = 0;

                if (node.Level > levelParents.Count)
                    node.Level = levelParents.Count;

                while (levelParents.Count > node.Level)
                    levelParents.RemoveAt(levelParents.Count - 1);

                if (node.Level == 0 || levelParents.Count == 0)
                {
                    node.ParentId = "";
                    roots.Add(node);
                }
                else
                {
                    ForumNode parent = levelParents[node.Level - 1];
                    node.ParentId = parent.Id;
                    parent.Children.Add(node);
                }

                if (levelParents.Count == node.Level)
                    levelParents.Add(node);
                else
                    levelParents[node.Level] = node;
            }

            return roots;
        }

        private static int CalculateForumLevel(string rawText)
        {
            if (String.IsNullOrWhiteSpace(rawText))
                return 0;

            string text = DecodeEntities(rawText).Replace('\u00A0', ' ');
            int pos = 0;
            int visual = 0;

            while (pos < text.Length)
            {
                char ch = text[pos];

                if (ch == ' ' || ch == '\t')
                {
                    visual++;
                    pos++;
                    continue;
                }

                if (ch == '|' || ch == '│' || ch == '¦' || ch == '├' || ch == '└' || ch == '┬' || ch == '┼')
                {
                    visual += 2;
                    pos++;
                    continue;
                }

                if (ch == '-' || ch == '–' || ch == '—' || ch == '─')
                {
                    visual += 3;
                    pos++;
                    continue;
                }

                break;
            }

            if (visual <= 0)
                return 0;

            int level = visual / 6;

            if (level < 0)
                return 0;

            return Math.Min(level, 12);
        }

        private static string RemoveForumIndent(string rawText)
        {
            if (String.IsNullOrWhiteSpace(rawText))
                return "";

            string text = DecodeEntities(StripTagsFast(rawText)).Replace('\u00A0', ' ');
            int index = 0;

            while (index < text.Length)
            {
                char ch = text[index];

                if (Char.IsWhiteSpace(ch) ||
                    ch == '|' ||
                    ch == '│' ||
                    ch == '¦' ||
                    ch == '├' ||
                    ch == '└' ||
                    ch == '┬' ||
                    ch == '┼' ||
                    ch == '-' ||
                    ch == '–' ||
                    ch == '—' ||
                    ch == '─')
                {
                    index++;
                    continue;
                }

                break;
            }

            return index > 0 && index < text.Length ? text.Substring(index).Trim() : text.Trim();
        }

        private static void MarkForumFlags(IEnumerable<ForumNode> nodes)
        {
            if (nodes == null)
                return;

            foreach (ForumNode node in nodes)
            {
                if (node == null)
                    continue;

                node.HasForums = node.Children.Count > 0;
                node.HasTopics = node.Children.Count == 0;

                MarkForumFlags(node.Children);
            }
        }

        private static string StripTags(string html)
        {
            return DecodeEntities(StripTagsFast(html));
        }

        private static void PrepareForumNode(ForumNode node, int level)
        {
            if (node == null)
                return;

            node.Level = level;
            node.IsExpanded = false;

            foreach (ForumNode child in node.Children)
                PrepareForumNode(child, level + 1);

            node.HasForums = node.Children.Count > 0;
            node.HasTopics = node.Children.Count == 0;
        }

        private static void CollapseForumTree(IEnumerable<ForumNode> nodes)
        {
            if (nodes == null)
                return;

            foreach (ForumNode node in nodes)
            {
                if (node == null)
                    continue;

                node.IsExpanded = false;
                CollapseForumTree(node.Children);
            }
        }

        private static List<ForumNode> ParseForumFromHtml(string html)
        {
            var result = new List<ForumNode>();
            var usedIds = new HashSet<string>();

            if (String.IsNullOrWhiteSpace(html))
                return result;

            int index = 0;

            while (index < html.Length && result.Count < 300)
            {
                int linkStart = IndexOfIgnoreCase(html, "<a", index);

                if (linkStart < 0)
                    break;

                int tagEnd = html.IndexOf('>', linkStart);

                if (tagEnd < 0)
                    break;

                int linkEnd = IndexOfIgnoreCase(html, "</a>", tagEnd + 1);

                if (linkEnd < 0)
                    linkEnd = tagEnd;

                string attrs = html.Substring(linkStart + 2, tagEnd - linkStart - 2);
                string href = GetAttribute(attrs, "href");

                if (href.IndexOf("showtopic=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    href.IndexOf("showuser=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    href.IndexOf("findpost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    href.IndexOf("act=rep", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    index = linkEnd + 4;
                    continue;
                }

                string id = ExtractForumId(href);

                if (!String.IsNullOrWhiteSpace(id) && !usedIds.Contains(id))
                {
                    string title = CleanText(html.Substring(tagEnd + 1, Math.Max(0, linkEnd - tagEnd - 1)));

                    if (!String.IsNullOrWhiteSpace(title) && title.Length >= 2)
                    {
                        usedIds.Add(id);

                        result.Add(new ForumNode
                        {
                            Id = id,
                            Title = title,
                            Description = "",
                            Url = NormalizeUrl(href),
                            Level = 0,
                            IsExpanded = false,
                            HasTopics = true,
                            HasForums = false
                        });
                    }
                }

                index = linkEnd + 4;
            }

            return CleanForumCategoryTree(result);
        }

        private void RebuildVisibleForumItems()
        {
            _visibleForumItems.Clear();

            foreach (ForumNode root in _forumRoots)
                AddVisibleForumNode(root);
        }

        private void AddVisibleForumNode(ForumNode node)
        {
            if (node == null)
                return;

            _visibleForumItems.Add(node);

            if (!node.IsExpanded)
                return;

            foreach (ForumNode child in node.Children)
                AddVisibleForumNode(child);
        }

        private static int CountForumNodes(IEnumerable<ForumNode> nodes)
        {
            int count = 0;

            if (nodes == null)
                return count;

            foreach (ForumNode node in nodes)
            {
                if (node == null)
                    continue;

                count += 1 + CountForumNodes(node.Children);
            }

            return count;
        }

        private static bool HasBrokenForumTitles(IEnumerable<ForumNode> nodes)
        {
            if (nodes == null)
                return false;

            foreach (ForumNode node in nodes)
            {
                if (node == null)
                    continue;

                if (LooksLikeForumTreePrefix(node.Title))
                    return true;

                if (HasBrokenForumTitles(node.Children))
                    return true;
            }

            return false;
        }

        private static bool LooksLikeForumTreePrefix(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return false;

            string value = text.TrimStart();

            if (value.Length < 2)
                return false;

            int treeChars = 0;

            for (int i = 0; i < value.Length && i < 8; i++)
            {
                char ch = value[i];

                if (ch == '-' || ch == '–' || ch == '—' || ch == '─' || ch == '|' || ch == '│' || ch == '¦')
                    treeChars++;
                else if (ch != ' ' && ch != '\t')
                    break;
            }

            return treeChars >= 2;
        }

        private static LinkInfo ExtractFirstUsefulLink(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return null;

            int index = 0;

            while (index < html.Length)
            {
                int linkStart = IndexOfIgnoreCase(html, "<a", index);

                if (linkStart < 0)
                    break;

                int tagEnd = html.IndexOf('>', linkStart);

                if (tagEnd < 0)
                    break;

                int linkEnd = IndexOfIgnoreCase(html, "</a>", tagEnd + 1);

                if (linkEnd < 0)
                    linkEnd = tagEnd;

                string attrs = html.Substring(linkStart + 2, tagEnd - linkStart - 2);

                if (attrs.IndexOf("label", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string href = GetAttribute(attrs, "href");
                    string title = GetAttribute(attrs, "title");

                    if (String.IsNullOrWhiteSpace(title) && linkEnd > tagEnd)
                        title = CleanText(html.Substring(tagEnd + 1, linkEnd - tagEnd - 1));

                    if (!String.IsNullOrWhiteSpace(href) && !String.IsNullOrWhiteSpace(title))
                        return new LinkInfo { Title = title, Url = href };
                }

                index = linkEnd + 4;
            }

            return null;
        }

        private static string ExtractDescription(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int item = IndexOfIgnoreCase(html, "itemprop", 0);

            while (item >= 0)
            {
                int tagStart = html.LastIndexOf('<', item);
                int tagEnd = tagStart >= 0 ? html.IndexOf('>', tagStart) : -1;

                if (tagStart >= 0 && tagEnd > tagStart)
                {
                    string attrs = html.Substring(tagStart + 1, tagEnd - tagStart - 1);

                    if (String.Equals(GetAttribute(attrs, "itemprop"), "description", StringComparison.OrdinalIgnoreCase))
                    {
                        int close = IndexOfIgnoreCase(html, "</div>", tagEnd + 1);

                        if (close > tagEnd)
                            return CleanText(html.Substring(tagEnd + 1, close - tagEnd - 1));
                    }
                }

                item = IndexOfIgnoreCase(html, "itemprop", item + 8);
            }

            return "";
        }

        private static string ExtractNewsImageUrl(string articleAttributes, string articleBody)
        {
            string itemId = GetAttribute(articleAttributes, "itemId");
            string fallback = "";
            string html = articleBody ?? "";
            int index = 0;

            while (index < html.Length)
            {
                int imageStart = IndexOfIgnoreCase(html, "<img", index);

                if (imageStart < 0)
                    break;

                int tagEnd = html.IndexOf('>', imageStart);

                if (tagEnd < 0)
                    break;

                string attrs = html.Substring(imageStart + 4, tagEnd - imageStart - 4);
                string src = GetAttribute(attrs, "src");

                if (String.IsNullOrWhiteSpace(src))
                    src = GetAttribute(attrs, "data-src");

                if (!String.IsNullOrWhiteSpace(src))
                {
                    if (String.IsNullOrWhiteSpace(fallback))
                        fallback = src;

                    string itemProp = GetAttribute(attrs, "itemprop");
                    string id = GetAttribute(attrs, "id");

                    if (String.Equals(itemProp, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        if (String.IsNullOrWhiteSpace(itemId) || String.Equals(id, "hb" + itemId, StringComparison.OrdinalIgnoreCase))
                            return src;

                        fallback = src;
                    }
                }

                index = tagEnd + 1;
            }

            return fallback;
        }

        private static string ExtractRssImageUrl(XElement item, string descriptionHtml)
        {
            foreach (XElement element in item.Descendants())
            {
                if (element.Name.LocalName == "content" || element.Name.LocalName == "thumbnail")
                {
                    XAttribute url = element.Attribute("url");

                    if (url != null && !String.IsNullOrWhiteSpace(url.Value))
                        return url.Value;
                }
            }

            Match image = Regex.Match(descriptionHtml ?? "", @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!image.Success)
                return "";

            string attributes = image.Groups["attrs"].Value;
            string src = GetAttribute(attributes, "src");

            if (String.IsNullOrWhiteSpace(src))
                src = GetAttribute(attributes, "data-src");

            return src;
        }

        private static string ExtractMetaContent(string html, string itemProp)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(itemProp))
                return "";

            int index = 0;

            while (index < html.Length)
            {
                int metaStart = IndexOfIgnoreCase(html, "<meta", index);

                if (metaStart < 0)
                    break;

                int tagEnd = html.IndexOf('>', metaStart);

                if (tagEnd < 0)
                    break;

                string attrs = html.Substring(metaStart + 5, tagEnd - metaStart - 5);
                string prop = GetAttribute(attrs, "itemprop");

                if (String.Equals(prop, itemProp, StringComparison.OrdinalIgnoreCase))
                    return GetAttribute(attrs, "content");

                index = tagEnd + 1;
            }

            return "";
        }

        private static string ExtractAuthor(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int autor = IndexOfIgnoreCase(html, "autor", 0);

            if (autor >= 0)
            {
                int tagStart = html.LastIndexOf('<', autor);
                int tagEnd = tagStart >= 0 ? html.IndexOf('>', tagStart) : -1;

                if (tagStart >= 0 && tagEnd > tagStart)
                {
                    int close = FindSimpleCloseTag(html, tagStart, tagEnd);

                    if (close > tagEnd)
                        return CleanText(html.Substring(tagEnd + 1, close - tagEnd - 1));
                }
            }

            return ExtractMetaContent(html, "author");
        }

        private static string ExtractComments(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int pos = IndexOfIgnoreCase(html, "v-count", 0);

            if (pos < 0)
                return "";

            int tagStart = html.LastIndexOf('<', pos);
            int tagEnd = tagStart >= 0 ? html.IndexOf('>', tagStart) : -1;

            if (tagStart < 0 || tagEnd <= tagStart)
                return "";

            int close = FindSimpleCloseTag(html, tagStart, tagEnd);

            if (close <= tagEnd)
                return "";

            string value = CleanText(html.Substring(tagEnd + 1, close - tagEnd - 1));

            return String.IsNullOrWhiteSpace(value) ? "" : "комментариев: " + value;
        }

        private static string ElementValue(XElement parent, string name)
        {
            XElement element = parent.Element(name);
            return element == null ? "" : element.Value;
        }

        private static string GetAttribute(string attributes, string name)
        {
            if (String.IsNullOrEmpty(attributes) || String.IsNullOrEmpty(name))
                return "";

            int index = 0;

            while (index < attributes.Length)
            {
                int pos = IndexOfIgnoreCase(attributes, name, index);

                if (pos < 0)
                    return "";

                bool beforeOk = pos == 0 || !IsAttributeNameChar(attributes[pos - 1]);

                int afterName = pos + name.Length;
                bool afterOk = afterName >= attributes.Length || !IsAttributeNameChar(attributes[afterName]);

                if (!beforeOk || !afterOk)
                {
                    index = pos + name.Length;
                    continue;
                }

                int eq = afterName;

                while (eq < attributes.Length && Char.IsWhiteSpace(attributes[eq]))
                    eq++;

                if (eq >= attributes.Length || attributes[eq] != '=')
                {
                    index = afterName;
                    continue;
                }

                eq++;

                while (eq < attributes.Length && Char.IsWhiteSpace(attributes[eq]))
                    eq++;

                if (eq >= attributes.Length)
                    return "";

                char quote = attributes[eq];
                int valueStart;
                int valueEnd;

                if (quote == '\'' || quote == '"')
                {
                    valueStart = eq + 1;
                    valueEnd = attributes.IndexOf(quote, valueStart);

                    if (valueEnd < 0)
                        valueEnd = attributes.Length;
                }
                else
                {
                    valueStart = eq;
                    valueEnd = valueStart;

                    while (valueEnd < attributes.Length && !Char.IsWhiteSpace(attributes[valueEnd]) && attributes[valueEnd] != '>')
                        valueEnd++;
                }

                if (valueEnd <= valueStart)
                    return "";

                return DecodeEntities(attributes.Substring(valueStart, valueEnd - valueStart));
            }

            return "";
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string stripped = StripTagsFast(html);
            string decoded = DecodeEntities(stripped);

            return CollapseWhiteSpace(decoded);
        }

        private static int IndexOfIgnoreCase(string text, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(value))
                return -1;

            if (startIndex < 0)
                startIndex = 0;

            if (startIndex >= text.Length)
                return -1;

            return text.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAttributeNameChar(char ch)
        {
            return Char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':';
        }

        private static bool IsDigits(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (!Char.IsDigit(value[i]))
                    return false;
            }

            return true;
        }

        private static string StripTagsFast(string html)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            var builder = new StringBuilder(html.Length);
            int i = 0;

            while (i < html.Length)
            {
                char ch = html[i];

                if (ch == '<')
                {
                    if (StartsWithAt(html, i, "<br") ||
                        StartsWithAt(html, i, "</p") ||
                        StartsWithAt(html, i, "</div") ||
                        StartsWithAt(html, i, "</li"))
                    {
                        builder.Append(' ');
                    }

                    if (StartsWithAt(html, i, "<script"))
                    {
                        int scriptEnd = IndexOfIgnoreCase(html, "</script>", i + 7);
                        i = scriptEnd >= 0 ? scriptEnd + 9 : html.Length;
                        continue;
                    }

                    if (StartsWithAt(html, i, "<style"))
                    {
                        int styleEnd = IndexOfIgnoreCase(html, "</style>", i + 6);
                        i = styleEnd >= 0 ? styleEnd + 8 : html.Length;
                        continue;
                    }

                    int tagEnd = html.IndexOf('>', i + 1);

                    if (tagEnd < 0)
                        break;

                    i = tagEnd + 1;
                    continue;
                }

                builder.Append(ch);
                i++;
            }

            return builder.ToString();
        }

        private static string CollapseWhiteSpace(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            var builder = new StringBuilder(value.Length);
            bool wasSpace = false;

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];

                if (Char.IsWhiteSpace(ch))
                {
                    if (!wasSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        wasSpace = true;
                    }
                }
                else
                {
                    builder.Append(ch);
                    wasSpace = false;
                }
            }

            return builder.ToString().Trim();
        }

        private static int FindSimpleCloseTag(string html, int tagStart, int tagEnd)
        {
            if (String.IsNullOrEmpty(html) || tagStart < 0 || tagEnd <= tagStart)
                return -1;

            int nameStart = tagStart + 1;

            while (nameStart < tagEnd && (html[nameStart] == '/' || Char.IsWhiteSpace(html[nameStart])))
                nameStart++;

            int nameEnd = nameStart;

            while (nameEnd < tagEnd && (Char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-'))
                nameEnd++;

            if (nameEnd <= nameStart)
                return -1;

            string tagName = html.Substring(nameStart, nameEnd - nameStart);
            string close = "</" + tagName + ">";

            return IndexOfIgnoreCase(html, close, tagEnd + 1);
        }

        private static int FindJsonObjectEnd(string json, int start)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = start; i < json.Length; i++)
            {
                char ch = json[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{')
                    depth++;
                else if (ch == '}')
                {
                    depth--;

                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static string ExtractJsonString(string obj, string name)
        {
            int namePos = IndexOfIgnoreCase(obj, "\"" + name + "\"", 0);

            if (namePos < 0)
                return "";

            int colon = obj.IndexOf(':', namePos + name.Length + 2);

            if (colon < 0)
                return "";

            int start = colon + 1;

            while (start < obj.Length && Char.IsWhiteSpace(obj[start]))
                start++;

            if (start >= obj.Length || obj[start] == 'n')
                return "";

            if (obj[start] != '"')
            {
                int end = start;

                while (end < obj.Length && obj[end] != ',' && obj[end] != '}')
                    end++;

                return JsonUnescape(obj.Substring(start, end - start).Trim());
            }

            start++;

            var builder = new StringBuilder();
            bool escaped = false;

            for (int i = start; i < obj.Length; i++)
            {
                char ch = obj[i];

                if (escaped)
                {
                    if (ch == 'n')
                    {
                        builder.Append('\n');
                    }
                    else if (ch == 'r')
                    {
                        builder.Append('\r');
                    }
                    else if (ch == 't')
                    {
                        builder.Append('\t');
                    }
                    else if (ch == 'u' && i + 4 < obj.Length)
                    {
                        string hex = obj.Substring(i + 1, 4);
                        int code;

                        if (Int32.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    break;
                }
                else
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static bool ExtractJsonBool(string obj, string name)
        {
            int namePos = IndexOfIgnoreCase(obj, "\"" + name + "\"", 0);

            if (namePos < 0)
                return false;

            int colon = obj.IndexOf(':', namePos + name.Length + 2);

            if (colon < 0)
                return false;

            int start = colon + 1;

            while (start < obj.Length && Char.IsWhiteSpace(obj[start]))
                start++;

            return start + 4 <= obj.Length &&
                String.Compare(obj, start, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string JsonUnescape(string value)
        {
            if (String.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
                return value ?? "";

            return value
                .Replace("\\/", "/")
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\");
        }

        private static string DecodeEntities(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            try
            {
                return HtmlUtilities.ConvertToText(value);
            }
            catch
            {
                return value
                    .Replace("&amp;", "&")
                    .Replace("&quot;", "\"")
                    .Replace("&#39;", "'")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .Replace("&nbsp;", " ");
            }
        }

        private static string NormalizeRss(string body)
        {
            return Regex.Replace(body, @"&(?!(?:#\d+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);)", "&amp;");
        }

        private static string NormalizeUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            url = url.Trim();

            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;

            Uri absolute;

            if (Uri.TryCreate(url, UriKind.Absolute, out absolute))
                return absolute.ToString();

            Uri baseUri = url.IndexOf("forum/", StringComparison.OrdinalIgnoreCase) >= 0
                ? new Uri("https://" + Host + "/forum/")
                : new Uri("https://" + Host + "/");

            return new Uri(baseUri, url).ToString();
        }

        private static BitmapImage CreateBitmapImage(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                BitmapImage image = new BitmapImage();
                image.DecodePixelWidth = NewsImageDecodeWidth;
                image.UriSource = new Uri(url, UriKind.Absolute);
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsBlockedNews(string title, string description, string url)
        {
            string text = JoinNonEmpty(" ", title, description, url).ToLowerInvariant();

            int pos = text.IndexOf("camon", StringComparison.OrdinalIgnoreCase);

            if (pos < 0)
                return false;

            int index = pos + 5;

            while (index < text.Length && (Char.IsWhiteSpace(text[index]) || text[index] == '-' || text[index] == '_'))
                index++;

            return index + 2 <= text.Length && text[index] == '5' && text[index + 1] == '0';
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return String.Join(separator, values.Where(v => !String.IsNullOrWhiteSpace(v)));
        }

        private static string FormatDate(string value)
        {
            DateTimeOffset date;
            return DateTimeOffset.TryParse(value, out date) ? date.ToString("dd.MM.yyyy") : value;
        }

        private static bool StartsWithAt(string value, int index, string prefix)
        {
            if (String.IsNullOrEmpty(value) || String.IsNullOrEmpty(prefix))
                return false;

            if (index < 0 || index + prefix.Length > value.Length)
                return false;

            return String.Compare(value, index, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private void SelectForumNode(ForumNode node)
        {
            if (Object.ReferenceEquals(_selectedForumNode, node))
                return;

            if (_selectedForumNode != null)
                _selectedForumNode.IsSelected = false;

            _selectedForumNode = node;

            if (_selectedForumNode != null)
                _selectedForumNode.IsSelected = true;
        }

        private void ToggleForumNode(ForumNode node)
        {
            if (node == null || node.Children.Count == 0)
                return;

            int index = _visibleForumItems.IndexOf(node);

            if (index < 0)
            {
                node.IsExpanded = !node.IsExpanded;
                RebuildVisibleForumItems();
                return;
            }

            if (node.IsExpanded)
            {
                int removeCount = CountVisibleForumDescendants(node);

                node.IsExpanded = false;

                for (int i = 0; i < removeCount && index + 1 < _visibleForumItems.Count; i++)
                    _visibleForumItems.RemoveAt(index + 1);
            }
            else
            {
                node.IsExpanded = true;
                InsertVisibleForumDescendants(node, index + 1);
            }
        }

        private int InsertVisibleForumDescendants(ForumNode node, int insertIndex)
        {
            if (node == null)
                return insertIndex;

            foreach (ForumNode child in node.Children)
            {
                _visibleForumItems.Insert(insertIndex, child);
                insertIndex++;

                if (child.IsExpanded)
                    insertIndex = InsertVisibleForumDescendants(child, insertIndex);
            }

            return insertIndex;
        }

        private static int CountVisibleForumDescendants(ForumNode node)
        {
            if (node == null)
                return 0;

            int count = 0;

            foreach (ForumNode child in node.Children)
            {
                count++;

                if (child.IsExpanded)
                    count += CountVisibleForumDescendants(child);
            }

            return count;
        }

        private void ForumArrow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            ForumNode node = element == null ? null : element.Tag as ForumNode;

            if (node == null)
                return;

            SelectForumNode(node);

            if (node.Children.Count > 0)
            {
                ToggleForumNode(node);
                e.Handled = true;
            }
        }

        private void NewsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            FeedItem item = e.ClickedItem as FeedItem;

            if (item == null || String.IsNullOrWhiteSpace(item.Url))
                return;

            Frame.Navigate(typeof(NewsPage), item.Url);
        }

        private void ForumListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ForumNode node = e.ClickedItem as ForumNode;

            if (node == null)
                return;

            SelectForumNode(node);

            if (node.Children.Count > 0)
            {
                ToggleForumNode(node);
                return;
            }

            string forumId = node.Id;

            if (String.IsNullOrWhiteSpace(forumId))
                forumId = ExtractForumId(node.Url);

            if (String.IsNullOrWhiteSpace(forumId))
                return;

            Frame.Navigate(typeof(ForumPage), forumId);
        }

        private async Task EnsureQmsLoadedAsync(bool forceReload)
        {
            if (_qmsLoading)
            {
                _qmsLoadQueued = true;
                return;
            }

            if (!ForumAuthService.IsAuthorized)
            {
                _qmsRoots.Clear();
                _visibleQmsItems.Clear();

                if (QmsEmptyTextBlock != null)
                {
                    QmsEmptyTextBlock.Text = "Войдите в аккаунт, чтобы открыть QMS.";
                    QmsEmptyTextBlock.Visibility = Visibility.Visible;
                }

                LiveTileService.UpdateFromNewsAndQms(_newsItems, _qmsRoots);
                return;
            }

            if (!forceReload && _qmsRoots.Count > 0)
            {
                if (QmsEmptyTextBlock != null)
                    QmsEmptyTextBlock.Visibility = Visibility.Collapsed;

                LiveTileService.UpdateFromNewsAndQms(_newsItems, _qmsRoots);
                return;
            }

            _qmsLoading = true;

            if (QmsProgressRing != null)
                QmsProgressRing.IsActive = true;

            if (RefreshAppBarButton != null)
                RefreshAppBarButton.IsEnabled = false;

            if (QmsEmptyTextBlock != null)
            {
                QmsEmptyTextBlock.Text = "";
                QmsEmptyTextBlock.Visibility = _visibleQmsItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            SetStatus("", false);

            try
            {
                await LoadQmsContactsAsync();

                if (QmsEmptyTextBlock != null)
                {
                    QmsEmptyTextBlock.Text = _visibleQmsItems.Count == 0 ? "Контакты QMS не найдены." : "";
                    QmsEmptyTextBlock.Visibility = _visibleQmsItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                if (_qmsRoots.Count == 0)
                    _visibleQmsItems.Clear();

                if (QmsEmptyTextBlock != null)
                {
                    QmsEmptyTextBlock.Text = "QMS не загружен: " + ex.Message;
                    QmsEmptyTextBlock.Visibility = Visibility.Visible;
                }

                SetStatus("QMS не загружен: " + ex.Message, true);
            }
            finally
            {
                if (QmsProgressRing != null)
                    QmsProgressRing.IsActive = false;

                if (RefreshAppBarButton != null)
                    RefreshAppBarButton.IsEnabled = true;

                _qmsLoading = false;
            }

            if (_qmsLoadQueued && MainPivot != null && MainPivot.SelectedIndex == 2)
            {
                _qmsLoadQueued = false;
                await EnsureQmsLoadedAsync(false);
            }
        }

        private async Task LoadQmsContactsAsync()
        {
            List<QmsContact> contacts = await _qmsService.GetContactsAsync();

            _qmsRoots.Clear();
            _visibleQmsItems.Clear();

            foreach (QmsContact contact in contacts)
            {
                if (contact == null || String.IsNullOrWhiteSpace(contact.Id))
                    continue;

                QmsNode node = new QmsNode();

                node.Id = contact.Id;
                node.ContactId = contact.Id;
                node.ContactNick = contact.Title;
                node.Title = String.IsNullOrWhiteSpace(contact.Title) ? "Пользователь " + contact.Id : contact.Title;
                node.Subtitle = contact.UnreadCount > 0 ? "новых: " + contact.UnreadCount : "нажмите, чтобы открыть чаты";
                node.AvatarUrl = contact.AvatarUrl;
                node.UnreadCount = contact.UnreadCount;
                node.HasThreads = true;
                node.IsThread = false;
                node.Level = 0;
                node.ApplyAvatar();

                _qmsRoots.Add(node);
                _visibleQmsItems.Add(node);
            }

            LiveTileService.UpdateFromNewsAndQms(_newsItems, _qmsRoots);
        }

        private async Task LoadQmsThreadsAsync(QmsNode contactNode)
        {
            if (contactNode == null || String.IsNullOrWhiteSpace(contactNode.ContactId) || contactNode.ThreadsLoaded)
                return;

            contactNode.IsLoadingThreads = true;

            string oldSubtitle = contactNode.Subtitle;

            contactNode.Subtitle = "";

            try
            {
                List<QmsThread> threads = await _qmsService.GetThreadsAsync(contactNode.ContactId);

                contactNode.Children.Clear();

                foreach (QmsThread thread in threads)
                {
                    if (thread == null || String.IsNullOrWhiteSpace(thread.Id))
                        continue;

                    QmsNode child = new QmsNode();

                    child.Id = thread.Id;
                    child.ThreadId = thread.Id;
                    child.ContactId = contactNode.ContactId;
                    child.ContactNick = contactNode.ContactNick;
                    child.Title = String.IsNullOrWhiteSpace(thread.Title) ? "Без названия" : thread.Title;
                    child.Subtitle = BuildQmsThreadSubtitle(thread);
                    child.UnreadCount = thread.UnreadCount;
                    child.MessagesCount = thread.MessagesCount;
                    child.Level = 1;
                    child.IsThread = true;
                    child.Parent = contactNode;
                    child.AvatarUrl = contactNode.AvatarUrl;
                    child.AvatarImage = contactNode.AvatarImage;

                    contactNode.Children.Add(child);
                }

                contactNode.ThreadsLoaded = true;
                contactNode.Subtitle = contactNode.Children.Count == 0 ? "чатов нет" : "чатов: " + contactNode.Children.Count;
            }
            catch (Exception ex)
            {
                contactNode.Subtitle = String.IsNullOrWhiteSpace(oldSubtitle) ? "ошибка загрузки чатов" : oldSubtitle;
                SetStatus("Чаты QMS не загружены: " + ex.Message, true);
                throw;
            }
            finally
            {
                contactNode.IsLoadingThreads = false;
            }
        }

        private static string BuildQmsThreadSubtitle(QmsThread thread)
        {
            if (thread == null)
                return "";

            List<string> parts = new List<string>();

            if (thread.MessagesCount > 0)
                parts.Add("сообщений: " + thread.MessagesCount);

            if (thread.UnreadCount > 0)
                parts.Add("новых: " + thread.UnreadCount);

            if (!String.IsNullOrWhiteSpace(thread.LastMessageText))
                parts.Add(thread.LastMessageText);

            return String.Join(" · ", parts);
        }

        private async void QmsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            QmsNode node = e.ClickedItem as QmsNode;

            if (node == null)
                return;

            if (!node.IsThread)
            {
                await ToggleQmsNodeAsync(node);
                return;
            }

            if (String.IsNullOrWhiteSpace(node.ContactId) || String.IsNullOrWhiteSpace(node.ThreadId))
                return;

            QmsThreadNavigationArgs args = new QmsThreadNavigationArgs();

            args.ContactId = node.ContactId;
            args.ContactNick = node.ContactNick;
            args.ThreadId = node.ThreadId;
            args.ThreadTitle = node.Title;
            args.AvatarUrl = node.AvatarUrl;

            Frame.Navigate(typeof(ChatPage), args);
        }

        private async void QmsArrow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            QmsNode node = element == null ? null : element.Tag as QmsNode;

            if (node != null && !node.IsThread)
            {
                await ToggleQmsNodeAsync(node);
                e.Handled = true;
            }
        }

        private async Task ToggleQmsNodeAsync(QmsNode node)
        {
            if (node == null || node.IsThread)
                return;

            int nodeIndex = _visibleQmsItems.IndexOf(node);

            if (!node.ThreadsLoaded)
                await LoadQmsThreadsAsync(node);

            if (node.IsExpanded)
            {
                int removeCount = CountVisibleQmsDescendants(node);

                node.IsExpanded = false;

                if (nodeIndex >= 0)
                {
                    for (int i = 0; i < removeCount && nodeIndex + 1 < _visibleQmsItems.Count; i++)
                        _visibleQmsItems.RemoveAt(nodeIndex + 1);
                }
                else
                {
                    RebuildVisibleQmsItems();
                }

                return;
            }

            node.IsExpanded = true;

            if (nodeIndex >= 0)
                InsertVisibleQmsDescendants(node, nodeIndex + 1);
            else
                RebuildVisibleQmsItems();
        }

        private void RebuildVisibleQmsItems()
        {
            _visibleQmsItems.Clear();

            foreach (QmsNode root in _qmsRoots)
            {
                if (root == null)
                    continue;

                _visibleQmsItems.Add(root);

                if (root.IsExpanded)
                    InsertVisibleQmsDescendants(root, _visibleQmsItems.Count);
            }
        }

        private int InsertVisibleQmsDescendants(QmsNode node, int insertIndex)
        {
            if (node == null)
                return insertIndex;

            foreach (QmsNode child in node.Children)
            {
                _visibleQmsItems.Insert(insertIndex, child);
                insertIndex++;

                if (child.IsExpanded)
                    insertIndex = InsertVisibleQmsDescendants(child, insertIndex);
            }

            return insertIndex;
        }

        private static int CountVisibleQmsDescendants(QmsNode node)
        {
            if (node == null)
                return 0;

            int count = 0;

            foreach (QmsNode child in node.Children)
            {
                count++;

                if (child.IsExpanded)
                    count += CountVisibleQmsDescendants(child);
            }

            return count;
        }

        private static string ExtractForumId(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string text = value.Trim();

            if (IsDigits(text))
                return text;

            string[] markers = new[] { "showforum=", "showforum/", "f=", "/forum/" };

            foreach (string marker in markers)
            {
                int pos = IndexOfIgnoreCase(text, marker, 0);

                if (pos < 0)
                    continue;

                int start = pos + marker.Length;

                while (start < text.Length && !Char.IsDigit(text[start]))
                    start++;

                int end = start;

                while (end < text.Length && Char.IsDigit(text[end]))
                    end++;

                if (end > start)
                    return text.Substring(start, end - start);
            }

            return "";
        }

        public sealed class FeedItem : INotifyPropertyChanged
        {
            private BitmapImage _image;

            public event PropertyChangedEventHandler PropertyChanged;

            public string Title { get; set; }
            public string Description { get; set; }
            public string Info { get; set; }
            public string Url { get; set; }
            public string ImageUrl { get; set; }

            public BitmapImage Image
            {
                get { return _image; }
                set
                {
                    if (Object.ReferenceEquals(_image, value))
                        return;

                    _image = value;
                    OnPropertyChanged("Image");
                }
            }

            public Visibility ImageVisibility { get; set; }

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChangedEventHandler handler = PropertyChanged;

                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public sealed class ForumNode : INotifyPropertyChanged
        {
            private bool _isExpanded;
            private bool _isSelected;

            public ForumNode()
            {
                Children = new List<ForumNode>();
                Description = "";
                Title = "";
                Id = "";
                ParentId = "";
                Url = "";
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public string Id { get; set; }
            public string ParentId { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string Url { get; set; }
            public bool HasTopics { get; set; }
            public bool HasForums { get; set; }

            public bool IsExpanded
            {
                get { return _isExpanded; }
                set
                {
                    if (_isExpanded == value)
                        return;

                    _isExpanded = value;

                    OnPropertyChanged("IsExpanded");
                    OnPropertyChanged("TreeButtonText");
                    OnPropertyChanged("TreeButtonOpacity");
                    OnPropertyChanged("KindText");
                }
            }

            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;

                    OnPropertyChanged("IsSelected");
                    OnPropertyChanged("RowBackground");
                }
            }

            public int Level { get; set; }
            public List<ForumNode> Children { get; private set; }

            public Thickness RowMargin
            {
                get
                {
                    if (Level <= 0)
                        return new Thickness(0, 10, 0, 2);

                    if (Level == 1)
                        return new Thickness(24, 0, 0, 0);

                    return new Thickness(42, 0, 0, 0);
                }
            }

            public double RowHeight
            {
                get
                {
                    if (Level <= 0)
                        return 58.0;

                    return 46.0;
                }
            }

            private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x8E, 0xE8));
            private static readonly Brush TransparentRowBrush = new SolidColorBrush(Colors.Transparent);

            public Brush RowBackground
            {
                get
                {
                    return IsSelected ? SelectedRowBrush : TransparentRowBrush;
                }
            }

            public string TreeButtonText
            {
                get
                {
                    if (Children.Count == 0)
                        return "";

                    return IsExpanded ? "−" : "+";
                }
            }

            public double TreeButtonFontSize
            {
                get
                {
                    if (Children.Count == 0)
                        return 0.0;

                    return 30.0;
                }
            }

            public double TreeButtonOpacity
            {
                get
                {
                    if (Children.Count == 0)
                        return 0.0;

                    return 0.95;
                }
            }

            public Visibility RootTitleVisibility
            {
                get
                {
                    return Level <= 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            public Visibility ChildTitleVisibility
            {
                get
                {
                    return Level <= 0 ? Visibility.Collapsed : Visibility.Visible;
                }
            }

            public string KindText
            {
                get
                {
                    if (Children.Count > 0)
                    {
                        if (IsExpanded)
                            return "разделы открыты: " + Children.Count;

                        return "разделы: " + Children.Count;
                    }

                    return "форум";
                }
            }

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChangedEventHandler handler = PropertyChanged;

                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public sealed class QmsNode : INotifyPropertyChanged
        {
            private bool _isExpanded;
            private string _subtitle;
            private BitmapImage _avatarImage;
            private bool _isLoadingThreads;

            public QmsNode()
            {
                Children = new List<QmsNode>();
                Title = "";
                Subtitle = "";
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public string Id { get; set; }
            public string ContactId { get; set; }
            public string ContactNick { get; set; }
            public string ThreadId { get; set; }
            public string Title { get; set; }
            public string AvatarUrl { get; set; }
            public bool HasThreads { get; set; }
            public bool ThreadsLoaded { get; set; }
            public bool IsThread { get; set; }
            public int Level { get; set; }
            public int MessagesCount { get; set; }
            public QmsNode Parent { get; set; }
            public List<QmsNode> Children { get; private set; }

            public string Subtitle
            {
                get { return _subtitle; }
                set
                {
                    if (_subtitle == value)
                        return;

                    _subtitle = value;
                    OnPropertyChanged("Subtitle");
                }
            }

            public int UnreadCount { get; set; }

            public bool IsLoadingThreads
            {
                get { return _isLoadingThreads; }
                set
                {
                    if (_isLoadingThreads == value)
                        return;

                    _isLoadingThreads = value;
                    OnPropertyChanged("IsLoadingThreads");
                    OnPropertyChanged("ExpandButtonText");
                }
            }

            public bool IsExpanded
            {
                get { return _isExpanded; }
                set
                {
                    if (_isExpanded == value)
                        return;

                    _isExpanded = value;
                    OnPropertyChanged("IsExpanded");
                    OnPropertyChanged("ExpandButtonText");
                }
            }

            public BitmapImage AvatarImage
            {
                get { return _avatarImage; }
                set
                {
                    if (Object.ReferenceEquals(_avatarImage, value))
                        return;

                    _avatarImage = value;
                    OnPropertyChanged("AvatarImage");
                    OnPropertyChanged("AvatarVisibility");
                    OnPropertyChanged("IconText");
                }
            }

            public string ExpandButtonText
            {
                get
                {
                    if (IsThread)
                        return "";

                    if (IsLoadingThreads)
                        return "…";

                    return IsExpanded ? "▾" : "▸";
                }
            }

            public double ExpandButtonOpacity
            {
                get { return IsThread ? 0.0 : 0.88; }
            }

            public string IconText
            {
                get
                {
                    if (IsThread)
                        return "";

                    return AvatarImage == null ? "" : "";
                }
            }

            public Visibility AvatarVisibility
            {
                get { return AvatarImage == null ? Visibility.Collapsed : Visibility.Visible; }
            }

            public Visibility UnreadBadgeVisibility
            {
                get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
            }

            public string UnreadCountText
            {
                get { return UnreadCount > 99 ? "99+" : UnreadCount.ToString(); }
            }

            public double TitleFontSize
            {
                get { return IsThread ? 18.0 : 21.0; }
            }

            public Thickness RowMargin
            {
                get { return IsThread ? new Thickness(28, 0, 0, 0) : new Thickness(0); }
            }

            public void ApplyAvatar()
            {
                if (String.IsNullOrWhiteSpace(AvatarUrl))
                    return;

                try
                {
                    BitmapImage image = new BitmapImage();
                    image.DecodePixelWidth = 64;
                    image.UriSource = new Uri(AvatarUrl, UriKind.Absolute);
                    AvatarImage = image;
                }
                catch
                {
                    AvatarImage = null;
                }
            }

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChangedEventHandler handler = PropertyChanged;

                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        [DataContract]
        private sealed class ForumJsonItem
        {
            [DataMember(Name = "id")]
            public string Id { get; set; }

            [DataMember(Name = "title")]
            public string Title { get; set; }

            [DataMember(Name = "description")]
            public string Description { get; set; }

            [DataMember(Name = "parentId")]
            public string ParentId { get; set; }

            [DataMember(Name = "isHasTopics")]
            public bool IsHasTopics { get; set; }

            [DataMember(Name = "isHasForums")]
            public bool IsHasForums { get; set; }
        }

        private sealed class LinkInfo
        {
            public string Title { get; set; }
            public string Url { get; set; }
        }
    }
}