using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed partial class NewsPage : Page
    {
        private const string Host = "4pda.to";
        private const int ArticleImageDecodeWidth = 720;
        private const int MaxArticleImages = 12;
        private const int MaxCommentsToParse = 80;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, NewsDetails> NewsDetailsCache = new Dictionary<string, NewsDetails>(StringComparer.OrdinalIgnoreCase);

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly SolidColorBrush _primaryTextBrush = new SolidColorBrush(Color.FromArgb(255, 242, 242, 242));
        private readonly SolidColorBrush _secondaryTextBrush = new SolidColorBrush(Color.FromArgb(255, 158, 158, 158));
        private readonly SolidColorBrush _mutedTextBrush = new SolidColorBrush(Color.FromArgb(255, 130, 130, 130));
        private readonly SolidColorBrush _separatorBrush = new SolidColorBrush(Color.FromArgb(255, 38, 38, 38));
        private readonly SolidColorBrush _quoteBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 18, 18, 18));
        private readonly SolidColorBrush _commentBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));

        public NewsPage()
        {
            this.InitializeComponent();

            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko");
            }
            catch
            {
                // На некоторых сборках SDK User-Agent может не установиться.
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            string url = e.Parameter as string;
            if (String.IsNullOrWhiteSpace(url))
            {
                StatusTextBlock.Text = "Не передан адрес новости.";
                return;
            }

            await LoadNewsAsync(NormalizeUrl(url));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }


        private async Task LoadNewsAsync(string url)
        {
            ProgressRing.IsActive = true;
            StatusTextBlock.Text = "";
            ClearPage();

            string cacheKey = NormalizeUrl(url);
            NewsDetails cached = GetCachedNewsDetails(cacheKey);
            if (cached != null)
            {
                RenderNews(cached);
                ProgressRing.IsActive = false;
                return;
            }

            try
            {
                string html = await DownloadStringAsync(url);
                NewsDetails details = await Task.Run<NewsDetails>(delegate
                {
                    return ParseNewsDetails(url, html);
                });
                PutCachedNewsDetails(cacheKey, details);
                RenderNews(details);
                StatusTextBlock.Text = "";
            }
            catch (Exception ex)
            {
                ClearPage();
                StatusTextBlock.Text = "Новость не загружена: " + ex.Message;
            }

            ProgressRing.IsActive = false;
        }

        private void ClearPage()
        {
            TitleTextBlock.Text = "";
            MetaTextBlock.Text = "";
            SourceTextBlock.Text = "";
            CommentsHeaderTextBlock.Visibility = Visibility.Collapsed;
            ArticleContentPanel.Children.Clear();
            CommentsPanel.Children.Clear();
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            return await _httpClient.GetStringAsync(new Uri(url, UriKind.Absolute));
        }

        private static NewsDetails GetCachedNewsDetails(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
                return null;

            lock (CacheLock)
            {
                NewsDetails details;
                return NewsDetailsCache.TryGetValue(key, out details) ? details : null;
            }
        }

        private static void PutCachedNewsDetails(string key, NewsDetails details)
        {
            if (String.IsNullOrWhiteSpace(key) || details == null)
                return;

            lock (CacheLock)
            {
                NewsDetailsCache[key] = details;
                if (NewsDetailsCache.Count > 25)
                {
                    string firstKey = NewsDetailsCache.Keys.FirstOrDefault();
                    if (!String.IsNullOrWhiteSpace(firstKey))
                        NewsDetailsCache.Remove(firstKey);
                }
            }
        }

        private static NewsDetails ParseNewsDetails(string url, string html)
        {
            html = html ?? "";

            NewsDetails details = new NewsDetails();
            details.Url = NormalizeUrl(url);
            details.Id = ExtractArticleId(details.Url, html);

            ArticleSlices slices = ExtractArticleSlices(html);
            ArticleMetaInfo articleMeta = ExtractArticleMetaInfo(slices.ArticleHtml, slices.ContentHtml);
            SourceInfo source = ExtractSourceInfo(slices.ArticleHtml, html, articleMeta);

            details.Title = JoinFirstNonEmpty(
                ExtractTitleFromArticle(slices.ArticleHtml),
                ExtractMetaContent(html, "property", "og:title"),
                ExtractMetaContent(html, "name", "title"),
                ExtractTitleTag(html));

            details.Title = RemoveSiteSuffix(CleanText(details.Title));

            details.Date = FormatDate(JoinFirstNonEmpty(
                ExtractTimeFromArticle(slices.ArticleHtml),
                ExtractMetaContent(html, "property", "article:published_time"),
                ExtractMetaContent(html, "itemprop", "datePublished"),
                ExtractMetaContent(html, "name", "pubdate")));

            details.Author = CleanMetaValue(JoinFirstNonEmpty(
                ExtractAuthorFromArticle(slices.ArticleHtml),
                articleMeta.Author,
                ExtractMetaContent(html, "name", "author"),
                ExtractMetaContent(html, "property", "article:author"),
                "News"));

            details.MainImageUrl = NormalizeUrl(JoinFirstNonEmpty(
                ExtractMetaContent(html, "property", "og:image"),
                ExtractMetaContent(html, "itemprop", "image"),
                ExtractFirstImageUrl(slices.ContentHtml)));

            details.SourceTitle = CleanMetaValue(String.IsNullOrWhiteSpace(source.Title) ? "4PDA" : source.Title);
            details.SourceUrl = String.IsNullOrWhiteSpace(source.Url) ? details.Url : source.Url;

            details.ArticleBlocks = ParseArticleBlocks(slices.ContentHtml, details.MainImageUrl);
            details.Comments = ParseComments(slices.CommentsHtml);

            return details;
        }

        private void RenderNews(NewsDetails details)
        {
            TitleTextBlock.Text = String.IsNullOrWhiteSpace(details.Title) ? "Без названия" : details.Title;

            MetaTextBlock.Text = JoinNonEmpty(" · ", details.Date, details.Author);
            MetaTextBlock.Visibility = String.IsNullOrWhiteSpace(MetaTextBlock.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            SourceTextBlock.Text = String.IsNullOrWhiteSpace(details.SourceTitle)
                ? ""
                : "Источник: " + details.SourceTitle;
            SourceTextBlock.Visibility = String.IsNullOrWhiteSpace(SourceTextBlock.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (details.ArticleBlocks.Count == 0)
            {
                ArticleContentPanel.Children.Add(CreateMutedParagraph("Текст новости не найден."));
            }
            else
            {
                foreach (ContentBlock block in details.ArticleBlocks)
                    AddContentBlock(block);
            }

            CommentsHeaderTextBlock.Visibility = Visibility.Visible;
            CommentsHeaderTextBlock.Text = details.Comments.Count > 0
                ? "Комментарии (" + details.Comments.Count + ")"
                : "Комментарии";

            if (details.Comments.Count == 0)
            {
                CommentsPanel.Children.Add(CreateMutedParagraph("Комментарии не найдены."));
            }
            else
            {
                foreach (CommentItem comment in details.Comments)
                {
                    UIElement commentView = CreateCommentView(comment);
                    if (commentView != null)
                        CommentsPanel.Children.Add(commentView);
                }
            }
        }

        private void AddContentBlock(ContentBlock block)
        {
            if (block == null)
                return;

            if (block.Kind == ContentBlockKind.Image)
            {
                Image image = CreateImage(block.ImageUrl);
                if (image != null)
                    ArticleContentPanel.Children.Add(image);
                return;
            }

            if (block.Kind == ContentBlockKind.Heading)
            {
                ArticleContentPanel.Children.Add(CreateTextBlock(block.Text, 23, _primaryTextBrush, new Thickness(0, 16, 0, 8)));
                return;
            }

            if (block.Kind == ContentBlockKind.Quote)
            {
                Border quote = new Border();
                quote.Background = _quoteBackgroundBrush;
                quote.BorderBrush = _separatorBrush;
                quote.BorderThickness = new Thickness(3, 0, 0, 0);
                quote.Margin = new Thickness(0, 8, 0, 12);
                quote.Padding = new Thickness(10, 8, 8, 8);
                quote.Child = CreateTextBlock(block.Text, 17, _secondaryTextBrush, new Thickness(0));
                ArticleContentPanel.Children.Add(quote);
                return;
            }

            ArticleContentPanel.Children.Add(CreateParagraph(block.Text));
        }

        private TextBlock CreateParagraph(string text)
        {
            return CreateTextBlock(text, 18, _primaryTextBrush, new Thickness(0, 0, 0, 14));
        }

        private TextBlock CreateMutedParagraph(string text)
        {
            return CreateTextBlock(text, 16, _mutedTextBrush, new Thickness(0, 0, 0, 14));
        }

        private TextBlock CreateTextBlock(string text, double fontSize, Brush foreground, Thickness margin)
        {
            TextBlock textBlock = new TextBlock();
            textBlock.Text = text ?? "";
            textBlock.FontSize = fontSize;
            textBlock.Foreground = foreground;
            textBlock.TextWrapping = TextWrapping.Wrap;
            textBlock.Margin = margin;
            return textBlock;
        }

        private Image CreateImage(string url)
        {
            url = NormalizeUrl(url);
            if (String.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                BitmapImage source = new BitmapImage();
                source.DecodePixelWidth = ArticleImageDecodeWidth;
                source.UriSource = new Uri(url, UriKind.Absolute);

                Image image = new Image();
                image.Source = source;
                image.Stretch = Stretch.Uniform;
                image.HorizontalAlignment = HorizontalAlignment.Stretch;
                image.VerticalAlignment = VerticalAlignment.Top;
                image.Width = Math.Max(1, Window.Current.Bounds.Width - 24);
                image.Margin = new Thickness(0, 8, 0, 14);
                return image;
            }
            catch
            {
                return null;
            }
        }

        private UIElement CreateCommentView(CommentItem comment)
        {
            if (comment == null || String.IsNullOrWhiteSpace(comment.Text))
                return null;

            Border border = new Border();
            border.Background = _commentBackgroundBrush;
            border.BorderBrush = _separatorBrush;
            border.BorderThickness = new Thickness(0, 1, 0, 0);
            border.Margin = new Thickness(0, 0, 0, 0);
            border.Padding = new Thickness(0, 10, 0, 10);
            border.HorizontalAlignment = HorizontalAlignment.Stretch;

            StackPanel panel = new StackPanel();
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;

            TextBlock header = CreateTextBlock(
                JoinNonEmpty(" · ", String.IsNullOrWhiteSpace(comment.Author) ? "Гость" : comment.Author, comment.Date),
                15,
                _secondaryTextBrush,
                new Thickness(0, 0, 0, 4));
            header.TextAlignment = TextAlignment.Left;
            header.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(header);

            TextBlock content = CreateTextBlock(comment.Text, 17, _primaryTextBrush, new Thickness(0));
            content.TextAlignment = TextAlignment.Left;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(content);

            border.Child = panel;
            return border;
        }

        private static ArticleSlices ExtractArticleSlices(string html)
        {
            ArticleSlices result = new ArticleSlices();

            result.ArticleHtml = JoinFirstNonEmpty(
                ExtractElementWithClass(html, "div", "article"),
                ExtractElementWithClass(html, "article", "article"),
                ExtractElementWithAttribute(html, "article", "class", "post"),
                ExtractElementWithAttribute(html, "div", "data-ztm", ""),
                ExtractBodyHtml(html));

            result.ContentHtml = JoinFirstNonEmpty(
                ExtractElementWithAttribute(result.ArticleHtml, "div", "itemprop", "articleBody"),
                ExtractElementWithClass(result.ArticleHtml, "div", "content-box"),
                ExtractBetweenArticleHeaderAndFooter(result.ArticleHtml),
                result.ArticleHtml);

            result.CommentsHtml = ExtractCommentsSourceByForPdaPattern(html);

            return result;
        }

        private static string ExtractBetweenArticleHeaderAndFooter(string articleHtml)
        {
            if (String.IsNullOrWhiteSpace(articleHtml))
                return "";

            string withoutHeader = RemoveElementsByClass(articleHtml, "div", "article-header");
            string withoutFooter = RemoveElementsByClass(withoutHeader, "div", "article-footer");
            string cleaned = CleanArticleChrome(withoutFooter);
            string text = CleanText(cleaned);

            if (text.Length < 20 && ExtractFirstImageUrl(cleaned).Length == 0)
                return "";

            return cleaned;
        }

        private static string ExtractCommentsSourceByForPdaPattern(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            // RadiationX/ForPDA берет commentsSource из блока
            // <div class="comment-box" id="comments"> ... <ul class="comment-list"> ... </ul>
            // и отдельно отрезает форму ответа. Здесь делаем то же самое,
            // но без WebView и без стороннего HTML-парсера.
            string byCommentBox = ExtractCommentListFromCommentsBox(html);
            if (!String.IsNullOrWhiteSpace(byCommentBox))
                return RemoveCommentForm(byCommentBox);

            string byAnyCommentList = ExtractElementWithClass(html, "ul", "comment-list");
            if (!String.IsNullOrWhiteSpace(byAnyCommentList))
                return RemoveCommentForm(byAnyCommentList);

            string byLooseRegex = ExtractCommentListLoose(html);
            if (!String.IsNullOrWhiteSpace(byLooseRegex))
                return RemoveCommentForm(byLooseRegex);

            return "";
        }

        private static string ExtractCommentListFromCommentsBox(string html)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection divs = Regex.Matches(html ?? "", @"<div\b(?<attrs>[^>]*)>", options);

            foreach (Match div in divs)
            {
                string attrs = div.Groups["attrs"].Value;
                string classes = GetAttribute(attrs, "class");
                string id = GetAttribute(attrs, "id");

                bool isCommentBox = ContainsClass(classes, "comment-box") || classes.IndexOf("comment-box", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isComments = String.Equals(id, "comments", StringComparison.OrdinalIgnoreCase);

                if (!isCommentBox && !isComments)
                    continue;

                int boxEnd = FindMatchingEndTag(html, div.Index, "div");
                string box = boxEnd > div.Index
                    ? html.Substring(div.Index, boxEnd - div.Index)
                    : html.Substring(div.Index);

                string list = ExtractElementWithClass(box, "ul", "comment-list");
                if (!String.IsNullOrWhiteSpace(list))
                    return list;

                list = ExtractCommentListLoose(box);
                if (!String.IsNullOrWhiteSpace(list))
                    return list;
            }

            return "";
        }

        private static string ExtractCommentListLoose(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match ulStart = Regex.Match(html, @"<ul\b(?=[^>]*\bclass\s*=\s*(['""])[^'""]*comment-list[^'""]*\1)[^>]*>", options);
            if (!ulStart.Success)
                return "";

            int end = FindMatchingEndTag(html, ulStart.Index, "ul");
            if (end > ulStart.Index)
                return html.Substring(ulStart.Index, end - ulStart.Index);

            int stop = FindFirstIndexAfter(html, ulStart.Index, new[]
            {
                "<form",
                "</section>",
                "<article",
                "<div class=\"materials-box",
                "<div class='materials-box"
            });

            if (stop > ulStart.Index)
                return html.Substring(ulStart.Index, stop - ulStart.Index);

            return html.Substring(ulStart.Index);
        }

        private static string RemoveCommentForm(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            Match form = Regex.Match(html, @"<form\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (form.Success)
                return html.Substring(0, form.Index);

            return html;
        }

        private static int FindFirstIndexAfter(string html, int startIndex, string[] needles)
        {
            if (String.IsNullOrEmpty(html) || needles == null)
                return -1;

            int result = -1;
            foreach (string needle in needles)
            {
                if (String.IsNullOrEmpty(needle))
                    continue;

                int index = html.IndexOf(needle, Math.Max(0, startIndex), StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (result < 0 || index < result))
                    result = index;
            }

            return result;
        }

        private static List<ContentBlock> ParseArticleBlocks(string contentHtml, string mainImageUrl)
        {
            var result = new List<ContentBlock>();
            var addedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string mainImage = NormalizeUrl(mainImageUrl);
            AddImageBlockIfUnique(result, addedImages, mainImage);

            string prepared = PrepareContentForNativeBlocks(contentHtml);

            foreach (string rawChunk in SplitNativeChunks(prepared))
            {
                if (String.IsNullOrWhiteSpace(rawChunk))
                    continue;

                MatchCollection imageMarkers = Regex.Matches(rawChunk, @"\[\[IMG:(?<url>[\s\S]*?)\]\]", RegexOptions.IgnoreCase);
                if (imageMarkers.Count > 0)
                {
                    string textOnly = Regex.Replace(rawChunk, @"\[\[IMG:[\s\S]*?\]\]", " ", RegexOptions.IgnoreCase);
                    string beforeText = CleanText(RemoveNativeMarkers(textOnly));
                    if (!ShouldSkipArticleText(beforeText))
                    {
                        ContentBlockKind textKind = DetectKind(rawChunk);
                        result.Add(new ContentBlock { Kind = textKind, Text = beforeText });
                    }

                    foreach (Match marker in imageMarkers)
                    {
                        string imageUrl = NormalizeUrl(marker.Groups["url"].Value);
                        if (String.IsNullOrWhiteSpace(imageUrl))
                            continue;

                        AddImageBlockIfUnique(result, addedImages, imageUrl);
                    }

                    continue;
                }

                string text = CleanText(RemoveNativeMarkers(rawChunk));
                if (ShouldSkipArticleText(text))
                    continue;

                if (rawChunk.IndexOf("[[LI]]", StringComparison.OrdinalIgnoreCase) >= 0)
                    text = "• " + text;

                result.Add(new ContentBlock { Kind = DetectKind(rawChunk), Text = text });
            }

            if (result.Count == 0 || (result.Count == 1 && result[0].Kind == ContentBlockKind.Image))
            {
                string fallback = CleanText(CleanArticleChrome(contentHtml));
                if (!ShouldSkipArticleText(fallback))
                    result.Add(new ContentBlock { Kind = ContentBlockKind.Text, Text = fallback });
            }

            return MergeSmallTextBlocks(result);
        }

        private static void AddImageBlockIfUnique(List<ContentBlock> result, HashSet<string> addedImages, string imageUrl)
        {
            imageUrl = NormalizeUrl(imageUrl);
            if (String.IsNullOrWhiteSpace(imageUrl))
                return;

            if (result.Count(block => block != null && block.Kind == ContentBlockKind.Image) >= MaxArticleImages)
                return;

            string imageKey = GetImageKey(imageUrl);
            if (String.IsNullOrWhiteSpace(imageKey))
                imageKey = imageUrl;

            if (addedImages.Contains(imageKey))
                return;

            result.Add(new ContentBlock { Kind = ContentBlockKind.Image, ImageUrl = imageUrl });
            addedImages.Add(imageKey);
        }

        private static string GetImageKey(string imageUrl)
        {
            imageUrl = NormalizeUrl(imageUrl);
            if (String.IsNullOrWhiteSpace(imageUrl))
                return "";

            try
            {
                Uri uri = new Uri(imageUrl, UriKind.Absolute);
                string path = Uri.UnescapeDataString(uri.AbsolutePath).ToLowerInvariant();

                path = Regex.Replace(path, @"/(?:resize|resize_cache|cache)/[^/]+/", "/", RegexOptions.IgnoreCase);
                path = Regex.Replace(path, @"/(?:thumb|thumbnail|thumbnails)/", "/", RegexOptions.IgnoreCase);

                int slash = path.LastIndexOf('/');
                string fileName = slash >= 0 ? path.Substring(slash + 1) : path;
                fileName = Regex.Replace(fileName, @"[-_](?:\d{2,5})x(?:\d{2,5})(?=\.)", "", RegexOptions.IgnoreCase);
                fileName = Regex.Replace(fileName, @"@[0-9]+x(?=\.)", "", RegexOptions.IgnoreCase);

                if (fileName.Length > 6)
                    return fileName;

                return Regex.Replace(path, @"\?.*$", "", RegexOptions.IgnoreCase);
            }
            catch
            {
                return Regex.Replace(imageUrl.ToLowerInvariant(), @"[?#].*$", "", RegexOptions.IgnoreCase);
            }
        }

        private static string PrepareContentForNativeBlocks(string html)
        {
            string body = html ?? "";
            body = CleanArticleChrome(body);
            body = ReplaceImagesWithMarkers(body);

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            body = Regex.Replace(body, @"<(h1|h2|h3|h4)\b[^>]*>", "\n[[H]]", options);
            body = Regex.Replace(body, @"</(h1|h2|h3|h4)>", "[[BR]]\n", options);
            body = Regex.Replace(body, @"<blockquote\b[^>]*>", "\n[[QUOTE]]", options);
            body = Regex.Replace(body, @"</blockquote>", "[[BR]]\n", options);
            body = Regex.Replace(body, @"<li\b[^>]*>", "\n[[LI]]", options);
            body = Regex.Replace(body, @"<\s*br\s*/?\s*>", "[[BR]]\n", options);
            body = Regex.Replace(body, @"</(p|div|section|article|figure|figcaption|li|ul|ol|table|tr)>\s*", "[[BR]]\n", options);
            body = Regex.Replace(body, @"<hr\b[^>]*>", "[[BR]]\n", options);

            return body;
        }

        private static string CleanArticleChrome(string html)
        {
            string body = html ?? "";

            body = RemoveTagsByName(body, "script");
            body = RemoveTagsByName(body, "style");
            body = RemoveTagsByName(body, "noscript");
            body = RemoveTagsByName(body, "iframe");
            body = RemoveTagsByName(body, "form");
            body = RemoveTagsByName(body, "svg");

            string[] divClasses = new[]
            {
                "article-header",
                "article-footer",
                "article-footer-tags",
                "comment-box",
                "comments-box",
                "materials-box",
                "share",
                "sharing",
                "social",
                "tags",
                "banner",
                "advertising",
                "ad",
                "v-count"
            };

            foreach (string className in divClasses)
            {
                body = RemoveElementsByClass(body, "div", className);
                body = RemoveElementsByClass(body, "ul", className);
                body = RemoveElementsByClass(body, "section", className);
            }

            body = RemoveElementsByClass(body, "ul", "comment-list");
            body = Regex.Replace(body, @"<meta\b[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Regex.Replace(body, @"<link\b[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            body = Regex.Replace(body, @"<!--([\s\S]*?)-->", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return body;
        }

        private static string ReplaceImagesWithMarkers(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            return Regex.Replace(
                html,
                @"<img\b(?<attrs>[^>]*)>",
                delegate(Match match)
                {
                    string attrs = match.Groups["attrs"].Value;
                    string src = NormalizeUrl(JoinFirstNonEmpty(
                        GetAttribute(attrs, "data-src"),
                        GetAttribute(attrs, "data-original"),
                        GetAttribute(attrs, "data-lazy-src"),
                        GetAttribute(attrs, "src")));

                    if (String.IsNullOrWhiteSpace(src))
                        return " ";

                    if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        return " ";

                    if (IsServiceImage(src, attrs))
                        return " ";

                    return "[[BR]]\n[[IMG:" + src + "]][[BR]]\n";
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool IsServiceImage(string src, string attrs)
        {
            string value = ((src ?? "") + " " + (attrs ?? "")).ToLowerInvariant();

            if (value.IndexOf("avatar", StringComparison.Ordinal) >= 0)
                return true;

            if (value.IndexOf("spacer", StringComparison.Ordinal) >= 0)
                return true;

            if (value.IndexOf("counter", StringComparison.Ordinal) >= 0)
                return true;

            if (value.IndexOf("/style_images/", StringComparison.Ordinal) >= 0)
                return true;

            return false;
        }

        private static List<string> SplitNativeChunks(string html)
        {
            var chunks = new List<string>();
            string[] parts = Regex.Split(html ?? "", @"\[\[BR\]\]", RegexOptions.IgnoreCase);

            foreach (string part in parts)
            {
                string value = part == null ? "" : part.Trim();
                if (value.Length == 0)
                    continue;

                chunks.Add(value);
            }

            return chunks;
        }

        private static string RemoveNativeMarkers(string text)
        {
            string value = text ?? "";
            value = value.Replace("[[H]]", " ");
            value = value.Replace("[[h]]", " ");
            value = value.Replace("[[QUOTE]]", " ");
            value = value.Replace("[[quote]]", " ");
            value = value.Replace("[[LI]]", " ");
            value = value.Replace("[[li]]", " ");
            return value;
        }

        private static ContentBlockKind DetectKind(string rawChunk)
        {
            if (rawChunk.IndexOf("[[H]]", StringComparison.OrdinalIgnoreCase) >= 0)
                return ContentBlockKind.Heading;

            if (rawChunk.IndexOf("[[QUOTE]]", StringComparison.OrdinalIgnoreCase) >= 0)
                return ContentBlockKind.Quote;

            return ContentBlockKind.Text;
        }

        private static List<ContentBlock> MergeSmallTextBlocks(List<ContentBlock> blocks)
        {
            var result = new List<ContentBlock>();

            foreach (ContentBlock block in blocks)
            {
                if (block.Kind != ContentBlockKind.Text || result.Count == 0)
                {
                    result.Add(block);
                    continue;
                }

                ContentBlock previous = result[result.Count - 1];
                bool canMerge = previous.Kind == ContentBlockKind.Text
                    && !String.IsNullOrWhiteSpace(previous.Text)
                    && !String.IsNullOrWhiteSpace(block.Text)
                    && previous.Text.Length < 90
                    && block.Text.Length < 90;

                if (canMerge)
                    previous.Text = previous.Text.TrimEnd() + " " + block.Text.TrimStart();
                else
                    result.Add(block);
            }

            return result;
        }

        private static bool ShouldSkipArticleText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return true;

            string value = text.Trim();
            string lower = value.ToLowerInvariant();

            if (value.Length < 2)
                return true;

            if (lower == "поделиться" || lower == "комментарии" || lower == "обсудить")
                return true;

            if (lower.StartsWith("источник:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (lower.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (lower.StartsWith("автор:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (lower.StartsWith("author:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (lower.IndexOf("реклама", StringComparison.OrdinalIgnoreCase) >= 0 && value.Length < 120)
                return true;

            if (Regex.IsMatch(value, @"^\d+\s*$"))
                return true;

            return false;
        }

        private static List<CommentItem> ParseComments(string commentsHtml)
        {
            var result = new List<CommentItem>();

            if (String.IsNullOrWhiteSpace(commentsHtml))
                return result;

            string source = RemoveCommentForm(commentsHtml);
            source = RemoveTagsByName(source, "script");
            source = RemoveTagsByName(source, "style");
            source = RemoveTagsByName(source, "noscript");

            // Основной путь как в ForPDA: идти по div id="comment-..." внутри ul.comment-list.
            // Этот способ не ломается, если у <li> нет корректного закрывающего тега.
            ParseCommentsByAnchors(source, result);

            // Запасной путь для старой верстки, где id может находиться не на div.
            if (result.Count == 0)
            {
                string ul = ExtractElementWithClass(source, "ul", "comment-list");
                if (String.IsNullOrWhiteSpace(ul))
                    ul = source;

                ParseCommentList(ul, 0, result);
            }

            return RemoveDuplicateComments(result);
        }

        private static void ParseCommentsByAnchors(string html, List<CommentItem> result)
        {
            if (String.IsNullOrWhiteSpace(html))
                return;

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection anchors = Regex.Matches(
                html,
                @"<div\b(?=[^>]*\bid\s*=\s*(['""]?)comment-(?<id>\d+)\1)(?<attrs>[^>]*)>",
                options);

            if (anchors.Count == 0)
                return;

            for (int i = 0; i < anchors.Count && result.Count < MaxCommentsToParse; i++)
            {
                Match anchor = anchors[i];
                int start = FindCommentItemStart(html, anchor.Index);
                int end = i + 1 < anchors.Count ? anchors[i + 1].Index : FindCommentItemEnd(html, anchor.Index);

                if (end <= start)
                    end = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;

                if (end <= start)
                    continue;

                string block = html.Substring(start, end - start);
                CommentItem comment = ParseSingleCommentBlock(block, 0);
                if (comment == null)
                    continue;

                comment.Id = anchor.Groups["id"].Value;
                result.Add(comment);
            }
        }

        private static int FindCommentItemStart(string html, int anchorIndex)
        {
            int li = LastIndexOfIgnoreCase(html, "<li", anchorIndex);
            int ul = LastIndexOfIgnoreCase(html, "<ul", anchorIndex);
            if (li >= 0 && li > ul)
                return li;

            return anchorIndex;
        }

        private static int FindCommentItemEnd(string html, int anchorIndex)
        {
            int divEnd = FindMatchingEndTag(html, anchorIndex, "div");
            if (divEnd > anchorIndex)
                return divEnd;

            int liEnd = IndexOfIgnoreCase(html, "</li>", anchorIndex);
            if (liEnd > anchorIndex)
                return liEnd + 5;

            return html.Length;
        }

        private static int LastIndexOfIgnoreCase(string html, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(html) || String.IsNullOrEmpty(value))
                return -1;

            int safeStart = Math.Min(Math.Max(0, startIndex), html.Length - 1);
            return html.LastIndexOf(value, safeStart, StringComparison.OrdinalIgnoreCase);
        }

        private static int IndexOfIgnoreCase(string html, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(html) || String.IsNullOrEmpty(value))
                return -1;

            return html.IndexOf(value, Math.Max(0, startIndex), StringComparison.OrdinalIgnoreCase);
        }

        private static List<CommentItem> RemoveDuplicateComments(List<CommentItem> comments)
        {
            var result = new List<CommentItem>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CommentItem comment in comments)
            {
                if (comment == null || String.IsNullOrWhiteSpace(comment.Text))
                    continue;

                string key = JoinNonEmpty("|", comment.Id, comment.Author, comment.Date, comment.Text);
                if (String.IsNullOrWhiteSpace(key))
                    continue;

                if (keys.Contains(key))
                    continue;

                keys.Add(key);
                result.Add(comment);
            }

            return result;
        }

        private static void ParseCommentList(string ulHtml, int level, List<CommentItem> result)
        {
            if (String.IsNullOrWhiteSpace(ulHtml) || level > 12)
                return;

            string inner = GetInnerHtml(ulHtml, "ul");
            if (String.IsNullOrWhiteSpace(inner))
                inner = ulHtml;

            List<string> liItems = ExtractTopLevelElements(inner, "li");
            if (liItems.Count == 0)
                liItems = ExtractLooseLiItems(inner);

            foreach (string li in liItems)
            {
                if (result.Count >= MaxCommentsToParse)
                    break;

                CommentItem comment = ParseSingleCommentBlock(li, level);
                if (comment != null)
                    result.Add(comment);

                string nested = ExtractElementWithClass(li, "ul", "comment-list");
                if (!String.IsNullOrWhiteSpace(nested))
                    ParseCommentList(nested, level + 1, result);
            }
        }

        private static List<string> ExtractLooseLiItems(string html)
        {
            var result = new List<string>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            MatchCollection starts = Regex.Matches(html, @"<li\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i].Index;
                int end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
                if (end > start)
                    result.Add(html.Substring(start, end - start));
            }

            return result;
        }

        private static CommentItem ParseSingleCommentBlock(string commentHtml, int level)
        {
            if (String.IsNullOrWhiteSpace(commentHtml))
                return null;

            string anchor = ExtractCommentAnchor(commentHtml);
            bool deleted = anchor.IndexOf("deleted", StringComparison.OrdinalIgnoreCase) >= 0
                || commentHtml.IndexOf("comment-deleted", StringComparison.OrdinalIgnoreCase) >= 0;

            string nickHtml = JoinFirstNonEmpty(
                ExtractElementWithClass(commentHtml, "a", "nickname"),
                ExtractElementWithClass(commentHtml, "span", "nickname"),
                ExtractElementWithClass(commentHtml, "b", "nickname"),
                ExtractByClassLoose(commentHtml, "nickname"));

            string dateHtml = JoinFirstNonEmpty(
                ExtractElementWithClass(commentHtml, "a", "date"),
                ExtractElementWithClass(commentHtml, "span", "date"),
                ExtractElementWithClass(commentHtml, "time", "date"),
                ExtractByClassLoose(commentHtml, "date"));

            string contentHtml = JoinFirstNonEmpty(
                ExtractElementWithClass(commentHtml, "p", "content"),
                ExtractElementWithClass(commentHtml, "div", "content"),
                ExtractByClassLoose(commentHtml, "content"));

            // Не тащим в текст вложенные ответы и элементы управления.
            contentHtml = RemoveElementsByClass(contentHtml, "ul", "comment-list");
            contentHtml = RemoveElementsByClass(contentHtml, "a", "reply");
            contentHtml = RemoveElementsByClass(contentHtml, "a", "quote");
            contentHtml = RemoveElementsByClass(contentHtml, "div", "actions");
            contentHtml = RemoveElementsByClass(contentHtml, "div", "karma");

            string author = CleanText(nickHtml);
            string date = CleanText(dateHtml);
            string text = CleanText(contentHtml);

            if (deleted && String.IsNullOrWhiteSpace(text))
                text = "Комментарий удалён.";

            if (ShouldSkipCommentText(text) && !deleted)
                return null;

            if (String.IsNullOrWhiteSpace(author) && String.IsNullOrWhiteSpace(text))
                return null;

            CommentItem comment = new CommentItem();
            comment.Author = author;
            comment.Date = date;
            comment.Text = text;
            comment.Level = level;
            comment.Id = ExtractCommentId(commentHtml);
            return comment;
        }

        private static string ExtractByClassLoose(string html, string className)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(className))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match start = Regex.Match(
                html,
                @"<(?<tag>[a-z0-9]+)\b(?=[^>]*\bclass\s*=\s*(['""])[^'""]*" + Regex.Escape(className) + @"[^'""]*\2)[^>]*>",
                options);

            if (!start.Success)
                return "";

            string tag = start.Groups["tag"].Value;
            int end = FindMatchingEndTag(html, start.Index, tag);
            if (end > start.Index)
                return html.Substring(start.Index, end - start.Index);

            int close = IndexOfIgnoreCase(html, "</" + tag + ">", start.Index);
            if (close > start.Index)
                return html.Substring(start.Index, close + tag.Length + 3 - start.Index);

            return start.Value;
        }

        private static string ExtractCommentId(string html)
        {
            Match match = Regex.Match(html ?? "", @"\bid\s*=\s*(['""]?)comment-(?<id>\d+)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["id"].Value : "";
        }

        private static bool ShouldSkipCommentText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return true;

            string value = text.Trim();
            string lower = value.ToLowerInvariant();

            if (lower == "ответить" || lower == "цитировать" || lower == "поделиться")
                return true;

            if (Regex.IsMatch(value, @"^[\s\-—–•·.]+$"))
                return true;

            return false;
        }

        private static string ExtractCommentAnchor(string html)
        {
            MatchCollection tags = Regex.Matches(html ?? "", @"<div\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match tag in tags)
            {
                string attrs = tag.Groups["attrs"].Value;
                string id = GetAttribute(attrs, "id");
                if (id.StartsWith("comment-", StringComparison.OrdinalIgnoreCase))
                    return attrs;
            }

            return "";
        }

        private static List<string> ExtractTopLevelElements(string html, string tagName)
        {
            var result = new List<string>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            int index = 0;
            while (index < html.Length)
            {
                int start = IndexOfTag(html, tagName, index);
                if (start < 0)
                    break;

                int end = FindMatchingEndTag(html, start, tagName);
                if (end <= start)
                    break;

                result.Add(html.Substring(start, end - start));
                index = end;
            }

            return result;
        }

        private static string ExtractTitleFromArticle(string articleHtml)
        {
            string header = ExtractElementWithClass(articleHtml, "div", "article-header");
            string title = JoinFirstNonEmpty(
                ExtractFirstElement(header, "h1"),
                ExtractFirstElement(articleHtml, "h1"));

            return CleanText(title);
        }

        private static string ExtractTimeFromArticle(string articleHtml)
        {
            string time = ExtractFirstElement(articleHtml, "time");
            if (!String.IsNullOrWhiteSpace(time))
            {
                string startTag = ExtractStartTag(time);
                string datetime = GetAttribute(GetAttributesFromStartTag(startTag), "datetime");
                if (!String.IsNullOrWhiteSpace(datetime))
                    return datetime;

                return CleanText(time);
            }

            return JoinFirstNonEmpty(
                ExtractTextByClass(articleHtml, "date"),
                ExtractTextByClass(articleHtml, "time"));
        }

        private static string ExtractAuthorFromArticle(string articleHtml)
        {
            string header = ExtractElementWithClass(articleHtml, "div", "article-header");

            string nameBlock = JoinFirstNonEmpty(
                ExtractElementWithClass(header, "span", "name"),
                ExtractElementWithClass(articleHtml, "span", "name"));

            if (!String.IsNullOrWhiteSpace(nameBlock))
            {
                string a = ExtractFirstElement(nameBlock, "a");
                if (!String.IsNullOrWhiteSpace(a))
                    return CleanMetaValue(CleanText(a));
            }

            string author = ExtractElementWithAttribute(articleHtml, "a", "rel", "author");
            if (!String.IsNullOrWhiteSpace(author))
                return CleanMetaValue(CleanText(author));

            author = JoinFirstNonEmpty(
                ExtractTextByClass(header, "author"),
                ExtractTextByClass(articleHtml, "article-author"),
                ExtractTextByClass(articleHtml, "author"),
                ExtractLabeledMetaValue(articleHtml, "Автор|Author", new[]
                {
                    "Источник|Source",
                    @"Самые\s+комментируемые",
                    "Комментарии"
                }));

            return CleanMetaValue(author);
        }

        private static ArticleMetaInfo ExtractArticleMetaInfo(string articleHtml, string contentHtml)
        {
            ArticleMetaInfo result = new ArticleMetaInfo();
            string source = JoinNonEmpty(" ", articleHtml, contentHtml);

            result.Author = ExtractLabeledMetaValue(source, "Автор|Author", new[]
            {
                "Источник|Source",
                @"Самые\s+комментируемые",
                "Комментарии"
            });

            SourceInfo sourceInfo = ExtractLabeledSourceInfo(source);
            result.SourceTitle = sourceInfo.Title;
            result.SourceUrl = sourceInfo.Url;

            return result;
        }

        private static SourceInfo ExtractSourceInfo(string articleHtml, string fullHtml, ArticleMetaInfo articleMeta)
        {
            SourceInfo result = ExtractLabeledSourceInfo(articleHtml);
            if (!String.IsNullOrWhiteSpace(result.Title) || !String.IsNullOrWhiteSpace(result.Url))
                return result;

            if (articleMeta != null && (!String.IsNullOrWhiteSpace(articleMeta.SourceTitle) || !String.IsNullOrWhiteSpace(articleMeta.SourceUrl)))
            {
                result.Title = articleMeta.SourceTitle;
                result.Url = articleMeta.SourceUrl;
                return result;
            }

            string siteName = ExtractMetaContent(fullHtml, "property", "og:site_name");
            result.Title = String.IsNullOrWhiteSpace(siteName) ? "4PDA" : CleanText(siteName);
            return result;
        }

        private static SourceInfo ExtractLabeledSourceInfo(string html)
        {
            SourceInfo result = new SourceInfo();
            string fragment = ExtractLabeledFragment(html, "Источник|Source");
            if (String.IsNullOrWhiteSpace(fragment))
                return result;

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match link = Regex.Match(fragment, @"<a\b(?<attrs>[^>]*)>(?<title>[\s\S]*?)</a>", options);
            if (link.Success)
            {
                result.Url = NormalizeUrl(GetAttribute(link.Groups["attrs"].Value, "href"));
                result.Title = CleanMetaValue(CleanText(link.Groups["title"].Value));

                if (!String.IsNullOrWhiteSpace(result.Title) || !String.IsNullOrWhiteSpace(result.Url))
                    return result;
            }

            result.Title = ExtractValueFromCleanText(CleanText(fragment), "Источник|Source", new[]
            {
                "Автор|Author",
                @"Самые\s+комментируемые",
                "Комментарии"
            });

            result.Title = CleanMetaValue(result.Title);
            return result;
        }

        private static string ExtractLabeledMetaValue(string html, string labelPattern, string[] stopLabelPatterns)
        {
            string fragment = ExtractLabeledFragment(html, labelPattern);
            if (String.IsNullOrWhiteSpace(fragment))
                return "";

            return CleanMetaValue(ExtractValueFromCleanText(CleanText(fragment), labelPattern, stopLabelPatterns));
        }

        private static string ExtractLabeledFragment(string html, string labelPattern)
        {
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(labelPattern))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match label = Regex.Match(html, @"(?<![\p{L}\p{N}_])(?:" + labelPattern + @")\s*:", options);
            if (!label.Success)
                return "";

            int start = label.Index;
            int end = FindNearestMetaBlockEnd(html, start);
            int maxEnd = Math.Min(html.Length, start + 1200);

            if (end < 0 || end > maxEnd)
                end = maxEnd;

            if (end <= start)
                return "";

            return html.Substring(start, end - start);
        }

        private static int FindNearestMetaBlockEnd(string html, int startIndex)
        {
            string[] needles = new[]
            {
                "</p>",
                "</li>",
                "<br",
                "</div>",
                "</section>"
            };

            int result = -1;
            foreach (string needle in needles)
            {
                int index = html.IndexOf(needle, Math.Max(0, startIndex), StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (result < 0 || index < result))
                    result = index;
            }

            return result;
        }

        private static string ExtractValueFromCleanText(string text, string labelPattern, string[] stopLabelPatterns)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match match = Regex.Match(text, @"(?<![\p{L}\p{N}_])(?:" + labelPattern + @")\s*:\s*(?<value>[\s\S]*)", options);
            if (!match.Success)
                return "";

            string value = match.Groups["value"].Value;
            if (stopLabelPatterns != null)
            {
                foreach (string stopLabelPattern in stopLabelPatterns)
                {
                    if (String.IsNullOrWhiteSpace(stopLabelPattern))
                        continue;

                    Match stop = Regex.Match(value, @"(?:^|[\s\.;,])(?:" + stopLabelPattern + @")\s*:", options);
                    if (stop.Success)
                    {
                        value = value.Substring(0, stop.Index);
                        break;
                    }
                }
            }

            return value;
        }

        private static string ExtractElementWithClass(string html, string tagName, string className)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            MatchCollection starts = Regex.Matches(html, @"<" + Regex.Escape(tagName) + @"\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match start in starts)
            {
                string classes = GetAttribute(start.Groups["attrs"].Value, "class");
                if (!ContainsClass(classes, className))
                    continue;

                int end = FindMatchingEndTag(html, start.Index, tagName);
                if (end > start.Index)
                    return html.Substring(start.Index, end - start.Index);
            }

            return "";
        }

        private static string ExtractElementWithAttribute(string html, string tagName, string attributeName, string attributeValue)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            MatchCollection starts = Regex.Matches(html, @"<" + Regex.Escape(tagName) + @"\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match start in starts)
            {
                string value = GetAttribute(start.Groups["attrs"].Value, attributeName);
                bool match;
                if (String.IsNullOrEmpty(attributeValue))
                    match = !String.IsNullOrEmpty(value);
                else if (attributeName.Equals("class", StringComparison.OrdinalIgnoreCase))
                    match = ContainsClass(value, attributeValue);
                else
                    match = String.Equals(value, attributeValue, StringComparison.OrdinalIgnoreCase);

                if (!match)
                    continue;

                int end = FindMatchingEndTag(html, start.Index, tagName);
                if (end > start.Index)
                    return html.Substring(start.Index, end - start.Index);
            }

            return "";
        }

        private static string ExtractFirstElement(string html, string tagName)
        {
            int start = IndexOfTag(html, tagName, 0);
            if (start < 0)
                return "";

            int end = FindMatchingEndTag(html, start, tagName);
            if (end <= start)
                return "";

            return html.Substring(start, end - start);
        }

        private static string ExtractBodyHtml(string html)
        {
            string body = ExtractFirstElement(html, "body");
            return String.IsNullOrWhiteSpace(body) ? (html ?? "") : body;
        }

        private static int IndexOfTag(string html, string tagName, int startIndex)
        {
            if (String.IsNullOrEmpty(html))
                return -1;

            Match match = Regex.Match(
                html.Substring(Math.Max(0, startIndex)),
                @"<" + Regex.Escape(tagName) + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return match.Success ? Math.Max(0, startIndex) + match.Index : -1;
        }

        private static int FindMatchingEndTag(string html, int startIndex, string tagName)
        {
            if (String.IsNullOrWhiteSpace(html) || startIndex < 0 || startIndex >= html.Length)
                return -1;

            Regex tagRegex = new Regex(@"<(?<close>/?)" + Regex.Escape(tagName) + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection tags = tagRegex.Matches(html, startIndex);
            int depth = 0;

            foreach (Match tag in tags)
            {
                bool closing = tag.Groups["close"].Value == "/";
                bool selfClosing = tag.Value.EndsWith("/>", StringComparison.Ordinal);

                if (!closing)
                {
                    depth++;
                    if (selfClosing)
                        depth--;
                }
                else
                {
                    depth--;
                    if (depth <= 0)
                        return tag.Index + tag.Length;
                }
            }

            return -1;
        }

        private static string RemoveElementsByClass(string html, string tagName, string className)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int safety = 0;
            while (safety < 100)
            {
                safety++;
                MatchCollection starts = Regex.Matches(html, @"<" + Regex.Escape(tagName) + @"\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                bool removed = false;

                foreach (Match start in starts)
                {
                    string classes = GetAttribute(start.Groups["attrs"].Value, "class");
                    if (!ContainsClass(classes, className))
                        continue;

                    int end = FindMatchingEndTag(html, start.Index, tagName);
                    if (end <= start.Index)
                        continue;

                    html = html.Remove(start.Index, end - start.Index);
                    removed = true;
                    break;
                }

                if (!removed)
                    break;
            }

            return html;
        }

        private static string RemoveTagsByName(string html, string tagName)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            return Regex.Replace(
                html,
                @"<" + Regex.Escape(tagName) + @"\b[\s\S]*?</" + Regex.Escape(tagName) + @">",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string GetInnerHtml(string elementHtml, string tagName)
        {
            if (String.IsNullOrWhiteSpace(elementHtml))
                return "";

            Match start = Regex.Match(elementHtml, @"<" + Regex.Escape(tagName) + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!start.Success)
                return elementHtml;

            Match end = Regex.Match(elementHtml, @"</" + Regex.Escape(tagName) + @">\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!end.Success || end.Index <= start.Index + start.Length)
                return elementHtml.Substring(start.Index + start.Length);

            return elementHtml.Substring(start.Index + start.Length, end.Index - (start.Index + start.Length));
        }

        private static string ExtractStartTag(string html)
        {
            Match match = Regex.Match(html ?? "", @"<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Value : "";
        }

        private static string GetAttributesFromStartTag(string startTag)
        {
            Match match = Regex.Match(startTag ?? "", @"<\w+\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["attrs"].Value : "";
        }

        private static string ExtractTextByClass(string html, string className)
        {
            string element = JoinFirstNonEmpty(
                ExtractElementWithClass(html, "span", className),
                ExtractElementWithClass(html, "div", className),
                ExtractElementWithClass(html, "em", className),
                ExtractElementWithClass(html, "a", className));

            return CleanText(element);
        }

        private static string ExtractMetaContent(string html, string attributeName, string attributeValue)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection tags = Regex.Matches(html ?? "", @"<meta\b(?<attrs>[^>]*)>", options);

            foreach (Match tag in tags)
            {
                string attrs = tag.Groups["attrs"].Value;
                string value = GetAttribute(attrs, attributeName);

                if (String.Equals(value, attributeValue, StringComparison.OrdinalIgnoreCase))
                    return GetAttribute(attrs, "content");
            }

            return "";
        }

        private static string ExtractTitleTag(string html)
        {
            Match match = Regex.Match(html ?? "", @"<title\b[^>]*>(?<value>[\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? CleanText(match.Groups["value"].Value) : "";
        }

        private static string ExtractFirstImageUrl(string html)
        {
            MatchCollection images = Regex.Matches(html ?? "", @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match image in images)
            {
                string attrs = image.Groups["attrs"].Value;
                string src = JoinFirstNonEmpty(
                    GetAttribute(attrs, "data-src"),
                    GetAttribute(attrs, "data-original"),
                    GetAttribute(attrs, "data-lazy-src"),
                    GetAttribute(attrs, "src"));

                src = NormalizeUrl(src);
                if (!String.IsNullOrWhiteSpace(src) && !IsServiceImage(src, attrs))
                    return src;
            }

            return "";
        }

        private static string GetAttribute(string attributes, string name)
        {
            Match quoted = Regex.Match(
                attributes ?? "",
                @"\b" + Regex.Escape(name) + @"\s*=\s*(['" + "\"" + @"])(?<value>[\s\S]*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (quoted.Success)
                return DecodeEntities(quoted.Groups["value"].Value);

            Match unquoted = Regex.Match(
                attributes ?? "",
                @"\b" + Regex.Escape(name) + @"\s*=\s*(?<value>[^\s>]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return unquoted.Success ? DecodeEntities(unquoted.Groups["value"].Value) : "";
        }

        private static bool ContainsClass(string classes, string className)
        {
            if (String.IsNullOrWhiteSpace(classes) || String.IsNullOrWhiteSpace(className))
                return false;

            string[] parts = classes.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (String.Equals(part, className, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string prepared = html;
            prepared = Regex.Replace(prepared, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"</(p|div|li|h1|h2|h3|h4|blockquote|tr|td)>\s*", "\n", RegexOptions.IgnoreCase);
            prepared = RemoveTagsByName(prepared, "script");
            prepared = RemoveTagsByName(prepared, "style");
            prepared = RemoveTagsByName(prepared, "noscript");
            prepared = Regex.Replace(prepared, @"\[\[IMG:[\s\S]*?\]\]", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            prepared = Regex.Replace(prepared, @"\[\[(H|QUOTE|LI|BR)\]\]", " ", RegexOptions.IgnoreCase);

            string text;
            try
            {
                text = HtmlUtilities.ConvertToText(prepared);
            }
            catch
            {
                text = Regex.Replace(prepared, @"<[^>]+>", " ");
                text = DecodeEntities(text);
            }

            text = text.Replace('\u00A0', ' ');
            text = Regex.Replace(text, @"[ \t\r\n]+", " ");
            return text.Trim();
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
                    .Replace("&apos;", "'")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .Replace("&nbsp;", " ");
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            url = DecodeEntities(url.Trim());

            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;

            Uri absolute;
            if (Uri.TryCreate(url, UriKind.Absolute, out absolute))
                return absolute.ToString();

            Uri baseUri = new Uri("https://" + Host + "/");
            return new Uri(baseUri, url).ToString();
        }

        private static int ExtractArticleId(string url, string html)
        {
            Match fromUrl = Regex.Match(url ?? "", @"/(\d+)(?:/|$|\?)", RegexOptions.IgnoreCase);
            if (fromUrl.Success)
                return ToInt(fromUrl.Groups[1].Value);

            Match fromZtm = Regex.Match(html ?? "", @"data-ztm\s*=\s*(['" + "\"" + @"])[^:'" + "\"" + @"]*:(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (fromZtm.Success)
                return ToInt(fromZtm.Groups["id"].Value);

            return 0;
        }

        private static int ToInt(string value)
        {
            int result;
            return Int32.TryParse(value, out result) ? result : 0;
        }

        private static string RemoveSiteSuffix(string title)
        {
            if (String.IsNullOrWhiteSpace(title))
                return "";

            return Regex.Replace(title, @"\s+[-–—]\s+4PDA\s*$", "", RegexOptions.IgnoreCase).Trim();
        }

        private static string FormatDate(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            DateTimeOffset date;
            return DateTimeOffset.TryParse(value, out date) ? date.ToString("dd.MM.yyyy HH:mm") : CleanText(value);
        }

        private static string CleanMetaValue(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string result = CleanText(value);
            result = Regex.Replace(result, @"^(?:Источник|Source|Автор|Author)\s*:\s*", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s*(?:Самые\s+комментируемые|Комментарии)\s*$", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"^[\s:;,.\-–—]+", "");
            result = Regex.Replace(result, @"[\s;,.\-–—]+$", "");
            return result.Trim();
        }

        private static string JoinFirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!String.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return String.Join(separator, values.Where(v => !String.IsNullOrWhiteSpace(v)));
        }

        private sealed class ArticleSlices
        {
            public string ArticleHtml { get; set; }
            public string ContentHtml { get; set; }
            public string CommentsHtml { get; set; }
        }

        private sealed class SourceInfo
        {
            public string Title { get; set; }
            public string Url { get; set; }
        }

        private sealed class ArticleMetaInfo
        {
            public string Author { get; set; }
            public string SourceTitle { get; set; }
            public string SourceUrl { get; set; }
        }

        private sealed class NewsDetails
        {
            public int Id { get; set; }
            public string Url { get; set; }
            public string Title { get; set; }
            public string Date { get; set; }
            public string Author { get; set; }
            public string SourceTitle { get; set; }
            public string SourceUrl { get; set; }
            public string MainImageUrl { get; set; }
            public List<ContentBlock> ArticleBlocks { get; set; }
            public List<CommentItem> Comments { get; set; }

            public NewsDetails()
            {
                ArticleBlocks = new List<ContentBlock>();
                Comments = new List<CommentItem>();
            }
        }

        private enum ContentBlockKind
        {
            Text,
            Heading,
            Quote,
            Image
        }

        private sealed class ContentBlock
        {
            public ContentBlockKind Kind { get; set; }
            public string Text { get; set; }
            public string ImageUrl { get; set; }
        }

        private sealed class CommentItem
        {
            public string Id { get; set; }
            public string Author { get; set; }
            public string Date { get; set; }
            public string Text { get; set; }
            public int Level { get; set; }
        }
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackNavigationService.GoBack(Frame);
        }

    }
}
