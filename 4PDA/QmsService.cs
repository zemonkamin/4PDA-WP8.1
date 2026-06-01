using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed class QmsService
    {
        private const string ForumEndpoint = "https://4pda.to/forum/index.php";
        private static readonly RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
        private readonly HttpClient _httpClient = new HttpClient();

        public QmsService()
        {
            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; Touch; rv:11.0) like Gecko");
            }
            catch
            {
            }
        }

        public async Task<List<QmsContact>> GetContactsAsync()
        {
            string url = ForumEndpoint + "?&act=qms-xhr&action=userlist";
            string html = await GetStringAsync(url);
            EnsureAuthorized(html);
            return ParseContacts(html);
        }

        public async Task<List<QmsThread>> GetThreadsAsync(string contactId)
        {
            if (String.IsNullOrWhiteSpace(contactId))
                return new List<QmsThread>();

            string url = ForumEndpoint + "?act=qms&mid=" + Uri.EscapeDataString(contactId);
            string html = await GetStringAsync(url);
            EnsureAuthorized(html);
            return ParseThreads(html);
        }


        public Task<List<QmsMessage>> GetMessagesAsync(string contactId, string threadId, string contactNick, string threadTitle)
        {
            return GetMessagesAsync(contactId, threadId, contactNick, threadTitle, "");
        }

        public async Task<List<QmsMessage>> GetMessagesAsync(string contactId, string threadId, string contactNick, string threadTitle, string contactAvatarUrl)
        {
            if (String.IsNullOrWhiteSpace(contactId) || String.IsNullOrWhiteSpace(threadId))
                return new List<QmsMessage>();

            string url = ForumEndpoint + "?act=qms&mid=" + Uri.EscapeDataString(contactId) + "&t=" + Uri.EscapeDataString(threadId);
            string html = await GetStringAsync(url);
            EnsureAuthorized(html);
            return ParseMessages(html, contactNick, threadTitle, contactAvatarUrl);
        }

        public async Task SendMessageAsync(string contactId, string threadId, string message)
        {
            if (String.IsNullOrWhiteSpace(contactId) || String.IsNullOrWhiteSpace(threadId))
                throw new InvalidOperationException("Не указан контакт или чат.");

            if (String.IsNullOrWhiteSpace(message))
                throw new InvalidOperationException("Сообщение пустое.");

            string url = ForumEndpoint + "?act=qms&mid=" + Uri.EscapeDataString(contactId) +
                "&t=" + Uri.EscapeDataString(threadId) + "&xhr=body&do=1";

            List<KeyValuePair<string, string>> data = new List<KeyValuePair<string, string>>();
            data.Add(new KeyValuePair<string, string>("action", "send-message"));
            data.Add(new KeyValuePair<string, string>("mid", contactId));
            data.Add(new KeyValuePair<string, string>("t", threadId));
            data.Add(new KeyValuePair<string, string>("message", message));

            string html = await PostFormAsync(url, data);
            EnsureAuthorized(html);

            if (LooksLikeQmsError(html))
            {
                data[0] = new KeyValuePair<string, string>("action", "add-message");
                html = await PostFormAsync(url, data);
                EnsureAuthorized(html);
                if (LooksLikeQmsError(html))
                    throw new InvalidOperationException(CleanText(html));
            }
        }

        private async Task<string> GetStringAsync(string url)
        {
            HttpResponseMessage response = await _httpClient.GetAsync(new Uri(url, UriKind.Absolute));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> data)
        {
            HttpFormUrlEncodedContent content = new HttpFormUrlEncodedContent(data);
            HttpResponseMessage response = await _httpClient.PostAsync(new Uri(url, UriKind.Absolute), content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private static List<QmsContact> ParseContacts(string html)
        {
            List<QmsContact> result = new List<QmsContact>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            string pattern = "<a\\s+class=\"list-group-item[\\s\\S]*?(?:mid=|/)(\\d+)\">[\\s\\S]*?<div\\s+class=\"ba?ge\">([\\s\\S]*?)</div>[\\s\\S]*?<img[^>]*src=\"([^\"]*)\"[^>]*title=\"([^\"]*)\"";
            foreach (Match match in Regex.Matches(html, pattern, Rx))
            {
                QmsContact contact = new QmsContact();
                contact.Id = match.Groups[1].Value;
                contact.UnreadCount = ParseInt(match.Groups[2].Value);
                contact.AvatarUrl = NormalizeUrl(Decode(match.Groups[3].Value));
                contact.Title = Decode(match.Groups[4].Value);
                AddContactIfNew(result, contact);
            }

            if (result.Count > 0)
                return result;

            string itemPattern = "<a[^>]+href=\"[^\"]*(?:mid=|/)(\\d+)[^\"]*\"[^>]*>([\\s\\S]*?)</a>";
            foreach (Match match in Regex.Matches(html, itemPattern, Rx))
            {
                string block = match.Groups[2].Value;
                QmsContact contact = new QmsContact();
                contact.Id = match.Groups[1].Value;
                contact.Title = FirstNonEmpty(GetAttribute(block, "title"), CleanText(block));
                contact.AvatarUrl = NormalizeUrl(GetAttribute(block, "src"));
                contact.UnreadCount = ParseInt(FirstMatch(block, "<div[^>]+class=\"ba?ge\"[^>]*>([\\s\\S]*?)</div>"));
                AddContactIfNew(result, contact);
            }

            return result;
        }

        private static void AddContactIfNew(List<QmsContact> result, QmsContact contact)
        {
            if (contact == null || String.IsNullOrWhiteSpace(contact.Id))
                return;

            foreach (QmsContact item in result)
            {
                if (item != null && item.Id == contact.Id)
                    return;
            }

            if (String.IsNullOrWhiteSpace(contact.Title))
                contact.Title = "Пользователь " + contact.Id;

            result.Add(contact);
        }

        private static List<QmsThread> ParseThreads(string html)
        {
            List<QmsThread> result = new List<QmsThread>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            string container = FirstMatch(html, "<div\\s+class=\"list-group\">([\\s\\S]*?)(?:<form\\s|</form>|<script|$)");
            if (String.IsNullOrWhiteSpace(container))
                container = html;

            string itemPattern = "<a\\s+class=\"list-group-item[^\"]*?(?:-(\\d+))?[^\"]*\"[^>]*>([\\s\\S]*?)</a>";
            foreach (Match match in Regex.Matches(container, itemPattern, Rx))
            {
                string block = match.Groups[2].Value;
                QmsThread thread = new QmsThread();
                thread.Id = match.Groups[1].Value;
                if (String.IsNullOrWhiteSpace(thread.Id))
                    thread.Id = FirstMatch(match.Value, "list-group-item[^\"']*-(\\d+)");
                if (String.IsNullOrWhiteSpace(thread.Id))
                    thread.Id = FirstMatch(match.Value, "[?&]t=(\\d+)");

                thread.Title = Decode(FirstNonEmpty(
                    FirstMatch(block, "<strong[^>]*>([\\s\\S]*?)</strong>"),
                    FirstMatch(block, "<span[^>]*class=\"[^\"]*title[^\"]*\"[^>]*>([\\s\\S]*?)</span>"),
                    CleanText(block)));
                thread.UnreadCount = ParseInt(FirstMatch(block, "<span[^>]*class=\"[^\"]*(?:badge|new|unread)[^\"]*\"[^>]*>([\\s\\S]*?)</span>"));
                if (thread.UnreadCount == 0)
                    thread.UnreadCount = ParseInt(FirstMatch(block, "<div[^>]*class=\"[^\"]*(?:badge|new|unread|ba?ge)[^\"]*\"[^>]*>([\\s\\S]*?)</div>"));
                thread.MessagesCount = ParseInt(FirstMatch(block, "<span[^>]*class=\"[^\"]*(?:count|messages)[^\"]*\"[^>]*>([\\s\\S]*?)</span>"));
                thread.LastMessageText = CleanText(FirstNonEmpty(
                    FirstMatch(block, "<div[^>]*class=\"[^\"]*(?:desc|last|small)[^\"]*\"[^>]*>([\\s\\S]*?)</div>"),
                    ""));

                if (!String.IsNullOrWhiteSpace(thread.Id))
                    result.Add(thread);
            }

            if (result.Count > 0)
                return result;

            string linkPattern = "<a[^>]+href=\"[^\"]*[?&]t=(\\d+)[^\"]*\"[^>]*>([\\s\\S]*?)</a>";
            foreach (Match match in Regex.Matches(html, linkPattern, Rx))
            {
                QmsThread thread = new QmsThread();
                thread.Id = match.Groups[1].Value;
                thread.Title = CleanText(match.Groups[2].Value);
                if (!String.IsNullOrWhiteSpace(thread.Id))
                    result.Add(thread);
            }

            return result;
        }

        private sealed class QmsHtmlMarker
        {
            public int Index { get; set; }
            public bool IsDate { get; set; }
            public string Html { get; set; }
        }

        private static List<QmsMessage> ParseMessages(string html, string contactNick, string threadTitle, string contactAvatarUrl)
        {
            List<QmsMessage> result = new List<QmsMessage>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            List<QmsHtmlMarker> markers = FindQmsMarkers(html);
            string currentDay = "";
            string lastPrintedDay = "";

            for (int i = 0; i < markers.Count; i++)
            {
                QmsHtmlMarker marker = markers[i];
                if (marker == null)
                    continue;

                if (marker.IsDate)
                {
                    string day = ExtractDateSeparatorText(marker.Html);
                    if (!String.IsNullOrWhiteSpace(day))
                        currentDay = day;
                    continue;
                }

                int blockEnd = GetQmsBlockEnd(html, markers, i);
                if (blockEnd <= marker.Index)
                    continue;

                string block = html.Substring(marker.Index, blockEnd - marker.Index);
                QmsMessage message = ParseMessageBlock(block, contactNick, threadTitle, contactAvatarUrl, currentDay);
                if (message == null)
                    continue;

                if (!String.IsNullOrWhiteSpace(currentDay) && currentDay != lastPrintedDay)
                {
                    message.DaySeparatorText = currentDay;
                    message.DateSeparatorVisibility = Visibility.Visible;
                    lastPrintedDay = currentDay;
                }

                result.Add(message);
            }

            if (result.Count > 0)
                return result;

            List<string> blocks = ExtractMessageBlocks(html);
            foreach (string block in blocks)
            {
                QmsMessage message = ParseMessageBlock(block, contactNick, threadTitle, contactAvatarUrl, "");
                if (message != null)
                    result.Add(message);
            }

            return result;
        }

        private static List<QmsHtmlMarker> FindQmsMarkers(string html)
        {
            List<QmsHtmlMarker> markers = new List<QmsHtmlMarker>();
            if (String.IsNullOrWhiteSpace(html))
                return markers;

            string pattern =
                "(?<date><div\\b[^>]*class\\s*=\\s*[\"'][^\"']*date[^\"']*[\"'][^>]*>[\\s\\S]*?</div>)" +
                "|(?<msg><(?:div|li|a)\\b(?=[^>]*\\bdata-(?:message|msg)-id\\s*=)[^>]*>)";

            foreach (Match match in Regex.Matches(html, pattern, Rx))
            {
                if (match.Groups["date"].Success)
                {
                    if (!IsDateSeparatorBlock(match.Value))
                        continue;

                    QmsHtmlMarker marker = new QmsHtmlMarker();
                    marker.Index = match.Index;
                    marker.IsDate = true;
                    marker.Html = match.Value;
                    markers.Add(marker);
                    continue;
                }

                string openTag = match.Groups["msg"].Value;
                if (!IsQmsMessageOpenTag(openTag))
                    continue;

                QmsHtmlMarker messageMarker = new QmsHtmlMarker();
                messageMarker.Index = match.Index;
                messageMarker.IsDate = false;
                messageMarker.Html = openTag;
                markers.Add(messageMarker);
            }

            return markers;
        }

        private static bool IsQmsMessageOpenTag(string openTag)
        {
            if (String.IsNullOrWhiteSpace(openTag))
                return false;

            string classes = GetAttribute(openTag, "class");
            if (HasClassToken(classes, "list-group-item") ||
                HasClassToken(classes, "msgbox") ||
                HasClassToken(classes, "qms-message") ||
                HasClassToken(classes, "message-row") ||
                HasClassToken(classes, "msg-row") ||
                HasClassToken(classes, "msg-item"))
                return true;

            return false;
        }

        private static int GetQmsBlockEnd(string html, List<QmsHtmlMarker> markers, int markerIndex)
        {
            int end = html.Length;

            if (markers != null && markerIndex + 1 < markers.Count && markers[markerIndex + 1] != null)
                end = markers[markerIndex + 1].Index;

            int formIndex = IndexOfIgnoreCase(html, "<form", markers[markerIndex].Index);
            if (formIndex >= 0 && formIndex < end)
                end = formIndex;

            int textAreaIndex = IndexOfIgnoreCase(html, "<textarea", markers[markerIndex].Index);
            if (textAreaIndex >= 0 && textAreaIndex < end)
                end = textAreaIndex;

            int bottomIndex = IndexOfIgnoreCase(html, "id=\"thread-inside-bottom\"", markers[markerIndex].Index);
            if (bottomIndex >= 0 && bottomIndex < end)
                end = bottomIndex;

            return end;
        }

        private static int IndexOfIgnoreCase(string text, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(value) || startIndex < 0 || startIndex >= text.Length)
                return -1;

            return text.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDateSeparatorBlock(string block)
        {
            if (String.IsNullOrWhiteSpace(block))
                return false;

            string open = FirstMatch(block.TrimStart(), "^(<[^>]+>)");
            string classes = GetAttribute(open, "class");
            return HasExactClassToken(classes, "date");
        }

        private static string ExtractDateSeparatorText(string block)
        {
            string day = CleanText(FirstNonEmpty(
                FirstMatch(block, "<span[^>]*>([\\s\\S]*?)</span>"),
                block));

            return day;
        }

        private static List<string> ExtractMessageBlocks(string html)
        {
            List<string> blocks = new List<string>();
            if (String.IsNullOrWhiteSpace(html))
                return blocks;

            string[] patterns = new string[]
            {
                "<(?:div|li|a)[^>]+(?=class=\"[^\"]*(?:list-group-item|msgbox|qms-message|message-row|msg-row|msg-item)[^\"]*\")(?=[^>]*(?:data-message-id|data-msg-id))[\\s\\S]*?(?=<(?:div|li|a)[^>]+(?=class=\"[^\"]*(?:date|list-group-item|msgbox|qms-message|message-row|msg-row|msg-item)[^\"]*\")|<form|<textarea|$)",
                "<(?:div|li)[^>]+class=\"[^\"]*(?:qms-message|message-row|msg-row|msg-item|msg\\b)[^\"]*\"[^>]*>[\\s\\S]*?(?=<(?:div|li)[^>]+class=\"[^\"]*(?:qms-message|message-row|msg-row|msg-item|msg\\b)[^\"]*\"|<form|<textarea|$)",
                "<(?:div|li)[^>]+(?:data-msg-id|data-message-id)[^>]*>[\\s\\S]*?(?=<(?:div|li)[^>]+(?:data-msg-id|data-message-id)|<form|<textarea|$)"
            };

            foreach (string pattern in patterns)
            {
                foreach (Match match in Regex.Matches(html, pattern, Rx))
                {
                    string block = match.Value;
                    if (!IsPotentialMessageBlock(block))
                        continue;

                    if (!ContainsString(blocks, block))
                        blocks.Add(block);
                }

                if (blocks.Count > 0)
                    return blocks;
            }

            return blocks;
        }

        private static bool ContainsString(List<string> items, string value)
        {
            if (items == null)
                return false;

            foreach (string item in items)
            {
                if (item == value)
                    return true;
            }

            return false;
        }

        private static bool IsPotentialMessageBlock(string block)
        {
            if (String.IsNullOrWhiteSpace(block))
                return false;

            string lower = block.ToLowerInvariant();
            if (lower.IndexOf("act=qms", StringComparison.Ordinal) >= 0 &&
                lower.IndexOf("data-message-id", StringComparison.Ordinal) < 0 &&
                lower.IndexOf("data-msg-id", StringComparison.Ordinal) < 0)
                return false;

            bool hasTextContainer = Regex.IsMatch(block, "class=\"[^\"]*(?:msg-content|msg-text|message-body|message-text|postcolor|content|body|text)[^\"]*\"", Rx);
            bool hasId = Regex.IsMatch(block, "(?:data-msg-id|data-message-id)\\s*=", Rx);
            return hasTextContainer || hasId;
        }

        private static QmsMessage ParseMessageBlock(string block, string contactNick, string threadTitle, string contactAvatarUrl, string currentDay)
        {
            if (String.IsNullOrWhiteSpace(block))
                return null;

            string content = ExtractMessageContent(block);
            if (String.IsNullOrWhiteSpace(content))
                return null;

            bool isOwn = DetectOwnMessage(block, contactNick);
            string timeText = ExtractMessageTime(block);
            string author = GetMessageAuthor(isOwn, contactNick, block);

            QmsMessage message = new QmsMessage();
            message.Id = ExtractMessageId(block);
            message.Author = author;
            message.Text = CleanMessageText(content);
            message.TimeText = timeText;
            message.DateText = BuildDateText(currentDay, timeText);
            message.IsOwn = isOwn;
            message.IsRead = !HasUnreadMarker(block);

            List<QmsMessageImage> images = ExtractImages(content, contactAvatarUrl, "");
            foreach (QmsMessageImage image in images)
                message.Images.Add(image);

            if (String.IsNullOrWhiteSpace(message.Text) && message.Images.Count == 0)
                return null;

            message.ApplyViewState();
            return message;
        }

        private static string ExtractMessageId(string block)
        {
            string open = FirstMatch(block.TrimStart(), "^(<[^>]+>)");
            return FirstNonEmpty(
                GetAttribute(open, "data-message-id"),
                GetAttribute(open, "data-msg-id"),
                FirstMatch(block, "data-message-id\\s*=\\s*\"([^\"]+)\""),
                FirstMatch(block, "data-message-id\\s*=\\s*'([^']+)'"),
                FirstMatch(block, "data-msg-id\\s*=\\s*\"([^\"]+)\""),
                FirstMatch(block, "data-msg-id\\s*=\\s*'([^']+)'"));
        }

        private static bool HasUnreadMarker(string block)
        {
            if (String.IsNullOrWhiteSpace(block))
                return false;

            string lower = block.ToLowerInvariant();
            return lower.IndexOf("data-unread-status=\"1\"", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("data-unread-status='1'", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf(" unread", StringComparison.Ordinal) >= 0;
        }

        private static string BuildDateText(string day, string time)
        {
            if (!String.IsNullOrWhiteSpace(day) && !String.IsNullOrWhiteSpace(time))
                return day + " " + time;
            if (!String.IsNullOrWhiteSpace(day))
                return day;
            return time;
        }

        private static string ExtractMessageTime(string block)
        {
            if (String.IsNullOrWhiteSpace(block))
                return "";

            string raw = CleanText(FirstNonEmpty(
                FirstMatch(block, "</b>\\s*([^<]{1,80})"),
                FirstMatch(block, "<(?:span|div)[^>]+class=\"[^\"]*(?:msg-date|message-date|time)[^\"]*\"[^>]*>([\\s\\S]*?)</(?:span|div)>"),
                FirstMatch(block, "<time[^>]*>([\\s\\S]*?)</time>")));

            string time = ExtractTimeValue(raw);
            if (!String.IsNullOrWhiteSpace(time))
                return time;

            time = ExtractTimeValue(CleanText(block));
            return time;
        }

        private static string ExtractTimeValue(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            Match match = Regex.Match(text, "\\b([0-2]?\\d:[0-5]\\d(?::[0-5]\\d)?)\\b", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string GetMessageAuthor(bool isOwn, string contactNick, string block)
        {
            if (isOwn)
            {
                string current = GetCurrentUserLoginSafe();
                if (!String.IsNullOrWhiteSpace(current))
                    return current;

                return "Вы";
            }

            if (!String.IsNullOrWhiteSpace(contactNick))
                return contactNick;

            string extracted = ExtractAuthorNick(block);
            return String.IsNullOrWhiteSpace(extracted) ? "Собеседник" : extracted;
        }

        private static string GetCurrentUserLoginSafe()
        {
            try
            {
                return ForumAuthService.CurrentUserLogin;
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractMessageContent(string block)
        {
            string content = FirstNonEmpty(
                ExtractDivContentByClass(block, "msg-content"),
                ExtractDivContentByClass(block, "msg-text"),
                ExtractDivContentByClass(block, "message-text"),
                ExtractDivContentByClass(block, "message-body"),
                ExtractDivContentByClass(block, "postcolor"),
                FirstMatch(block, "<td[^>]+class=\"[^\"]*(?:msg-text|message-text|message-body|postcolor|content|body|text)[^\"]*\"[^>]*>([\\s\\S]*?)</td>"));

            if (String.IsNullOrWhiteSpace(content))
            {
                string cleanedBlock = RemoveNonMessageParts(block);
                if (HasMessageContent(cleanedBlock))
                    content = cleanedBlock;
            }

            return RemoveNonMessageParts(content);
        }

        private static string ExtractDivContentByClass(string html, string classToken)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(classToken))
                return "";

            foreach (Match match in Regex.Matches(html, "<div[^>]+class=\"([^\"]*)\"[^>]*>", Rx))
            {
                string classes = match.Groups[1].Value;
                if (!HasClassToken(classes, classToken))
                    continue;

                int contentStart = match.Index + match.Length;
                int contentEnd = FindClosingDiv(html, contentStart);
                if (contentEnd > contentStart)
                    return html.Substring(contentStart, contentEnd - contentStart);
            }

            foreach (Match match in Regex.Matches(html, "<div[^>]+class='([^']*)'[^>]*>", Rx))
            {
                string classes = match.Groups[1].Value;
                if (!HasClassToken(classes, classToken))
                    continue;

                int contentStart = match.Index + match.Length;
                int contentEnd = FindClosingDiv(html, contentStart);
                if (contentEnd > contentStart)
                    return html.Substring(contentStart, contentEnd - contentStart);
            }

            return "";
        }

        private static int FindClosingDiv(string html, int contentStart)
        {
            int depth = 1;
            foreach (Match match in Regex.Matches(html.Substring(contentStart), "</?div\\b[^>]*>", Rx))
            {
                bool close = match.Value.StartsWith("</", StringComparison.Ordinal);
                if (close)
                    depth--;
                else
                    depth++;

                if (depth == 0)
                    return contentStart + match.Index;
            }

            return -1;
        }

        private static bool HasMessageContent(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            if (CleanMessageText(html).Length > 0)
                return true;

            return ExtractImages(html).Count > 0;
        }

        private static string RemoveNonMessageParts(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string result = html;
            result = Regex.Replace(result, "<(?:script|style)[\\s\\S]*?</(?:script|style)>", " ", Rx);
            result = Regex.Replace(result, "<(?:div|span|a)[^>]+class=\"[^\"]*(?:avatar|ava|userpic|nick|name|author|date|time|status|read-status|controls|actions)[^\"]*\"[^>]*>[\\s\\S]*?</(?:div|span|a)>", " ", Rx);
            result = Regex.Replace(result, "<(?:div|span|a)[^>]+class='[^']*(?:avatar|ava|userpic|nick|name|author|date|time|status|read-status|controls|actions)[^']*'[^>]*>[\\s\\S]*?</(?:div|span|a)>", " ", Rx);
            result = Regex.Replace(result, "<img[^>]+(?:class|alt|title)=\"[^\"]*(?:avatar|аватар|userpic)[^\"]*\"[^>]*>", " ", Rx);
            result = Regex.Replace(result, "<img[^>]+(?:class|alt|title)='[^']*(?:avatar|аватар|userpic)[^']*'[^>]*>", " ", Rx);
            result = Regex.Replace(result, "<a[^>]+href=\"[^\"]*act=members[^\"]*\"[^>]*>[\\s\\S]*?</a>", " ", Rx);
            result = Regex.Replace(result, "<a[^>]+href='[^']*act=members[^']*'[^>]*>[\\s\\S]*?</a>", " ", Rx);
            return result;
        }

        private static bool DetectOwnMessage(string block, string contactNick)
        {
            if (String.IsNullOrWhiteSpace(block))
                return false;

            string open = FirstMatch(block.TrimStart(), "^(<[^>]+>)");
            string classes = GetAttribute(open, "class");

            if (HasClassToken(classes, "not-our-message"))
                return false;

            if (HasClassToken(classes, "our-message"))
                return true;

            if (HasClassToken(classes, "my-message") ||
                HasClassToken(classes, "own-message") ||
                HasClassToken(classes, "from-me") ||
                HasClassToken(classes, "outgoing") ||
                HasClassToken(classes, "msg-my") ||
                HasClassToken(classes, "my-msg") ||
                HasClassToken(classes, "message-my") ||
                HasClassToken(classes, "msg-right"))
                return true;

            if (HasClassToken(classes, "list-group-item") &&
                Regex.IsMatch(open, "data-(?:message|msg)-id\\s*=", Rx))
                return false;

            if (HasClassToken(classes, "incoming") ||
                HasClassToken(classes, "from-them") ||
                HasClassToken(classes, "other") ||
                HasClassToken(classes, "msg-left"))
                return false;

            string authorNick = NormalizeComparable(ExtractAuthorNick(block));
            string contact = NormalizeComparable(contactNick);
            if (contact.Length > 0 && authorNick == contact)
                return false;

            string current = NormalizeComparable(GetCurrentUserLoginSafe());
            if (current.Length > 0 && authorNick == current)
                return true;

            return false;
        }

        private static string ExtractAuthorNick(string block)
        {
            string avatarTag = FirstMatch(block, "(<img[^>]+class=\"[^\"]*(?:avatar|userpic|photo)[^\"]*\"[^>]*>)");
            if (String.IsNullOrWhiteSpace(avatarTag))
                avatarTag = FirstMatch(block, "(<img[^>]+(?:title|alt)=\"[^\"]+\"[^>]*>)");

            string authorHtml = FirstNonEmpty(
                FirstMatch(block, "<(?:a|span|div)[^>]+class=\"[^\"]*(?:nick|name|author|user)[^\"]*\"[^>]*>([\\s\\S]*?)</(?:a|span|div)>"),
                GetAttribute(avatarTag, "title"),
                GetAttribute(avatarTag, "alt"),
                FirstMatch(block, "<b[^>]*>([\\s\\S]*?)</b>"));

            return CleanText(authorHtml);
        }

        private static bool HasExactClassToken(string classes, string token)
        {
            if (String.IsNullOrWhiteSpace(classes) || String.IsNullOrWhiteSpace(token))
                return false;

            string[] parts = classes.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (String.Equals(part, token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool HasClassToken(string classes, string token)
        {
            if (String.IsNullOrWhiteSpace(classes) || String.IsNullOrWhiteSpace(token))
                return false;

            string text = " " + classes.Replace('_', ' ').Replace('-', ' ') + " ";
            string normalized = " " + token.Replace('_', ' ').Replace('-', ' ') + " ";
            return text.IndexOf(normalized, StringComparison.Ordinal) >= 0;
        }

        private static string NormalizeComparable(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string result = CleanText(value).ToLowerInvariant();
            result = Regex.Replace(result, "\\s+", " ");
            return result.Trim();
        }

        private static bool SameUrl(string left, string right)
        {
            string a = NormalizeUrl(left).TrimEnd('/').ToLowerInvariant();
            string b = NormalizeUrl(right).TrimEnd('/').ToLowerInvariant();
            return a.Length > 0 && b.Length > 0 && a == b;
        }

        private static string CleanMessageText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string text = html;
            text = Regex.Replace(text, "<img[^>]*>", " ", Rx);
            text = Regex.Replace(text, "<script[\\s\\S]*?</script>", " ", Rx);
            text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", Rx);
            text = Regex.Replace(text, "<blockquote[\\s\\S]*?</blockquote>", " ", Rx);
            text = Regex.Replace(text, "<br\\s*/?>", "\n", Rx);
            text = Regex.Replace(text, "</p>|</div>|</li>|</tr>", "\n", Rx);
            text = Regex.Replace(text, "<(?:span|div)[^>]+class=\"[^\"]*(?:date|time|avatar|controls|actions)[^\"]*\"[^>]*>[\\s\\S]*?</(?:span|div)>", " ", Rx);
            return CleanText(text);
        }

        private static List<QmsMessageImage> ExtractImages(string html)
        {
            return ExtractImages(html, "", "");
        }

        private static List<QmsMessageImage> ExtractImages(string html, string skipAvatarUrl, string skipMessageAvatarUrl)
        {
            List<QmsMessageImage> result = new List<QmsMessageImage>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            string content = RemoveNonMessageParts(html);
            foreach (Match match in Regex.Matches(content, "<img[^>]*>", Rx))
            {
                string tag = match.Value;
                string url = FirstNonEmpty(
                    GetAttribute(tag, "data-fullsrc"),
                    GetAttribute(tag, "data-src"),
                    GetAttribute(tag, "data-lazy-src"),
                    GetAttribute(tag, "data-original"),
                    GetAttribute(tag, "data-url"),
                    GetAttribute(tag, "src"));
                url = NormalizeUrl(Decode(url));

                AddMessageImageIfAllowed(result, tag, url, skipAvatarUrl, skipMessageAvatarUrl);
            }

            foreach (Match match in Regex.Matches(content, "<a[^>]+href=\"([^\"]+)\"[^>]*>[\\s\\S]*?</a>", Rx))
            {
                string url = NormalizeUrl(Decode(match.Groups[1].Value));
                if (!LooksLikeImageUrl(url))
                    continue;

                AddMessageImageIfAllowed(result, match.Value, url, skipAvatarUrl, skipMessageAvatarUrl);
            }

            return result;
        }

        private static void AddMessageImageIfAllowed(List<QmsMessageImage> result, string tag, string url, string skipAvatarUrl, string skipMessageAvatarUrl)
        {
            if (String.IsNullOrWhiteSpace(url) ||
                !LooksLikeImageUrl(url) ||
                SameUrl(url, skipAvatarUrl) ||
                SameUrl(url, skipMessageAvatarUrl) ||
                IsIgnoredMessageImage(tag, url) ||
                ContainsImage(result, url))
                return;

            QmsMessageImage image = new QmsMessageImage();
            image.Url = url;
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 360;
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                image.Image = bitmap;
            }
            catch
            {
                image.Image = null;
            }

            result.Add(image);
        }

        private static bool ContainsImage(List<QmsMessageImage> result, string url)
        {
            foreach (QmsMessageImage image in result)
            {
                if (image != null && SameUrl(image.Url, url))
                    return true;
            }

            return false;
        }

        private static bool LooksLikeImageUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return false;

            string lower = url.ToLowerInvariant();
            int sharp = lower.IndexOf('#');
            if (sharp >= 0)
                lower = lower.Substring(0, sharp);

            int question = lower.IndexOf('?');
            if (question >= 0)
                lower = lower.Substring(0, question);

            return lower.EndsWith(".jpg", StringComparison.Ordinal) ||
                lower.EndsWith(".jpeg", StringComparison.Ordinal) ||
                lower.EndsWith(".png", StringComparison.Ordinal) ||
                lower.EndsWith(".gif", StringComparison.Ordinal) ||
                lower.EndsWith(".bmp", StringComparison.Ordinal) ||
                lower.EndsWith(".webp", StringComparison.Ordinal) ||
                lower.IndexOf("/forum/dl/post/", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("/uploads/", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("attach", StringComparison.Ordinal) >= 0;
        }

        private static bool IsIgnoredMessageImage(string tag, string url)
        {
            string lowerUrl = (url == null ? "" : url).ToLowerInvariant();
            string lowerTag = (tag == null ? "" : tag).ToLowerInvariant();

            if (lowerTag.IndexOf("avatar", StringComparison.Ordinal) >= 0 ||
                lowerTag.IndexOf("аватар", StringComparison.Ordinal) >= 0 ||
                lowerTag.IndexOf("userpic", StringComparison.Ordinal) >= 0 ||
                lowerTag.IndexOf("emoticon", StringComparison.Ordinal) >= 0 ||
                lowerTag.IndexOf("smil", StringComparison.Ordinal) >= 0)
                return true;

            int width = ParseAttributeInt(tag, "width");
            int height = ParseAttributeInt(tag, "height");
            if (width > 0 && height > 0 && width <= 32 && height <= 32)
                return true;

            return lowerUrl.IndexOf("style_emoticons", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("style_images", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("/icons/", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("icon_", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("emoticon", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("smil", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("avatar", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("userpic", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("spacer", StringComparison.Ordinal) >= 0 ||
                lowerUrl.IndexOf("captcha", StringComparison.Ordinal) >= 0;
        }


        private static int ParseAttributeInt(string html, string attributeName)
        {
            string value = GetAttribute(html, attributeName);
            return ParseInt(value);
        }

        private static void EnsureAuthorized(string html)
        {
            if (LooksLikeLoginPage(html))
                throw new UnauthorizedAccessException("Нужно войти в аккаунт 4PDA.");
        }

        private static bool LooksLikeLoginPage(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            string lower = html.ToLowerInvariant();
            return (lower.IndexOf("act=login", StringComparison.Ordinal) >= 0 ||
                    lower.IndexOf("login-form", StringComparison.Ordinal) >= 0 ||
                    lower.IndexOf("name=\"username\"", StringComparison.Ordinal) >= 0) &&
                   lower.IndexOf("act=qms", StringComparison.Ordinal) < 0;
        }

        private static bool LooksLikeQmsError(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            string cleaned = CleanText(html).ToLowerInvariant();
            if (cleaned.Length == 0)
                return false;

            return cleaned.IndexOf("ошибка", StringComparison.Ordinal) >= 0 ||
                cleaned.IndexOf("error", StringComparison.Ordinal) >= 0 ||
                cleaned.IndexOf("не удалось", StringComparison.Ordinal) >= 0 ||
                cleaned.IndexOf("заполните", StringComparison.Ordinal) >= 0;
        }

        private static string FirstMatch(string html, string pattern)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(pattern))
                return "";

            Match match = Regex.Match(html, pattern, Rx);
            return match.Success ? match.Groups[1].Value : "";
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

        private static string GetAttribute(string html, string attributeName)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(attributeName))
                return "";

            Match match = Regex.Match(html, attributeName + "\\s*=\\s*\"([^\"]*)\"", Rx);
            if (!match.Success)
                match = Regex.Match(html, attributeName + "\\s*=\\s*'([^']*)'", Rx);

            return match.Success ? Decode(match.Groups[1].Value) : "";
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string text = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", Rx);
            text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", Rx);
            text = Regex.Replace(text, "<[^>]+>", " ", Rx);
            text = Decode(text);
            text = Regex.Replace(text, "[ \\t\\x0B\\f\\r]+", " ");
            text = Regex.Replace(text, " *\\n *", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string Decode(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            try
            {
                return HtmlUtilities.ConvertToText(value).Trim();
            }
            catch
            {
                return value.Trim();
            }
        }

        private static int ParseInt(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return 0;

            Match match = Regex.Match(value, "\\d+");
            if (!match.Success)
                return 0;

            int result;
            return Int32.TryParse(match.Value, out result) ? result : 0;
        }

        private static string NormalizeUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            string result = Decode(url).Trim();
            if (result.StartsWith("//", StringComparison.Ordinal))
                result = "https:" + result;
            else if (result.StartsWith("/", StringComparison.Ordinal))
                result = "https://4pda.to" + result;
            else if (result.StartsWith("http://4pda.ru", StringComparison.OrdinalIgnoreCase))
                result = "https://4pda.to" + result.Substring("http://4pda.ru".Length);
            else if (result.StartsWith("https://4pda.ru", StringComparison.OrdinalIgnoreCase))
                result = "https://4pda.to" + result.Substring("https://4pda.ru".Length);
            else if (!result.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !result.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                result = "https://4pda.to/forum/" + result.TrimStart('/');

            return result;
        }
    }

    public sealed class QmsContact
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string AvatarUrl { get; set; }
        public int UnreadCount { get; set; }
    }

    public sealed class QmsThread
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string LastMessageText { get; set; }
        public int MessagesCount { get; set; }
        public int UnreadCount { get; set; }
    }

    public sealed class QmsMessage
    {
        public QmsMessage()
        {
            Images = new ObservableCollection<QmsMessageImage>();
            Id = "";
            Author = "";
            Text = "";
            DateText = "";
            TimeText = "";
            DaySeparatorText = "";
            DateSeparatorVisibility = Visibility.Collapsed;
            TimeVisibility = Visibility.Collapsed;
            BubbleAlignment = HorizontalAlignment.Left;
            BubbleBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            AuthorBrush = new SolidColorBrush(Color.FromArgb(255, 255, 153, 0));
        }

        public string Id { get; set; }
        public string Author { get; set; }
        public string Text { get; set; }
        public string DateText { get; set; }
        public string TimeText { get; set; }
        public string DaySeparatorText { get; set; }
        public bool IsOwn { get; set; }
        public bool IsRead { get; set; }
        public ObservableCollection<QmsMessageImage> Images { get; private set; }
        public HorizontalAlignment BubbleAlignment { get; set; }
        public Brush BubbleBrush { get; set; }
        public Brush AuthorBrush { get; set; }
        public Visibility DateSeparatorVisibility { get; set; }
        public Visibility TimeVisibility { get; set; }

        public void ApplyViewState()
        {
            BubbleAlignment = HorizontalAlignment.Left;
            BubbleBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            AuthorBrush = new SolidColorBrush(IsOwn
                ? Color.FromArgb(255, 184, 83, 255)
                : Color.FromArgb(255, 255, 153, 0));
            TimeVisibility = String.IsNullOrWhiteSpace(TimeText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public sealed class QmsMessageImage
    {
        public string Url { get; set; }
        public BitmapImage Image { get; set; }
    }

    public sealed class QmsThreadNavigationArgs
    {
        public string ContactId { get; set; }
        public string ContactNick { get; set; }
        public string ThreadId { get; set; }
        public string ThreadTitle { get; set; }
        public string AvatarUrl { get; set; }
    }
}
