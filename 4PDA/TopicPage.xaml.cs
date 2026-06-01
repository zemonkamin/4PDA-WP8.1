using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Web.Http.Filters;
using Windows.Storage.Streams;
using Windows.Storage.Provider;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.ApplicationModel.Activation;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.Graphics.Imaging;
using Windows.Phone.UI.Input;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Popups;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed partial class TopicPage : Page
    {
        private const string ForumBaseUrl = "https://4pda.to/forum/index.php";
        private const int DefaultPageSize = 20;
        private const string FileIconGif = "https://4pda.to/s/mQ600b0kBHtbbyz1UYtbg5GfeT1YikOfEOteLNm9MACz2oFQOdeHvBJLWbb5SV.gif";
        private const string SnapbackGif = "mQ60RwKIZz1PFQxMexb5gSBJeDUF9SO5wjSGuib1UEdTaYz1Tsyi.gif";
        private const string LinkTokenStart = "\uE100LINK:";
        private const string LinkTokenSeparator = "|";
        private const string LinkTokenEnd = "\uE101";
        private const string ParserCacheVersion = "posts-v13-centered-pagination-user-links";

        private readonly ObservableCollection<TopicPostItem> _posts = new ObservableCollection<TopicPostItem>();
        private readonly ObservableCollection<PageNavigationItem> _pages = new ObservableCollection<PageNavigationItem>();
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly HttpBaseProtocolFilter _downloadFilter = new HttpBaseProtocolFilter();
        private readonly HttpBaseProtocolFilter _resolveFilter = new HttpBaseProtocolFilter();
        private readonly HttpClient _downloadHttpClient;
        private readonly HttpClient _resolveHttpClient;
        private const string FileSaveContinuationOperationKey = "Operation";
        private const string FileSaveContinuationOperation = "TopicFileDownload";
        private const string FileSaveContinuationUrlKey = "FileUrl";
        private const string FileSaveContinuationNameKey = "FileName";
        private const string FileSaveContinuationTempNameKey = "TempFileName";
        private const int DownloadBufferSize = 64 * 1024;
        private static TopicPage _activeFileSavePage;
        private readonly Stack<TopicPageState> _history = new Stack<TopicPageState>();

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, TopicData> TopicCache = new Dictionary<string, TopicData>();
        private static readonly object SmileFrameCacheLock = new object();
        private static readonly Dictionary<string, Task<AnimatedSmileFrames>> SmileFrameCache = new Dictionary<string, Task<AnimatedSmileFrames>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex CachedRichTextTokenRegex = new Regex(
            Regex.Escape(LinkTokenStart) + @"(?<linkUrl>[A-Za-z0-9+/=]+)" + Regex.Escape(LinkTokenSeparator) + @"(?<linkText>[A-Za-z0-9+/=]+)" + Regex.Escape(LinkTokenEnd) + "|" + @":(?<name>[a-z0-9_\-]{2,40}):",
            RegexOptions.IgnoreCase);

        private string _topicId = "";
        private int _start;
        private string _forumId = "0";
        private string _authKey = "";
        private bool _canReply;
        private bool _loading;

        public TopicPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            PostsListView.ItemsSource = _posts;
            PagesItemsControl.ItemsSource = _pages;

            _downloadHttpClient = new HttpClient(_downloadFilter);
            _resolveHttpClient = new HttpClient(_resolveFilter);

            ForumCookieHelper.ApplyDefaultHeaders(_httpClient);
            ForumCookieHelper.ApplyDefaultHeaders(_downloadHttpClient);
            ForumCookieHelper.ApplyDefaultHeaders(_resolveHttpClient);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _activeFileSavePage = this;
            ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("TopicPage.OnNavigatedTo");

            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;

            string parameter = e.Parameter as string;
            _topicId = ExtractTopicId(parameter);
            if (String.IsNullOrWhiteSpace(_topicId))
                _topicId = "1";

            int parsedStart;
            _start = TryExtractStart(parameter, out parsedStart) ? Math.Max(0, parsedStart) : 0;
            _history.Clear();
            UpdateReplyVisibility();
            await LoadTopicAsync(false);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            base.OnNavigatedFrom(e);
        }

        private async void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (_history.Count > 0)
            {
                e.Handled = true;
                TopicPageState state = _history.Pop();
                _topicId = state.TopicId;
                _start = state.Start;
                await LoadTopicAsync(false);
                return;
            }

            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private async void RefreshAppBarButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadTopicAsync(true);
        }

        private async void PageButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            PageNavigationItem page = element.DataContext as PageNavigationItem;
            if (page == null || !page.IsEnabled || _loading)
                return;

            if (page.Start == _start)
                return;

            PushHistory();
            _start = Math.Max(0, page.Start);
            await LoadTopicAsync(false);
        }

        private void AuthorButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicPostItem post = element.DataContext as TopicPostItem;
            if (post == null)
                return;

            string authorId = OnlyDigits(post.AuthorId);
            if (String.IsNullOrWhiteSpace(authorId))
            {
                SetStatus("Не удалось открыть профиль: в сообщении нет ID автора.");
                return;
            }

            if (!ForumAuthService.IsAuthorized)
            {
                if (Frame != null)
                    Frame.Navigate(typeof(LoginPage));
                return;
            }

            if (Frame != null)
                Frame.Navigate(typeof(UserPage), "https://4pda.to/forum/index.php?showuser=" + authorId);
        }

        private void SpoilerButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicContentItem spoiler = element.DataContext as TopicContentItem;
            if (spoiler != null)
                spoiler.IsExpanded = !spoiler.IsExpanded;
        }

        private async void MediaImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicContentItem media = element.DataContext as TopicContentItem;
            if (media == null || String.IsNullOrWhiteSpace(media.Url))
                return;

            await Launcher.LaunchUriAsync(new Uri(media.Url, UriKind.Absolute));
        }

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicContentItem file = element.DataContext as TopicContentItem;
            if (file == null || String.IsNullOrWhiteSpace(file.Url))
                return;

            await PrepareAndStartFileSavePickerAsync(file);
        }

        private void RichTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            RichTextBlock block = sender as RichTextBlock;
            if (block == null)
                return;

            TopicContentItem item = block.DataContext as TopicContentItem;
            if (item == null)
                return;

            FillRichTextBlock(block, item.Text);
        }

        private void FillRichTextBlock(RichTextBlock block, string text)
        {
            if (block == null)
                return;

            string safeText = text ?? "";
            if (Object.Equals(block.Tag, safeText))
                return;

            block.Tag = safeText;
            block.Blocks.Clear();

            Paragraph paragraph = new Paragraph();
            AddRichTextInlines(paragraph.Inlines, safeText, block.FontSize);
            block.Blocks.Add(paragraph);
        }

        private void AddRichTextInlines(InlineCollection inlines, string text, double fontSize)
        {
            if (inlines == null)
                return;

            string source = text ?? "";
            int position = 0;
            Regex tokenRegex = RichTextTokenRegex();
            MatchCollection matches = tokenRegex.Matches(source);
            foreach (Match match in matches)
            {
                if (match.Index > position)
                    AddRunWithLineBreaks(inlines, source.Substring(position, match.Index - position));

                if (match.Groups["linkUrl"].Success)
                {
                    string url = DecodeLinkTokenValue(match.Groups["linkUrl"].Value);
                    string caption = DecodeLinkTokenValue(match.Groups["linkText"].Value);
                    AddPostLinkInline(inlines, caption, url, fontSize);
                }
                else
                {
                    string smileName = ExtractSmileNameFromToken(match.Value);
                    if (!String.IsNullOrWhiteSpace(smileName))
                        AddSmileInline(inlines, smileName, fontSize);
                    else
                        AddRunWithLineBreaks(inlines, match.Value);
                }

                position = match.Index + match.Length;
            }

            if (position < source.Length)
                AddRunWithLineBreaks(inlines, source.Substring(position));
        }

        private static Regex RichTextTokenRegex()
        {
            return CachedRichTextTokenRegex;
        }

        private void AddPostLinkInline(InlineCollection inlines, string caption, string url, double fontSize)
        {
            if (inlines == null)
                return;

            string text = NormalizePostLinkCaption(caption);
            TopicPostLink link = ParseTopicPostLink(url, _topicId);
            if (link == null || String.IsNullOrWhiteSpace(link.PostId))
            {
                AddRunWithLineBreaks(inlines, text);
                return;
            }

            TextBlock textBlock = new TextBlock();
            textBlock.Text = text;
            textBlock.FontSize = fontSize;
            textBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 142, 232));
            textBlock.TextWrapping = TextWrapping.NoWrap;
            textBlock.Margin = new Thickness(1, 0, 1, -2);
            textBlock.DataContext = link;
            textBlock.Tapped += MessagePostLink_Tapped;

            InlineUIContainer container = new InlineUIContainer();
            container.Child = textBlock;
            inlines.Add(container);
        }

        private async void MessagePostLink_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;

            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicPostLink link = element.DataContext as TopicPostLink;
            if (link == null)
                return;

            await OpenPostLinkAsync(link);
        }

        private static void AddRunWithLineBreaks(InlineCollection inlines, string text)
        {
            if (inlines == null || String.IsNullOrEmpty(text))
                return;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split(new char[] { '\n' });
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    inlines.Add(new LineBreak());

                if (!String.IsNullOrEmpty(lines[i]))
                    inlines.Add(new Run { Text = lines[i] });
            }
        }

        private static void AddSmileInline(InlineCollection inlines, string smileName, double fontSize)
        {
            if (inlines == null || String.IsNullOrWhiteSpace(smileName))
                return;

            InlineUIContainer container = new InlineUIContainer();
            container.Child = CreateAnimatedSmileElement(smileName, GetSmileSize(fontSize));
            inlines.Add(container);
        }

        private static UIElement CreateAnimatedSmileElement(string smileName, double size)
        {
            double safeSize = size <= 0 ? 22 : size;
            string name = SanitizeSmileName(smileName);
            if (String.IsNullOrWhiteSpace(name))
                name = "smile";

            Image image = new Image();
            image.Width = safeSize;
            image.Height = safeSize;
            image.MinWidth = safeSize;
            image.MinHeight = safeSize;
            image.MaxWidth = safeSize;
            image.MaxHeight = safeSize;
            image.Stretch = Stretch.Uniform;
            image.Margin = new Thickness(1, 0, 1, -3);
            image.IsHitTestVisible = false;

            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = (int)safeSize;
                bitmap.UriSource = new Uri(GetSmileAssetUri(name), UriKind.Absolute);
                image.Source = bitmap;
            }
            catch
            {
            }

            return image;
        }

        private static void InlineSmileImage_Loaded(object sender, RoutedEventArgs e)
        {
            Image image = sender as Image;
            if (image == null)
                return;

            SmileRenderInfo info = image.DataContext as SmileRenderInfo;
            if (info == null || String.IsNullOrWhiteSpace(info.Name))
                return;

            BeginAnimatedSmile(image, info.Name, info.Size);
        }

        private static void InlineSmileImage_Unloaded(object sender, RoutedEventArgs e)
        {
            Image image = sender as Image;
            if (image != null)
                image.Tag = null;
        }

        private static void BeginAnimatedSmile(Image image, string smileName, double size)
        {
            if (image == null)
                return;

            string name = SanitizeSmileName(smileName);
            if (String.IsNullOrWhiteSpace(name))
                return;

            double safeSize = size <= 0 ? 26 : size;
            image.Width = safeSize;
            image.Height = safeSize;
            image.MinWidth = safeSize;
            image.MinHeight = safeSize;
            image.MaxWidth = safeSize;
            image.MaxHeight = safeSize;

            object token = new object();
            image.Tag = token;

            try
            {
                image.Source = new BitmapImage(new Uri(GetSmileAssetUri(name), UriKind.Absolute));
            }
            catch
            {
            }

            Task unused = AnimateSmileAsync(image, name, token);
        }

        private static async Task AnimateSmileAsync(Image image, string smileName, object token)
        {
            try
            {
                AnimatedSmileFrames frames = await GetAnimatedSmileFramesAsync(smileName);
                if (frames == null || frames.Frames == null || frames.Frames.Count == 0)
                    return;

                if (!Object.ReferenceEquals(image.Tag, token))
                    return;

                if (frames.Frames.Count == 1)
                {
                    image.Source = frames.Frames[0].Bitmap;
                    return;
                }

                int index = 0;
                while (Object.ReferenceEquals(image.Tag, token))
                {
                    AnimatedSmileFrame frame = frames.Frames[index];
                    image.Source = frame.Bitmap;

                    int delay = frame.DelayMs;
                    if (delay < 40)
                        delay = 100;

                    await Task.Delay(delay);
                    index++;
                    if (index >= frames.Frames.Count)
                        index = 0;
                }
            }
            catch
            {
            }
        }

        private static Task<AnimatedSmileFrames> GetAnimatedSmileFramesAsync(string smileName)
        {
            string name = SanitizeSmileName(smileName);
            if (String.IsNullOrWhiteSpace(name))
                name = "smile";

            lock (SmileFrameCacheLock)
            {
                Task<AnimatedSmileFrames> cached;
                if (SmileFrameCache.TryGetValue(name, out cached))
                    return cached;

                Task<AnimatedSmileFrames> task = LoadAnimatedSmileFramesAsync(name);
                SmileFrameCache[name] = task;
                return task;
            }
        }

        private static async Task<AnimatedSmileFrames> LoadAnimatedSmileFramesAsync(string smileName)
        {
            AnimatedSmileFrames result = new AnimatedSmileFrames();
            result.Frames = new List<AnimatedSmileFrame>();

            StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(GetSmileAssetUri(smileName), UriKind.Absolute));
            using (IRandomAccessStream stream = await file.OpenReadAsync())
            {
                BitmapDecoder decoder;
                try
                {
                    decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);
                }
                catch
                {
                    stream.Seek(0);
                    decoder = await BitmapDecoder.CreateAsync(stream);
                }

                uint frameCount = decoder.FrameCount;
                if (frameCount == 0)
                    frameCount = 1;

                int canvasWidth = decoder.PixelWidth == 0 ? 1 : (int)decoder.PixelWidth;
                int canvasHeight = decoder.PixelHeight == 0 ? 1 : (int)decoder.PixelHeight;
                byte[] canvas = new byte[canvasWidth * canvasHeight * 4];
                List<RawSmileFrame> rawFrames = new List<RawSmileFrame>();

                for (uint i = 0; i < frameCount; i++)
                {
                    BitmapFrame frame = await decoder.GetFrameAsync(i);
                    RawSmileFrame raw = await ReadRawGifFrameAsync(frame);
                    if (raw == null || raw.Pixels == null || raw.Width <= 0 || raw.Height <= 0)
                        continue;

                    raw.DelayMs = await ReadGifFrameDelayAsync(frame);
                    raw.DisposalMethod = await ReadGifFrameDisposalAsync(frame);
                    rawFrames.Add(raw);

                    int right = Math.Max(canvasWidth, raw.Left + raw.Width);
                    int bottom = Math.Max(canvasHeight, raw.Top + raw.Height);
                    if (right != canvasWidth || bottom != canvasHeight)
                    {
                        canvasWidth = right;
                        canvasHeight = bottom;
                    }
                }

                if (rawFrames.Count == 0)
                    return result;

                canvas = new byte[canvasWidth * canvasHeight * 4];

                foreach (RawSmileFrame raw in rawFrames)
                {
                    byte[] beforeFrame = null;
                    if (raw.DisposalMethod == 3)
                        beforeFrame = (byte[])canvas.Clone();

                    CompositeGifFrame(canvas, canvasWidth, canvasHeight, raw);

                    AnimatedSmileFrame animatedFrame = new AnimatedSmileFrame();
                    animatedFrame.Bitmap = await CreateBitmapFromPixelsAsync(canvas, canvasWidth, canvasHeight);
                    animatedFrame.DelayMs = raw.DelayMs;
                    result.Frames.Add(animatedFrame);

                    if (raw.DisposalMethod == 2)
                        ClearGifFrameRect(canvas, canvasWidth, canvasHeight, raw);
                    else if (raw.DisposalMethod == 3 && beforeFrame != null)
                        canvas = beforeFrame;
                }
            }

            return result;
        }

        private static async Task<RawSmileFrame> ReadRawGifFrameAsync(BitmapFrame frame)
        {
            if (frame == null)
                return null;

            uint width = frame.PixelWidth == 0 ? 1u : frame.PixelWidth;
            uint height = frame.PixelHeight == 0 ? 1u : frame.PixelHeight;

            PixelDataProvider provider = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            RawSmileFrame raw = new RawSmileFrame();
            raw.Width = (int)width;
            raw.Height = (int)height;
            raw.Left = await ReadGifFrameIntPropertyAsync(frame, "/imgdesc/Left", 0);
            raw.Top = await ReadGifFrameIntPropertyAsync(frame, "/imgdesc/Top", 0);
            raw.Pixels = provider.DetachPixelData();
            return raw;
        }

        private static async Task<WriteableBitmap> CreateBitmapFromPixelsAsync(byte[] pixels, int width, int height)
        {
            if (pixels == null || width <= 0 || height <= 0)
                return null;

            WriteableBitmap bitmap = new WriteableBitmap(width, height);
            using (Stream target = bitmap.PixelBuffer.AsStream())
            {
                await target.WriteAsync(pixels, 0, pixels.Length);
            }
            bitmap.Invalidate();
            return bitmap;
        }

        private static void CompositeGifFrame(byte[] canvas, int canvasWidth, int canvasHeight, RawSmileFrame frame)
        {
            if (canvas == null || frame == null || frame.Pixels == null)
                return;

            for (int y = 0; y < frame.Height; y++)
            {
                int targetY = frame.Top + y;
                if (targetY < 0 || targetY >= canvasHeight)
                    continue;

                for (int x = 0; x < frame.Width; x++)
                {
                    int targetX = frame.Left + x;
                    if (targetX < 0 || targetX >= canvasWidth)
                        continue;

                    int sourceIndex = (y * frame.Width + x) * 4;
                    int targetIndex = (targetY * canvasWidth + targetX) * 4;
                    if (sourceIndex + 3 >= frame.Pixels.Length || targetIndex + 3 >= canvas.Length)
                        continue;

                    byte alpha = frame.Pixels[sourceIndex + 3];
                    if (alpha == 0)
                        continue;

                    canvas[targetIndex] = frame.Pixels[sourceIndex];
                    canvas[targetIndex + 1] = frame.Pixels[sourceIndex + 1];
                    canvas[targetIndex + 2] = frame.Pixels[sourceIndex + 2];
                    canvas[targetIndex + 3] = frame.Pixels[sourceIndex + 3];
                }
            }
        }

        private static void ClearGifFrameRect(byte[] canvas, int canvasWidth, int canvasHeight, RawSmileFrame frame)
        {
            if (canvas == null || frame == null)
                return;

            for (int y = 0; y < frame.Height; y++)
            {
                int targetY = frame.Top + y;
                if (targetY < 0 || targetY >= canvasHeight)
                    continue;

                for (int x = 0; x < frame.Width; x++)
                {
                    int targetX = frame.Left + x;
                    if (targetX < 0 || targetX >= canvasWidth)
                        continue;

                    int targetIndex = (targetY * canvasWidth + targetX) * 4;
                    if (targetIndex + 3 >= canvas.Length)
                        continue;

                    canvas[targetIndex] = 0;
                    canvas[targetIndex + 1] = 0;
                    canvas[targetIndex + 2] = 0;
                    canvas[targetIndex + 3] = 0;
                }
            }
        }

        private static async Task<int> ReadGifFrameIntPropertyAsync(BitmapFrame frame, string key, int defaultValue)
        {
            try
            {
                var properties = await frame.BitmapProperties.GetPropertiesAsync(new string[] { key });
                if (properties != null && properties.ContainsKey(key) && properties[key] != null && properties[key].Value != null)
                    return Convert.ToInt32(properties[key].Value);
            }
            catch
            {
            }

            return defaultValue;
        }

        private static async Task<int> ReadGifFrameDisposalAsync(BitmapFrame frame)
        {
            return await ReadGifFrameIntPropertyAsync(frame, "/grctlext/Disposal", 1);
        }

        private static async Task<int> ReadGifFrameDelayAsync(BitmapFrame frame)
        {
            int raw = await ReadGifFrameIntPropertyAsync(frame, "/grctlext/Delay", 10);
            int delay = raw * 10;
            if (delay < 40)
                delay = 100;
            return delay;
        }

        private async Task PrepareAndStartFileSavePickerAsync(TopicContentItem file)
        {
            if (file == null || String.IsNullOrWhiteSpace(file.Url))
                return;

            if (!ForumAuthService.IsAuthorized)
            {
                await ShowLoginRequiredForDownloadAsync();
                return;
            }

            if (_loading)
                return;

            _loading = true;
            SetBusy(true);
            SetStatus("Подготавливаем ссылку скачивания...");
            ForumDownloadLog("FileButton_Click title=" + SafeForLog(file.FileTitle) + " url=" + SafeForLog(file.Url));

            bool overlayShown = false;

            try
            {
                ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("before download resolve");
                ForumDownloadLog("Known cookies before resolve: " + ForumCookieHelper.DescribeKnownCookies());

                string suggestedName = GetSuggestedDownloadFileName(file);
                string resolvedUrl = await ResolveDownloadUriAsync(file.Url);
                if (String.IsNullOrWhiteSpace(resolvedUrl))
                    throw new InvalidOperationException("Не удалось получить ссылку скачивания.");

                SetBusy(false);
                ShowDownloadOverlay(suggestedName);
                overlayShown = true;
                SetStatus("Загрузка файла...");
                await Task.Delay(250);

                StorageFile tempFile = await DownloadFileToTemporaryFileAsync(resolvedUrl, suggestedName);
                SetStatus("Файл загружен. Выберите, куда его сохранить.");

                HideDownloadOverlay();
                overlayShown = false;

                StartFileSavePicker(file, resolvedUrl, tempFile.Name);
            }
            catch (ForumAntibotException ex)
            {
                ForumDownloadLog("PrepareAndStartFileSavePickerAsync ANTIBOT " + ex.ToString());
                SetStatus("4PDA требует проверку. Откроется страница проверки, пройдите её и нажмите «готово», потом повторите скачивание.");
                OpenClearancePage(file.Url);
            }
            catch (Exception ex)
            {
                ForumDownloadLog("PrepareAndStartFileSavePickerAsync ERROR " + ex.ToString());
                SetStatus("Не удалось скачать файл: " + ex.Message);
            }
            finally
            {
                if (overlayShown)
                    HideDownloadOverlay();

                SetBusy(false);
                _loading = false;
            }
        }

        private async Task ShowLoginRequiredForDownloadAsync()
        {
            const string message = "Для скачивания файлов надо войти в аккаунт.";
            SetStatus(message);
            ForumDownloadLog("Download blocked: user is not authorized");

            try
            {
                MessageDialog dialog = new MessageDialog(message, "Нужна авторизация");
                await dialog.ShowAsync();
            }
            catch
            {
            }
        }

        private void StartFileSavePicker(TopicContentItem file, string resolvedUrl, string tempFileName)
        {
            string suggestedName = GetSuggestedDownloadFileName(file);
            string extension = GetSafeFileExtension(suggestedName);

            FileSavePicker savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.Downloads;
            savePicker.SuggestedFileName = suggestedName;
            savePicker.FileTypeChoices.Add("Файл", new List<string> { extension });
            savePicker.ContinuationData[FileSaveContinuationOperationKey] = FileSaveContinuationOperation;
            savePicker.ContinuationData[FileSaveContinuationUrlKey] = resolvedUrl;
            savePicker.ContinuationData[FileSaveContinuationNameKey] = suggestedName;
            savePicker.ContinuationData[FileSaveContinuationTempNameKey] = tempFileName ?? "";

            ForumDownloadLog("StartFileSavePicker suggestedName=" + SafeForLog(suggestedName) + " extension=" + extension + " temp=" + SafeForLog(tempFileName) + " continuationUrl=" + SafeForLog(resolvedUrl));
            savePicker.PickSaveFileAndContinue();
        }

        public static async void ContinueFileSavePicker(FileSavePickerContinuationEventArgs args)
        {
            ForumDownloadLogStatic("ContinueFileSavePicker called args=" + (args == null ? "null" : "ok"));

            if (!IsOurFileSaveContinuation(args))
            {
                ForumDownloadLogStatic("ContinueFileSavePicker ignored: not our continuation or missing Operation key");
                return;
            }

            TopicPage page = _activeFileSavePage;
            if (page == null)
            {
                try
                {
                    Frame rootFrame = Window.Current.Content as Frame;
                    page = rootFrame == null ? null : rootFrame.Content as TopicPage;
                    if (page != null)
                        _activeFileSavePage = page;
                }
                catch (Exception ex)
                {
                    ForumDownloadLogStatic("ContinueFileSavePicker cannot get current TopicPage: " + ex.ToString());
                }
            }

            if (page == null)
            {
                ForumDownloadLogStatic("ContinueFileSavePicker ERROR: TopicPage instance is null. Check App.xaml.cs OnActivated.");
                try
                {
                    MessageDialog dialog = new MessageDialog("Не удалось начать скачивание после выбора файла. Проверь, что в App.xaml.cs добавлен вызов TopicPage.ContinueFileSavePicker(...) в OnActivated.", "Скачивание не началось");
                    await dialog.ShowAsync();
                }
                catch
                {
                }
                return;
            }

            ForumDownloadLogStatic("ContinueFileSavePicker route to TopicPage instance");
            await page.CompleteFileSavePickerAsync(args);
        }

        private async Task CompleteFileSavePickerAsync(FileSavePickerContinuationEventArgs args)
        {
            ForumDownloadLog("CompleteFileSavePickerAsync entered");

            if (!IsOurFileSaveContinuation(args))
            {
                ForumDownloadLog("CompleteFileSavePickerAsync ignored: not our continuation");
                return;
            }

            if (args.File == null)
            {
                ForumDownloadLog("CompleteFileSavePickerAsync cancelled: args.File is null");
                SetStatus("Сохранение отменено.");
                return;
            }

            string url = GetContinuationString(args, FileSaveContinuationUrlKey);
            string fileName = GetContinuationString(args, FileSaveContinuationNameKey);
            string tempFileName = GetContinuationString(args, FileSaveContinuationTempNameKey);
            if (String.IsNullOrWhiteSpace(fileName))
                fileName = args.File.Name;

            _loading = true;
            SetBusy(true);
            ForumDownloadLog("CompleteFileSavePickerAsync save selected file=" + SafeForLog(args.File.Name) + " temp=" + SafeForLog(tempFileName) + " url=" + SafeForLog(url));

            bool overlayShown = false;

            try
            {
                CachedFileManager.DeferUpdates(args.File);

                StorageFile tempFile = null;
                if (!String.IsNullOrWhiteSpace(tempFileName))
                {
                    try
                    {
                        tempFile = await ApplicationData.Current.TemporaryFolder.GetFileAsync(tempFileName);
                    }
                    catch (Exception ex)
                    {
                        ForumDownloadLog("Temp file not found, fallback to direct download: " + ex.Message);
                    }
                }

                if (tempFile != null)
                {
                    ShowDownloadOverlay(fileName);
                    overlayShown = true;
                    DownloadTitleTextBlock.Text = "Сохранение файла";
                    DownloadProgressTextBlock.Text = fileName + "\nКопируем в выбранную папку...";
                    await Task.Delay(100);

                    await CopyStorageFileToStorageFileAsync(tempFile, args.File, fileName);

                    try
                    {
                        await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }
                else
                {
                    if (String.IsNullOrWhiteSpace(url))
                    {
                        SetStatus("Не удалось сохранить файл: потеряна ссылка скачивания.");
                        return;
                    }

                    ShowDownloadOverlay(fileName);
                    overlayShown = true;
                    SetStatus("Загрузка файла...");
                    await Task.Delay(250);
                    await DownloadFileToStorageFileAsync(url, args.File, fileName);
                }

                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(args.File);

                if (status == FileUpdateStatus.Complete)
                    SetStatus("Файл сохранён: " + args.File.Name);
                else
                    SetStatus("Файл сохранён, но система вернула: " + status.ToString());
            }
            catch (ForumAntibotException ex)
            {
                ForumDownloadLog("CompleteFileSavePickerAsync ANTIBOT " + ex.ToString());
                SetStatus("4PDA снова требует проверку. Откроется страница проверки, затем повторите скачивание.");
                OpenClearancePage(url);
            }
            catch (Exception ex)
            {
                ForumDownloadLog("CompleteFileSavePickerAsync ERROR " + ex.ToString());
                SetStatus("Не удалось сохранить файл: " + ex.Message);
            }
            finally
            {
                if (overlayShown)
                    HideDownloadOverlay();

                SetBusy(false);
                _loading = false;
            }
        }

        private void SendAppBarButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ForumAuthService.IsAuthorized)
            {
                SetStatus("Для ответа надо войти в аккаунт.");
                return;
            }

            if (!_canReply)
            {
                SetStatus("Тема закрыта или сервер не отдал форму ответа.");
                return;
            }

            ReplyPanel.Visibility = Visibility.Visible;
            if (_posts.Count > 0)
                PostsListView.ScrollIntoView(_posts[_posts.Count - 1]);
            ReplyTextBox.Focus(FocusState.Programmatic);
        }

        private async void SendReplyButton_Click(object sender, RoutedEventArgs e)
        {
            await SendReplyAsync();
        }

        private void QuotePostButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicPostItem post = element.DataContext as TopicPostItem;
            if (post == null)
                return;

            if (!ForumAuthService.IsAuthorized)
            {
                SetStatus("Для цитирования надо войти в аккаунт.");
                return;
            }

            if (!_canReply)
            {
                SetStatus("Сервер не отдал форму ответа. Обновите тему или проверьте, не закрыта ли она.");
                return;
            }

            ReplyPanel.Visibility = Visibility.Visible;

            string quote = BuildQuoteText(post);
            if (!String.IsNullOrWhiteSpace(ReplyTextBox.Text))
                ReplyTextBox.Text = ReplyTextBox.Text.TrimEnd() + "\r\n\r\n" + quote;
            else
                ReplyTextBox.Text = quote;

            if (_posts.Count > 0)
                PostsListView.ScrollIntoView(_posts[_posts.Count - 1]);

            ReplyTextBox.Focus(FocusState.Programmatic);
            ReplyTextBox.SelectionStart = ReplyTextBox.Text.Length;
        }

        private async void ReputationButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            TopicPostItem post = element.DataContext as TopicPostItem;
            if (post == null)
                return;

            string type = element.Tag as string;
            bool positive = String.Equals(type, "add", StringComparison.OrdinalIgnoreCase);
            await ChangePostReputationAsync(post, positive);
        }

        private async Task ChangePostReputationAsync(TopicPostItem post, bool positive)
        {
            if (_loading)
                return;

            if (!ForumAuthService.IsAuthorized)
            {
                SetStatus("Для оценки сообщения надо войти в аккаунт.");
                if (Frame != null)
                    Frame.Navigate(typeof(LoginPage));
                return;
            }

            string postId = post == null ? "" : OnlyDigits(post.PostId);
            string authorId = post == null ? "" : OnlyDigits(post.AuthorId);
            if (String.IsNullOrWhiteSpace(postId) || String.IsNullOrWhiteSpace(authorId))
            {
                SetStatus("Не удалось определить сообщение или автора для оценки.");
                return;
            }

            string currentUserId = OnlyDigits(ForumAuthService.CurrentUserId);
            if (!String.IsNullOrWhiteSpace(currentUserId) && String.Equals(currentUserId, authorId, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Нельзя оценивать свои сообщения.");
                return;
            }

            _loading = true;
            SetBusy(true);
            SetStatus(positive ? "Отправляем оценку хорошо..." : "Отправляем оценку плохо...");

            try
            {
                ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("TopicPage.ChangePostReputation");

                var values = new List<KeyValuePair<string, string>>();
                values.Add(new KeyValuePair<string, string>("act", "rep"));
                values.Add(new KeyValuePair<string, string>("p", postId));
                values.Add(new KeyValuePair<string, string>("mid", authorId));
                values.Add(new KeyValuePair<string, string>("type", positive ? "add" : "minus"));
                values.Add(new KeyValuePair<string, string>("message", positive ? "хорошо" : "плохо"));

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(ForumBaseUrl, UriKind.Absolute));
                PrepareForumRequest(request, BuildTopicUrl(_topicId, _start));
                request.Content = new HttpFormUrlEncodedContent(values);

                HttpResponseMessage response = await _httpClient.SendRequestAsync(request);
                string html = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("сервер вернул " + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString());

                string error = ExtractReputationError(html);
                if (!String.IsNullOrWhiteSpace(error))
                {
                    SetStatus(error);
                    return;
                }

                RemoveCachedTopicData(BuildCacheKey(_topicId, _start));
                SetStatus(positive ? "Оценка 'хорошо' отправлена." : "Оценка 'плохо' отправлена.");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось изменить репутацию: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private void PushHistory()
        {
            if (!String.IsNullOrWhiteSpace(_topicId))
                _history.Push(new TopicPageState { TopicId = _topicId, Start = _start });
        }

        private async Task LoadTopicAsync(bool forceReload)
        {
            if (_loading)
                return;

            _loading = true;
            SetBusy(true);
            SetStatus("");

            string cacheKey = BuildCacheKey(_topicId, _start);
            TopicData cached = GetCachedTopicData(cacheKey);
            if (cached != null && !forceReload)
                ApplyTopicData(cached, true);

            try
            {
                string url = BuildTopicUrl(_topicId, _start);
                string html = await _httpClient.GetStringAsync(new Uri(url, UriKind.Absolute));
                TopicData data = await Task.Run<TopicData>(delegate { return ParseTopicPage(html, _topicId, _start); });
                PutCachedTopicData(cacheKey, data);
                ApplyTopicData(data, false);

                if (data.Posts.Count == 0)
                    SetStatus("Тема загружена, но сообщения не найдены.");
            }
            catch (Exception ex)
            {
                if (cached != null)
                    ApplyTopicData(cached, true);
                else
                    _posts.Clear();

                SetStatus(cached == null ? "Не удалось загрузить тему: " + ex.Message : "Показан кэш. Обновление не удалось: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private void ApplyTopicData(TopicData data, bool fromCache)
        {
            if (data == null)
                return;

            string title = data.Title == null ? "" : data.Title.Trim();
            if (IsBadTitle(title))
                title = "";
            TitleTextBlock.Text = title;
            TitleTextBlock.Visibility = String.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;

            _forumId = String.IsNullOrWhiteSpace(data.ForumId) ? _forumId : data.ForumId;
            _authKey = data.AuthKey == null ? "" : data.AuthKey;
            _canReply = ForumAuthService.IsAuthorized && data.CanReply && !String.IsNullOrWhiteSpace(_authKey);

            _posts.Clear();
            foreach (TopicPostItem post in data.Posts)
                _posts.Add(post);

            _pages.Clear();
            foreach (PageNavigationItem page in data.Pages)
                _pages.Add(page);
            PageNavigationPanel.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            UpdateReplyVisibility();
        }

        private async Task OpenPostLinkAsync(TopicPostLink link)
        {
            if (link == null || _loading)
                return;

            string postId = OnlyDigits(link.PostId);
            if (String.IsNullOrWhiteSpace(postId))
                return;

            string topicId = String.IsNullOrWhiteSpace(link.TopicId) ? _topicId : link.TopicId;
            if (String.IsNullOrWhiteSpace(topicId))
                topicId = _topicId;

            bool sameTopic = String.Equals(topicId, _topicId, StringComparison.OrdinalIgnoreCase);
            if (sameTopic && await ScrollToPostAsync(postId))
                return;

            if (link.Start >= 0)
            {
                int targetStart = Math.Max(0, link.Start);
                if (!sameTopic || targetStart != _start)
                    PushHistory();

                _topicId = topicId;
                _start = targetStart;
                await LoadTopicAsync(false);

                if (await ScrollToPostAsync(postId))
                    return;
            }

            string requestUrl = BuildFindPostUrl(topicId, postId, link.Url);
            await LoadPostLinkUrlAsync(requestUrl, topicId, postId);
        }

        private async Task LoadPostLinkUrlAsync(string url, string fallbackTopicId, string targetPostId)
        {
            if (String.IsNullOrWhiteSpace(url) || String.IsNullOrWhiteSpace(targetPostId))
                return;

            _loading = true;
            SetBusy(true);
            SetStatus("Открываем сообщение...");

            try
            {
                ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("TopicPage.OpenPostLink");

                string requestUrl = NormalizeUrl(url);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(requestUrl, UriKind.Absolute));
                PrepareForumRequest(request, "https://4pda.to/forum/");
                HttpResponseMessage response = await _httpClient.SendRequestAsync(request);
                string html = await response.Content.ReadAsStringAsync();
                string responseUrl = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                    ? response.RequestMessage.RequestUri.AbsoluteUri
                    : requestUrl;

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("сервер вернул " + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString());

                string resolvedTopicId = FirstNonEmpty(ExtractTopicId(responseUrl), ExtractTopicId(html), fallbackTopicId, _topicId);
                int resolvedStart;
                if (!TryExtractStart(responseUrl, out resolvedStart))
                    resolvedStart = ResolveCurrentStartFromHtml(html, resolvedTopicId, _start);

                PushHistory();
                _topicId = resolvedTopicId;
                _start = Math.Max(0, resolvedStart);

                TopicData data = await Task.Run<TopicData>(delegate { return ParseTopicPage(html, _topicId, _start); });
                PutCachedTopicData(BuildCacheKey(_topicId, _start), data);
                ApplyTopicData(data, false);

                if (!await ScrollToPostAsync(targetPostId))
                    SetStatus("Сообщение загружено, но точку прокрутки найти не удалось.");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось открыть сообщение: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private async Task<bool> ScrollToPostAsync(string postId)
        {
            string id = OnlyDigits(postId);
            if (String.IsNullOrWhiteSpace(id))
                return false;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                TopicPostItem target = null;
                foreach (TopicPostItem post in _posts)
                {
                    if (post != null && OnlyDigits(post.PostId) == id)
                    {
                        target = post;
                        break;
                    }
                }

                if (target != null)
                {
                    PostsListView.ScrollIntoView(target);
                    await Task.Delay(180);
                    PostsListView.ScrollIntoView(target);
                    SetStatus("Открыто сообщение #" + id + ".");
                    return true;
                }

                await Task.Delay(120);
            }

            return false;
        }

        private async Task SendReplyAsync()
        {
            if (_loading)
                return;

            if (!ForumAuthService.IsAuthorized)
            {
                SetStatus("Для ответа надо войти в аккаунт.");
                return;
            }

            if (!_canReply || String.IsNullOrWhiteSpace(_authKey))
            {
                SetStatus("Сервер не отдал форму ответа. Обновите тему или проверьте, не закрыта ли она.");
                return;
            }

            string message = ReplyTextBox.Text == null ? "" : ReplyTextBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(message))
            {
                SetStatus("Введите текст комментария.");
                return;
            }

            _loading = true;
            SetBusy(true);
            SetStatus("Отправляем комментарий...");

            try
            {
                var values = new List<KeyValuePair<string, string>>();
                values.Add(new KeyValuePair<string, string>("act", "Post"));
                values.Add(new KeyValuePair<string, string>("CODE", "03"));
                values.Add(new KeyValuePair<string, string>("f", String.IsNullOrWhiteSpace(_forumId) ? "0" : _forumId));
                values.Add(new KeyValuePair<string, string>("t", _topicId));
                values.Add(new KeyValuePair<string, string>("auth_key", _authKey));
                values.Add(new KeyValuePair<string, string>("Post", message));
                values.Add(new KeyValuePair<string, string>("enablesig", "yes"));
                values.Add(new KeyValuePair<string, string>("enableemo", "yes"));
                values.Add(new KeyValuePair<string, string>("st", _start.ToString()));
                values.Add(new KeyValuePair<string, string>("removeattachid", "0"));
                values.Add(new KeyValuePair<string, string>("MAX_FILE_SIZE", "0"));
                values.Add(new KeyValuePair<string, string>("parent_id", "0"));
                values.Add(new KeyValuePair<string, string>("ed-0_wysiwyg_used", "0"));
                values.Add(new KeyValuePair<string, string>("editor_ids[]", "ed-0"));
                values.Add(new KeyValuePair<string, string>("iconid", "0"));
                values.Add(new KeyValuePair<string, string>("_upload_single_file", "1"));
                values.Add(new KeyValuePair<string, string>("file-list", ""));

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(ForumBaseUrl, UriKind.Absolute));
                PrepareForumRequest(request, BuildTopicUrl(_topicId, _start));
                request.Content = new HttpFormUrlEncodedContent(values);
                HttpResponseMessage response = await _httpClient.SendRequestAsync(request);
                string html = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("сервер вернул " + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString());
                TopicData data = await Task.Run<TopicData>(delegate { return ParseTopicPage(html, _topicId, _start); });

                ReplyTextBox.Text = "";
                RemoveCachedTopicData(BuildCacheKey(_topicId, _start));
                PutCachedTopicData(BuildCacheKey(_topicId, _start), data);
                ApplyTopicData(data, false);
                SetStatus(data.Posts.Count > 0 ? "Комментарий отправлен." : "Ответ отправлен, но страницу темы разобрать не удалось. Обновите тему.");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось отправить комментарий: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private async Task<string> ResolveDownloadUriAsync(string originalUrl)
        {
            string currentUrl = NormalizeUrl(originalUrl);
            if (String.IsNullOrWhiteSpace(currentUrl))
                throw new InvalidOperationException("Пустая ссылка скачивания.");

            for (int step = 1; step <= 6; step++)
            {
                ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("resolve step " + step.ToString());

                Uri uri = new Uri(currentUrl, UriKind.Absolute);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                PrepareForumRequest(request, "https://4pda.to/forum/");

                ForumDownloadLog("Resolve request #" + step.ToString() + " GET " + uri.AbsoluteUri);
                HttpResponseMessage response = await _resolveHttpClient.SendRequestAsync(request);
                string responseUri = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                    ? response.RequestMessage.RequestUri.AbsoluteUri
                    : currentUrl;
                string contentType = GetContentType(response);

                ForumDownloadLog("resolve #" + step.ToString() + " response status=" + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString() + " requestUri=" + SafeForLog(responseUri) + " contentType=" + SafeForLog(contentType) + " length=" + GetContentLengthText(response));

                if ((int)response.StatusCode == 403)
                {
                    string body = await TryReadStringAsync(response);
                    ForumDownloadLog("Resolve 403 body: " + SafeForLog(TrimForLog(CleanTextOneLine(body), 650)));
                    if (IsAntibotHtml(body))
                    {
                        ForumDownloadLog("ANTI-BOT 403 detected. Sent cookies: " + ForumCookieHelper.DescribeCookieHeader(ForumCookieHelper.GetCookieHeaderForUrl(uri)));
                        throw new ForumAntibotException("4PDA вернул антибот-проверку 403. Нужна cookie cf_clearance из WebView.");
                    }
                    throw new InvalidOperationException("сервер вернул 403 Forbidden при подготовке ссылки " + currentUrl);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await TryReadStringAsync(response);
                    ForumDownloadLog("Resolve error body: " + SafeForLog(TrimForLog(CleanTextOneLine(body), 650)));
                    throw new InvalidOperationException("сервер вернул " + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString());
                }

                bool looksLikeLandingPage = IsDownloadLandingUrl(currentUrl) || IsHtmlContentType(contentType);
                if (looksLikeLandingPage)
                {
                    string html = await TryReadStringAsync(response);
                    if (IsAntibotHtml(html))
                    {
                        ForumDownloadLog("ANTI-BOT html detected while resolve. Sent cookies: " + ForumCookieHelper.DescribeCookieHeader(ForumCookieHelper.GetCookieHeaderForUrl(uri)));
                        throw new ForumAntibotException("4PDA требует проверку браузером. Нужна cookie cf_clearance.");
                    }

                    string href = ExtractDownloadAnchorHref(html, responseUri);
                    if (!String.IsNullOrWhiteSpace(href))
                    {
                        ForumDownloadLog("Resolve exact download anchor selected " + SafeForLog(href));
                        return href;
                    }

                    string meta = ExtractMetaRefreshUrl(html, responseUri);
                    if (!String.IsNullOrWhiteSpace(meta) && !String.Equals(meta, currentUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        ForumDownloadLog("Resolve meta refresh " + SafeForLog(currentUrl) + " -> " + SafeForLog(meta));
                        currentUrl = meta;
                        continue;
                    }

                    if (IsHtmlContentType(contentType))
                    {
                        ForumDownloadLog("Resolve html without download anchor. Text=" + SafeForLog(TrimForLog(CleanTextOneLine(html), 900)));
                        throw new InvalidOperationException("страница скачивания открылась, но ссылка «Скачать» не найдена.");
                    }
                }

                if (!String.IsNullOrWhiteSpace(responseUri))
                    return responseUri;

                return currentUrl;
            }

            throw new InvalidOperationException("слишком много переходов при подготовке ссылки скачивания.");
        }

        private async Task<StorageFile> DownloadFileToTemporaryFileAsync(string resolvedUrl, string suggestedName)
        {
            string safeName = String.IsNullOrWhiteSpace(suggestedName) ? "download.bin" : suggestedName;
            safeName = Regex.Replace(safeName, @"[\\/:*?""<>|]", "_");
            safeName = safeName.Trim('.', ' ');
            if (String.IsNullOrWhiteSpace(safeName))
                safeName = "download.bin";

            string uniqueName = DateTime.Now.ToString("yyyyMMdd_HHmmss_") + safeName;
            StorageFile tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(uniqueName, CreationCollisionOption.GenerateUniqueName);

            ForumDownloadLog("Download to temp begin file=" + SafeForLog(tempFile.Name) + " url=" + SafeForLog(resolvedUrl));
            await DownloadFileToStorageFileAsync(resolvedUrl, tempFile, suggestedName);
            ForumDownloadLog("Download to temp complete file=" + SafeForLog(tempFile.Name));

            return tempFile;
        }

        private async Task CopyStorageFileToStorageFileAsync(StorageFile sourceFile, StorageFile targetFile, string displayName)
        {
            ulong total = 0;
            try
            {
                var props = await sourceFile.GetBasicPropertiesAsync();
                total = props.Size;
            }
            catch
            {
            }

            ulong? totalBytes = total > 0 ? (ulong?)total : null;
            ulong copied = 0;
            UpdateDownloadProgress(displayName, copied, totalBytes);

            IRandomAccessStream sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read);
            try
            {
                IRandomAccessStream targetStream = await targetFile.OpenAsync(FileAccessMode.ReadWrite);
                try
                {
                    targetStream.Size = 0;
                    IInputStream input = sourceStream.GetInputStreamAt(0);
                    IOutputStream output = targetStream.GetOutputStreamAt(0);
                    try
                    {
                        while (true)
                        {
                            IBuffer buffer = new Windows.Storage.Streams.Buffer(DownloadBufferSize);
                            IBuffer readBuffer = await input.ReadAsync(buffer, DownloadBufferSize, InputStreamOptions.None);
                            if (readBuffer == null || readBuffer.Length == 0)
                                break;

                            await output.WriteAsync(readBuffer);
                            copied += readBuffer.Length;
                            UpdateDownloadProgress(displayName, copied, totalBytes);
                        }

                        await output.FlushAsync();
                    }
                    finally
                    {
                        input.Dispose();
                        output.Dispose();
                    }
                }
                finally
                {
                    targetStream.Dispose();
                }
            }
            finally
            {
                sourceStream.Dispose();
            }

            ForumDownloadLog("Save copy completed bytes=" + copied.ToString());
        }

        private async Task DownloadFileToStorageFileAsync(string url, StorageFile targetFile, string displayName)
        {
            string currentUrl = NormalizeUrl(url);
            if (String.IsNullOrWhiteSpace(currentUrl))
                throw new InvalidOperationException("Пустая ссылка скачивания.");

            for (int step = 1; step <= 4; step++)
            {
                ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("download step " + step.ToString());

                Uri uri = new Uri(currentUrl, UriKind.Absolute);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                PrepareForumRequest(request, "https://4pda.to/forum/");

                ForumDownloadLog("Download request #" + step.ToString() + " GET " + uri.AbsoluteUri);
                HttpResponseMessage response = await _downloadHttpClient.SendRequestAsync(request);
                string responseUri = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                    ? response.RequestMessage.RequestUri.AbsoluteUri
                    : currentUrl;
                string contentType = GetContentType(response);

                ForumDownloadLog("download #" + step.ToString() + " response status=" + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString() + " requestUri=" + SafeForLog(responseUri) + " contentType=" + SafeForLog(contentType) + " length=" + GetContentLengthText(response));

                if ((int)response.StatusCode == 403)
                {
                    string body = await TryReadStringAsync(response);
                    ForumDownloadLog("Download 403 body: " + SafeForLog(TrimForLog(CleanTextOneLine(body), 650)));
                    if (IsAntibotHtml(body))
                        throw new ForumAntibotException("4PDA вернул антибот-проверку 403. Нужна cookie cf_clearance из WebView.");

                    throw new InvalidOperationException("сервер вернул 403 Forbidden при скачивании файла.");
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("сервер вернул " + ((int)response.StatusCode).ToString() + " " + response.StatusCode.ToString() + " при скачивании файла.");

                if (IsHtmlContentType(contentType) || IsDownloadLandingUrl(responseUri))
                {
                    string html = await TryReadStringAsync(response);
                    if (IsAntibotHtml(html))
                        throw new ForumAntibotException("4PDA требует проверку браузером. Нужна cookie cf_clearance.");

                    string href = ExtractDownloadAnchorHref(html, responseUri);
                    if (!String.IsNullOrWhiteSpace(href) && !String.Equals(href, currentUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        ForumDownloadLog("Download html intermediate anchor " + SafeForLog(currentUrl) + " -> " + SafeForLog(href));
                        currentUrl = href;
                        continue;
                    }

                    ForumDownloadLog("Download got html instead of file: " + SafeForLog(TrimForLog(CleanTextOneLine(html), 900)));
                    throw new InvalidOperationException("сервер вернул HTML-страницу вместо файла.");
                }

                ulong? totalBytes = GetContentLength(response);
                await CopyResponseToFileAsync(response, targetFile, displayName, totalBytes);
                return;
            }

            throw new InvalidOperationException("слишком много переходов при скачивании файла.");
        }

        private async Task CopyResponseToFileAsync(HttpResponseMessage response, StorageFile targetFile, string displayName, ulong? totalBytes)
        {
            ulong downloaded = 0;
            UpdateDownloadProgress(displayName, downloaded, totalBytes);

            IInputStream input = await response.Content.ReadAsInputStreamAsync();
            try
            {
                IRandomAccessStream outputStream = await targetFile.OpenAsync(FileAccessMode.ReadWrite);
                try
                {
                    outputStream.Size = 0;
                    IOutputStream output = outputStream.GetOutputStreamAt(0);
                    try
                    {
                        while (true)
                        {
                            IBuffer buffer = new Windows.Storage.Streams.Buffer(DownloadBufferSize);
                            IBuffer readBuffer = await input.ReadAsync(buffer, DownloadBufferSize, InputStreamOptions.None);
                            if (readBuffer == null || readBuffer.Length == 0)
                                break;

                            await output.WriteAsync(readBuffer);
                            downloaded += readBuffer.Length;
                            UpdateDownloadProgress(displayName, downloaded, totalBytes);
                        }

                        await output.FlushAsync();
                    }
                    finally
                    {
                        output.Dispose();
                    }
                }
                finally
                {
                    outputStream.Dispose();
                }
            }
            finally
            {
                input.Dispose();
            }

            ForumDownloadLog("Download completed bytes=" + downloaded.ToString());
        }

        private static string BuildTopicUrl(string topicId, int start)
        {
            string id = String.IsNullOrWhiteSpace(topicId) ? "1" : topicId;
            return ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id) + "&st=" + Math.Max(0, start).ToString();
        }

        private static string FirstMatch(string html, string pattern)
        {
            Match match = Regex.Match(html ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? DecodeEntities(match.Groups["v"].Value) : "";
        }

        private static string ExtractReputationError(string html)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(source))
                return "";

            string title = CleanTextOneLine(FirstMatch(source, @"<title[^>]*>(?<v>[\s\S]*?)</title>"));
            if (Regex.IsMatch(source, @"(?:need_login|Войдите|Авториз|Необходимо\s+авториз)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                return "Для оценки сообщения надо войти в аккаунт.";

            if (!String.IsNullOrWhiteSpace(title) && Regex.IsMatch(title, @"ошиб|error", RegexOptions.IgnoreCase))
            {
                string message = FirstMatch(source, @"<div[^>]*class\s*=\s*['"" ][^'"" >]*(?:content|message|error|postcolor)[^'"" >]*['"" ][^>]*>(?<v>[\s\S]*?)</div>");
                message = CleanTextOneLine(message);
                if (!String.IsNullOrWhiteSpace(message))
                    return message;
                return title;
            }

            string plain = CleanTextOneLine(HtmlToText(source));
            if (Regex.IsMatch(plain, @"(?:Вы\s+не\s+можете|нельзя|ошибка|error|не\s+имеете\s+прав)", RegexOptions.IgnoreCase) && plain.Length <= 240)
                return plain;

            return "";
        }

        private void PrepareForumRequest(HttpRequestMessage request, string referer)
        {
            ForumCookieHelper.ApplyDefaultHeaders(request);
            if (!String.IsNullOrWhiteSpace(referer))
                ForumCookieHelper.TryAppendHeader(request, "Referer", referer);

            string cookieHeader = ForumCookieHelper.GetCookieHeaderForUrl(request.RequestUri);
            if (!String.IsNullOrWhiteSpace(cookieHeader))
                ForumCookieHelper.TryAppendHeader(request, "Cookie", cookieHeader);

            ForumDownloadLog("PrepareForumRequest url=" + request.RequestUri.AbsoluteUri + " cookies=" + ForumCookieHelper.DescribeCookieHeader(cookieHeader));
        }

        private void OpenClearancePage(string returnUrl)
        {
            try
            {
                string url = String.IsNullOrWhiteSpace(returnUrl) ? "https://4pda.to/forum/" : NormalizeUrl(returnUrl);
                ForumDownloadLog("Open ForumClearancePage url=" + SafeForLog(url));
                if (Frame != null)
                    Frame.Navigate(typeof(ForumClearancePage), url);
            }
            catch (Exception ex)
            {
                ForumDownloadLog("OpenClearancePage failed " + ex.ToString());
            }
        }

        private static bool IsOurFileSaveContinuation(FileSavePickerContinuationEventArgs args)
        {
            if (args == null || args.ContinuationData == null)
                return false;
            if (!args.ContinuationData.ContainsKey(FileSaveContinuationOperationKey))
                return false;
            object value = args.ContinuationData[FileSaveContinuationOperationKey];
            return value != null && String.Equals(value.ToString(), FileSaveContinuationOperation, StringComparison.Ordinal);
        }

        private static string GetContinuationString(FileSavePickerContinuationEventArgs args, string key)
        {
            if (args == null || args.ContinuationData == null || String.IsNullOrWhiteSpace(key))
                return "";
            if (!args.ContinuationData.ContainsKey(key))
                return "";
            object value = args.ContinuationData[key];
            return value == null ? "" : value.ToString();
        }

        private void ShowDownloadOverlay(string fileName)
        {
            try
            {
                ForumDownloadLog("ShowDownloadOverlay fileName=" + SafeForLog(fileName));
                TopicProgressRing.IsActive = false;
                TopicProgressRing.Visibility = Visibility.Collapsed;
                SetBusy(false);
                DownloadOverlay.Visibility = Visibility.Visible;
                DownloadTitleTextBlock.Text = "Загрузка файла";
                DownloadProgressBar.IsIndeterminate = true;
                DownloadProgressBar.Value = 0;
                DownloadProgressTextBlock.Text = (String.IsNullOrWhiteSpace(fileName) ? "Файл" : fileName) + "\nПодключаемся...";
                DownloadOverlay.UpdateLayout();
            }
            catch (Exception ex)
            {
                ForumDownloadLog("ShowDownloadOverlay failed: " + ex.Message);
            }
        }

        private void UpdateDownloadProgress(string fileName, ulong downloaded, ulong? totalBytes)
        {
            try
            {
                DownloadTitleTextBlock.Text = "Загрузка файла";
                string name = String.IsNullOrWhiteSpace(fileName) ? "Файл" : fileName;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double percent = downloaded * 100.0 / totalBytes.Value;
                    if (percent < 0) percent = 0;
                    if (percent > 100) percent = 100;
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = percent;
                    DownloadProgressTextBlock.Text = name + "\nЗагружено " + FormatBytes(downloaded) + " из " + FormatBytes(totalBytes.Value) + " (" + ((int)Math.Round(percent)).ToString() + "%)";
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressTextBlock.Text = name + "\nЗагружено " + FormatBytes(downloaded);
                }
            }
            catch (Exception ex)
            {
                ForumDownloadLog("UpdateDownloadProgress failed: " + ex.Message);
            }
        }

        private void HideDownloadOverlay()
        {
            try
            {
                DownloadOverlay.Visibility = Visibility.Collapsed;
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = 0;
                DownloadProgressTextBlock.Text = "";
            }
            catch
            {
            }
        }

        private static string ExtractDownloadAnchorHref(string html, string baseUrl)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            MatchCollection links = Regex.Matches(html, @"<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string fallback = "";

            foreach (Match link in links)
            {
                string attrs = link.Groups["attrs"].Value;
                string text = CleanTextOneLine(link.Groups["text"].Value);
                string href = ExtractAttribute(attrs, "href");
                if (String.IsNullOrWhiteSpace(href))
                    continue;

                string absolute = NormalizeUrlAgainstBase(href, baseUrl);
                string check = (text + " " + href).ToLowerInvariant();
                bool hasDownloadWord = check.IndexOf("скач") >= 0 || check.IndexOf("download") >= 0;
                bool looksLikeAttach = href.IndexOf("act=attach", StringComparison.OrdinalIgnoreCase) >= 0 || href.IndexOf("/forum/dl/", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hasDownloadWord)
                {
                    ForumDownloadLogStatic("Resolve exact download anchor text=" + SafeForLog(text) + " href=" + SafeForLog(absolute));
                    return absolute;
                }

                if (looksLikeAttach && String.IsNullOrWhiteSpace(fallback))
                    fallback = absolute;
            }

            if (!String.IsNullOrWhiteSpace(fallback))
                ForumDownloadLogStatic("Resolve fallback attach anchor href=" + SafeForLog(fallback));
            return fallback;
        }

        private static string ExtractMetaRefreshUrl(string html, string baseUrl)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            Match match = Regex.Match(html, @"<meta\b[^>]*http-equiv\s*=\s*['""]?refresh['"" ]?[^>]*content\s*=\s*(?<q>['""])(?<content>[\s\S]*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return "";

            string content = DecodeEntities(match.Groups["content"].Value);
            Match urlMatch = Regex.Match(content, @"url\s*=\s*(?<url>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!urlMatch.Success)
                return "";

            return NormalizeUrlAgainstBase(urlMatch.Groups["url"].Value.Trim(' ', '\'', '"'), baseUrl);
        }

        private static string NormalizeUrlAgainstBase(string url, string baseUrl)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            string value = DecodeEntities(url.Trim());
            if (value.StartsWith("//"))
                return "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("/"))
                return "https://4pda.to" + value;
            if (value.StartsWith("?"))
            {
                string root = String.IsNullOrWhiteSpace(baseUrl) ? ForumBaseUrl : baseUrl;
                int q = root.IndexOf('?');
                if (q >= 0)
                    root = root.Substring(0, q);
                return root + value;
            }
            if (value.StartsWith("index.php", StringComparison.OrdinalIgnoreCase))
                return "https://4pda.to/forum/" + value;

            try
            {
                Uri baseUri = new Uri(String.IsNullOrWhiteSpace(baseUrl) ? "https://4pda.to/forum/" : baseUrl, UriKind.Absolute);
                return new Uri(baseUri, value).AbsoluteUri;
            }
            catch
            {
                return NormalizeUrl(value);
            }
        }

        private static bool IsDownloadLandingUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return false;
            return url.IndexOf("/forum/dl/post/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("/forum/dl/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHtmlContentType(string contentType)
        {
            return !String.IsNullOrWhiteSpace(contentType) && contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAntibotHtml(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;
            string text = CleanTextOneLine(html).ToLowerInvariant();
            return text.IndexOf("enable javascript and cookies") >= 0 ||
                   text.IndexOf("убедиться, что вы человек") >= 0 ||
                   text.IndexOf("кто здесь") >= 0 ||
                   text.IndexOf("cf_clearance") >= 0;
        }

        private static string GetContentType(HttpResponseMessage response)
        {
            try
            {
                if (response != null && response.Content != null && response.Content.Headers != null && response.Content.Headers.ContentType != null)
                    return response.Content.Headers.ContentType.ToString();
            }
            catch
            {
            }
            return "";
        }

        private static ulong? GetContentLength(HttpResponseMessage response)
        {
            try
            {
                if (response != null && response.Content != null && response.Content.Headers != null)
                    return response.Content.Headers.ContentLength;
            }
            catch
            {
            }
            return null;
        }

        private static string GetContentLengthText(HttpResponseMessage response)
        {
            ulong? length = GetContentLength(response);
            return length.HasValue ? length.Value.ToString() : "";
        }

        private static async Task<string> TryReadStringAsync(HttpResponseMessage response)
        {
            try
            {
                if (response == null || response.Content == null)
                    return "";
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return "<read failed: " + ex.Message + ">";
            }
        }

        private static string GetSuggestedDownloadFileName(TopicContentItem file)
        {
            string name = "";
            if (file != null)
                name = FirstNonEmpty(file.FileTitle, FileNameFromUrl(file.Url), "файл.bin");

            name = DecodeEntities(name ?? "").Trim();
            name = Regex.Replace(name, @"[\\/:*?""<>|]", "_");
            name = name.Trim('.', ' ');

            if (String.IsNullOrWhiteSpace(name))
                name = "файл.bin";

            if (String.IsNullOrWhiteSpace(System.IO.Path.GetExtension(name)))
                name += ".bin";

            return name;
        }

        private static string GetSafeFileExtension(string fileName)
        {
            string extension = "";
            try
            {
                extension = System.IO.Path.GetExtension(fileName);
            }
            catch
            {
            }

            if (String.IsNullOrWhiteSpace(extension) || extension.Length < 2 || extension.Length > 12)
                extension = ".bin";
            if (!extension.StartsWith("."))
                extension = "." + extension;
            return extension.ToLowerInvariant();
        }

        private static string FormatBytes(ulong bytes)
        {
            double value = bytes;
            string[] units = new string[] { "Б", "КБ", "МБ", "ГБ" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.00") + " " + units[unit];
        }

        private static string TrimForLog(string value, int maxLength)
        {
            if (String.IsNullOrEmpty(value))
                return "";
            if (value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
        }

        private static string SafeForLog(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static void ForumDownloadLogStatic(string message)
        {
            Debug.WriteLine("[4PDA.Download] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + message);
        }

        private void ForumDownloadLog(string message)
        {
            ForumDownloadLogStatic(message);
        }

        private sealed class ForumAntibotException : Exception
        {
            public ForumAntibotException(string message) : base(message)
            {
            }
        }

        private void SetBusy(bool busy)
        {
            bool showRing = busy && DownloadOverlay.Visibility != Visibility.Visible;
            TopicProgressRing.IsActive = showRing;
            TopicProgressRing.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;
            RefreshAppBarButton.IsEnabled = !busy;
            SendAppBarButton.IsEnabled = !busy && ForumAuthService.IsAuthorized;
            SendReplyButton.IsEnabled = !busy;
            ReplyTextBox.IsEnabled = !busy;
        }

        private void SetStatus(string text)
        {
            StatusTextBlock.Text = text;
            StatusTextBlock.Visibility = String.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateReplyVisibility()
        {
            bool show = ForumAuthService.IsAuthorized && _canReply;
            ReplyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SendAppBarButton.IsEnabled = !_loading && ForumAuthService.IsAuthorized;
        }

        private static TopicData ParseTopicPage(string html, string topicId, int currentStart)
        {
            TopicData data = new TopicData();
            data.Title = ParseTopicTitle(html, topicId);
            data.ForumId = ParseForumId(html);
            data.AuthKey = ParseAuthKey(html);
            data.CanReply = ParseCanReply(html);
            data.Pages = ParseTopicPagination(html, topicId, currentStart);

            List<TopicPostItem> posts = ParsePosts(html);
            foreach (TopicPostItem post in posts)
                data.AddUniquePost(post);

            return data;
        }

        private static List<TopicPostItem> ParsePosts(string html)
        {
            var result = new List<TopicPostItem>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            List<HtmlBlock> bodyBlocks = FindMessageBodyBlocks(html);
            var used = new HashSet<string>();

            foreach (HtmlBlock body in bodyBlocks)
            {
                string key = body.Start.ToString();
                if (used.Contains(key))
                    continue;
                used.Add(key);

                int beforeStart = Math.Max(0, body.Start - 6500);
                string before = html.Substring(beforeStart, body.Start - beforeStart);
                string postContext = BuildPostContext(html, body);
                TopicPostItem item = ParsePostFromBody(body.Html, before, postContext);
                if (item == null)
                    continue;
                if (item.Parts.Count == 0 && String.IsNullOrWhiteSpace(item.EditedText))
                    continue;
                result.Add(item);
            }

            return DeduplicatePosts(result);
        }

        private static TopicPostItem ParsePostFromBody(string bodyHtml, string beforeHtml, string postHtml)
        {
            if (String.IsNullOrWhiteSpace(bodyHtml))
                return null;

            string working = bodyHtml;
            string edited = ExtractEditedText(ref working);
            working = RemoveScriptsAndComments(working);

            string postHeader = GetPostHeaderOnly(postHtml);
            TopicPostItem item = new TopicPostItem();
            item.PostId = ExtractCurrentPostId(beforeHtml, postHeader, postHtml);
            item.AuthorId = ExtractAuthorId(beforeHtml, postHeader, postHtml);
            item.Author = ExtractAuthor(beforeHtml, postHeader, postHtml);
            item.Date = FirstNonEmpty(ExtractPostDate(postHeader), ExtractPostDate(postHtml));
            item.Number = FirstNonEmpty(ExtractPostNumber(postHeader), ExtractPostNumber(postHtml));
            item.EditedText = edited;

            if (String.IsNullOrWhiteSpace(item.Author))
                item.Author = "сообщение";

            List<TopicContentItem> parts = ParseOrderedContent(working, true);
            foreach (TopicContentItem part in parts)
                item.Parts.Add(part);

            return item;
        }

        private static List<TopicPostItem> DeduplicatePosts(List<TopicPostItem> source)
        {
            var result = new List<TopicPostItem>();
            var used = new HashSet<string>();
            foreach (TopicPostItem post in source)
            {
                string key = !String.IsNullOrWhiteSpace(post.PostId) ? post.PostId : (post.Author + "|" + post.Date + "|" + post.PlainText);
                if (used.Contains(key))
                    continue;
                used.Add(key);
                result.Add(post);
            }
            return result;
        }

        private static List<TopicContentItem> ParseOrderedContent(string html, bool allowSpoilers)
        {
            var result = new List<TopicContentItem>();
            var usedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string source = RemoveScriptsAndComments(html ?? "");
            int position = 0;

            while (position < source.Length)
            {
                ContentCandidate candidate = FindNextContentCandidate(source, position, allowSpoilers);
                if (candidate == null)
                {
                    AddTextPart(result, source.Substring(position));
                    break;
                }

                if (candidate.Start > position)
                    AddTextPart(result, source.Substring(position, candidate.Start - position));

                if (candidate.Kind == TopicContentKind.Spoiler)
                {
                    TopicContentItem spoiler = BuildSpoilerItem(candidate.Html);
                    if (spoiler != null)
                        result.Add(spoiler);
                }
                else if (candidate.Kind == TopicContentKind.Quote)
                {
                    TopicContentItem quote = BuildQuoteItem(candidate.Html);
                    if (quote != null)
                        result.Add(quote);
                }
                else if (candidate.Kind == TopicContentKind.Image)
                {
                    TopicContentItem image = BuildImageItem(candidate.Html);
                    if (image != null && !usedMedia.Contains(image.ImageUrl))
                    {
                        usedMedia.Add(image.ImageUrl);
                        result.Add(image);
                    }
                }
                else if (candidate.Kind == TopicContentKind.File)
                {
                    TopicContentItem file = BuildFileItem(candidate.Html, candidate.TailText);
                    if (file != null && !usedMedia.Contains(file.Url))
                    {
                        usedMedia.Add(file.Url);
                        result.Add(file);
                    }
                }

                position = Math.Max(position + 1, candidate.End);
            }

            return MergeTextParts(result);
        }

        private static ContentCandidate FindNextContentCandidate(string html, int position, bool allowSpoilers)
        {
            Regex tagRegex = new Regex(@"<(div|table|a|img)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection matches = tagRegex.Matches(html ?? "", position);
            foreach (Match match in matches)
            {
                string open = match.Value;
                string tag = Regex.Match(open, @"^<(?<tag>\w+)", RegexOptions.IgnoreCase).Groups["tag"].Value.ToLowerInvariant();
                string attrs = open;
                string cls = ExtractAttribute(attrs, "class");
                string id = ExtractAttribute(attrs, "id");

                if (tag == "div")
                {
                    if (allowSpoilers && IsClassContains(cls, "spoil"))
                    {
                        int end = FindElementEnd(html, "div", match.Index, match.Index + match.Length);
                        if (end > match.Index)
                            return new ContentCandidate { Kind = TopicContentKind.Spoiler, Start = match.Index, End = end, Html = html.Substring(match.Index, end - match.Index) };
                    }

                    if (IsClassContains(cls, "quote"))
                    {
                        int end = FindElementEnd(html, "div", match.Index, match.Index + match.Length);
                        if (end > match.Index)
                            return new ContentCandidate { Kind = TopicContentKind.Quote, Start = match.Index, End = end, Html = html.Substring(match.Index, end - match.Index) };
                    }
                }
                else if (tag == "table")
                {
                    if (id.IndexOf("ipb-attach-table", StringComparison.OrdinalIgnoreCase) >= 0 || IsClassContains(cls, "attach"))
                    {
                        int end = FindElementEnd(html, "table", match.Index, match.Index + match.Length);
                        if (end > match.Index)
                            return new ContentCandidate { Kind = TopicContentKind.Image, Start = match.Index, End = end, Html = html.Substring(match.Index, end - match.Index) };
                    }
                }
                else if (tag == "a")
                {
                    int end = FindElementEnd(html, "a", match.Index, match.Index + match.Length);
                    if (end <= match.Index)
                        continue;

                    string block = html.Substring(match.Index, end - match.Index);
                    string href = NormalizeUrl(ExtractAttribute(attrs, "href"));
                    bool hasImage = Regex.IsMatch(block, @"<img\b", RegexOptions.IgnoreCase);

                    if (hasImage && IsSmileImageBlock(block))
                        continue;

                    if (IsSnapbackImageUrl(href) && !HasUsableImage(block))
                        continue;

                    if (IsFileAttachmentBlock(href, attrs, block))
                    {
                        string tail;
                        int extendedEnd = ExtendFileTail(html, end, out tail);
                        return new ContentCandidate { Kind = TopicContentKind.File, Start = match.Index, End = extendedEnd, Html = block, TailText = tail };
                    }

                    if (hasImage && IsImageLikeBlock(block))
                        return new ContentCandidate { Kind = TopicContentKind.Image, Start = match.Index, End = end, Html = block };

                    if (IsAttachmentUrl(href, attrs, block))
                    {
                        string tail;
                        int extendedEnd = ExtendFileTail(html, end, out tail);
                        return new ContentCandidate { Kind = TopicContentKind.File, Start = match.Index, End = extendedEnd, Html = block, TailText = tail };
                    }
                }
                else if (tag == "img")
                {
                    if (IsSmileImageAttrs(attrs))
                        continue;

                    string src = FirstNonEmpty(ExtractAttribute(attrs, "data-src"), ExtractAttribute(attrs, "data-lazy-src"), ExtractAttribute(attrs, "data-original"), ExtractAttribute(attrs, "src"));
                    string url = NormalizeUrl(src);
                    if (!String.IsNullOrWhiteSpace(url) && !IsIgnoredImage(url, attrs))
                    {
                        return new ContentCandidate { Kind = TopicContentKind.Image, Start = match.Index, End = match.Index + match.Length, Html = match.Value };
                    }
                }
            }
            return null;
        }

        private static TopicContentItem BuildSpoilerItem(string html)
        {
            string title = ExtractBlockTitle(html);
            if (String.IsNullOrWhiteSpace(title))
                title = "Спойлер";
            title = NormalizePlainText(title);
            if (title.Length > 90)
                title = "Спойлер";

            string body = ExtractBlockBody(html);
            if (String.IsNullOrWhiteSpace(body))
                body = RemoveSpoilerHeader(html);

            TopicContentItem item = new TopicContentItem();
            item.Kind = TopicContentKind.Spoiler;
            item.Header = title;
            List<TopicContentItem> children = ParseOrderedContent(body, false);
            foreach (TopicContentItem child in children)
                item.Children.Add(child);
            if (item.Children.Count == 0)
                AddTextPart(item.Children, body);
            return item;
        }

        private static TopicContentItem BuildQuoteItem(string html)
        {
            string title = ExtractBlockTitle(html);
            string body = ExtractBlockBody(html);
            if (String.IsNullOrWhiteSpace(body))
                body = RemoveQuoteHeader(html);

            TopicContentItem item = new TopicContentItem();
            item.Kind = TopicContentKind.Quote;
            item.Header = String.IsNullOrWhiteSpace(title) ? "цитата" : NormalizePlainText(title);
            item.Text = HtmlToText(body, true);
            if (String.IsNullOrWhiteSpace(item.Text))
                return null;
            return item;
        }

        private static TopicContentItem BuildImageItem(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return null;

            string fullUrl = "";
            Match link = Regex.Match(html, @"<a\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (link.Success)
                fullUrl = NormalizeUrl(ExtractAttribute(link.Groups["attrs"].Value, "href"));

            if (IsSnapbackImageUrl(fullUrl))
                return null;

            string imageUrl = "";
            string caption = "";
            MatchCollection imgs = Regex.Matches(html, @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match img in imgs)
            {
                string attrs = img.Groups["attrs"].Value;
                string src = FirstNonEmpty(ExtractAttribute(attrs, "data-src"), ExtractAttribute(attrs, "data-lazy-src"), ExtractAttribute(attrs, "data-original"), ExtractAttribute(attrs, "src"));
                string url = NormalizeUrl(src);
                if (String.IsNullOrWhiteSpace(url) || IsIgnoredImage(url, attrs) || IsSnapbackImageUrl(url))
                    continue;

                imageUrl = url;
                caption = FirstNonEmpty(CleanTextOneLine(ExtractAttribute(attrs, "title")), CleanTextOneLine(ExtractAttribute(attrs, "alt")));
                break;
            }

            if (String.IsNullOrWhiteSpace(imageUrl))
                return null;

            if (IsSnapbackImageUrl(imageUrl) || IsSnapbackImageUrl(fullUrl))
                return null;

            if (String.IsNullOrWhiteSpace(fullUrl))
                fullUrl = imageUrl;
            if (!IsImageUrl(imageUrl) && IsImageUrl(fullUrl))
                imageUrl = fullUrl;

            if (String.IsNullOrWhiteSpace(caption))
            {
                Match size = Regex.Match(html, @">\s*(?<cap>\d+\s*x\s*\d+\s*\([^<]{1,40}\))\s*<", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (size.Success)
                    caption = CleanTextOneLine(size.Groups["cap"].Value);
            }

            TopicContentItem item = new TopicContentItem();
            item.Kind = TopicContentKind.Image;
            item.Url = fullUrl;
            item.ImageUrl = imageUrl;
            item.FileTitle = IsServiceCaption(caption) ? "" : caption;
            return item;
        }

        private static TopicContentItem BuildFileItem(string html, string tailText)
        {
            if (String.IsNullOrWhiteSpace(html))
                return null;

            Match link = Regex.Match(html, @"<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!link.Success)
                return null;

            string attrs = link.Groups["attrs"].Value;
            string url = NormalizeUrl(ExtractAttribute(attrs, "href"));
            if (String.IsNullOrWhiteSpace(url))
                return null;

            string textHtml = Regex.Replace(link.Groups["text"].Value, @"<img\b[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string name = HtmlToText(textHtml);
            if (String.IsNullOrWhiteSpace(name) || IsServiceText(name))
                name = FirstNonEmpty(CleanTextOneLine(ExtractAttribute(attrs, "title")), FileNameFromUrl(url));
            name = Regex.Replace(name, @"^скачать\s*", "", RegexOptions.IgnoreCase).Trim();

            string size = NormalizePlainText(tailText);
            if (!String.IsNullOrWhiteSpace(size))
                name = name + " " + size;

            string icon = FileIconGif;
            Match img = Regex.Match(link.Groups["text"].Value, @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (img.Success)
            {
                string imgAttrs = img.Groups["attrs"].Value;
                string src = FirstNonEmpty(ExtractAttribute(imgAttrs, "data-src"), ExtractAttribute(imgAttrs, "src"));
                string srcUrl = NormalizeUrl(src);
                if (IsFileIcon(srcUrl))
                    icon = srcUrl;
            }

            TopicContentItem item = new TopicContentItem();
            item.Kind = TopicContentKind.File;
            item.Url = url;
            item.FileIconUrl = icon;
            item.FileTitle = name;
            return item;
        }

        private static void AddTextPart(ICollection<TopicContentItem> target, string html)
        {
            string text = HtmlToText(html, true);
            if (String.IsNullOrWhiteSpace(text))
                return;

            TopicContentItem item = new TopicContentItem();
            item.Kind = TopicContentKind.Text;
            item.Text = text;
            target.Add(item);
        }

        private static List<TopicContentItem> MergeTextParts(List<TopicContentItem> source)
        {
            var result = new List<TopicContentItem>();
            foreach (TopicContentItem item in source)
            {
                if (item == null)
                    continue;

                if (item.Kind == TopicContentKind.Text && result.Count > 0 && result[result.Count - 1].Kind == TopicContentKind.Text)
                {
                    result[result.Count - 1].Text = NormalizePlainTextKeepLines(result[result.Count - 1].Text + "\n" + item.Text);
                }
                else
                {
                    result.Add(item);
                }
            }
            return result;
        }

        private static List<HtmlBlock> FindMessageBodyBlocks(string html)
        {
            var blocks = new List<HtmlBlock>();

            AddBlocks(blocks, FindBodyBlocksInsidePostContainers(html));

            AddBlocks(blocks, FindBlocksByClass(html, "div", MessageBodyClassNames()));
            AddBlocks(blocks, FindBlocksByClass(html, "td", MessageBodyClassNames()));

            return blocks.OrderBy(b => b.Start).ToList();
        }

        private static string[] MessageBodyClassNames()
        {
            return new string[]
            {
                "postcolor", "post_body", "post-content", "post_content", "post_text", "postbody",
                "message-content", "message_content", "entry-content", "entry_content", "post-entry", "post_entry"
            };
        }

        private static List<HtmlBlock> FindBodyBlocksInsidePostContainers(string html)
        {
            var result = new List<HtmlBlock>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            Regex tagRegex = new Regex(@"<(div|table|tr|li)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection matches = tagRegex.Matches(html);
            foreach (Match match in matches)
            {
                string open = match.Value;
                if (!IsPostContainerOpenTag(open))
                    continue;

                string tag = Regex.Match(open, @"^<(?<tag>\w+)", RegexOptions.IgnoreCase).Groups["tag"].Value.ToLowerInvariant();
                int end = FindElementEnd(html, tag, match.Index, match.Index + match.Length);
                if (end <= match.Index)
                    continue;

                string postHtml = html.Substring(match.Index, end - match.Index);
                HtmlBlock body = FindBestBodyBlockInPost(postHtml);
                if (body == null)
                    continue;

                body.Start += match.Index;
                body.End += match.Index;
                body.PostHtml = postHtml;
                result.Add(body);
            }

            return result;
        }

        private static bool IsPostContainerOpenTag(string openTag)
        {
            string tag = openTag ?? "";
            if (String.IsNullOrWhiteSpace(tag))
                return false;

            string id = ExtractAttribute(tag, "id");
            string name = ExtractAttribute(tag, "name");
            string cls = ExtractAttribute(tag, "class");

            if (Regex.IsMatch(id + " " + name, @"(?:^|[-_ ])(?:post|entry|msg|message)[-_]?\d+", RegexOptions.IgnoreCase))
                return true;

            if (String.IsNullOrWhiteSpace(cls))
                return false;

            if (IsClassContains(cls, "postcolor") || IsClassContains(cls, "post_body") || IsClassContains(cls, "post-content") || IsClassContains(cls, "entry-content"))
                return false;

            return IsClassContains(cls, "post_block") || IsClassContains(cls, "post-block") ||
                   IsClassContains(cls, "post_container") || IsClassContains(cls, "post-container") ||
                   IsClassContains(cls, "postrow") || IsClassContains(cls, "topic-post") ||
                   IsClassContains(cls, "hentry") || Regex.IsMatch(cls, @"(^|\s)post(\s|$)", RegexOptions.IgnoreCase);
        }

        private static HtmlBlock FindBestBodyBlockInPost(string postHtml)
        {
            var candidates = new List<HtmlBlock>();
            AddBlocks(candidates, FindBlocksByClass(postHtml, "div", MessageBodyClassNames()));
            AddBlocks(candidates, FindBlocksByClass(postHtml, "td", MessageBodyClassNames()));
            if (candidates.Count == 0)
                return null;

            candidates = candidates.OrderBy(b => b.Start).ToList();
            foreach (HtmlBlock candidate in candidates)
            {
                string text = NormalizePlainText(HtmlToText(candidate.Html));
                if (!String.IsNullOrWhiteSpace(text) || Regex.IsMatch(candidate.Html ?? "", @"<(img|a)\b", RegexOptions.IgnoreCase))
                    return candidate;
            }

            return candidates[0];
        }

        private static string BuildPostContext(string html, HtmlBlock body)
        {
            if (body != null && !String.IsNullOrWhiteSpace(body.PostHtml))
                return body.PostHtml;

            if (String.IsNullOrWhiteSpace(html) || body == null)
                return "";

            int start = FindNearestPostStart(html, body.Start);
            int end = FindNextPostStart(html, body.Start + 1);
            if (start >= 0 && end > body.End)
                return html.Substring(start, end - start);
            if (start >= 0)
                return html.Substring(start, Math.Min(html.Length, body.End + 1600) - start);

            return GetContext(html, body.Start, body.End, 6500, 900);
        }

        private static int FindNearestPostStart(string html, int beforeIndex)
        {
            if (String.IsNullOrWhiteSpace(html) || beforeIndex <= 0)
                return -1;

            int result = -1;
            Regex regex = PostStartRegex();
            MatchCollection matches = regex.Matches(html, 0);
            foreach (Match match in matches)
            {
                if (match.Index >= beforeIndex)
                    break;
                if (beforeIndex - match.Index <= 9000)
                    result = match.Index;
            }
            return result;
        }

        private static int FindNextPostStart(string html, int afterIndex)
        {
            if (String.IsNullOrWhiteSpace(html) || afterIndex < 0 || afterIndex >= html.Length)
                return -1;

            Match match = PostStartRegex().Match(html, afterIndex);
            return match.Success ? match.Index : -1;
        }

        private static Regex PostStartRegex()
        {
            return new Regex(
                @"<!--\s*(?:Begin\s+)?(?:Msg|Message|Post)\b[\s\S]{0,100}?-->|<a\b[^>]*(?:id|name)\s*=\s*['"" ][^'"" >]*(?:post|entry|msg|message)[-_]?\d+[^>]*>|<(?:div|table|tr|li)\b[^>]*(?:(?:id|name)\s*=\s*['"" ][^'"" >]*(?:post|entry|msg|message)[-_]?\d+|class\s*=\s*['"" ][^'"" >]*(?:post_block|post-block|post_container|post-container|postrow|topic-post|hentry)[^'"" >]*)[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string GetPostHeaderOnly(string postHtml)
        {
            string source = postHtml ?? "";
            if (String.IsNullOrWhiteSpace(source))
                return "";

            int bodyStart = FindFirstMessageBodyStart(source);
            if (bodyStart > 0)
                return source.Substring(0, Math.Min(source.Length, bodyStart + 450));

            return source.Length > 3500 ? source.Substring(0, 3500) : source;
        }

        private static int FindFirstMessageBodyStart(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return -1;

            Regex tagRegex = new Regex(@"<(div|td)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection matches = tagRegex.Matches(html);
            foreach (Match match in matches)
            {
                string cls = ExtractAttribute(match.Value, "class");
                foreach (string token in MessageBodyClassNames())
                {
                    if (cls.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        return match.Index;
                }
            }
            return -1;
        }

        private static List<HtmlBlock> FindBlocksByClass(string html, string tag, string[] classContains)
        {
            var result = new List<HtmlBlock>();
            if (String.IsNullOrWhiteSpace(html))
                return result;

            Regex tagRegex = new Regex("<" + tag + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection matches = tagRegex.Matches(html);
            foreach (Match match in matches)
            {
                string open = match.Value;
                string cls = ExtractAttribute(open, "class");
                if (String.IsNullOrWhiteSpace(cls))
                    continue;

                bool ok = false;
                foreach (string token in classContains)
                {
                    if (cls.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                    continue;

                int end = FindElementEnd(html, tag, match.Index, match.Index + match.Length);
                if (end <= match.Index)
                    continue;

                result.Add(new HtmlBlock { Start = match.Index, End = end, Html = html.Substring(match.Index, end - match.Index) });
            }
            return result;
        }

        private static int FindElementEnd(string html, string tag, int start, int afterOpen)
        {
            if (String.IsNullOrEmpty(html) || String.IsNullOrEmpty(tag))
                return -1;

            Regex regex = new Regex(@"</?" + tag + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            MatchCollection matches = regex.Matches(html, start);
            int depth = 0;
            foreach (Match match in matches)
            {
                bool closing = match.Value.StartsWith("</", StringComparison.OrdinalIgnoreCase);
                bool selfClosing = match.Value.EndsWith("/>", StringComparison.OrdinalIgnoreCase);

                if (!closing)
                {
                    if (!selfClosing)
                        depth++;
                }
                else
                {
                    depth--;
                    if (depth <= 0)
                        return match.Index + match.Length;
                }
            }

            return Math.Min(html.Length, afterOpen);
        }

        private static void AddBlocks(List<HtmlBlock> target, List<HtmlBlock> source)
        {
            foreach (HtmlBlock block in source)
            {
                bool exists = false;
                foreach (HtmlBlock old in target)
                {
                    if (Math.Abs(old.Start - block.Start) < 5 || (block.Start >= old.Start && block.End <= old.End))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    target.Add(block);
            }
        }

        private static string ExtractBlockTitle(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            Match match = Regex.Match(html, @"<div[^>]*class\s*=\s*['"" ][^'"" >]*block-title[^'"" >]*['"" ][^>]*>(?<title>[\s\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return HtmlToText(RemoveSnapbackImages(match.Groups["title"].Value));

            match = Regex.Match(html, @"<div[^>]*class\s*=\s*['"" ][^'"" >]*(?:hidetop|spoilertop|spoiler_title|spoiler-head)[^'"" >]*['"" ][^>]*>(?<title>[\s\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return HtmlToText(RemoveSnapbackImages(match.Groups["title"].Value));

            return "";
        }

        private static string ExtractBlockBody(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            HtmlBlock block = FindFirstBlockByClass(html, "div", new string[] { "block-body", "hidemain", "spoilermain", "spoiler_body", "spoiler-body", "quote-body" });
            return block == null ? "" : block.Html;
        }

        private static HtmlBlock FindFirstBlockByClass(string html, string tag, string[] classes)
        {
            List<HtmlBlock> blocks = FindBlocksByClass(html, tag, classes);
            return blocks.Count == 0 ? null : blocks[0];
        }

        private static string RemoveSpoilerHeader(string html)
        {
            string result = html ?? "";
            result = Regex.Replace(result, @"<div[^>]*class\s*=\s*['"" ][^'"" >]*(?:block-title|hidetop|spoilertop|spoiler_title|spoiler-head)[^'"" >]*['"" ][^>]*>[\s\S]*?</div>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return result;
        }

        private static string RemoveQuoteHeader(string html)
        {
            string result = html ?? "";
            result = Regex.Replace(result, @"<div[^>]*class\s*=\s*['"" ][^'"" >]*block-title[^'"" >]*['"" ][^>]*>[\s\S]*?</div>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return result;
        }

        private static string RemoveSnapbackImages(string html)
        {
            return Regex.Replace(html ?? "", @"<img\b[^>]*(?:" + Regex.Escape(SnapbackGif) + @"|alt\s*=\s*['"" ]\*['"" ])[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string ExtractEditedText(ref string bodyHtml)
        {
            var values = new List<string>();
            List<HtmlRange> ranges = new List<HtmlRange>();
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;

            MatchCollection matches = Regex.Matches(bodyHtml ?? "", @"<(?:span|div)[^>]*class\s*=\s*['"" ][^'"" >]*(?:\bedit\b|edited|post-edit|post-edit-reason|modedit)[^'"" >]*['"" ][^>]*>[\s\S]*?</(?:span|div)>", options);
            foreach (Match match in matches)
            {
                string text = HtmlToText(match.Value);
                if (!String.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, "редакт|причин|измен", RegexOptions.IgnoreCase))
                    values.Add(text);
                ranges.Add(new HtmlRange { Start = match.Index, End = match.Index + match.Length });
            }

            foreach (HtmlRange range in ranges.OrderByDescending(r => r.Start))
                bodyHtml = RemoveRange(bodyHtml, range.Start, range.End);

            values = values.Distinct().ToList();
            string joined = String.Join(" · ", values.ToArray());
            return String.IsNullOrWhiteSpace(joined) ? "" : joined;
        }

        private static string ReplacePostLinksWithTokens(string html)
        {
            return Regex.Replace(html ?? "", @"<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a>", delegate(Match match)
            {
                string href = NormalizeUrl(ExtractAttribute(match.Groups["attrs"].Value, "href"));
                TopicPostLink link = ParseTopicPostLink(href, "");
                if (link == null || String.IsNullOrWhiteSpace(link.PostId))
                    return match.Value;

                string caption = ExtractPostLinkCaption(match.Groups["text"].Value);
                return BuildPostLinkToken(href, caption);
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string ExtractPostLinkCaption(string innerHtml)
        {
            string text = innerHtml ?? "";
            text = ReplaceSmileImagesWithTokens(text);
            text = RemoveSnapbackImages(text);
            text = Regex.Replace(text, @"<img\b(?<attrs>[^>]*)>", delegate(Match match)
            {
                string attrs = match.Groups["attrs"].Value;
                return FirstNonEmpty(ExtractAttribute(attrs, "alt"), ExtractAttribute(attrs, "title"), " ");
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, @"<[^>]+>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = DecodeEntities(text);
            return NormalizePostLinkCaption(NormalizePlainText(text));
        }

        private static string NormalizePostLinkCaption(string caption)
        {
            string text = NormalizePlainText(caption ?? "");
            if (String.IsNullOrWhiteSpace(text) || text == "*" || text == "#" || IsServiceText(text))
                return "перейти к сообщению";
            if (text.Length > 80)
                text = text.Substring(0, 80).Trim() + "...";
            return text;
        }

        private static string BuildPostLinkToken(string url, string caption)
        {
            string safeUrl = EncodeLinkTokenValue(NormalizeUrl(url));
            string safeText = EncodeLinkTokenValue(NormalizePostLinkCaption(caption));
            return LinkTokenStart + safeUrl + LinkTokenSeparator + safeText + LinkTokenEnd;
        }

        private static string EncodeLinkTokenValue(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            return Convert.ToBase64String(bytes);
        }

        private static string DecodeLinkTokenValue(string value)
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

        private static string HtmlToText(string html)
        {
            return HtmlToText(html, false);
        }

        private static string HtmlToText(string html, bool preservePostLinks)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string result = RemoveScriptsAndComments(html);
            result = ReplaceSmileImagesWithTokens(result);
            if (preservePostLinks)
                result = ReplacePostLinksWithTokens(result);
            result = RemoveSnapbackImages(result);
            result = Regex.Replace(result, @"<img\b[^>]*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            result = Regex.Replace(result, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<sup[^>]*>(?<v>[\s\S]*?)</sup>", delegate(Match m) { return "[" + HtmlToText(m.Groups["v"].Value, preservePostLinks) + "]"; }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<sub[^>]*>(?<v>[\s\S]*?)</sub>", delegate(Match m) { return "[" + HtmlToText(m.Groups["v"].Value, preservePostLinks) + "]"; }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<a\b[^>]*>(?<text>[\s\S]*?)</a>", delegate(Match m) { return HtmlToText(m.Groups["text"].Value, preservePostLinks); }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            result = ReplaceOrderedLists(result, preservePostLinks);
            result = ReplaceUnorderedLists(result, preservePostLinks);
            result = ReplaceLooseListTags(result);

            result = Regex.Replace(result, @"</(?:p|div|tr|table|blockquote)>\s*", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<(?:blockquote|p|div|tr|table)\b[^>]*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<[^>]+>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = DecodeEntities(result);
            return NormalizePlainTextKeepLines(result);
        }

        private static string ReplaceOrderedLists(string html)
        {
            return ReplaceOrderedLists(html, false);
        }

        private static string ReplaceOrderedLists(string html, bool preservePostLinks)
        {
            return Regex.Replace(html ?? "", @"<ol\b[^>]*>(?<body>[\s\S]*?)</ol>", delegate(Match match)
            {
                string body = match.Groups["body"].Value;
                MatchCollection items = Regex.Matches(body, @"<li\b[^>]*>(?<li>[\s\S]*?)(?:</li>|(?=<li\b)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var lines = new List<string>();
                int number = 1;
                foreach (Match item in items)
                {
                    string text = HtmlToText(item.Groups["li"].Value, preservePostLinks);
                    if (!String.IsNullOrWhiteSpace(text))
                        lines.Add(number.ToString() + ". " + text.Trim());
                    number++;
                }
                return "\n" + String.Join("\n", lines.ToArray()) + "\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string ReplaceUnorderedLists(string html)
        {
            return ReplaceUnorderedLists(html, false);
        }

        private static string ReplaceUnorderedLists(string html, bool preservePostLinks)
        {
            return Regex.Replace(html ?? "", @"<ul\b[^>]*>(?<body>[\s\S]*?)</ul>", delegate(Match match)
            {
                string body = match.Groups["body"].Value;
                MatchCollection items = Regex.Matches(body, @"<li\b[^>]*>(?<li>[\s\S]*?)(?:</li>|(?=<li\b)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var lines = new List<string>();
                foreach (Match item in items)
                {
                    string text = HtmlToText(item.Groups["li"].Value, preservePostLinks);
                    if (!String.IsNullOrWhiteSpace(text))
                        lines.Add("• " + text.Trim());
                }
                return "\n" + String.Join("\n", lines.ToArray()) + "\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string ReplaceLooseListTags(string html)
        {
            string result = html ?? "";
            result = Regex.Replace(result, @"<(?:ul|ol)\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"</(?:ul|ol)>\s*", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"</li>\s*", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return result;
        }

        private static string NormalizePlainTextKeepLines(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            string value = text.Replace("\r", "").Replace("\t", " ");
            value = Regex.Replace(value, @"[ \u00A0]+", " ");
            value = Regex.Replace(value, @" *\n *", "\n");
            value = Regex.Replace(value, @"\n{3,}", "\n\n");

            string[] lines = value.Split(new char[] { '\n' });
            var cleaned = new List<string>();
            foreach (string line in lines)
            {
                string l = line.Trim();
                if (l == "[изображение]" || l == "#" || IsSpoilerCloseButtonText(l))
                    continue;

                if (!String.IsNullOrWhiteSpace(l))
                {
                    l = RemoveSpoilerCloseButtonText(l).Trim();
                    if (String.IsNullOrWhiteSpace(l) || IsSpoilerCloseButtonText(l))
                        continue;
                }

                cleaned.Add(l);
            }

            return String.Join("\n", cleaned.ToArray()).Trim();
        }

        private static string RemoveSpoilerCloseButtonText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return text ?? "";

            return Regex.Replace(text, @"\s*\[\s*(?:закрыть|закрыть окно|свернуть|скрыть|close|hide|×|x|-)\s*\]\s*", " ", RegexOptions.IgnoreCase);
        }

        private static bool IsSpoilerCloseButtonText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return false;

            return Regex.IsMatch(text.Trim(), @"^\[\s*(?:закрыть|закрыть окно|свернуть|скрыть|close|hide|×|x|-)\s*\]$", RegexOptions.IgnoreCase);
        }

        private static string NormalizePlainText(string text)
        {
            return Regex.Replace(NormalizePlainTextKeepLines(text ?? ""), @"\s+", " ").Trim();
        }

        private static string CleanTextOneLine(string html)
        {
            return NormalizePlainText(HtmlToText(html));
        }

        private static string DecodeEntities(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            string result = value;
            result = Regex.Replace(result, @"&#x(?<n>[0-9a-f]+);?", delegate(Match m)
            {
                int code;
                if (Int32.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.HexNumber, null, out code))
                    return CharFromCode(code);
                return m.Value;
            }, RegexOptions.IgnoreCase);

            result = Regex.Replace(result, @"&#(?<n>\d+);?", delegate(Match m)
            {
                int code;
                if (Int32.TryParse(m.Groups["n"].Value, out code))
                    return CharFromCode(code);
                return m.Value;
            }, RegexOptions.IgnoreCase);

            result = result.Replace("&nbsp;", " ").Replace("&#160;", " ");
            result = result.Replace("&amp;", "&");
            result = result.Replace("&quot;", "\"");
            result = result.Replace("&apos;", "'").Replace("&#39;", "'");
            result = result.Replace("&lt;", "<").Replace("&gt;", ">");
            result = result.Replace("&laquo;", "«").Replace("&raquo;", "»");
            result = result.Replace("&ndash;", "–").Replace("&mdash;", "—");
            result = result.Replace("&hellip;", "…");
            return result.Replace("\u00A0", " ");
        }

        private static string CharFromCode(int code)
        {
            try
            {
                if (code <= 0)
                    return "";
                if (code <= 0xFFFF)
                    return ((char)code).ToString();
                code -= 0x10000;
                return new string(new char[] { (char)((code >> 10) + 0xD800), (char)((code & 0x3FF) + 0xDC00) });
            }
            catch
            {
                return "";
            }
        }

        private static string RemoveScriptsAndComments(string html)
        {
            string result = html ?? "";
            result = Regex.Replace(result, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = Regex.Replace(result, @"<!--[\s\S]*?-->", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return result;
        }

        private static string ExtractCurrentPostId(string beforeHtml, string postHeaderHtml, string postHtml)
        {
            string id = ExtractPostId(postHeaderHtml);
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            string fallbackHeader = GetPostHeaderHtml(beforeHtml);
            id = ExtractLastPostId(fallbackHeader);
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            return ExtractPostId(postHtml);
        }

        private static string ExtractPostId(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"(?:id|name)\s*=\s*['"" ][^'"" >]*(?:post|entry|msg|message)[-_]?(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count > 0)
                return matches[0].Groups["id"].Value;

            matches = Regex.Matches(html ?? "", @"(?:[?&]|\b)(?:p|pid|post)=(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return matches.Count > 0 ? matches[0].Groups["id"].Value : "";
        }

        private static string ExtractLastPostId(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"(?:id|name)\s*=\s*['"" ][^'"" >]*(?:post|entry|msg|message)[-_]?(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count > 0)
                return matches[matches.Count - 1].Groups["id"].Value;

            matches = Regex.Matches(html ?? "", @"(?:[?&]|\b)(?:p|pid|post)=(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return matches.Count > 0 ? matches[matches.Count - 1].Groups["id"].Value : "";
        }

        private static string ExtractPostNumber(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"(?:Сообщение\s*)?#(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count > 0)
                return matches[0].Groups["num"].Value;
            return "";
        }

        private static string ExtractAuthor(string beforeHtml, string postHeaderHtml, string postHtml)
        {
            string name = PickAuthorFromHtml(postHeaderHtml, true, false);
            if (!String.IsNullOrWhiteSpace(name))
                return name;

            name = PickAuthorFromHtml(postHeaderHtml, false, false);
            if (!String.IsNullOrWhiteSpace(name))
                return name;

            string header = GetPostHeaderHtml(beforeHtml);
            name = PickAuthorFromHtml(header, true, true);
            if (!String.IsNullOrWhiteSpace(name))
                return name;

            name = PickAuthorFromHtml(header, false, true);
            if (!String.IsNullOrWhiteSpace(name))
                return name;

            return PickAuthorFromHtml(GetPostHeaderOnly(postHtml), true, false);
        }

        private static string ExtractAuthorId(string beforeHtml, string postHeaderHtml, string postHtml)
        {
            string id = PickAuthorIdFromHtml(postHeaderHtml, false);
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            string header = GetPostHeaderHtml(beforeHtml);
            id = PickAuthorIdFromHtml(header, true);
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            return PickAuthorIdFromHtml(GetPostHeaderOnly(postHtml), false);
        }

        private static string PickAuthorIdFromHtml(string html, bool preferLast)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(source))
                return "";

            string[] patterns = new string[]
            {
                @"<a\b[^>]*(?:showuser|MID|mid)=(?<id>\d+)[^>]*>",
                @"<a\b[^>]*/user/(?<id>\d+)[^>]*>",
                @"data-(?:member|user)-id\s*=\s*['"" ](?<id>\d+)['"" ]"
            };

            foreach (string pattern in patterns)
            {
                MatchCollection matches = Regex.Matches(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (preferLast)
                {
                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        string id = OnlyDigits(matches[i].Groups["id"].Value);
                        if (!String.IsNullOrWhiteSpace(id))
                            return id;
                    }
                }
                else
                {
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string id = OnlyDigits(matches[i].Groups["id"].Value);
                        if (!String.IsNullOrWhiteSpace(id))
                            return id;
                    }
                }
            }

            return "";
        }

        private static string GetPostHeaderHtml(string beforeHtml)
        {
            string source = beforeHtml ?? "";
            if (source.Length == 0)
                return "";

            int from = FindNearestPostStart(source, source.Length);
            if (from >= 0)
                return source.Substring(from);

            return source.Length > 2600 ? source.Substring(source.Length - 2600) : source;
        }

        private static string PickAuthorFromHtml(string html, bool classOnly, bool preferLast)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(source))
                return "";

            string classNames = @"normalname|username|user-name|author|post-author|post_author|nick|nickname|member-name|member_name|poster";
            string[] patterns = classOnly
                ? new string[]
                {
                    @"<a\b(?=[^>]*showuser=\d+)(?=[^>]*class\s*=\s*['"" ][^'"" >]*(?:" + classNames + @")[^'"" >]*['"" ])[^>]*>(?<name>[\s\S]*?)</a>",
                    @"<(?:span|div|strong)\b(?=[^>]*class\s*=\s*['"" ][^'"" >]*(?:" + classNames + @")[^'"" >]*['"" ])[^>]*>(?<name>[\s\S]*?)</(?:span|div|strong)>"
                }
                : new string[]
                {
                    @"<a\b(?=[^>]*(?:showuser=\d+|/user/\d+|act=Profile))[^>]*>(?<name>[\s\S]*?)</a>"
                };

            foreach (string pattern in patterns)
            {
                MatchCollection matches = Regex.Matches(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (preferLast)
                {
                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        string name = CleanTextOneLine(matches[i].Groups["name"].Value);
                        if (IsGoodAuthorName(name))
                            return name;
                    }
                }
                else
                {
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string name = CleanTextOneLine(matches[i].Groups["name"].Value);
                        if (IsGoodAuthorName(name))
                            return name;
                    }
                }
            }

            return "";
        }

        private static bool IsGoodAuthorName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || IsServiceText(name))
                return false;
            string value = NormalizePlainText(name);
            if (value.Length < 2 || value.Length > 80)
                return false;
            if (Regex.IsMatch(value, @"^(цитата|ответ|ответить|жалоба|профиль|сообщение|сообщения|тема|шапка|куратор|модератор|#\d+)$", RegexOptions.IgnoreCase))
                return false;
            if (value.IndexOf("4PDA", StringComparison.OrdinalIgnoreCase) >= 0 && value.Length > 12)
                return false;
            return true;
        }

        private static string ExtractPostDate(string html)
        {
            MatchCollection matches = Regex.Matches(
                HtmlToText(html ?? ""),
                @"(?<date>(?:Сегодня|Вчера),?\s*\d{1,2}:\d{2}|\d{1,2}\.\d{1,2}\.\d{2,4},?\s*\d{1,2}:\d{2})",
                RegexOptions.IgnoreCase);
            return matches.Count > 0 ? matches[matches.Count - 1].Groups["date"].Value.Trim() : "";
        }

        private static string ParseTopicTitle(string html, string topicId)
        {
            string source = html ?? "";
            string[] patterns = new string[]
            {
                @"<meta[^>]*(?:property|name)\s*=\s*['"" ]og:title['"" ][^>]*content\s*=\s*['""](?<title>[^'""<>]+)['""][^>]*>",
                @"<h1[^>]*>(?<title>[\s\S]*?)</h1>",
                @"<div[^>]*class\s*=\s*['"" ][^'"" >]*(?:topic-title|topic_title|maintitle|borderwrap)[^'"" >]*['"" ][^>]*>(?<title>[\s\S]*?)</div>",
                @"<title[^>]*>(?<title>[\s\S]*?)</title>"
            };

            foreach (string pattern in patterns)
            {
                Match match = Regex.Match(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success)
                {
                    string title = ClearTopicTitle(CleanTextOneLine(match.Groups["title"].Value));
                    if (!IsBadTitle(title))
                        return title;
                }
            }

            MatchCollection links = Regex.Matches(source, @"<a[^>]*href\s*=\s*['"" ][^'"" >]*showtopic=(?<id>\d+)[^'"" >]*['"" ][^>]*>(?<title>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match link in links)
            {
                if (link.Groups["id"].Value != topicId)
                    continue;
                string title = ClearTopicTitle(CleanTextOneLine(link.Groups["title"].Value));
                if (!IsBadTitle(title))
                    return title;
            }

            return "";
        }

        private static string ClearTopicTitle(string title)
        {
            title = DecodeEntities(title ?? "");
            title = Regex.Replace(title, @"\s*[-–—]\s*4PDA.*$", "", RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"\s*[-–—]\s*Страница\s*\d+\s*$", "", RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"^\s*4PDA\s*[>›]\s*", "", RegexOptions.IgnoreCase).Trim();
            return title;
        }

        private static bool IsBadTitle(string title)
        {
            if (String.IsNullOrWhiteSpace(title))
                return true;
            string t = title.Trim();
            if (Regex.IsMatch(t, @"^#?\d+$"))
                return true;
            if (Regex.IsMatch(t, @"^(тема|сообщение|опции|страниц[аы]?)\b", RegexOptions.IgnoreCase))
                return true;
            if (t.Length < 3 || t.Length > 180)
                return true;
            return false;
        }

        private static string ParseForumId(string html)
        {
            Match match = Regex.Match(html ?? "", @"<input[^>]*name\s*=\s*['"" ]f['"" ][^>]*value\s*=\s*['"" ](?<id>\d+)['"" ][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return match.Groups["id"].Value;

            match = Regex.Match(html ?? "", @"showforum=(?<id>\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : "0";
        }

        private static string ParseAuthKey(string html)
        {
            Match match = Regex.Match(html ?? "", @"name\s*=\s*['"" ]auth_key['"" ][^>]*value\s*=\s*['"" ](?<key>[^'"" >]+)['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                match = Regex.Match(html ?? "", @"auth_key\s*[:=]\s*['"" ](?<key>[a-z0-9]{8,})['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? DecodeEntities(match.Groups["key"].Value) : "";
        }

        private static bool ParseCanReply(string html)
        {
            string source = html ?? "";
            return Regex.IsMatch(source, @"name\s*=\s*['"" ]Post['"" ]|act=Post[^'"" >]*(?:CODE=03|code=03)|(?:Быстрый ответ|Ответить|Добавить ответ|reply)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static List<PageNavigationItem> ParseTopicPagination(string html, string topicId, int currentStart)
        {
            var starts = new List<int>();
            AddUniqueStart(starts, 0);

            MatchCollection links = Regex.Matches(html ?? "", @"<a\b[^>]*href\s*=\s*['"" ](?<href>[^'"" >]+)['"" ][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match link in links)
            {
                string href = DecodeEntities(link.Groups["href"].Value);
                if (!IsTopicPaginationHref(href, topicId))
                    continue;

                string text = CleanTextOneLine(link.Groups["text"].Value);
                if (!IsPaginationText(text, href))
                    continue;

                int start;
                if (!TryExtractStart(href, out start))
                    start = 0;
                AddUniqueStart(starts, Math.Max(0, start));
            }

            starts.Sort();
            int pageSize = GuessPageSize(starts);
            if (pageSize <= 0)
                pageSize = DefaultPageSize;

            int maxStart = starts.Count == 0 ? currentStart : Math.Max(starts[starts.Count - 1], currentStart);
            int maxPage = Math.Max(1, maxStart / pageSize + 1);
            int currentPage = Math.Max(1, Math.Max(0, currentStart) / pageSize + 1);

            var result = new List<PageNavigationItem>();
            if (maxPage <= 1)
                return result;

            int firstStart = 0;
            int previousStart = Math.Max(0, (currentPage - 2) * pageSize);
            int currentPageStart = Math.Max(0, (currentPage - 1) * pageSize);
            int nextStart = Math.Min((maxPage - 1) * pageSize, currentPage * pageSize);
            int lastStart = Math.Max(0, (maxPage - 1) * pageSize);

            AddPageNavigationItem(result, "«", firstStart, currentStart, currentPage > 1);
            AddPageNavigationItem(result, "‹", previousStart, currentStart, currentPage > 1);
            AddPageNavigationItem(result, currentPage.ToString(), currentPageStart, currentStart, false);
            AddPageNavigationItem(result, "›", nextStart, currentStart, currentPage < maxPage);
            AddPageNavigationItem(result, "»", lastStart, currentStart, currentPage < maxPage);
            return result;
        }

        private static bool IsTopicPaginationHref(string href, string topicId)
        {
            string value = href ?? "";
            if (String.IsNullOrWhiteSpace(value))
                return false;

            Match topicMatch = Regex.Match(value, @"showtopic=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (topicMatch.Success)
                return String.IsNullOrWhiteSpace(topicId) || topicMatch.Groups["id"].Value == topicId;

            return Regex.IsMatch(value, @"[?&]st=\d+", RegexOptions.IgnoreCase);
        }

        private static void AddPageNavigationItem(List<PageNavigationItem> result, string text, int start, int currentStart, bool enabled)
        {
            result.Add(new PageNavigationItem
            {
                Text = text,
                Start = Math.Max(0, start),
                IsEnabled = enabled && Math.Max(0, start) != Math.Max(0, currentStart)
            });
        }

        private static void AddUniqueStart(List<int> values, int start)
        {
            if (!values.Contains(start))
                values.Add(start);
        }

        private static int GuessPageSize(List<int> starts)
        {
            int best = 0;
            foreach (int start in starts)
            {
                if (start <= 0)
                    continue;
                if (best == 0 || start < best)
                    best = start;
            }
            return best;
        }

        private static bool IsPaginationText(string text, string href)
        {
            string t = NormalizePlainText(text);
            if (Regex.IsMatch(t, @"^\d+$"))
                return true;
            if (Regex.IsMatch(t, @"^(?:<|>|«|»|‹|›|назад|вперед|след|пред)", RegexOptions.IgnoreCase))
                return true;
            return Regex.IsMatch(href ?? "", @"[?&]st=\d+", RegexOptions.IgnoreCase) && t.Length <= 20;
        }

        private static int ExtendFileTail(string html, int end, out string tail)
        {
            tail = "";
            if (String.IsNullOrEmpty(html) || end >= html.Length)
                return end;

            string rest = html.Substring(end, Math.Min(120, html.Length - end));
            Match match = Regex.Match(rest, @"^\s*(?<size>\([^\)]{1,60}\))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
            {
                tail = match.Groups["size"].Value;
                return end + match.Length;
            }
            return end;
        }

        private static bool IsImageLikeBlock(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;
            if (Regex.IsMatch(html, @"<img\b", RegexOptions.IgnoreCase | RegexOptions.Singleline) && !HasUsableImage(html))
                return false;
            if (Regex.IsMatch(html, @"class\s*=\s*['"" ][^'"" >]*(?:attach-img|resized-linked-image|ipb)[^'"" >]*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                return true;
            if (Regex.IsMatch(html, @"\.(?:png|jpe?g|gif|webp)(?:[?'"" >]|$)", RegexOptions.IgnoreCase))
                return true;
            return false;
        }

        private static bool IsAttachmentUrl(string url, string attrs, string innerHtml)
        {
            string check = ((url ?? "") + " " + (attrs ?? "") + " " + (innerHtml ?? "")).ToLowerInvariant();
            if (check.IndexOf("attach-file") >= 0 || check.IndexOf("act=attach") >= 0 || check.IndexOf("attach_id=") >= 0)
                return true;
            if (check.IndexOf("/forum/dl/") >= 0 || check.IndexOf("/dl/") >= 0 || check.IndexOf("download") >= 0)
                return true;
            if (Regex.IsMatch(check, @"\.(apk|zip|rar|7z|pdf|docx?|xlsx?|txt|mp3|mp4)(?:\s|$|[?&<])"))
                return true;
            return false;
        }

        private static bool IsFileAttachmentBlock(string url, string attrs, string innerHtml)
        {
            string check = ((url ?? "") + " " + (attrs ?? "") + " " + (innerHtml ?? "")).ToLowerInvariant();
            if (check.IndexOf("attach-file") >= 0)
                return true;
            if (Regex.IsMatch(check, @"\.(apk|zip|rar|7z|pdf|docx?|xlsx?|txt|mp3|mp4)(?:\s|$|[?&<])"))
                return true;
            return false;
        }

        private static bool IsSnapbackImageUrl(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            string s = NormalizeUrl(value).ToLowerInvariant();
            return s.IndexOf(SnapbackGif.ToLowerInvariant()) >= 0;
        }

        private static bool IsSnapbackImageHtml(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            string s = html.ToLowerInvariant();
            if (s.IndexOf(SnapbackGif.ToLowerInvariant()) >= 0)
                return true;

            return Regex.IsMatch(html, @"(?:^|<img\b[^>]*|\s)alt\s*=\s*['"" ]\*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool HasUsableImage(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            MatchCollection imgs = Regex.Matches(html, @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match img in imgs)
            {
                string attrs = img.Groups["attrs"].Value;
                string src = FirstNonEmpty(ExtractAttribute(attrs, "data-src"), ExtractAttribute(attrs, "data-lazy-src"), ExtractAttribute(attrs, "data-original"), ExtractAttribute(attrs, "src"));
                string url = NormalizeUrl(src);
                if (!String.IsNullOrWhiteSpace(url) && !IsIgnoredImage(url, attrs))
                    return true;
            }

            return false;
        }

        private static string ReplaceSmileImagesWithTokens(string html)
        {
            return Regex.Replace(html ?? "", @"<img\b(?<attrs>[^>]*)>", delegate(Match match)
            {
                string name = ExtractSmileNameFromImageAttrs(match.Groups["attrs"].Value);
                if (String.IsNullOrWhiteSpace(name))
                    return match.Value;
                return " :" + name + ": ";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool IsSmileImageBlock(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return false;

            MatchCollection imgs = Regex.Matches(html, @"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (imgs.Count == 0)
                return false;

            foreach (Match img in imgs)
            {
                if (!IsSmileImageAttrs(img.Groups["attrs"].Value))
                    return false;
            }
            return true;
        }

        private static bool IsSmileImageAttrs(string attrs)
        {
            return !String.IsNullOrWhiteSpace(ExtractSmileNameFromImageAttrs(attrs));
        }

        private static string ExtractSmileNameFromImageAttrs(string attrs)
        {
            if (String.IsNullOrWhiteSpace(attrs))
                return "";

            string alt = DecodeEntities(ExtractAttribute(attrs, "alt"));
            string fromToken = ExtractSmileNameFromToken(CleanTextOneLine(alt));
            if (!String.IsNullOrWhiteSpace(fromToken))
                return fromToken;

            string title = DecodeEntities(ExtractAttribute(attrs, "title"));
            fromToken = ExtractSmileNameFromToken(CleanTextOneLine(title));
            if (!String.IsNullOrWhiteSpace(fromToken))
                return fromToken;

            string src = FirstNonEmpty(ExtractAttribute(attrs, "data-src"), ExtractAttribute(attrs, "data-lazy-src"), ExtractAttribute(attrs, "data-original"), ExtractAttribute(attrs, "src"));
            string url = NormalizeUrl(src);
            if (IsSmileImageUrl(url) || IsSmileImageClass(attrs))
                return ExtractSmileNameFromUrl(url);

            return "";
        }

        private static bool IsSmileImageClass(string attrs)
        {
            if (String.IsNullOrWhiteSpace(attrs))
                return false;

            string cls = ExtractAttribute(attrs, "class");
            return cls.IndexOf("emoticon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cls.IndexOf("smile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSmileImageUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return false;

            string value = url.ToLowerInvariant();
            if (value.IndexOf("style_emoticons") >= 0)
                return true;
            if (value.IndexOf("/smiles/") >= 0)
                return true;
            if (value.IndexOf("/emoticons/") >= 0)
                return true;
            return false;
        }

        private static string ExtractSmileNameFromUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            string clear = url;
            int q = clear.IndexOf('?');
            if (q >= 0)
                clear = clear.Substring(0, q);
            int hash = clear.IndexOf('#');
            if (hash >= 0)
                clear = clear.Substring(0, hash);

            int slash = clear.LastIndexOf('/');
            string file = slash >= 0 ? clear.Substring(slash + 1) : clear;
            int dot = file.LastIndexOf('.');
            if (dot > 0)
                file = file.Substring(0, dot);

            return SanitizeSmileName(file);
        }

        private static Regex SmileTokenRegex()
        {
            return new Regex(@":(?<name>[a-z0-9_\-]{2,40}):", RegexOptions.IgnoreCase);
        }

        private static string ExtractSmileNameFromToken(string token)
        {
            if (String.IsNullOrWhiteSpace(token))
                return "";

            Match match = SmileTokenRegex().Match(token);
            if (!match.Success)
                return "";

            return SanitizeSmileName(match.Groups["name"].Value);
        }

        private static string SanitizeSmileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                return "";

            string value = DecodeEntities(name.Trim().Trim(':'));
            value = Regex.Replace(value, @"[^a-zA-Z0-9_\-]", "");
            if (value.Length > 40)
                value = value.Substring(0, 40);
            return value.ToLowerInvariant();
        }

        private static string GetSmileAssetUri(string smileName)
        {
            string name = SanitizeSmileName(smileName);
            if (String.IsNullOrWhiteSpace(name))
                name = "smile";
            return "ms-appx:///Assets/smiles/" + name + ".gif";
        }

        private static double GetSmileSize(double fontSize)
        {
            if (fontSize <= 0)
                return 26;

            double size = Math.Round(fontSize + 6);
            if (size < 22)
                size = 22;
            if (size > 32)
                size = 32;
            return size;
        }

        private static bool IsIgnoredImage(string url, string attrs)
        {
            string s = ((url ?? "") + " " + (attrs ?? "")).ToLowerInvariant();
            if (String.IsNullOrWhiteSpace(s))
                return true;
            if (IsSnapbackImageUrl(url) || IsSnapbackImageHtml(attrs) || IsSmileImageAttrs(attrs))
                return true;
            if (IsFileIcon(url))
                return true;
            if (s.IndexOf("img-resized.png") >= 0 || s.IndexOf("spacer") >= 0 || s.IndexOf("pixel") >= 0 || s.IndexOf("blank.gif") >= 0)
                return true;
            if (s.IndexOf("button") >= 0 || s.IndexOf("folder") >= 0 || s.IndexOf("pips") >= 0 || s.IndexOf("rank") >= 0)
                return true;
            if (s.IndexOf("avatar") >= 0 || s.IndexOf("profile") >= 0 || s.IndexOf("photo-thumb") >= 0 || s.IndexOf("user_photo") >= 0)
                return true;
            if (s.IndexOf("style_images") >= 0 || s.IndexOf("images/4pda") >= 0)
                return true;
            return false;
        }

        private static bool IsFileIcon(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return false;
            return NormalizeUrl(url).IndexOf("mQ600b0kBHtbbyz1UYtbg5GfeT1YikOfEOteLNm9MACz2oFQOdeHvBJLWbb5SV.gif", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsImageUrl(string url)
        {
            return Regex.IsMatch(url ?? "", @"\.(?:png|jpe?g|gif|webp)(?:$|[?&])", RegexOptions.IgnoreCase);
        }

        private static bool IsClassContains(string cls, string token)
        {
            return !String.IsNullOrWhiteSpace(cls) && cls.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsServiceCaption(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return true;
            string t = NormalizePlainText(text);
            return Regex.IsMatch(t, @"^(прикрепленное изображение|нажмите для просмотра|фото|изображение)$", RegexOptions.IgnoreCase);
        }

        private static bool IsServiceText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return true;
            string t = NormalizePlainText(text);
            return Regex.IsMatch(t, @"^(жалоба|в faq|имя|цитировать|плохо|хорошо|ответить|быстрый ответ|изменить|удалить|опции|\+|-|#|\d+)$", RegexOptions.IgnoreCase);
        }

        private static string ExtractAttribute(string htmlOrAttributes, string attributeName)
        {
            if (String.IsNullOrWhiteSpace(htmlOrAttributes) || String.IsNullOrWhiteSpace(attributeName))
                return "";

            Match match = Regex.Match(htmlOrAttributes, @"\b" + Regex.Escape(attributeName) + @"\s*=\s*(?<q>['""])(?<v>[\s\S]*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return DecodeEntities(match.Groups["v"].Value);

            match = Regex.Match(htmlOrAttributes, @"\b" + Regex.Escape(attributeName) + @"\s*=\s*(?<v>[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? DecodeEntities(match.Groups["v"].Value.Trim('"', '\'')) : "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!String.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }

        private static string NormalizeUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            string value = DecodeEntities(url.Trim());
            if (value.StartsWith("//"))
                return "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("/"))
                return "https://4pda.to" + value;
            if (value.StartsWith("?"))
                return ForumBaseUrl + value;
            if (value.StartsWith("index.php", StringComparison.OrdinalIgnoreCase))
                return "https://4pda.to/forum/" + value;
            return value;
        }

        private static string FileNameFromUrl(string url)
        {
            string clear = url ?? "";
            int q = clear.IndexOf('?');
            if (q >= 0)
                clear = clear.Substring(0, q);
            int slash = clear.LastIndexOf('/');
            string name = slash >= 0 ? clear.Substring(slash + 1) : clear;
            return String.IsNullOrWhiteSpace(name) ? "файл" : DecodeEntities(name);
        }

        private static string GetContext(string html, int start, int end, int before, int after)
        {
            int from = Math.Max(0, start - before);
            int to = Math.Min(html.Length, end + after);
            return html.Substring(from, to - from);
        }

        private static string RemoveRange(string value, int start, int end)
        {
            if (String.IsNullOrEmpty(value) || start < 0 || end <= start || start >= value.Length)
                return value;
            end = Math.Min(end, value.Length);
            return value.Remove(start, end - start);
        }

        private static string ExtractTopicId(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";
            Match match = Regex.Match(value, @"showtopic=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(value, @"^(?<id>\d+)$", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : "";
        }

        private static bool TryExtractStart(string value, out int start)
        {
            start = 0;
            if (String.IsNullOrWhiteSpace(value))
                return false;
            Match match = Regex.Match(value, @"(?:[?&]|^)st=(?<start>\d+)", RegexOptions.IgnoreCase);
            return match.Success && Int32.TryParse(match.Groups["start"].Value, out start);
        }

        private static TopicPostLink ParseTopicPostLink(string url, string currentTopicId)
        {
            string normalized = NormalizeUrl(url);
            if (String.IsNullOrWhiteSpace(normalized))
                return null;

            string topicId = ExtractTopicId(normalized);
            int start;
            bool hasStart = TryExtractStart(normalized, out start);
            string postId = ExtractLinkedPostId(normalized);

            if (String.IsNullOrWhiteSpace(postId))
                return null;

            if (String.IsNullOrWhiteSpace(topicId))
                topicId = currentTopicId ?? "";

            TopicPostLink link = new TopicPostLink();
            link.Url = normalized;
            link.TopicId = topicId;
            link.PostId = postId;
            link.Start = hasStart ? Math.Max(0, start) : -1;
            return link;
        }

        private static string ExtractLinkedPostId(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string decoded = DecodeEntities(value);
            Match match = Regex.Match(decoded, @"[#&?](?:entry|post|msg|message)[-_]?(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;

            match = Regex.Match(decoded, @"(?:[?&]|^)(?:p|pid|post)=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;

            match = Regex.Match(decoded, @"(?:view|act)=findpost[^#?&]*(?:[?&][^#]*)?(?:p|pid|post)=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;

            return "";
        }

        private static string BuildFindPostUrl(string topicId, string postId, string originalUrl)
        {
            string url = NormalizeUrl(originalUrl);
            if (!String.IsNullOrWhiteSpace(url) && (url.IndexOf("findpost", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(url, @"[?&](?:p|pid|post)=\d+", RegexOptions.IgnoreCase)))
                return url;

            string topic = String.IsNullOrWhiteSpace(topicId) ? "" : topicId;
            string post = OnlyDigits(postId);
            if (String.IsNullOrWhiteSpace(topic))
                return ForumBaseUrl + "?view=findpost&p=" + Uri.EscapeDataString(post);
            return ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(topic) + "&view=findpost&p=" + Uri.EscapeDataString(post);
        }

        private static int ResolveCurrentStartFromHtml(string html, string topicId, int fallbackStart)
        {
            string source = html ?? "";
            MatchCollection currentLinks = Regex.Matches(source, @"<(?:span|strong|b|li|a)[^>]*(?:class\s*=\s*['"" ][^'"" >]*(?:active|current|pagecurrent|cur)[^'"" >]*['"" ][^>]*)[^>]*>(?<text>\d+)</(?:span|strong|b|li|a)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            int currentPage = 0;
            foreach (Match match in currentLinks)
            {
                int value;
                if (Int32.TryParse(match.Groups["text"].Value, out value) && value > 0)
                {
                    currentPage = value;
                    break;
                }
            }

            List<PageNavigationItem> pages = ParseTopicPagination(source, topicId, fallbackStart);
            if (currentPage > 0)
            {
                foreach (PageNavigationItem page in pages)
                {
                    if (page.Text == currentPage.ToString())
                        return page.Start;
                }
            }

            foreach (PageNavigationItem page in pages)
            {
                if (!page.IsEnabled)
                    return page.Start;
            }

            return Math.Max(0, fallbackStart);
        }

        private static string BuildCacheKey(string topicId, int start)
        {
            return ParserCacheVersion + ":" + (topicId ?? "") + ":" + Math.Max(0, start).ToString();
        }

        private static TopicData GetCachedTopicData(string key)
        {
            lock (CacheLock)
            {
                TopicData data;
                return TopicCache.TryGetValue(key, out data) ? data : null;
            }
        }

        private static void PutCachedTopicData(string key, TopicData data)
        {
            if (String.IsNullOrWhiteSpace(key) || data == null)
                return;
            lock (CacheLock)
            {
                TopicCache[key] = data;
                if (TopicCache.Count > 80)
                {
                    string first = TopicCache.Keys.FirstOrDefault();
                    if (!String.IsNullOrWhiteSpace(first))
                        TopicCache.Remove(first);
                }
            }
        }

        private static void RemoveCachedTopicData(string key)
        {
            lock (CacheLock)
            {
                if (!String.IsNullOrWhiteSpace(key) && TopicCache.ContainsKey(key))
                    TopicCache.Remove(key);
            }
        }

        private static string BuildQuoteText(TopicPostItem post)
        {
            string text = post == null ? "" : post.PlainText;
            text = NormalizePlainTextKeepLines(text);
            if (String.IsNullOrWhiteSpace(text))
                text = "...";
            if (text.Length > 1200)
                text = text.Substring(0, 1200).Trim() + "...";

            string author = EscapeBbCodeAttribute(post == null ? "" : post.Author);
            string pid = OnlyDigits(post == null ? "" : post.PostId);
            if (!String.IsNullOrWhiteSpace(pid))
                return "[quote name=\"" + author + "\" post=\"" + pid + "\"]\r\n" + text + "\r\n[/quote]\r\n";
            return "[quote name=\"" + author + "\"]\r\n" + text + "\r\n[/quote]\r\n";
        }

        private static string EscapeBbCodeAttribute(string value)
        {
            string result = NormalizePlainText(value ?? "");
            result = result.Replace("\"", "'");
            result = result.Replace("[", "(").Replace("]", ")");
            return result.Trim();
        }

        private static string OnlyDigits(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";
            Match match = Regex.Match(value, @"\d+");
            return match.Success ? match.Value : "";
        }

        private sealed class TopicPostLink
        {
            public string Url { get; set; }
            public string TopicId { get; set; }
            public string PostId { get; set; }
            public int Start { get; set; }
        }

        private sealed class TopicPageState
        {
            public string TopicId { get; set; }
            public int Start { get; set; }
        }

        private sealed class TopicData
        {
            public TopicData()
            {
                Posts = new List<TopicPostItem>();
                Pages = new List<PageNavigationItem>();
            }

            public string Title { get; set; }
            public string ForumId { get; set; }
            public string AuthKey { get; set; }
            public bool CanReply { get; set; }
            public List<TopicPostItem> Posts { get; private set; }
            public List<PageNavigationItem> Pages { get; set; }

            public void AddUniquePost(TopicPostItem post)
            {
                if (post == null)
                    return;
                string key = !String.IsNullOrWhiteSpace(post.PostId) ? post.PostId : post.Author + "|" + post.Date + "|" + post.PlainText;
                foreach (TopicPostItem old in Posts)
                {
                    string oldKey = !String.IsNullOrWhiteSpace(old.PostId) ? old.PostId : old.Author + "|" + old.Date + "|" + old.PlainText;
                    if (oldKey == key)
                        return;
                }
                Posts.Add(post);
            }
        }

        private sealed class HtmlBlock
        {
            public int Start { get; set; }
            public int End { get; set; }
            public string Html { get; set; }
            public string PostHtml { get; set; }
        }

        private sealed class HtmlRange
        {
            public int Start { get; set; }
            public int End { get; set; }
        }

        private sealed class RawSmileFrame
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int DelayMs { get; set; }
            public int DisposalMethod { get; set; }
            public byte[] Pixels { get; set; }
        }

        private sealed class AnimatedSmileFrames
        {
            public List<AnimatedSmileFrame> Frames { get; set; }
        }

        private sealed class AnimatedSmileFrame
        {
            public WriteableBitmap Bitmap { get; set; }
            public int DelayMs { get; set; }
        }

        private sealed class SmileRenderInfo
        {
            public string Name { get; set; }
            public double Size { get; set; }
        }

        private sealed class ContentCandidate
        {
            public TopicContentKind Kind { get; set; }
            public int Start { get; set; }
            public int End { get; set; }
            public string Html { get; set; }
            public string TailText { get; set; }
        }

        public enum TopicContentKind
        {
            Text,
            Image,
            File,
            Spoiler,
            Quote
        }

        public sealed class PageNavigationItem
        {
            public string Text { get; set; }
            public int Start { get; set; }
            public bool IsEnabled { get; set; }
        }

        public sealed class TopicContentItem : INotifyPropertyChanged
        {
            private bool _isExpanded;

            public TopicContentItem()
            {
                Children = new ObservableCollection<TopicContentItem>();
                FileIconUrl = FileIconGif;
            }

            public TopicContentKind Kind { get; set; }
            public string Text { get; set; }
            public string Header { get; set; }
            public string Url { get; set; }
            public string ImageUrl { get; set; }
            public string FileTitle { get; set; }
            public string FileIconUrl { get; set; }
            public ObservableCollection<TopicContentItem> Children { get; private set; }

            public bool IsExpanded
            {
                get { return _isExpanded; }
                set
                {
                    if (_isExpanded == value)
                        return;
                    _isExpanded = value;
                    OnPropertyChanged("IsExpanded");
                    OnPropertyChanged("ExpandedVisibility");
                    OnPropertyChanged("ButtonText");
                }
            }

            public string ButtonText
            {
                get
                {
                    string title = String.IsNullOrWhiteSpace(Header) ? "Спойлер" : Header;
                    return (IsExpanded ? "Спойлер (-)" : "Спойлер (+)") + " (" + title + ")";
                }
            }

            public Visibility TextVisibility
            {
                get { return Kind == TopicContentKind.Text && !String.IsNullOrWhiteSpace(Text) ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility ImageVisibility
            {
                get { return Kind == TopicContentKind.Image && !String.IsNullOrWhiteSpace(ImageUrl) ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility FileVisibility
            {
                get { return Kind == TopicContentKind.File && !String.IsNullOrWhiteSpace(Url) ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility SpoilerVisibility
            {
                get { return Kind == TopicContentKind.Spoiler ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility QuoteVisibility
            {
                get { return Kind == TopicContentKind.Quote && !String.IsNullOrWhiteSpace(Text) ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility ExpandedVisibility
            {
                get { return IsExpanded ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility CaptionVisibility
            {
                get { return !String.IsNullOrWhiteSpace(FileTitle) ? Visibility.Visible : Visibility.Collapsed; }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged(string name)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(name));
            }
        }

        public sealed class TopicPostItem
        {
            public TopicPostItem()
            {
                Parts = new ObservableCollection<TopicContentItem>();
            }

            public string PostId { get; set; }
            public string AuthorId { get; set; }
            public string Author { get; set; }
            public string Date { get; set; }
            public string Number { get; set; }
            public string EditedText { get; set; }
            public ObservableCollection<TopicContentItem> Parts { get; private set; }

            public string NumberText
            {
                get { return String.IsNullOrWhiteSpace(Number) ? "" : "#" + Number; }
            }

            public bool HasAuthorProfile
            {
                get { return !String.IsNullOrWhiteSpace(OnlyDigits(AuthorId)); }
            }

            public Visibility EditedVisibility
            {
                get { return String.IsNullOrWhiteSpace(EditedText) ? Visibility.Collapsed : Visibility.Visible; }
            }

            public Visibility ReputationVisibility
            {
                get
                {
                    if (!ForumAuthService.IsAuthorized)
                        return Visibility.Collapsed;
                    if (String.IsNullOrWhiteSpace(PostId) || String.IsNullOrWhiteSpace(AuthorId))
                        return Visibility.Collapsed;

                    string currentUserId = OnlyDigits(ForumAuthService.CurrentUserId);
                    string authorId = OnlyDigits(AuthorId);
                    if (!String.IsNullOrWhiteSpace(currentUserId) && String.Equals(currentUserId, authorId, StringComparison.OrdinalIgnoreCase))
                        return Visibility.Collapsed;

                    return Visibility.Visible;
                }
            }

            public string PlainText
            {
                get
                {
                    var lines = new List<string>();
                    foreach (TopicContentItem part in Parts)
                        AppendText(lines, part);
                    return String.Join("\n", lines.ToArray()).Trim();
                }
            }

            private static void AppendText(List<string> lines, TopicContentItem part)
            {
                if (part == null)
                    return;
                if (!String.IsNullOrWhiteSpace(part.Text))
                    lines.Add(part.Text);
                foreach (TopicContentItem child in part.Children)
                    AppendText(lines, child);
            }
        }
    }
}
