using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

namespace _4PDA
{
    public sealed partial class ForumPage : Page
    {
        private const string Host = "4pda.to";
        private const string ForumBaseUrl = "https://4pda.to/forum/index.php";
        private const string ForumSearchUrl = "https://4pda.to/forum/index.php?act=search";
        private const string ParserCacheVersion = "forum-ultrafast-v10-compact-pages";
        private const string ForumPageCachePrefix = "forum_page_fast_";
        private const string SlartusForumStructUrl = "https://raw.githubusercontent.com/slartus/4pdaClient-plus/master/forum_struct.json";
        private const string ForumTreeCacheFileName = "forum_tree_cache_v3.txt";
        private const string ForumStructCacheFileName = "forum_struct_forumpage_v1.json";

        private readonly ObservableCollection<ForumListItem> _items = new ObservableCollection<ForumListItem>();
        private readonly ObservableCollection<PageNavigationItem> _pages = new ObservableCollection<PageNavigationItem>();
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly Stack<ForumPageState> _forumHistory = new Stack<ForumPageState>();

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, ForumData> ForumDataCache = new Dictionary<string, ForumData>();
        private static readonly Dictionary<string, List<ForumListItem>> ChildForumsCache = new Dictionary<string, List<ForumListItem>>();
        private static List<ForumStructureItem> ForumStructureItems;

        private string _forumId = "";
        private int _start;
        private PageNavigationItem _previousPage;
        private PageNavigationItem _nextPage;
        private bool _loading;

        public ForumPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            ForumItemsListView.ItemsSource = _items;
            PagesItemsControl.ItemsSource = _pages;

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
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;

            string parameter = e.Parameter as string;
            _forumId = ExtractForumId(parameter);
            if (String.IsNullOrWhiteSpace(_forumId))
                _forumId = "1";

            _start = 0;
            _forumHistory.Clear();
            await LoadForumAsync(false);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            base.OnNavigatedFrom(e);
        }

        private async void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (_forumHistory.Count > 0)
            {
                e.Handled = true;
                ForumPageState state = _forumHistory.Pop();
                _forumId = state.ForumId;
                _start = state.Start;
                await LoadForumAsync(false);
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
            await LoadForumAsync(true);
        }

        private async void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenForumPageAsync(_previousPage);
        }

        private async void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenForumPageAsync(_nextPage);
        }

        private async void PageButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
                return;

            PageNavigationItem page = element.DataContext as PageNavigationItem;
            await OpenForumPageAsync(page);
        }

        private async void ForumItemsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ForumListItem item = e.ClickedItem as ForumListItem;
            if (item == null)
                return;

            if (item.Kind == ForumItemKind.Forum && !String.IsNullOrWhiteSpace(item.Id))
            {
                await OpenForumInsidePageAsync(item.Id);
                return;
            }

            if (item.Kind == ForumItemKind.Topic && !String.IsNullOrWhiteSpace(item.Url))
            {
                Frame.Navigate(typeof(TopicPage), item.Url);
                return;
            }

            if (!String.IsNullOrWhiteSpace(item.Url))
                await Launcher.LaunchUriAsync(new Uri(item.Url, UriKind.Absolute));
        }

        private async Task OpenForumInsidePageAsync(string forumId)
        {
            if (_loading)
                return;

            forumId = ExtractForumId(forumId);
            if (String.IsNullOrWhiteSpace(forumId) || String.Equals(forumId, _forumId, StringComparison.OrdinalIgnoreCase))
                return;

            PushHistory();
            _forumId = forumId;
            _start = 0;
            await LoadForumAsync(false);
        }

        private async Task OpenForumPageAsync(PageNavigationItem page)
        {
            if (_loading || page == null || !page.IsEnabled)
                return;

            string forumId = ExtractForumId(page.Url);
            if (String.IsNullOrWhiteSpace(forumId))
                forumId = _forumId;

            int start = Math.Max(0, page.Start);
            if (String.Equals(forumId, _forumId, StringComparison.OrdinalIgnoreCase) && start == _start)
                return;

            PushHistory();
            _forumId = forumId;
            _start = start;
            await LoadForumAsync(false);
        }

        private void PushHistory()
        {
            if (!String.IsNullOrWhiteSpace(_forumId))
                _forumHistory.Push(new ForumPageState { ForumId = _forumId, Start = _start });
        }

        private async Task LoadForumAsync(bool forceReload)
        {
            if (_loading)
                return;

            _loading = true;
            SetBusy(true);
            SetStatus("");

            string cacheKey = BuildCacheKey(_forumId, _start);
            ForumData cachedData = GetCachedForumData(cacheKey);
            if (cachedData == null && !forceReload)
                cachedData = await LoadForumDataCacheAsync(cacheKey);

            if (!forceReload && cachedData != null)
                ApplyForumData(cachedData);

            ForumData structureData = null;
            bool needStructure = _start == 0 && (forceReload || cachedData == null || !cachedData.HasKind(ForumItemKind.Forum));
            if (needStructure)
            {
                structureData = await LoadForumStructureDataAsync(_forumId, forceReload);
                if (!forceReload && cachedData == null && structureData != null && structureData.Items.Count > 0)
                    ApplyForumData(structureData);
            }

            try
            {
                string url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(_forumId) + "&st=" + _start.ToString();
                string html = await _httpClient.GetStringAsync(new Uri(url, UriKind.Absolute));
                ForumData data = await Task.Run<ForumData>(delegate { return ParseForumPageFast(html, _forumId, _start); });

                if (_start == 0 && structureData != null)
                    data.InsertForumsAtTop(structureData.Items.Where(i => i.Kind == ForumItemKind.Forum));

                if (data.Items.Count == 0 && structureData != null && structureData.Items.Count > 0)
                    data = structureData;

                PutCachedForumData(cacheKey, data);
                ApplyForumData(data);
                await SaveForumDataCacheAsync(cacheKey, data);

                if (_items.Count == 0)
                    SetStatus("Раздел загружен, но темы или подразделы не найдены. Если это раздел с подразделами, откройте форум с главной страницы, чтобы обновился кэш дерева.");
            }
            catch (Exception ex)
            {
                if (cachedData != null)
                {
                    ApplyForumData(cachedData);
                    SetStatus("Показан кэш. Обновление не удалось: " + ex.Message);
                }
                else if (structureData != null && structureData.Items.Count > 0)
                {
                    ApplyForumData(structureData);
                    SetStatus("Показаны подразделы из структуры форума. Темы не обновились: " + ex.Message);
                }
                else
                {
                    _items.Clear();
                    UpdatePagination(new List<PageNavigationItem>());
                    SetStatus("Не удалось загрузить форум: " + ex.Message);
                }
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private void ApplyForumData(ForumData data)
        {
            if (data == null)
                return;



            _items.Clear();
            foreach (ForumListItem item in data.Items)
                _items.Add(item);

            UpdatePagination(data.Pages);

        }

        private static string BuildCacheKey(string forumId, int start)
        {
            return ParserCacheVersion + ":" + (forumId ?? "") + ":" + Math.Max(0, start).ToString();
        }

        private static ForumData GetCachedForumData(string key)
        {
            lock (CacheLock)
            {
                ForumData data;
                return ForumDataCache.TryGetValue(key, out data) ? data : null;
            }
        }

        private static void PutCachedForumData(string key, ForumData data)
        {
            if (String.IsNullOrWhiteSpace(key) || data == null)
                return;

            lock (CacheLock)
            {
                ForumDataCache[key] = data;
                if (ForumDataCache.Count > 60)
                {
                    string firstKey = ForumDataCache.Keys.FirstOrDefault();
                    if (!String.IsNullOrWhiteSpace(firstKey))
                        ForumDataCache.Remove(firstKey);
                }
            }
        }

        private static string GetCacheFileName(string cacheKey)
        {
            string safe = Regex.Replace(cacheKey ?? "", @"[^A-Za-z0-9_\-]", "_");
            return ForumPageCachePrefix + safe + ".txt";
        }

        private async Task<ForumData> LoadForumDataCacheAsync(string cacheKey)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(GetCacheFileName(cacheKey));
                string cache = await FileIO.ReadTextAsync(file);
                ForumData data = await Task.Run<ForumData>(delegate { return ParseForumDataCache(cache); });
                PutCachedForumData(cacheKey, data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        private async Task SaveForumDataCacheAsync(string cacheKey, ForumData data)
        {
            if (data == null)
                return;

            try
            {
                string cache = await Task.Run<string>(delegate { return CreateForumDataCache(data); });
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    GetCacheFileName(cacheKey),
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, cache);
            }
            catch
            {
            }
        }

        private static string CreateForumDataCache(ForumData data)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(ParserCacheVersion);
            builder.AppendLine(EscapeCache(data.Title));

            if (data.Pages != null)
            {
                foreach (PageNavigationItem page in data.Pages)
                {
                    if (page == null)
                        continue;

                    builder.Append("P|");
                    builder.Append((int)page.Kind).Append('|');
                    builder.Append(EscapeCache(page.Label)).Append('|');
                    builder.Append(EscapeCache(page.Url)).Append('|');
                    builder.Append(page.Start).Append('|');
                    builder.AppendLine(page.IsEnabled ? "1" : "0");
                }
            }

            if (data.Items != null)
            {
                foreach (ForumListItem item in data.Items)
                {
                    if (item == null)
                        continue;

                    builder.Append("I|");
                    builder.Append((int)item.Kind).Append('|');
                    builder.Append(EscapeCache(item.TypeLabel)).Append('|');
                    builder.Append(EscapeCache(item.Id)).Append('|');
                    builder.Append(EscapeCache(item.Title)).Append('|');
                    builder.Append(EscapeCache(item.Info)).Append('|');
                    builder.Append(EscapeCache(item.Url)).Append('|');
                    builder.AppendLine(item.IsPinned ? "1" : "0");
                }
            }

            return builder.ToString();
        }

        private static ForumData ParseForumDataCache(string cache)
        {
            if (String.IsNullOrWhiteSpace(cache))
                return null;

            string[] lines = cache.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2 || !String.Equals(lines[0], ParserCacheVersion, StringComparison.OrdinalIgnoreCase))
                return null;

            var data = new ForumData();
            data.Title = UnescapeCache(lines[1]);

            for (int i = 2; i < lines.Length; i++)
            {
                string line = lines[i];
                if (String.IsNullOrWhiteSpace(line))
                    continue;

                string[] parts = line.Split('|');
                if (parts.Length == 0)
                    continue;

                if (String.Equals(parts[0], "P", StringComparison.OrdinalIgnoreCase) && parts.Length >= 6)
                {
                    int kindValue;
                    int start;
                    data.Pages.Add(new PageNavigationItem
                    {
                        Kind = Int32.TryParse(parts[1], out kindValue) ? (PageNavigationKind)kindValue : PageNavigationKind.Number,
                        Label = UnescapeCache(parts[2]),
                        Url = UnescapeCache(parts[3]),
                        Start = Int32.TryParse(parts[4], out start) ? start : 0,
                        IsEnabled = String.Equals(parts[5], "1", StringComparison.OrdinalIgnoreCase)
                    });
                    continue;
                }

                if (String.Equals(parts[0], "I", StringComparison.OrdinalIgnoreCase) && parts.Length >= 8)
                {
                    int kindValue;
                    var item = new ForumListItem
                    {
                        Kind = Int32.TryParse(parts[1], out kindValue) ? (ForumItemKind)kindValue : ForumItemKind.Topic,
                        TypeLabel = UnescapeCache(parts[2]),
                        Id = UnescapeCache(parts[3]),
                        Title = UnescapeCache(parts[4]),
                        Info = UnescapeCache(parts[5]),
                        Url = UnescapeCache(parts[6]),
                        IsPinned = String.Equals(parts[7], "1", StringComparison.OrdinalIgnoreCase)
                    };
                    data.AddUnique(item);
                }
            }

            return data;
        }

        private static string EscapeCache(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        private static string UnescapeCache(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }
            catch
            {
                return "";
            }
        }


        private async Task<ForumData> LoadForumStructureDataAsync(string forumId, bool forceReload)
        {
            if (_start != 0)
                return null;

            List<ForumStructureItem> items = await LoadForumStructureItemsAsync(forceReload);
            if (items == null || items.Count == 0)
                return null;

            return await Task.Run<ForumData>(delegate { return BuildForumDataFromStructure(items, forumId); });
        }

        private async Task<List<ForumStructureItem>> LoadForumStructureItemsAsync(bool forceReload)
        {
            if (!forceReload && ForumStructureItems != null && ForumStructureItems.Count > 0)
                return ForumStructureItems;

            List<ForumStructureItem> items = null;

            if (!forceReload)
            {
                items = await LoadForumStructureFromMainCacheAsync();
                if (items != null && items.Count > 0)
                {
                    ForumStructureItems = items;
                    return ForumStructureItems;
                }

                items = await LoadForumStructureFromJsonCacheAsync();
                if (items != null && items.Count > 0)
                {
                    ForumStructureItems = items;
                    return ForumStructureItems;
                }
            }

            if (forceReload)
            {
                try
                {
                    string json = await _httpClient.GetStringAsync(new Uri(SlartusForumStructUrl, UriKind.Absolute));
                    items = await Task.Run<List<ForumStructureItem>>(delegate { return ParseForumStructureJson(json); });
                    if (items != null && items.Count > 0)
                    {
                        ForumStructureItems = items;
                        await SaveForumStructureJsonCacheAsync(json);
                        return ForumStructureItems;
                    }
                }
                catch
                {
                }

                items = await LoadForumStructureFromMainCacheAsync();
                if (items != null && items.Count > 0)
                {
                    ForumStructureItems = items;
                    return ForumStructureItems;
                }

                items = await LoadForumStructureFromJsonCacheAsync();
                if (items != null && items.Count > 0)
                {
                    ForumStructureItems = items;
                    return ForumStructureItems;
                }
            }

            return ForumStructureItems;
        }

        private async Task<List<ForumStructureItem>> LoadForumStructureFromMainCacheAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(ForumTreeCacheFileName);
                string cache = await FileIO.ReadTextAsync(file);
                return await Task.Run<List<ForumStructureItem>>(delegate { return ParseForumTreeCacheAsStructure(cache); });
            }
            catch
            {
                return null;
            }
        }

        private async Task<List<ForumStructureItem>> LoadForumStructureFromJsonCacheAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(ForumStructCacheFileName);
                string json = await FileIO.ReadTextAsync(file);
                return await Task.Run<List<ForumStructureItem>>(delegate { return ParseForumStructureJson(json); });
            }
            catch
            {
                return null;
            }
        }

        private async Task SaveForumStructureJsonCacheAsync(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
                return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    ForumStructCacheFileName,
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch
            {
            }
        }

        private static List<ForumStructureItem> ParseForumStructureJson(string json)
        {
            var result = new List<ForumStructureItem>();
            if (String.IsNullOrWhiteSpace(json))
                return result;

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ForumJsonItem>));
                object value = serializer.ReadObject(stream);
                List<ForumJsonItem> items = value as List<ForumJsonItem>;
                if (items == null)
                    return result;

                var usedIds = new HashSet<string>();
                foreach (ForumJsonItem item in items)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.Id) || String.IsNullOrWhiteSpace(item.Title))
                        continue;

                    if (usedIds.Contains(item.Id))
                        continue;

                    usedIds.Add(item.Id);
                    result.Add(new ForumStructureItem
                    {
                        Id = item.Id,
                        ParentId = item.ParentId,
                        Title = CleanTextFast(item.Title),
                        Description = CleanTextFast(item.Description),
                        HasForums = item.IsHasForums,
                        HasTopics = item.IsHasTopics
                    });
                }
            }

            return result;
        }

        private static List<ForumStructureItem> ParseForumTreeCacheAsStructure(string cache)
        {
            var result = new List<ForumStructureItem>();
            if (String.IsNullOrWhiteSpace(cache))
                return result;

            var parents = new List<ForumStructureItem>();
            ForumStructureItem lastParent = new ForumStructureItem { Id = "", Level = -1 };
            parents.Add(lastParent);

            string[] lines = cache.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 5)
                    continue;

                int level;
                if (!Int32.TryParse(parts[0], out level))
                    level = 0;

                string id = UnescapeCache(parts[1]);
                string title = UnescapeCache(parts[2]);
                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title))
                    continue;

                if (level <= lastParent.Level)
                {
                    int removeCount = lastParent.Level - level + 1;
                    for (int i = 0; i < removeCount && parents.Count > 1; i++)
                        parents.RemoveAt(parents.Count - 1);

                    lastParent = parents[parents.Count - 1];
                }

                var node = new ForumStructureItem
                {
                    Id = id,
                    ParentId = lastParent.Level < 0 ? null : lastParent.Id,
                    Title = title,
                    Description = UnescapeCache(parts[3]),
                    Level = level,
                    HasForums = false,
                    HasTopics = true
                };

                foreach (ForumStructureItem existingParent in parents)
                {
                    if (existingParent.Id == node.ParentId)
                    {
                        existingParent.HasForums = true;
                        break;
                    }
                }

                result.Add(node);

                if (node.Level > lastParent.Level)
                {
                    lastParent = node;
                    parents.Add(lastParent);
                }
            }

            var idsWithChildren = new HashSet<string>();
            foreach (ForumStructureItem item in result)
            {
                if (!String.IsNullOrWhiteSpace(item.ParentId))
                    idsWithChildren.Add(item.ParentId);
            }

            foreach (ForumStructureItem item in result)
            {
                item.HasForums = idsWithChildren.Contains(item.Id);
                item.HasTopics = !item.HasForums;
            }

            return result;
        }

        private static ForumData BuildForumDataFromStructure(List<ForumStructureItem> items, string forumId)
        {
            var data = new ForumData();
            if (items == null || items.Count == 0)
                return data;

            string normalizedForumId = ExtractForumIdUltraFast(forumId);
            if (String.IsNullOrWhiteSpace(normalizedForumId))
                normalizedForumId = forumId;

            string cacheKey = normalizedForumId ?? "";
            List<ForumListItem> cachedChildren;
            lock (CacheLock)
            {
                if (ChildForumsCache.TryGetValue(cacheKey, out cachedChildren))
                {
                    foreach (ForumListItem cached in cachedChildren)
                        data.AddUnique(cached);
                    return data;
                }
            }

            string title = "";
            for (int i = 0; i < items.Count; i++)
            {
                ForumStructureItem current = items[i];
                if (current != null && String.Equals(current.Id, normalizedForumId, StringComparison.OrdinalIgnoreCase))
                {
                    title = current.Title;
                    break;
                }
            }

            data.Title = title;

            for (int i = 0; i < items.Count; i++)
            {
                ForumStructureItem item = items[i];
                if (item == null)
                    continue;

                bool isChild;
                if (String.IsNullOrWhiteSpace(normalizedForumId) || String.Equals(normalizedForumId, "1", StringComparison.OrdinalIgnoreCase))
                    isChild = String.IsNullOrWhiteSpace(item.ParentId);
                else
                    isChild = String.Equals(item.ParentId, normalizedForumId, StringComparison.OrdinalIgnoreCase);

                if (!isChild)
                    continue;

                data.AddUnique(new ForumListItem
                {
                    Kind = ForumItemKind.Forum,
                    TypeLabel = item.HasForums ? "раздел" : "темы",
                    Id = item.Id,
                    Title = item.Title,
                    Info = item.Description,
                    Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(item.Id)
                });
            }

            lock (CacheLock)
            {
                ChildForumsCache[cacheKey] = new List<ForumListItem>(data.Items);
                if (ChildForumsCache.Count > 100)
                    ChildForumsCache.Clear();
            }

            return data;
        }

        private void SetBusy(bool value)
        {
            ForumProgressRing.IsActive = value;
            RefreshAppBarButton.IsEnabled = !value;
        }

        private void SetStatus(string text)
        {
            StatusTextBlock.Text = text;
            StatusTextBlock.Visibility = String.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdatePagination(List<PageNavigationItem> pages)
        {
            _pages.Clear();
            _previousPage = null;
            _nextPage = null;

            if (pages != null)
            {
                foreach (PageNavigationItem page in pages)
                {
                    if (page == null)
                        continue;

                    if (page.Kind == PageNavigationKind.Previous)
                    {
                        if (_previousPage == null)
                            _previousPage = page;
                        continue;
                    }

                    if (page.Kind == PageNavigationKind.Next)
                    {
                        if (_nextPage == null)
                            _nextPage = page;
                        continue;
                    }

                    if (page.Kind == PageNavigationKind.Number)
                        _pages.Add(page);
                }
            }

            PageNavigationPanel.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static ForumData ParseForumPageFast(string html, string forumId, int currentStart)
        {
            var data = new ForumData();
            if (String.IsNullOrEmpty(html))
                return data;

            data.Title = ParseForumTitleUltraFast(html, forumId);
            data.Pages = ParseForumPaginationUltraFast(html, forumId, currentStart);

            foreach (ForumListItem item in ParseAnnouncesUltraFast(html))
                data.AddUnique(item);

            foreach (ForumListItem item in ParseSubForumsUltraFast(html, forumId))
                data.AddUnique(item);

            List<ForumListItem> topics = ParseTopicsUltraFast(html);
            foreach (ForumListItem item in topics)
                data.AddUnique(item);

            return data;
        }

        private static string ParseForumTitleUltraFast(string html, string forumId)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            string id = ExtractForumIdUltraFast(forumId);
            if (!String.IsNullOrEmpty(id))
            {
                int pos = 0;
                while (pos < html.Length)
                {
                    int hrefPos = FindForumIdInText(html, id, pos);
                    if (hrefPos < 0)
                        break;

                    int aStart = LastIndexOfIgnoreCase(html, "<a", hrefPos);
                    int aEnd = aStart >= 0 ? html.IndexOf('>', aStart) : -1;
                    int close = aEnd >= 0 ? IndexOfIgnoreCase(html, "</a>", aEnd + 1) : -1;
                    if (aStart >= 0 && aEnd > aStart && close > aEnd && close - aEnd < 400)
                    {
                        string title = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                        if (!String.IsNullOrWhiteSpace(title) && IsUsableForumTitleUltraFast(title))
                            return title;
                    }

                    pos = hrefPos + 1;
                }
            }

            int titleStart = IndexOfIgnoreCase(html, "<title", 0);
            if (titleStart >= 0)
            {
                int titleOpenEnd = html.IndexOf('>', titleStart);
                int titleEnd = titleOpenEnd >= 0 ? IndexOfIgnoreCase(html, "</title>", titleOpenEnd + 1) : -1;
                if (titleOpenEnd >= 0 && titleEnd > titleOpenEnd)
                {
                    string title = CleanTextUltraFast(html.Substring(titleOpenEnd + 1, titleEnd - titleOpenEnd - 1));
                    int dash = title.IndexOf(" - 4PDA", StringComparison.OrdinalIgnoreCase);
                    if (dash > 0)
                        title = title.Substring(0, dash).Trim();
                    if (!String.IsNullOrWhiteSpace(title))
                        return title;
                }
            }

            return "";
        }

        private static List<ForumListItem> ParseAnnouncesUltraFast(string html)
        {
            var result = new List<ForumListItem>();
            if (String.IsNullOrEmpty(html))
                return result;

            int pos = 0;
            while (pos < html.Length && result.Count < 10)
            {
                int body = IndexOfIgnoreCase(html, "anonce_body", pos);
                if (body < 0)
                    break;

                int blockEnd = IndexOfIgnoreCase(html, "</div>", body);
                if (blockEnd < 0)
                    blockEnd = Math.Min(html.Length, body + 2500);

                int aStart = IndexOfIgnoreCase(html, "<a", body);
                if (aStart < 0 || aStart > blockEnd)
                {
                    pos = body + 11;
                    continue;
                }

                int aEnd = html.IndexOf('>', aStart);
                int close = aEnd >= 0 ? IndexOfIgnoreCase(html, "</a>", aEnd + 1) : -1;
                if (aEnd < 0 || close < 0 || close > blockEnd)
                {
                    pos = body + 11;
                    continue;
                }

                string tag = html.Substring(aStart, aEnd - aStart + 1);
                string href = GetAttributeUltraFast(tag, "href");
                string title = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                if (!String.IsNullOrWhiteSpace(title))
                {
                    result.Add(new ForumListItem
                    {
                        Kind = ForumItemKind.Announce,
                        TypeLabel = "объявление",
                        Title = title,
                        Info = "нажмите, чтобы открыть",
                        Url = NormalizeUrlUltraFast(href)
                    });
                }

                pos = close + 4;
            }

            return result;
        }

        private static List<ForumListItem> ParseSubForumsUltraFast(string html, string currentForumId)
        {
            var result = new List<ForumListItem>();
            var used = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            string currentId = ExtractForumIdUltraFast(currentForumId);
            int topicsStart = IndexOfIgnoreCase(html, "data-topic", 0);
            int scanEnd = topicsStart > 0 ? topicsStart : Math.Min(html.Length, 90000);
            int pos = 0;

            while (pos < scanEnd && result.Count < 80)
            {
                int aStart = IndexOfIgnoreCase(html, "<a", pos);
                if (aStart < 0 || aStart >= scanEnd)
                    break;

                int aEnd = html.IndexOf('>', aStart);
                if (aEnd < 0 || aEnd >= scanEnd)
                    break;

                string tag = html.Substring(aStart, Math.Min(aEnd - aStart + 1, 1200));
                string href = GetAttributeUltraFast(tag, "href");
                string id = ExtractForumIdUltraFast(href);
                if (String.IsNullOrWhiteSpace(id) || String.Equals(id, currentId, StringComparison.OrdinalIgnoreCase) || used.Contains(id) || HasStartParameterUltraFast(href))
                {
                    pos = aEnd + 1;
                    continue;
                }

                int close = IndexOfIgnoreCase(html, "</a>", aEnd + 1);
                if (close < 0 || close > scanEnd || close - aEnd > 800)
                {
                    pos = aEnd + 1;
                    continue;
                }

                string title = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                if (!IsUsableForumTitleUltraFast(title))
                {
                    pos = close + 4;
                    continue;
                }

                string context = GetBoundedWindowUltraFast(html, aStart, 700);
                if (IsNavigationContextUltraFast(context))
                {
                    pos = close + 4;
                    continue;
                }

                used.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Forum,
                    TypeLabel = "раздел",
                    Id = id,
                    Title = title,
                    Info = ExtractDescriptionUltraFast(context, 160),
                    Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(id)
                });

                pos = close + 4;
            }

            return result;
        }

        private static List<ForumListItem> ParseTopicsUltraFast(string html)
        {
            var result = new List<ForumListItem>();
            var used = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            List<ForumListItem> tableTopics = ParseTopicsTableUltraFast(html);
            if (tableTopics.Count > 0)
                return tableTopics;

            int pos = 0;
            while (pos < html.Length)
            {
                int topicMark = IndexOfIgnoreCase(html, "data-topic", pos);
                if (topicMark < 0)
                    break;

                int rowStart = LastIndexOfIgnoreCase(html, "<div", topicMark);
                if (rowStart < 0)
                    rowStart = LastIndexOfIgnoreCase(html, "<tr", topicMark);
                if (rowStart < 0)
                    rowStart = topicMark;

                int firstTagEnd = html.IndexOf('>', rowStart);
                if (firstTagEnd < 0)
                    break;

                string firstTag = html.Substring(rowStart, Math.Min(firstTagEnd - rowStart + 1, 1600));
                string id = GetAttributeUltraFast(firstTag, "data-topic");
                if (String.IsNullOrWhiteSpace(id))
                    id = ReadNumberAfter(html, topicMark);

                int nextMark = IndexOfIgnoreCase(html, "data-topic", topicMark + 10);
                int blockEnd = nextMark > topicMark ? nextMark : FindForumListEndUltraFast(html, firstTagEnd + 1);
                if (blockEnd <= firstTagEnd)
                    blockEnd = Math.Min(html.Length, firstTagEnd + 9000);
                if (blockEnd - rowStart > 16000)
                    blockEnd = rowStart + 16000;

                if (!String.IsNullOrWhiteSpace(id) && !used.Contains(id))
                {
                    TopicTitleLink titleLink = ExtractTopicTitleLinkUltraFast(html, rowStart, blockEnd, id);
                    if (titleLink != null && IsUsableTopicTitleUltraFast(titleLink.Title))
                    {
                        string context = html.Substring(rowStart, blockEnd - rowStart);
                        bool pinned = IsPinnedContextUltraFast(context);
                        string info = ExtractTopicInfoUltraFast(context);

                        used.Add(id);
                        result.Add(new ForumListItem
                        {
                            Kind = ForumItemKind.Topic,
                            TypeLabel = pinned ? "закреплено" : "тема",
                            Id = id,
                            Title = titleLink.Title,
                            Info = info,
                            Url = ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id),
                            IsPinned = pinned
                        });
                    }
                }

                pos = blockEnd > topicMark ? blockEnd : topicMark + 10;
            }

            if (result.Count == 0)
                result = ParseTopicLinksUltraFast(html);

            return result;
        }

        private static List<ForumListItem> ParseTopicsTableUltraFast(string html)
        {
            var result = new List<ForumListItem>();
            var used = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            int pos = 0;
            while (pos < html.Length && result.Count < 160)
            {
                int linkMark = IndexOfIgnoreCase(html, "tid-link-", pos);
                if (linkMark < 0)
                    break;

                string id = ReadDigitsAfterPrefixUltraFast(html, linkMark, "tid-link-");
                if (String.IsNullOrWhiteSpace(id) || used.Contains(id))
                {
                    pos = linkMark + 9;
                    continue;
                }

                int rowStart = LastIndexOfIgnoreCase(html, "<tr", linkMark);
                int rowEnd = rowStart >= 0 ? IndexOfIgnoreCase(html, "</tr>", linkMark) : -1;
                if (rowStart < 0 || rowEnd < 0 || rowEnd <= rowStart)
                {
                    pos = linkMark + 9;
                    continue;
                }

                rowEnd += 5;
                if (rowEnd - rowStart > 30000)
                {
                    pos = linkMark + 9;
                    continue;
                }

                string row = html.Substring(rowStart, rowEnd - rowStart);
                List<string> cells = ExtractTableCellsUltraFast(row);
                if (cells.Count < 7)
                {
                    pos = rowEnd;
                    continue;
                }

                TopicTitleLink titleLink = ExtractTopicTitleLinkFromTableRowUltraFast(row, id);
                if (titleLink == null || !IsUsableTopicTitleUltraFast(titleLink.Title))
                {
                    pos = rowEnd;
                    continue;
                }

                string titleCell = cells[2];
                string answersCell = cells[3];
                string authorCell = cells[4];
                string viewsCell = cells[5];
                string lastCell = cells[6];

                string author = ExtractUserNameAfterUltraFast(authorCell, 0);
                if (String.IsNullOrWhiteSpace(author))
                    author = CleanTableCellValueUltraFast(authorCell);
                if (author == "-")
                    author = "";

                string lastUser = ExtractUserNameAfterUltraFast(lastCell, 0);
                string description = ExtractDescriptionUltraFast(titleCell, 120);
                string answers = CleanTableCellValueUltraFast(answersCell);
                string views = CleanTableCellValueUltraFast(viewsCell);

                var infoParts = new List<string>();
                if (!String.IsNullOrWhiteSpace(description))
                    infoParts.Add(description);
                if (!String.IsNullOrWhiteSpace(author))
                    infoParts.Add("автор: " + author);
                if (!String.IsNullOrWhiteSpace(answers) && answers != "-")
                    infoParts.Add("ответов: " + answers);
                if (!String.IsNullOrWhiteSpace(views) && views != "-")
                    infoParts.Add("просмотров: " + views);
                if (!String.IsNullOrWhiteSpace(lastUser))
                    infoParts.Add("последний: " + lastUser);

                bool pinned = IsPinnedTableTopicRowUltraFast(html, rowStart, row);

                used.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Topic,
                    TypeLabel = pinned ? "закреплено" : "тема",
                    Id = id,
                    Title = titleLink.Title,
                    Info = JoinNonEmpty(" · ", infoParts.ToArray()),
                    Url = ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id),
                    IsPinned = pinned
                });

                pos = rowEnd;
            }

            return result;
        }

        private static TopicTitleLink ExtractTopicTitleLinkFromTableRowUltraFast(string row, string id)
        {
            if (String.IsNullOrEmpty(row) || String.IsNullOrWhiteSpace(id))
                return null;

            string marker = "tid-link-" + id;
            int markerPos = IndexOfIgnoreCase(row, marker, 0);
            if (markerPos >= 0)
            {
                int aStart = LastIndexOfIgnoreCase(row, "<a", markerPos);
                int aEnd = aStart >= 0 ? row.IndexOf('>', aStart) : -1;
                int close = aEnd >= 0 ? IndexOfIgnoreCase(row, "</a>", aEnd + 1) : -1;
                if (aStart >= 0 && aEnd > aStart && close > aEnd)
                {
                    string tag = row.Substring(aStart, Math.Min(aEnd - aStart + 1, 1600));
                    string href = GetAttributeUltraFast(tag, "href");
                    string linkId = ExtractTopicIdUltraFast(href);
                    if (String.Equals(linkId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        string title = CleanTextUltraFast(row.Substring(aEnd + 1, close - aEnd - 1));
                        if (!IsUsableTopicTitleUltraFast(title))
                            title = CleanTextUltraFast(GetAttributeUltraFast(tag, "title"));
                        if (IsUsableTopicTitleUltraFast(title))
                            return new TopicTitleLink { Href = href, Title = title };
                    }
                }
            }

            return ExtractTopicTitleLinkUltraFast(row, 0, row.Length, id);
        }

        private static List<string> ExtractTableCellsUltraFast(string row)
        {
            var cells = new List<string>();
            if (String.IsNullOrEmpty(row))
                return cells;

            int pos = 0;
            while (pos < row.Length && cells.Count < 16)
            {
                int tdStart = IndexOfIgnoreCase(row, "<td", pos);
                if (tdStart < 0)
                    break;

                int tdOpenEnd = row.IndexOf('>', tdStart);
                if (tdOpenEnd < 0)
                    break;

                int tdEnd = IndexOfIgnoreCase(row, "</td>", tdOpenEnd + 1);
                if (tdEnd < 0)
                    break;

                cells.Add(row.Substring(tdOpenEnd + 1, tdEnd - tdOpenEnd - 1));
                pos = tdEnd + 5;
            }

            return cells;
        }

        private static string ReadDigitsAfterPrefixUltraFast(string html, int prefixStart, string prefix)
        {
            if (String.IsNullOrEmpty(html) || String.IsNullOrEmpty(prefix) || prefixStart < 0)
                return "";

            int p = prefixStart + prefix.Length;
            if (p < 0 || p >= html.Length)
                return "";

            int start = p;
            while (p < html.Length && Char.IsDigit(html[p]))
                p++;

            return p > start ? html.Substring(start, p - start) : "";
        }

        private static string CleanTableCellValueUltraFast(string html)
        {
            string value = CleanTextUltraFast(html);
            if (String.IsNullOrWhiteSpace(value))
                return "";

            value = NormalizePlainTextUltraFast(value);
            return value.Trim();
        }

        private static bool IsPinnedTableTopicRowUltraFast(string html, int rowStart, string row)
        {
            if (IsPinnedContextUltraFast(row))
                return true;

            int beforeStart = Math.Max(0, rowStart - 6000);
            string before = html.Substring(beforeStart, rowStart - beforeStart);
            int important = LastIndexOfIgnoreCase(before, "Важные темы", before.Length - 1);
            int normal = LastIndexOfIgnoreCase(before, "Темы форума", before.Length - 1);
            return important >= 0 && important > normal;
        }

        private static List<ForumListItem> ParseTopicLinksUltraFast(string html)
        {
            var result = new List<ForumListItem>();
            var used = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            int pos = 0;
            while (pos < html.Length && result.Count < 120)
            {
                int aStart = IndexOfIgnoreCase(html, "<a", pos);
                if (aStart < 0)
                    break;

                int aEnd = html.IndexOf('>', aStart);
                if (aEnd < 0)
                    break;

                string tag = html.Substring(aStart, Math.Min(aEnd - aStart + 1, 1600));
                string href = GetAttributeUltraFast(tag, "href");
                string id = ExtractTopicIdUltraFast(href);
                if (String.IsNullOrWhiteSpace(id) || used.Contains(id) || HasStartParameterUltraFast(href) || IsTopicPageJumpHrefUltraFast(href))
                {
                    pos = aEnd + 1;
                    continue;
                }

                int close = IndexOfIgnoreCase(html, "</a>", aEnd + 1);
                if (close < 0 || close - aEnd > 900)
                {
                    pos = aEnd + 1;
                    continue;
                }

                string title = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                if (!IsUsableTopicTitleUltraFast(title))
                    title = CleanTextUltraFast(GetAttributeUltraFast(tag, "title"));

                if (!IsUsableTopicTitleUltraFast(title))
                {
                    pos = close + 4;
                    continue;
                }

                string context = GetBoundedWindowUltraFast(html, aStart, 900);
                if (IsNavigationContextUltraFast(context))
                {
                    pos = close + 4;
                    continue;
                }

                bool pinned = IsPinnedContextUltraFast(context);
                used.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Topic,
                    TypeLabel = pinned ? "закреплено" : "тема",
                    Id = id,
                    Title = title,
                    Info = ExtractTopicInfoUltraFast(context),
                    Url = ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id),
                    IsPinned = pinned
                });

                pos = close + 4;
            }

            return result;
        }

        private static TopicTitleLink ExtractTopicTitleLinkUltraFast(string html, int start, int end, string topicId)
        {
            int pos = start;
            while (pos < end)
            {
                int aStart = IndexOfIgnoreCase(html, "<a", pos);
                if (aStart < 0 || aStart >= end)
                    break;

                int aEnd = html.IndexOf('>', aStart);
                if (aEnd < 0 || aEnd >= end)
                    break;

                string tag = html.Substring(aStart, Math.Min(aEnd - aStart + 1, 1600));
                string href = GetAttributeUltraFast(tag, "href");
                string id = ExtractTopicIdUltraFast(href);
                if (!String.Equals(id, topicId, StringComparison.OrdinalIgnoreCase) || HasStartParameterUltraFast(href) || IsTopicPageJumpHrefUltraFast(href))
                {
                    pos = aEnd + 1;
                    continue;
                }

                int close = IndexOfIgnoreCase(html, "</a>", aEnd + 1);
                if (close < 0 || close > end || close - aEnd > 900)
                {
                    pos = aEnd + 1;
                    continue;
                }

                string title = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                if (!IsUsableTopicTitleUltraFast(title))
                    title = CleanTextUltraFast(GetAttributeUltraFast(tag, "title"));

                if (IsUsableTopicTitleUltraFast(title))
                    return new TopicTitleLink { Href = href, Title = title };

                pos = close + 4;
            }

            return null;
        }

        private static List<PageNavigationItem> ParseForumPaginationUltraFast(string html, string forumId, int currentStart)
        {
            var byStart = new Dictionary<int, PageNavigationItem>();
            var starts = new List<int>();
            AddForumUniqueStart(starts, 0);

            if (String.IsNullOrEmpty(html))
                return new List<PageNavigationItem>();

            string id = ExtractForumIdUltraFast(forumId);
            if (String.IsNullOrWhiteSpace(id))
                id = forumId ?? "";

            int pos = 0;
            while (pos < html.Length && byStart.Count < 200)
            {
                int aStart = IndexOfIgnoreCase(html, "<a", pos);
                if (aStart < 0)
                    break;

                int aEnd = html.IndexOf('>', aStart);
                if (aEnd < 0)
                    break;

                string tag = html.Substring(aStart, Math.Min(aEnd - aStart + 1, 1500));
                string href = GetAttributeUltraFast(tag, "href");
                string hrefForumId = ExtractForumIdUltraFast(href);
                if (!String.Equals(hrefForumId, id, StringComparison.OrdinalIgnoreCase))
                {
                    pos = aEnd + 1;
                    continue;
                }

                int startValue;
                if (!TryExtractStartUltraFast(href, out startValue))
                    startValue = 0;
                startValue = Math.Max(0, startValue);

                int close = IndexOfIgnoreCase(html, "</a>", aEnd + 1);
                if (close < 0 || close - aEnd > 120)
                {
                    pos = aEnd + 1;
                    continue;
                }

                string label = CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
                if (!IsForumPaginationLabel(label, tag) && startValue <= 0)
                {
                    pos = close + 4;
                    continue;
                }

                AddForumUniqueStart(starts, startValue);

                if (!IsNumericTextUltraFast(label))
                {
                    pos = close + 4;
                    continue;
                }

                int pageNumber;
                if (!Int32.TryParse(label, out pageNumber) || pageNumber < 1)
                {
                    pos = close + 4;
                    continue;
                }

                if (!byStart.ContainsKey(startValue))
                {
                    byStart[startValue] = new PageNavigationItem
                    {
                        Kind = PageNavigationKind.Number,
                        Label = pageNumber.ToString(),
                        Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(id) + "&st=" + startValue.ToString(),
                        Start = startValue,
                        IsEnabled = startValue != currentStart
                    };
                }

                pos = close + 4;
            }

            int pageSize = GuessForumPageSize(byStart.Values, starts, currentStart);
            if (pageSize <= 0)
                pageSize = 20;

            int currentPageNumber = Math.Max(1, Math.Max(0, currentStart) / pageSize + 1);
            int maxPageNumber = Math.Max(currentPageNumber, GetMaxPageNumber(byStart.Values));
            int maxStart = Math.Max(0, currentStart);
            foreach (int startValue in starts)
            {
                if (startValue > maxStart)
                    maxStart = startValue;
            }
            maxPageNumber = Math.Max(maxPageNumber, maxStart / pageSize + 1);

            return BuildForumCompactPagination(id, pageSize, currentPageNumber, maxPageNumber);
        }

        private static string ExtractTopicInfoUltraFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string author = ExtractTopicAuthorUltraFast(html);
            if (String.IsNullOrWhiteSpace(author))
                author = ExtractUserNameAfterUltraFast(html, 0);

            string last = ExtractLastTopicUserUltraFast(html);
            if (String.Equals(last, author, StringComparison.OrdinalIgnoreCase))
                last = "";

            string description = ExtractDescriptionUltraFast(html, 120);
            return JoinNonEmpty(" · ", description, Prefix("автор: ", author), Prefix("последний: ", last));
        }

        private static string ExtractTopicAuthorUltraFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int searchFrom = 0;
            while (searchFrom < html.Length)
            {
                int marker = IndexOfIgnoreCase(html, "topic_desc", searchFrom);
                if (marker < 0)
                    break;

                int tagStart = LastIndexOfIgnoreCase(html, "<", marker);
                int tagEnd = tagStart >= 0 ? html.IndexOf('>', tagStart) : -1;
                if (tagEnd < 0)
                {
                    searchFrom = marker + 10;
                    continue;
                }

                int close = FindTopicInfoValueEndUltraFast(html, tagEnd + 1);
                if (close < 0 || close <= tagEnd)
                {
                    searchFrom = tagEnd + 1;
                    continue;
                }

                string value = html.Substring(tagEnd + 1, close - tagEnd - 1);
                string text = CleanTextUltraFast(value);
                if (IndexOfIgnoreCase(text, "автор", 0) >= 0)
                {
                    string user = ExtractUserNameAfterUltraFast(value, 0);
                    if (!String.IsNullOrWhiteSpace(user))
                        return user;

                    user = ExtractTextAfterLabelUltraFast(text, "автор");
                    if (!String.IsNullOrWhiteSpace(user))
                        return user;
                }

                searchFrom = close + 1;
            }

            return "";
        }

        private static string ExtractLastTopicUserUltraFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int marker = IndexOfIgnoreCase(html, "getlastpost", 0);
            if (marker < 0)
                marker = IndexOfIgnoreCase(html, "Послед", 0);

            if (marker >= 0)
            {
                string lastAfterMarker = ExtractUserNameAfterUltraFast(html, marker);
                if (!String.IsNullOrWhiteSpace(lastAfterMarker))
                    return lastAfterMarker;
            }

            int lastUser = -1;
            int pos = 0;
            while (pos < html.Length)
            {
                int user = IndexOfIgnoreCase(html, "showuser=", pos);
                if (user < 0)
                    break;

                lastUser = user;
                pos = user + 9;
            }

            return lastUser >= 0 ? ExtractUserNameAfterUltraFast(html, lastUser) : "";
        }

        private static int FindTopicInfoValueEndUltraFast(string html, int start)
        {
            int spanClose = IndexOfIgnoreCase(html, "</span>", start);
            int br = IndexOfIgnoreCase(html, "<br", start);
            int close = -1;

            if (spanClose >= 0)
                close = spanClose;
            if (br >= 0 && (close < 0 || br < close))
                close = br;

            if (close < 0 || close - start > 1200)
                close = Math.Min(html.Length, start + 400);

            return close;
        }

        private static string ExtractTextAfterLabelUltraFast(string text, string label)
        {
            if (String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(label))
                return "";

            int labelPos = IndexOfIgnoreCase(text, label, 0);
            if (labelPos < 0)
                return "";

            int start = labelPos + label.Length;
            while (start < text.Length && (Char.IsWhiteSpace(text[start]) || text[start] == ':' || text[start] == '-' || text[start] == '—'))
                start++;

            if (start >= text.Length)
                return "";

            string result = text.Substring(start).Trim();
            int lastMarker = IndexOfIgnoreCase(result, "послед", 0);
            if (lastMarker > 0)
                result = result.Substring(0, lastMarker).Trim();

            return result.Trim(' ', ':', '-', '—');
        }

        private static string ExtractUserNameAfterUltraFast(string html, int start)
        {
            int user = IndexOfIgnoreCase(html, "showuser=", start);
            if (user < 0)
                return "";

            int aStart = LastIndexOfIgnoreCase(html, "<a", user);
            int aEnd = aStart >= 0 ? html.IndexOf('>', aStart) : -1;
            int close = aEnd >= 0 ? IndexOfIgnoreCase(html, "</a>", aEnd + 1) : -1;
            if (aStart < 0 || aEnd < 0 || close < 0 || close - aEnd > 300)
                return "";

            return CleanTextUltraFast(html.Substring(aEnd + 1, close - aEnd - 1));
        }

        private static string ExtractDescriptionUltraFast(string html, int maxLength)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string[] markers = new string[] { "topic_desc", "forum_desc", "forum_description", "desc" };
            for (int i = 0; i < markers.Length; i++)
            {
                int marker = IndexOfIgnoreCase(html, markers[i], 0);
                if (marker < 0)
                    continue;

                int tagStart = LastIndexOfIgnoreCase(html, "<", marker);
                int tagEnd = tagStart >= 0 ? html.IndexOf('>', tagStart) : -1;
                if (tagEnd < 0)
                    continue;

                int close = IndexOfIgnoreCase(html, "</", tagEnd + 1);
                if (close < 0 || close - tagEnd > 1200)
                    close = Math.Min(html.Length, tagEnd + 400);

                string text = CleanTextUltraFast(html.Substring(tagEnd + 1, close - tagEnd - 1));
                if (String.IsNullOrWhiteSpace(text))
                    continue;

                if (text.StartsWith("автор", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (text.Length > maxLength)
                    text = text.Substring(0, maxLength).Trim() + "...";
                return text;
            }

            return "";
        }

        private static int FindForumListEndUltraFast(string html, int start)
        {
            int end = IndexOfIgnoreCase(html, "</form>", start);
            if (end >= 0)
                return end;

            end = IndexOfIgnoreCase(html, "topic_foot_nav", start);
            if (end >= 0)
                return end;

            end = IndexOfIgnoreCase(html, "pagination", start);
            if (end >= 0)
                return end;

            return html.Length;
        }

        private static bool IsPinnedContextUltraFast(string context)
        {
            if (String.IsNullOrEmpty(context))
                return false;

            return IndexOfIgnoreCase(context, "pinned", 0) >= 0
                || IndexOfIgnoreCase(context, "important", 0) >= 0
                || IndexOfIgnoreCase(context, "закреп", 0) >= 0
                || IndexOfIgnoreCase(context, "важн", 0) >= 0
                || IndexOfIgnoreCase(context, "announce", 0) >= 0;
        }

        private static bool IsNavigationContextUltraFast(string context)
        {
            if (String.IsNullOrEmpty(context))
                return false;

            return IndexOfIgnoreCase(context, "navstrip", 0) >= 0
                || IndexOfIgnoreCase(context, "breadcrumb", 0) >= 0
                || IndexOfIgnoreCase(context, "pagination", 0) >= 0
                || IndexOfIgnoreCase(context, "pagejump", 0) >= 0
                || IndexOfIgnoreCase(context, "topic_foot_nav", 0) >= 0
                || IndexOfIgnoreCase(context, "formbuttonrow", 0) >= 0;
        }

        private static bool IsUsableForumTitleUltraFast(string title)
        {
            string text = NormalizePlainTextUltraFast(title);
            if (String.IsNullOrWhiteSpace(text) || text.Length < 2)
                return false;

            string lower = text.ToLowerInvariant();
            if (lower == "форум" || lower == "раздел" || lower == "подраздел" || lower == "темы" || lower == "назад" || lower == "вперед" || lower == "вперёд" || lower == "помощь" || lower == "правила" || lower == "поиск" || lower == "страница")
                return false;

            return !IsOnlyDigitsOrPunctuationUltraFast(text);
        }

        private static bool IsUsableTopicTitleUltraFast(string title)
        {
            string text = NormalizePlainTextUltraFast(title);
            if (String.IsNullOrWhiteSpace(text) || text.Length < 2)
                return false;

            string lower = text.ToLowerInvariant();
            if (lower.IndexOf("переход по страницам", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (lower.IndexOf("перейти на страницу", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (lower == "страница" || lower == "страницы" || lower == "page" || lower == "pages" || lower == "next" || lower == "previous" || lower == "last" || lower == "последнее" || lower == "новые")
                return false;
            if (IsNumericTextUltraFast(text))
                return false;

            return !IsOnlyDigitsOrPunctuationUltraFast(text);
        }

        private static bool IsOnlyDigitsOrPunctuationUltraFast(string text)
        {
            bool hasDigit = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (Char.IsLetter(c))
                    return false;
                if (Char.IsDigit(c))
                    hasDigit = true;
                else if (!Char.IsWhiteSpace(c) && ".,:;-–—/\\|«»<>›»()[]{}+".IndexOf(c) < 0)
                    return false;
            }

            return hasDigit || text.Length > 0;
        }

        private static bool IsNumericTextUltraFast(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                if (!Char.IsDigit(text[i]))
                    return false;
            }

            return true;
        }

        private static bool IsTopicPageJumpHrefUltraFast(string href)
        {
            if (String.IsNullOrWhiteSpace(href))
                return false;

            return HasStartParameterUltraFast(href) || IndexOfIgnoreCase(href, "/page/", 0) >= 0;
        }

        private static bool HasStartParameterUltraFast(string href)
        {
            int value;
            return TryExtractStartUltraFast(href, out value);
        }

        private static bool TryExtractStartUltraFast(string href, out int start)
        {
            start = 0;
            string value = href ?? "";
            int p = FindQueryParameterUltraFast(value, "st", 0);
            if (p < 0)
                return false;

            p += 3;
            int numberStart = p;
            while (p < value.Length && Char.IsDigit(value[p]))
                p++;

            return p > numberStart && Int32.TryParse(value.Substring(numberStart, p - numberStart), out start);
        }

        private static string ExtractTopicIdUltraFast(string href)
        {
            string id = ExtractQueryNumberUltraFast(href, "showtopic");
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            return ExtractQueryNumberUltraFast(href, "t");
        }

        private static string ExtractForumIdUltraFast(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string id = ExtractQueryNumberUltraFast(value, "showforum");
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            id = ExtractQueryNumberUltraFast(value, "f");
            if (!String.IsNullOrWhiteSpace(id))
                return id;

            string trimmed = value.Trim();
            bool allDigits = trimmed.Length > 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (!Char.IsDigit(trimmed[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            return allDigits ? trimmed : "";
        }

        private static string ExtractQueryNumberUltraFast(string value, string parameterName)
        {
            if (String.IsNullOrEmpty(value) || String.IsNullOrEmpty(parameterName))
                return "";

            int p = FindQueryParameterUltraFast(value, parameterName, 0);
            if (p < 0)
                return "";

            p += parameterName.Length + 1;
            int numberStart = p;
            while (p < value.Length && Char.IsDigit(value[p]))
                p++;

            return p > numberStart ? value.Substring(numberStart, p - numberStart) : "";
        }

        private static int FindQueryParameterUltraFast(string value, string parameterName, int start)
        {
            string pattern = parameterName + "=";
            int pos = start;
            while (pos < value.Length)
            {
                int found = IndexOfIgnoreCase(value, pattern, pos);
                if (found < 0)
                    return -1;

                bool leftOk = found == 0;
                if (!leftOk)
                {
                    char prev = value[found - 1];
                    leftOk = prev == '?' || prev == '&' || prev == ';' || prev == '/' || prev == ' ';
                    if (!leftOk && found >= 5)
                        leftOk = value.Substring(found - 5, 5).Equals("&amp;", StringComparison.OrdinalIgnoreCase);
                }

                if (leftOk)
                    return found;

                pos = found + pattern.Length;
            }

            return -1;
        }

        private static string ReadNumberAfter(string html, int index)
        {
            if (String.IsNullOrEmpty(html) || index < 0 || index >= html.Length)
                return "";

            int p = html.IndexOf('=', index);
            if (p < 0)
                return "";

            p++;
            while (p < html.Length && (html[p] == '\'' || html[p] == '"' || Char.IsWhiteSpace(html[p])))
                p++;

            int start = p;
            while (p < html.Length && Char.IsDigit(html[p]))
                p++;

            return p > start ? html.Substring(start, p - start) : "";
        }

        private static int FindForumIdInText(string html, string id, int start)
        {
            int p1 = IndexOfIgnoreCase(html, "showforum=" + id, start);
            int p2 = FindQueryParameterWithValueUltraFast(html, "f", id, start);
            if (p1 < 0)
                return p2;
            if (p2 < 0)
                return p1;
            return Math.Min(p1, p2);
        }

        private static int FindQueryParameterWithValueUltraFast(string value, string parameterName, string parameterValue, int start)
        {
            string pattern = parameterName + "=" + parameterValue;
            int pos = start;
            while (pos < value.Length)
            {
                int found = IndexOfIgnoreCase(value, pattern, pos);
                if (found < 0)
                    return -1;

                bool leftOk = found == 0;
                if (!leftOk)
                {
                    char prev = value[found - 1];
                    leftOk = prev == '?' || prev == '&' || prev == ';' || prev == '/' || prev == ' ';
                    if (!leftOk && found >= 5)
                        leftOk = value.Substring(found - 5, 5).Equals("&amp;", StringComparison.OrdinalIgnoreCase);
                }

                int after = found + pattern.Length;
                bool rightOk = after >= value.Length || !Char.IsDigit(value[after]);
                if (leftOk && rightOk)
                    return found;

                pos = found + pattern.Length;
            }

            return -1;
        }

        private static string GetAttributeUltraFast(string tag, string name)
        {
            if (String.IsNullOrEmpty(tag) || String.IsNullOrEmpty(name))
                return "";

            int pos = 0;
            while (pos < tag.Length)
            {
                int found = IndexOfIgnoreCase(tag, name, pos);
                if (found < 0)
                    return "";

                bool leftOk = found == 0 || !IsAttributeNameCharUltraFast(tag[found - 1]);
                int afterName = found + name.Length;
                bool rightOk = afterName < tag.Length && !IsAttributeNameCharUltraFast(tag[afterName]);
                if (!leftOk || !rightOk)
                {
                    pos = afterName;
                    continue;
                }

                int p = afterName;
                while (p < tag.Length && Char.IsWhiteSpace(tag[p]))
                    p++;
                if (p >= tag.Length || tag[p] != '=')
                {
                    pos = afterName;
                    continue;
                }

                p++;
                while (p < tag.Length && Char.IsWhiteSpace(tag[p]))
                    p++;
                if (p >= tag.Length)
                    return "";

                char quote = '\0';
                if (tag[p] == '\'' || tag[p] == '"')
                {
                    quote = tag[p];
                    p++;
                }

                int start = p;
                if (quote != '\0')
                {
                    while (p < tag.Length && tag[p] != quote)
                        p++;
                }
                else
                {
                    while (p < tag.Length && !Char.IsWhiteSpace(tag[p]) && tag[p] != '>')
                        p++;
                }

                return DecodeEntitiesFast(tag.Substring(start, p - start));
            }

            return "";
        }

        private static bool IsAttributeNameCharUltraFast(char c)
        {
            return Char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ':';
        }

        private static string CleanTextUltraFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            var builder = new System.Text.StringBuilder(html.Length);
            bool lastWasSpace = false;
            int i = 0;
            while (i < html.Length)
            {
                char c = html[i];
                if (c == '<')
                {
                    if (StartsWithIgnoreCase(html, i, "<br"))
                        AppendSpaceUltraFast(builder, ref lastWasSpace);

                    if (StartsWithIgnoreCase(html, i, "<script"))
                    {
                        int scriptEnd = IndexOfIgnoreCase(html, "</script>", i + 7);
                        i = scriptEnd >= 0 ? scriptEnd + 9 : html.Length;
                        continue;
                    }

                    if (StartsWithIgnoreCase(html, i, "<style"))
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

                if (c == '&')
                {
                    string entity = DecodeEntityAtUltraFast(html, i, out i);
                    if (entity == " ")
                        AppendSpaceUltraFast(builder, ref lastWasSpace);
                    else
                    {
                        builder.Append(entity);
                        lastWasSpace = false;
                    }
                    continue;
                }

                if (Char.IsWhiteSpace(c) || c == '\u00A0')
                {
                    AppendSpaceUltraFast(builder, ref lastWasSpace);
                    i++;
                    continue;
                }

                builder.Append(c);
                lastWasSpace = false;
                i++;
            }

            return builder.ToString().Trim();
        }

        private static string NormalizePlainTextUltraFast(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            return CleanTextUltraFast(value);
        }

        private static string DecodeEntityAtUltraFast(string text, int index, out int nextIndex)
        {
            nextIndex = index + 1;
            int end = text.IndexOf(';', index + 1);
            if (end < 0 || end - index > 12)
                return "&";

            string entity = text.Substring(index, end - index + 1);
            nextIndex = end + 1;
            if (entity.Equals("&amp;", StringComparison.OrdinalIgnoreCase))
                return "&";
            if (entity.Equals("&quot;", StringComparison.OrdinalIgnoreCase))
                return "\"";
            if (entity.Equals("&#39;", StringComparison.OrdinalIgnoreCase) || entity.Equals("&#039;", StringComparison.OrdinalIgnoreCase) || entity.Equals("&apos;", StringComparison.OrdinalIgnoreCase))
                return "'";
            if (entity.Equals("&lt;", StringComparison.OrdinalIgnoreCase))
                return "<";
            if (entity.Equals("&gt;", StringComparison.OrdinalIgnoreCase))
                return ">";
            if (entity.Equals("&raquo;", StringComparison.OrdinalIgnoreCase) || entity.Equals("&#187;", StringComparison.OrdinalIgnoreCase))
                return "»";
            if (entity.Equals("&laquo;", StringComparison.OrdinalIgnoreCase) || entity.Equals("&#171;", StringComparison.OrdinalIgnoreCase))
                return "«";
            if (entity.Equals("&nbsp;", StringComparison.OrdinalIgnoreCase))
                return " ";

            if (entity.Length > 3 && entity[1] == '#')
            {
                int code;
                if (Int32.TryParse(entity.Substring(2, entity.Length - 3), out code) && code > 0 && code <= 65535)
                    return new string((char)code, 1);
            }

            return entity;
        }

        private static void AppendSpaceUltraFast(System.Text.StringBuilder builder, ref bool lastWasSpace)
        {
            if (builder.Length == 0 || lastWasSpace)
                return;

            builder.Append(' ');
            lastWasSpace = true;
        }

        private static string GetBoundedWindowUltraFast(string html, int index, int radius)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            int start = Math.Max(0, index - radius);
            int end = Math.Min(html.Length, index + radius);
            return html.Substring(start, end - start);
        }

        private static string NormalizeUrlUltraFast(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return "";

            url = DecodeEntitiesFast(url.Trim());
            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;

            Uri absolute;
            if (Uri.TryCreate(url, UriKind.Absolute, out absolute))
                return absolute.ToString();

            Uri baseUri = url.IndexOf("forum/", StringComparison.OrdinalIgnoreCase) >= 0
                ? new Uri("https://" + Host + "/forum/")
                : new Uri("https://" + Host + "/forum/index.php");

            return new Uri(baseUri, url).ToString();
        }

        private static int IndexOfIgnoreCase(string source, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrEmpty(value) || startIndex >= source.Length)
                return -1;

            if (startIndex < 0)
                startIndex = 0;

            return source.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
        }

        private static int LastIndexOfIgnoreCase(string source, string value, int startIndex)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrEmpty(value))
                return -1;

            if (startIndex >= source.Length)
                startIndex = source.Length - 1;

            if (startIndex < 0)
                return -1;

            return source.LastIndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWithIgnoreCase(string source, int index, string value)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrEmpty(value) || index < 0 || index + value.Length > source.Length)
                return false;

            return String.Compare(source, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string ParseForumTitleFast(string html, string forumId)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match titleMatch = Regex.Match(
                html,
                @"<a\b[^>]*?href\s*=\s*['"" ][^'"" >]*(?:showforum|f)=" + Regex.Escape(forumId ?? "") + @"(?:&|&amp;|['"" >])[^>]*>(?<title>[\s\S]*?)</a>",
                options);
            if (titleMatch.Success)
            {
                string title = CleanTextFast(titleMatch.Groups["title"].Value);
                if (!String.IsNullOrWhiteSpace(title))
                    return title;
            }

            Match titleTag = Regex.Match(html, @"<title[^>]*>(?<value>[\s\S]*?)</title>", options);
            if (titleTag.Success)
            {
                string title = CleanTextFast(titleTag.Groups["value"].Value);
                title = Regex.Replace(title, @"\s*[-–—]\s*4PDA.*$", "", RegexOptions.IgnoreCase).Trim();
                if (!String.IsNullOrWhiteSpace(title))
                    return title;
            }

            return "";
        }

        private static List<ForumListItem> ParseAnnouncesFast(string html)
        {
            var result = new List<ForumListItem>();
            if (String.IsNullOrEmpty(html))
                return result;

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection matches = Regex.Matches(
                html,
                @"<div[^>]*?anonce_body[^>]*?>[\s\S]*?<a[^>]*?href\s*=\s*['""](?<href>[^'"" ]*?)['"" ][^>]*>(?<title>[\s\S]*?)</a>[\s\S]*?</div>",
                options);

            foreach (Match match in matches)
            {
                string title = CleanTextFast(match.Groups["title"].Value);
                if (String.IsNullOrWhiteSpace(title))
                    continue;

                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Announce,
                    TypeLabel = "объявление",
                    Title = title,
                    Info = "нажмите, чтобы открыть",
                    Url = NormalizeUrl(match.Groups["href"].Value)
                });
            }

            return result;
        }

        private static List<ForumListItem> ParseSubForumsFast(string html, string currentForumId)
        {
            var result = new List<ForumListItem>();
            var usedIds = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection rows = Regex.Matches(
                html,
                @"<div[^>]*?\b(?:class|id)\s*=\s*['"" ][^'"" >]*(?:board_forum_row|forum_row|forum-row|subforum|forums-row)[^'"" >]*['"" ][^>]*>(?<body>[\s\S]*?)(?=<div[^>]*?\b(?:class|id)\s*=\s*['"" ][^'"" >]*(?:board_forum_row|forum_row|forum-row|subforum|forums-row|topic|pagination|topic_foot_nav)[^'"" >]*['"" ]|</form>|<!--\s*TABLE FOOTER)",
                options);

            foreach (Match row in rows)
            {
                string body = row.Groups["body"].Value;
                Match link = Regex.Match(
                    body,
                    @"<a\b(?<attrs>[^>]*)href\s*=\s*['""](?<href>[^'"" >]*(?:showforum|f)=\d+[^'"" >]*)['""][^>]*>(?<title>[\s\S]*?)</a>",
                    options);

                if (!link.Success)
                    continue;

                string href = link.Groups["href"].Value;
                string id = ExtractForumId(href);
                if (String.IsNullOrWhiteSpace(id) || String.Equals(id, currentForumId, StringComparison.OrdinalIgnoreCase) || usedIds.Contains(id))
                    continue;

                string title = CleanTextFast(link.Groups["title"].Value);
                if (!IsUsableForumTitleFast(title))
                    continue;

                usedIds.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Forum,
                    TypeLabel = "раздел",
                    Id = id,
                    Title = title,
                    Info = ExtractForumDescriptionFast(body),
                    Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(id)
                });
            }

            return result;
        }

        private static List<ForumListItem> ParseTopicsFast(string html)
        {
            var result = new List<ForumListItem>();
            var usedIds = new HashSet<string>();
            if (String.IsNullOrEmpty(html))
                return result;

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection blocks = Regex.Matches(
                html,
                @"<div[^>]*?\bdata-topic\s*=\s*['""](?<id>\d+)['""][^>]*>(?<body>[\s\S]*?)(?=<div[^>]*?\bdata-topic\s*=|</form>|<!--\s*TABLE FOOTER)",
                options);

            foreach (Match block in blocks)
            {
                string id = block.Groups["id"].Value;
                if (String.IsNullOrWhiteSpace(id) || usedIds.Contains(id))
                    continue;

                string body = block.Groups["body"].Value;
                Match selectedLink = null;
                string selectedHref = "";
                string selectedTitle = "";
                MatchCollection topicLinks = Regex.Matches(
                    body,
                    @"<a\b(?<attrs>[^>]*)href\s*=\s*['""](?<href>[^'"" >]*(?:showtopic|t)=" + Regex.Escape(id) + @"[^'"" >]*)['""][^>]*>(?<title>[\s\S]*?)</a>",
                    options);

                foreach (Match link in topicLinks)
                {
                    string candidateHref = DecodeEntitiesFast(link.Groups["href"].Value);
                    if (HasStartParameter(candidateHref))
                        continue;

                    string candidateTitle = CleanTextFast(link.Groups["title"].Value);
                    if (!IsUsableTopicTitleFast(candidateTitle))
                    {
                        string titleAttribute = GetAttributeFast(link.Groups["attrs"].Value, "title");
                        if (IsUsableTopicTitleFast(titleAttribute))
                            candidateTitle = titleAttribute;
                    }

                    if (!IsUsableTopicTitleFast(candidateTitle))
                        continue;

                    selectedLink = link;
                    selectedHref = candidateHref;
                    selectedTitle = candidateTitle;
                    break;
                }

                if (selectedLink == null)
                    continue;

                string href = selectedHref;
                string title = selectedTitle;

                bool isPinned = IsPinnedContextFast(body);
                string modifier = CleanTextFast(Regex.Match(body, @"<span[^>]*?\bclass\s*=\s*['"" ][^'"" >]*modifier[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)</span>", options).Groups["value"].Value);
                string flags = BuildTopicFlags(
                    modifier.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0,
                    modifier.IndexOf("^", StringComparison.OrdinalIgnoreCase) >= 0,
                    modifier.IndexOf("Х", StringComparison.OrdinalIgnoreCase) >= 0 || modifier.IndexOf("x", StringComparison.OrdinalIgnoreCase) >= 0);
                string description = ExtractTopicDescriptionFast(body);
                List<UserLink> users = ExtractUserLinksFast(body);
                string author = ExtractTopicAuthorFast(body);
                if (String.IsNullOrWhiteSpace(author))
                    author = users.Count > 0 ? users[0].Name : "";
                string lastUser = ExtractLastTopicUserFast(body);
                if (String.IsNullOrWhiteSpace(lastUser))
                    lastUser = users.Count > 1 ? users[users.Count - 1].Name : "";
                if (String.Equals(lastUser, author, StringComparison.OrdinalIgnoreCase))
                    lastUser = "";
                string date = ExtractLastDateFast(body, users.Count > 0 ? users[users.Count - 1].EndIndex : -1);

                usedIds.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Topic,
                    TypeLabel = isPinned ? "закреплено" : "тема",
                    Id = id,
                    Title = title,
                    Info = JoinNonEmpty(" · ", flags, description, Prefix("автор: ", author), Prefix("последний: ", lastUser), date),
                    Url = ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id),
                    IsPinned = isPinned
                });
            }

            return result;
        }

        private static List<PageNavigationItem> ParseForumPaginationFast(string html, string forumId, int currentStart)
        {
            var byStart = new Dictionary<int, PageNavigationItem>();
            var starts = new List<int>();
            AddForumUniqueStart(starts, 0);

            if (String.IsNullOrEmpty(html))
                return new List<PageNavigationItem>();

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            string pattern = @"<a\b(?<attrs>[^>]*)href\s*=\s*['""](?<href>[^'"" >]*(?:showforum|f)=" + Regex.Escape(forumId ?? "") + @"[^'"" >]*)['""][^>]*>(?<text>[\s\S]*?)</a>";
            MatchCollection links = Regex.Matches(html, pattern, options);
            foreach (Match link in links)
            {
                string href = DecodeEntitiesFast(link.Groups["href"].Value);
                int startValue;
                if (!TryExtractStart(href, out startValue))
                    startValue = 0;
                startValue = Math.Max(0, startValue);

                string label = CleanTextFast(link.Groups["text"].Value);
                string linkHtml = link.Value;
                if (!IsForumPaginationLabel(label, linkHtml) && startValue <= 0)
                    continue;

                AddForumUniqueStart(starts, startValue);

                if (GetPageNavigationKind(label, linkHtml) != PageNavigationKind.Number)
                    continue;

                string numberLabel = ExtractPageNumberLabel(label, startValue);
                int pageNumber;
                if (!Int32.TryParse(numberLabel, out pageNumber) || pageNumber < 1)
                    continue;

                if (!byStart.ContainsKey(startValue))
                {
                    byStart[startValue] = new PageNavigationItem
                    {
                        Kind = PageNavigationKind.Number,
                        Label = pageNumber.ToString(),
                        Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(forumId) + "&st=" + startValue.ToString(),
                        Start = startValue,
                        IsEnabled = startValue != currentStart
                    };
                }
            }

            int pageSize = GuessForumPageSize(byStart.Values, starts, currentStart);
            if (pageSize <= 0)
                pageSize = 20;

            int currentPageNumber = Math.Max(1, Math.Max(0, currentStart) / pageSize + 1);
            int maxPageNumber = Math.Max(currentPageNumber, GetMaxPageNumber(byStart.Values));
            int maxStart = Math.Max(0, currentStart);
            foreach (int startValue in starts)
            {
                if (startValue > maxStart)
                    maxStart = startValue;
            }
            maxPageNumber = Math.Max(maxPageNumber, maxStart / pageSize + 1);

            return BuildForumCompactPagination(forumId, pageSize, currentPageNumber, maxPageNumber);
        }

        private static string ExtractForumDescriptionFast(string html)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match match = Regex.Match(html ?? "", @"<div[^>]*?\bclass\s*=\s*['"" ][^'"" >]*(?:desc|forum_desc|forum_description)[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)</div>", options);
            if (!match.Success)
                return "";

            string text = CleanTextFast(match.Groups["value"].Value);
            return text.Length > 180 ? text.Substring(0, 180).Trim() + "..." : text;
        }

        private static string ExtractTopicDescriptionFast(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"<span[^>]*?\bclass\s*=\s*['"" ][^'"" >]*topic_desc[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)(?:<br\s*/?>|</span>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string text = CleanTextFast(match.Groups["value"].Value);
                if (String.IsNullOrWhiteSpace(text))
                    continue;

                if (text.StartsWith("автор", StringComparison.OrdinalIgnoreCase))
                    continue;

                return text.Length > 150 ? text.Substring(0, 150).Trim() + "..." : text;
            }

            return "";
        }

        private static string ExtractTopicAuthorFast(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"<span[^>]*topic_desc[^>]*>(?<value>[\s\S]*?)(?:<br\s*/?>|</span>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string value = match.Groups["value"].Value;
                string text = CleanTextFast(value);
                if (IndexOfIgnoreCase(text, "автор", 0) < 0)
                    continue;

                Match user = Regex.Match(value, @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (user.Success)
                {
                    string name = CleanTextFast(user.Groups["name"].Value);
                    if (!String.IsNullOrWhiteSpace(name))
                        return name;
                }

                string afterLabel = ExtractTextAfterLabel(text, "автор");
                if (!String.IsNullOrWhiteSpace(afterLabel))
                    return afterLabel;
            }

            return "";
        }

        private static string ExtractLastTopicUserFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int marker = IndexOfIgnoreCase(html, "getlastpost", 0);
            if (marker < 0)
                marker = IndexOfIgnoreCase(html, "Послед", 0);

            string scope = marker >= 0 ? html.Substring(marker) : html;
            MatchCollection matches = Regex.Matches(scope, @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0)
                return "";

            string name = CleanTextFast(matches[0].Groups["name"].Value);
            return String.IsNullOrWhiteSpace(name) ? "" : name;
        }

        private static string ExtractTextAfterLabel(string text, string label)
        {
            if (String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(label))
                return "";

            int labelPos = IndexOfIgnoreCase(text, label, 0);
            if (labelPos < 0)
                return "";

            int start = labelPos + label.Length;
            while (start < text.Length && (Char.IsWhiteSpace(text[start]) || text[start] == ':' || text[start] == '-' || text[start] == '—'))
                start++;

            if (start >= text.Length)
                return "";

            string result = text.Substring(start).Trim();
            int lastMarker = IndexOfIgnoreCase(result, "послед", 0);
            if (lastMarker > 0)
                result = result.Substring(0, lastMarker).Trim();

            return result.Trim(' ', ':', '-', '—');
        }

        private static List<UserLink> ExtractUserLinksFast(string html)
        {
            var result = new List<UserLink>();
            MatchCollection matches = Regex.Matches(html ?? "", @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string name = CleanTextFast(match.Groups["name"].Value);
                if (String.IsNullOrWhiteSpace(name))
                    continue;

                result.Add(new UserLink
                {
                    Id = match.Groups["id"].Value,
                    Name = name,
                    EndIndex = match.Index + match.Length
                });
            }

            return result;
        }

        private static string ExtractLastDateFast(string html, int startIndex)
        {
            if (String.IsNullOrWhiteSpace(html) || startIndex < 0 || startIndex >= html.Length)
                return "";

            string tail = html.Substring(startIndex);
            Match match = Regex.Match(tail, @"^(?<value>[^<]{1,100})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return "";

            string text = CleanTextFast(match.Groups["value"].Value);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 80)
                text = text.Substring(0, 80).Trim();

            return text;
        }

        private static bool IsPinnedContextFast(string context)
        {
            string value = context ?? "";
            return Regex.IsMatch(value, @"(?:pin|pinned|important|fixed|закреп|важн|class\s*=\s*['"" ][^'"" >]*(?:announce|pinned|important|fixed))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool IsUsableForumTitleFast(string title)
        {
            string text = NormalizePlainTextFast(title);
            if (String.IsNullOrWhiteSpace(text) || text.Length < 2)
                return false;

            if (Regex.IsMatch(text, @"^(?:форум|раздел|подраздел|темы|назад|вперед|вперёд|новые|помощь|правила|поиск|страница|page|pages)$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[\d\s\.,:;\-–—/\\|«»<>]+$", RegexOptions.IgnoreCase))
                return false;

            return true;
        }

        private static bool IsUsableTopicTitleFast(string title)
        {
            string text = NormalizePlainTextFast(title);
            if (String.IsNullOrWhiteSpace(text))
                return false;

            if (Regex.IsMatch(text, @"^(?:[»›>]+\s*)?\d+$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[»›>]+\s*\d+", RegexOptions.IgnoreCase))
                return false;

            if (text.IndexOf("переход по страницам", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (text.IndexOf("перейти на страницу", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (Regex.IsMatch(text, @"^(?:страниц[аы]?|стр\.?|page|pages|перейти|последнее|новые|last|next|previous)\s*\d*$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[\d\s\.,:;\-–—/\\|«»<>]+$", RegexOptions.IgnoreCase))
                return false;

            return true;
        }

        private static string GetAttributeFast(string attributes, string name)
        {
            if (String.IsNullOrWhiteSpace(attributes) || String.IsNullOrWhiteSpace(name))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            string escaped = Regex.Escape(name);
            Match match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*""(?<value>[^""]*)""", options);
            if (!match.Success)
                match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*'(?<value>[^']*)'", options);
            if (!match.Success)
                match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*(?<value>[^\s>]+)", options);

            return match.Success ? DecodeEntitiesFast(match.Groups["value"].Value) : "";
        }

        private static string CleanTextFast(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string prepared = Regex.Replace(html, @"<\s*br\s*/?\s*>", " ", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"<[^>]+>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return NormalizePlainTextFast(prepared);
        }

        private static string NormalizePlainTextFast(string value)
        {
            string text = DecodeEntitiesFast(value ?? "");
            text = text.Replace("\u00A0", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private static string DecodeEntitiesFast(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            return value
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&#039;", "'")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&raquo;", "»")
                .Replace("&#187;", "»")
                .Replace("&laquo;", "«")
                .Replace("&#171;", "«")
                .Replace("&nbsp;", " ");
        }

        private static ForumData ParseForumPage(string html, string forumId, int currentStart)
        {
            var data = new ForumData();
            data.Title = ParseForumTitle(html, forumId);
            data.Pages = ParseForumPagination(html, forumId, currentStart);

            foreach (ForumListItem item in ParseAnnounces(html))
                data.AddUnique(item);

            foreach (ForumListItem item in ParseSubForums(html, forumId))
                data.AddUnique(item);

            List<ForumListItem> topics = ParseTopics(html);
            foreach (ForumListItem item in topics.Where(t => t.IsPinned))
                data.AddUnique(item);

            foreach (ForumListItem item in topics.Where(t => !t.IsPinned))
                data.AddUnique(item);

            return data;
        }

        private static string ParseForumTitle(string html, string forumId)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match navstrip = Regex.Match(html ?? "", @"<div[^>]*?\bclass\s*=\s*['"" ][^'"" >]*navstrip[^'"" >]*['"" ][^>]*>(?<body>[\s\S]*?)</div>", options);
            string source = navstrip.Success ? navstrip.Groups["body"].Value : html;

            MatchCollection links = Regex.Matches(source ?? "", @"<a[^>]*?href\s*=\s*['"" ][^'"" >]*showforum=(?<id>\d+)[^'"" >]*['"" ][^>]*>(?<title>[\s\S]*?)</a>", options);
            string lastTitle = "";
            foreach (Match link in links)
            {
                string title = CleanText(link.Groups["title"].Value);
                if (String.IsNullOrWhiteSpace(title))
                    continue;

                lastTitle = title;
                if (String.Equals(link.Groups["id"].Value, forumId, StringComparison.OrdinalIgnoreCase))
                    return title;
            }

            Match titleTag = Regex.Match(html ?? "", @"<title[^>]*>(?<value>[\s\S]*?)</title>", options);
            if (titleTag.Success)
            {
                string title = CleanText(titleTag.Groups["value"].Value);
                title = Regex.Replace(title, @"\s*[-–—]\s*4PDA.*$", "", RegexOptions.IgnoreCase).Trim();
                if (!String.IsNullOrWhiteSpace(title))
                    return title;
            }

            return lastTitle;
        }

        private static List<ForumListItem> ParseAnnounces(string html)
        {
            var result = new List<ForumListItem>();
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection matches = Regex.Matches(html ?? "", @"<div[^>]*?anonce_body[^>]*?>[\s\S]*?<a[^>]*?href\s*=\s*['""](?<href>[^'"" ]*?)['"" ][^>]*>(?<title>[\s\S]*?)</a>[\s\S]*?</div>", options);

            foreach (Match match in matches)
            {
                string title = CleanText(match.Groups["title"].Value);
                string url = NormalizeUrl(match.Groups["href"].Value);
                if (String.IsNullOrWhiteSpace(title))
                    continue;

                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Announce,
                    TypeLabel = "объявление",
                    Title = title,
                    Info = "нажмите, чтобы открыть",
                    Url = url
                });
            }

            return result;
        }

        private static List<ForumListItem> ParseSubForums(string html, string currentForumId)
        {
            var result = new List<ForumListItem>();
            var usedIds = new HashSet<string>();
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection rows = Regex.Matches(
                html ?? "",
                @"<div[^>]*?\b(?:class|id)\s*=\s*['"" ][^'"" >]*(?:board_forum_row|forum_row|forum-row|subforum|forums-row)[^'"" >]*['"" ][^>]*>(?<body>[\s\S]*?)(?=<div[^>]*?\b(?:class|id)\s*=\s*['"" ][^'"" >]*(?:board_forum_row|forum_row|forum-row|subforum|forums-row|topic|pagination|topic_foot_nav)[^'"" >]*['"" ]|</form>|<!--\s*TABLE FOOTER)",
                options);

            foreach (Match row in rows)
            {
                string body = row.Groups["body"].Value;
                HtmlLink link = ExtractLinks(body).FirstOrDefault(l => IsForumHref(l.Href, currentForumId) && IsUsableForumTitle(l.Text, l.Href));
                if (link == null)
                    continue;

                string id = ExtractForumId(link.Href);
                if (usedIds.Contains(id))
                    continue;

                usedIds.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Forum,
                    TypeLabel = "раздел",
                    Id = id,
                    Title = link.Text,
                    Info = ExtractForumDescription(body),
                    Url = NormalizeUrl(link.Href)
                });
            }

            foreach (ForumListItem item in ParseSubForumsByLinks(html, currentForumId))
            {
                if (!usedIds.Contains(item.Id))
                {
                    usedIds.Add(item.Id);
                    result.Add(item);
                }
            }

            return result;
        }

        private static List<ForumListItem> ParseSubForumsByLinks(string html, string currentForumId)
        {
            var result = new List<ForumListItem>();
            var bestById = new Dictionary<string, ForumLinkCandidate>();

            foreach (HtmlLink link in ExtractLinks(html))
            {
                if (!IsForumHref(link.Href, currentForumId))
                    continue;

                string id = ExtractForumId(link.Href);
                string title = link.Text;
                if (!IsUsableForumTitle(title, link.Href))
                    continue;

                string context = GetHtmlWindow(html, link.Index, 700);
                if (IsNavigationContext(context))
                    continue;

                int score = GetForumLinkScore(link.Html, context, title);
                if (score < 15)
                    continue;

                ForumLinkCandidate old;
                if (!bestById.TryGetValue(id, out old) || score > old.Score)
                {
                    bestById[id] = new ForumLinkCandidate
                    {
                        Id = id,
                        Href = link.Href,
                        Title = title,
                        Context = context,
                        Score = score,
                        Index = link.Index
                    };
                }
            }

            foreach (ForumLinkCandidate candidate in bestById.Values.OrderBy(v => v.Index))
            {
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Forum,
                    TypeLabel = "раздел",
                    Id = candidate.Id,
                    Title = candidate.Title,
                    Info = ExtractForumDescription(candidate.Context),
                    Url = NormalizeUrl(candidate.Href)
                });
            }

            return result;
        }

        private static bool IsForumHref(string href, string currentForumId)
        {
            string id = ExtractForumId(href);
            if (String.IsNullOrWhiteSpace(id))
                return false;

            if (String.Equals(id, currentForumId, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static List<ForumListItem> ParseTopics(string html)
        {
            var result = new List<ForumListItem>();
            var usedIds = new HashSet<string>();
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            MatchCollection blocks = Regex.Matches(
                html ?? "",
                @"<div[^>]*?\bdata-topic\s*=\s*['""](?<id>\d+)['""][^>]*>(?<body>[\s\S]*?)(?=<div[^>]*?\bdata-topic\s*=|</form>|<!--\s*TABLE FOOTER)",
                options);

            foreach (Match block in blocks)
            {
                string id = block.Groups["id"].Value;
                if (usedIds.Contains(id))
                    continue;

                string body = RemoveTopicPagingHtml(block.Groups["body"].Value);
                TopicTitleLink titleLink = ExtractTopicTitleLink(body, id);
                if (titleLink == null || String.IsNullOrWhiteSpace(titleLink.Title))
                    continue;

                bool isPinned = IsPinnedContext(body);
                string modifier = ExtractModifier(body);
                string flags = BuildTopicFlags(
                    modifier.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0,
                    modifier.IndexOf("^", StringComparison.OrdinalIgnoreCase) >= 0,
                    modifier.IndexOf("Х", StringComparison.OrdinalIgnoreCase) >= 0 || modifier.IndexOf("x", StringComparison.OrdinalIgnoreCase) >= 0);
                string description = ExtractTopicDescription(body);
                List<UserLink> users = ExtractUserLinks(body);
                string author = ExtractTopicAuthor(body);
                if (String.IsNullOrWhiteSpace(author))
                    author = users.Count > 0 ? users[0].Name : "";
                string lastUser = ExtractLastTopicUser(body);
                if (String.IsNullOrWhiteSpace(lastUser))
                    lastUser = users.Count > 1 ? users[users.Count - 1].Name : "";
                if (String.Equals(lastUser, author, StringComparison.OrdinalIgnoreCase))
                    lastUser = "";
                string date = ExtractLastDate(body, users.Count > 0 ? users[users.Count - 1].EndIndex : -1);

                usedIds.Add(id);
                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Topic,
                    TypeLabel = isPinned ? "закреплено" : "тема",
                    Id = id,
                    Title = titleLink.Title,
                    Info = JoinNonEmpty(" · ", flags, description, Prefix("автор: ", author), Prefix("последний: ", lastUser), date),
                    Url = NormalizeTopicUrl(titleLink.Href, id),
                    IsPinned = isPinned
                });
            }

            foreach (ForumListItem item in ParseTopicsByLinks(html))
            {
                if (!usedIds.Contains(item.Id))
                {
                    usedIds.Add(item.Id);
                    result.Add(item);
                }
            }

            return result;
        }

        private static List<ForumListItem> ParseTopicsByLinks(string html)
        {
            var bestById = new Dictionary<string, TopicLinkCandidate>();

            foreach (HtmlLink link in ExtractLinks(html))
            {
                if (IsTopicMiniPageLink(link))
                    continue;

                string decodedHref = DecodeEntities(link.Href ?? "");
                Match idMatch = Regex.Match(decodedHref, @"(?:showtopic|t)=(?<id>\d+)", RegexOptions.IgnoreCase);
                if (!idMatch.Success)
                    continue;

                string id = idMatch.Groups["id"].Value;
                string title = ChooseTopicTitle(link);
                if (!IsUsableTopicTitle(title, link.Href))
                    continue;

                string context = GetHtmlWindow(html, link.Index, 1000);
                int score = GetTopicTitleLinkScore(link.Html, title, link.Href) + GetTopicContextScore(context);
                if (IsNavigationContext(context) && score < 130)
                    continue;

                if (score < 25)
                    continue;

                TopicLinkCandidate old;
                if (!bestById.TryGetValue(id, out old) || score > old.Score)
                {
                    bestById[id] = new TopicLinkCandidate
                    {
                        Id = id,
                        Href = link.Href,
                        Title = title,
                        Context = context,
                        Score = score,
                        Index = link.Index
                    };
                }
            }

            var result = new List<ForumListItem>();
            foreach (TopicLinkCandidate candidate in bestById.Values.OrderBy(v => v.Index))
            {
                bool isPinned = IsPinnedContext(candidate.Context);
                string description = ExtractTopicDescription(candidate.Context);
                List<UserLink> users = ExtractUserLinks(candidate.Context);
                string author = ExtractTopicAuthor(candidate.Context);
                if (String.IsNullOrWhiteSpace(author))
                    author = users.Count > 0 ? users[0].Name : "";
                string lastUser = ExtractLastTopicUser(candidate.Context);
                if (String.IsNullOrWhiteSpace(lastUser))
                    lastUser = users.Count > 1 ? users[users.Count - 1].Name : "";
                if (String.Equals(lastUser, author, StringComparison.OrdinalIgnoreCase))
                    lastUser = "";

                result.Add(new ForumListItem
                {
                    Kind = ForumItemKind.Topic,
                    TypeLabel = isPinned ? "закреплено" : "тема",
                    Id = candidate.Id,
                    Title = candidate.Title,
                    Info = JoinNonEmpty(" · ", description, Prefix("автор: ", author), Prefix("последний: ", lastUser)),
                    Url = NormalizeTopicUrl(candidate.Href, candidate.Id),
                    IsPinned = isPinned
                });
            }

            return result;
        }

        private static TopicTitleLink ExtractTopicTitleLink(string html, string topicId)
        {
            TopicTitleLink best = null;
            int bestScore = Int32.MinValue;

            foreach (HtmlLink link in ExtractLinks(html))
            {
                if (IsTopicMiniPageLink(link))
                    continue;

                string href = DecodeEntities(link.Href ?? "");
                if (IsTopicPageJumpHref(href))
                    continue;

                Match idMatch = Regex.Match(href, @"(?:showtopic|t)=(?<id>\d+)", RegexOptions.IgnoreCase);
                if (!idMatch.Success || !String.Equals(idMatch.Groups["id"].Value, topicId, StringComparison.OrdinalIgnoreCase))
                    continue;

                string title = ChooseTopicTitle(link);
                if (!IsUsableTopicTitle(title, href))
                    continue;

                int score = GetTopicTitleLinkScore(link.Html, title, href);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                best = new TopicTitleLink { Href = href, Title = title };
            }

            return best;
        }

        private static string ChooseTopicTitle(HtmlLink link)
        {
            string title = link.Text;
            if (IsUsableTopicTitle(title, link.Href))
                return title;

            if (IsUsableTopicTitle(link.TitleAttribute, link.Href))
                return link.TitleAttribute;

            return title;
        }

        private static string RemoveTopicPagingHtml(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            string result = html;

            result = Regex.Replace(
                result,
                @"<a\b(?=[^>]*?href\s*=\s*['""][^'"" >]*(?:showtopic|t)=\d+[^'"" >]*(?:(?:&amp;|&)st=\d+|/page/\d+)[^'"" >]*['""])[^>]*>[\s\S]*?</a>",
                " ",
                options);

            result = Regex.Replace(
                result,
                @"<(?:span|div|p|ul|li)\b[^>]*?class\s*=\s*['""][^'""]*(?:topic_pages|topic-pages|topic-page|mini_pages|mini-pages|minipages|pagination|pagejump|pagelink|pager|pages)[^'""]*['"" ][^>]*>[\s\S]*?</(?:span|div|p|ul|li)>",
                " ",
                options);

            return result;
        }

        private static bool IsTopicPageJumpHref(string href)
        {
            string decodedHref = DecodeEntities(href ?? "");
            if (!Regex.IsMatch(decodedHref, @"(?:showtopic|t)=\d+", RegexOptions.IgnoreCase))
                return false;

            if (HasStartParameter(decodedHref))
                return true;

            if (Regex.IsMatch(decodedHref, @"/page/\d+", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private static bool IsTopicMiniPageLink(HtmlLink link)
        {
            if (link == null)
                return false;

            string title = NormalizePlainText(link.Text);
            string href = DecodeEntities(link.Href ?? "");
            string html = link.Html ?? "";

            if (IsTopicPageJumpHref(href))
                return true;

            if (IsPagingLinkHtml(html))
                return true;

            if (Regex.IsMatch(title, @"^(?:[»›>]+\s*)?\d+$", RegexOptions.IgnoreCase))
                return true;

            if (Regex.IsMatch(title, @"^[»›>]+\s*\d+", RegexOptions.IgnoreCase))
                return true;

            if ((HasStartParameter(href) || Regex.IsMatch(href, @"/page/\d+", RegexOptions.IgnoreCase)) && title.Length <= 32 && Regex.IsMatch(title, @"\d"))
                return true;

            return false;
        }

        private static bool IsUsableTopicTitle(string title, string href)
        {
            if (String.IsNullOrWhiteSpace(title))
                return false;

            string text = NormalizePlainText(title);
            if (String.IsNullOrWhiteSpace(text))
                return false;

            if (Regex.IsMatch(text, @"^(?:[»›>]+\s*)?\d+$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[»›>]+\s*\d+", RegexOptions.IgnoreCase))
                return false;

            if (text.IndexOf("переход по страницам", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (text.IndexOf("перейти на страницу", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (Regex.IsMatch(text, @"^(?:страниц[аы]?|стр\.?|page|pages|перейти|последнее|новые|last|next|previous)\s*\d*$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[\d\s\.,:;\-–—/\\|«»<>]+$", RegexOptions.IgnoreCase))
                return false;

            return true;
        }

        private static bool IsPagingLinkHtml(string linkHtml)
        {
            string link = linkHtml ?? "";
            return Regex.IsMatch(link, @"\bclass\s*=\s*['"" ][^'"" >]*(?:topic_pages|topic-pages|topic-page|mini_pages|mini-pages|minipages|pagination|pagejump|pagelink|pager|pages)[^'"" >]*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static int GetTopicTitleLinkScore(string linkHtml, string title, string href)
        {
            int score = 0;
            string link = linkHtml ?? "";
            string decodedHref = DecodeEntities(href ?? "");
            string text = NormalizePlainText(title);

            if (!HasStartParameter(decodedHref))
                score += 100;
            else
                score -= 60;

            if (Regex.IsMatch(link, @"\bclass\s*=\s*['"" ][^'"" >]*(?:topic_title|topic-title|topic_link|topic-link|topictitle)[^'"" >]*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                score += 80;

            if (Regex.IsMatch(link, @"\btitle\s*=", RegexOptions.IgnoreCase))
                score += 8;

            if (text.Length > 20)
                score += 25;
            else
                score += text.Length;

            if (Regex.IsMatch(decodedHref, @"(?:\?|&)(?:view=getnewpost|view=findpost|pid=|p=)", RegexOptions.IgnoreCase))
                score -= 25;

            return score;
        }

        private static int GetTopicContextScore(string context)
        {
            int score = 0;
            string value = context ?? "";
            if (Regex.IsMatch(value, @"\b(?:data-topic|topic_title|topic-title|topic_row|topic-row|topic_item|topic-item|rowtopic)\b", RegexOptions.IgnoreCase))
                score += 45;

            if (IsNavigationContext(value))
                score -= 80;

            return score;
        }

        private static bool IsPinnedContext(string context)
        {
            string value = context ?? "";
            return Regex.IsMatch(value, @"(?:pin|pinned|important|fixed|закреп|важн|class\s*=\s*['"" ][^'"" >]*(?:announce|pinned|important|fixed))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool IsUsableForumTitle(string title, string href)
        {
            string text = NormalizePlainText(title);
            if (String.IsNullOrWhiteSpace(text) || text.Length < 2)
                return false;

            if (Regex.IsMatch(text, @"^(?:форум|раздел|подраздел|темы|назад|вперед|вперёд|новые|помощь|правила|поиск|страница|page|pages)$", RegexOptions.IgnoreCase))
                return false;

            if (Regex.IsMatch(text, @"^[\d\s\.,:;\-–—/\\|«»<>]+$", RegexOptions.IgnoreCase))
                return false;

            return true;
        }

        private static int GetForumLinkScore(string linkHtml, string context, string title)
        {
            int score = 0;
            string link = linkHtml ?? "";
            string value = context ?? "";
            string text = NormalizePlainText(title);

            if (Regex.IsMatch(link, @"\bclass\s*=\s*['"" ][^'"" >]*(?:forum_title|forum-title|forum_link|forum-link|forum_name|forum-name)[^'"" >]*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                score += 80;

            if (Regex.IsMatch(value, @"\b(?:board_forum_row|forum_row|forum-row|subforum|forums-row)\b", RegexOptions.IgnoreCase))
                score += 45;

            if (Regex.IsMatch(value, @"\b(?:topic|topic_title|topic-row|topic_row)\b", RegexOptions.IgnoreCase))
                score -= 40;

            if (text.Length > 7)
                score += 15;
            else
                score += text.Length;

            return score;
        }

        private static bool IsNavigationContext(string context)
        {
            string value = context ?? "";
            return Regex.IsMatch(value, @"\b(?:navstrip|breadcrumb|breadcrumbs|topic_foot_nav|copyright|footer|sort_options|pagination|pagejump|pager)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static List<PageNavigationItem> ParseForumPagination(string html, string forumId, int currentStart)
        {
            var byStart = new Dictionary<int, PageNavigationItem>();
            var starts = new List<int>();
            int current = Math.Max(0, currentStart);
            AddForumUniqueStart(starts, 0);

            foreach (HtmlLink link in ExtractLinks(html))
            {
                string href = DecodeEntities(link.Href ?? "");
                string linkForumId = ExtractForumId(href);
                if (String.IsNullOrWhiteSpace(linkForumId) || !String.Equals(linkForumId, forumId, StringComparison.OrdinalIgnoreCase))
                    continue;

                int startValue;
                if (!TryExtractStart(href, out startValue))
                    startValue = 0;
                startValue = Math.Max(0, startValue);

                string label = NormalizePlainText(link.Text);
                if (String.IsNullOrWhiteSpace(label))
                    label = startValue.ToString();

                if (!IsForumPaginationLabel(label, link.Html) && startValue <= 0)
                    continue;

                AddForumUniqueStart(starts, startValue);

                if (GetPageNavigationKind(label, link.Html) != PageNavigationKind.Number)
                    continue;

                string numberLabel = ExtractPageNumberLabel(label, startValue);
                int pageNumber;
                if (!Int32.TryParse(numberLabel, out pageNumber) || pageNumber < 1)
                    continue;

                PageNavigationItem page;
                if (!byStart.TryGetValue(startValue, out page))
                {
                    byStart[startValue] = new PageNavigationItem
                    {
                        Kind = PageNavigationKind.Number,
                        Label = pageNumber.ToString(),
                        Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(forumId) + "&st=" + startValue.ToString(),
                        Start = startValue,
                        IsEnabled = startValue != current
                    };
                }
            }

            int pageSize = GuessForumPageSize(byStart.Values, starts, current);
            if (pageSize <= 0)
                pageSize = 20;

            int currentPageNumber = Math.Max(1, current / pageSize + 1);
            int maxPageNumber = Math.Max(currentPageNumber, GetMaxPageNumber(byStart.Values));
            int maxStart = current;
            foreach (int startValue in starts)
            {
                if (startValue > maxStart)
                    maxStart = startValue;
            }
            maxPageNumber = Math.Max(maxPageNumber, maxStart / pageSize + 1);

            return BuildForumCompactPagination(forumId, pageSize, currentPageNumber, maxPageNumber);
        }

        private static List<PageNavigationItem> BuildForumCompactPagination(string forumId, int pageSize, int currentPage, int maxPage)
        {
            var result = new List<PageNavigationItem>();
            if (maxPage <= 1)
                return result;

            if (pageSize <= 0)
                pageSize = 20;

            currentPage = Math.Max(1, Math.Min(currentPage, maxPage));

            AddForumPageButton(result, forumId, "«", 1, pageSize, currentPage > 1);
            AddForumPageButton(result, forumId, "‹", Math.Max(1, currentPage - 1), pageSize, currentPage > 1);
            AddForumPageButton(result, forumId, currentPage.ToString(), currentPage, pageSize, false);
            AddForumPageButton(result, forumId, "›", Math.Min(maxPage, currentPage + 1), pageSize, currentPage < maxPage);
            AddForumPageButton(result, forumId, "»", maxPage, pageSize, currentPage < maxPage);

            return result;
        }

        private static void AddForumPageButton(List<PageNavigationItem> result, string forumId, string label, int pageNumber, int pageSize, bool isEnabled)
        {
            int start = Math.Max(0, (pageNumber - 1) * pageSize);
            result.Add(new PageNavigationItem
            {
                Kind = PageNavigationKind.Number,
                Label = label,
                Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(forumId ?? "") + "&st=" + start.ToString(),
                Start = start,
                IsEnabled = isEnabled
            });
        }

        private static void AddForumUniqueStart(List<int> starts, int start)
        {
            start = Math.Max(0, start);
            if (!starts.Contains(start))
                starts.Add(start);
        }

        private static int GuessForumPageSize(IEnumerable<PageNavigationItem> pages, IEnumerable<int> starts, int currentStart)
        {
            int best = 0;
            if (pages != null)
            {
                foreach (PageNavigationItem page in pages)
                {
                    int number;
                    if (!Int32.TryParse(page.Label, out number) || number <= 1 || page.Start <= 0)
                        continue;

                    int candidate = page.Start / (number - 1);
                    if (candidate > 0 && (best == 0 || candidate < best))
                        best = candidate;
                }
            }

            if (best > 0)
                return best;

            int minPositive = 0;
            if (starts != null)
            {
                foreach (int start in starts)
                {
                    if (start <= 0)
                        continue;

                    if (minPositive == 0 || start < minPositive)
                        minPositive = start;
                }
            }

            if (minPositive > 0)
                return minPositive;

            if (pages != null)
            {
                foreach (PageNavigationItem page in pages)
                {
                    if (page.Start <= 0)
                        continue;

                    if (minPositive == 0 || page.Start < minPositive)
                        minPositive = page.Start;
                }
            }

            if (minPositive > 0)
                return minPositive;

            if (currentStart > 0)
                return 20;

            return 20;
        }

        private static int GuessForumPageSize(IEnumerable<PageNavigationItem> pages, int currentStart)
        {
            return GuessForumPageSize(pages, null, currentStart);
        }

        private static int GetMaxPageNumber(IEnumerable<PageNavigationItem> pages)
        {
            int max = 1;
            foreach (PageNavigationItem page in pages)
            {
                int number;
                if (Int32.TryParse(page.Label, out number) && number > max)
                    max = number;
            }

            return max;
        }

        private static bool IsForumPaginationLabel(string label, string linkHtml)
        {
            string text = NormalizePlainText(label);
            string html = linkHtml ?? "";

            if (Regex.IsMatch(html, @"\bclass\s*=\s*['"" ][^'"" >]*(?:pagination|pages|pagejump|pager|topic_foot_nav|formbuttonrow)[^'"" >]*['"" ]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                return true;

            if (Regex.IsMatch(text, @"^\d+$", RegexOptions.IgnoreCase))
                return true;

            if (Regex.IsMatch(text, @"^(?:[«‹<]+|[»›>]+|назад|впер[её]д|след\.?|prev|previous|next)$", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private static PageNavigationKind GetPageNavigationKind(string label, string linkHtml)
        {
            string text = NormalizePlainText(label);
            if (Regex.IsMatch(text, @"^(?:[«‹<]+|назад|prev|previous)$", RegexOptions.IgnoreCase))
                return PageNavigationKind.Previous;

            if (Regex.IsMatch(text, @"^(?:[»›>]+|впер[её]д|след\.?|next)$", RegexOptions.IgnoreCase))
                return PageNavigationKind.Next;

            string html = linkHtml ?? "";
            if (Regex.IsMatch(html, @"rel\s*=\s*['""]prev['""]", RegexOptions.IgnoreCase))
                return PageNavigationKind.Previous;

            if (Regex.IsMatch(html, @"rel\s*=\s*['""]next['""]", RegexOptions.IgnoreCase))
                return PageNavigationKind.Next;

            return PageNavigationKind.Number;
        }

        private static string ExtractPageNumberLabel(string label, int start)
        {
            string text = NormalizePlainText(label);
            Match match = Regex.Match(text, @"\d+", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Value;

            return start.ToString();
        }

        private static string GuessCurrentPageLabel(IEnumerable<int> knownStarts, int currentStart)
        {
            int minPositive = 0;
            foreach (int value in knownStarts)
            {
                if (value <= 0)
                    continue;

                if (minPositive == 0 || value < minPositive)
                    minPositive = value;
            }

            int pageSize = minPositive > 0 ? minPositive : 30;
            return (currentStart / pageSize + 1).ToString();
        }

        private static bool TryExtractStart(string href, out int start)
        {
            start = 0;
            string decodedHref = DecodeEntities(href ?? "");
            Match match = Regex.Match(decodedHref, @"(?:\?|&)st=(?<st>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(href ?? "", @"(?:\?|&amp;|&)st=(?<st>\d+)", RegexOptions.IgnoreCase);

            return match.Success && Int32.TryParse(match.Groups["st"].Value, out start);
        }

        private static bool HasStartParameter(string href)
        {
            int unused;
            return TryExtractStart(href, out unused);
        }

        private static List<ForumListItem> ParseChildForumsFromSearch(string html, string parentForumId)
        {
            var flatItems = new List<ForumSearchNode>();
            var result = new List<ForumListItem>();
            var usedIds = new HashSet<string>();
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;

            Match forumsSelect = Regex.Match(html ?? "", @"<select\b(?=[^>]*(?:name\s*=\s*['""]?forums|id\s*=\s*['""]?forums))[^>]*>(?<body>[\s\S]*?)</select>", options);
            string source = forumsSelect.Success ? forumsSelect.Groups["body"].Value : (html ?? "");
            MatchCollection optionsList = Regex.Matches(source, @"<option\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</option>", options);

            foreach (Match option in optionsList)
            {
                string id = GetAttribute(option.Groups["attrs"].Value, "value");
                if (String.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^\d+$") || usedIds.Contains(id))
                    continue;

                string rawText = StripTags(option.Groups["text"].Value);
                string title = CleanText(RemoveForumIndent(rawText));
                if (String.IsNullOrWhiteSpace(title))
                    continue;

                usedIds.Add(id);
                flatItems.Add(new ForumSearchNode
                {
                    Id = id,
                    Title = title,
                    Level = CalculateForumLevel(rawText)
                });
            }

            var parents = new List<ForumSearchNode>();
            ForumSearchNode lastParent = new ForumSearchNode { Id = "", Level = -1 };
            parents.Add(lastParent);

            foreach (ForumSearchNode node in flatItems)
            {
                if (node.Level < 0)
                    node.Level = 0;

                if (node.Level <= lastParent.Level)
                {
                    int removeCount = lastParent.Level - node.Level + 1;
                    for (int i = 0; i < removeCount && parents.Count > 1; i++)
                        parents.RemoveAt(parents.Count - 1);

                    lastParent = parents[parents.Count - 1];
                }

                node.ParentId = lastParent.Id;

                if (String.Equals(node.ParentId, parentForumId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new ForumListItem
                    {
                        Kind = ForumItemKind.Forum,
                        TypeLabel = "раздел",
                        Id = node.Id,
                        Title = node.Title,
                        Info = "",
                        Url = ForumBaseUrl + "?showforum=" + Uri.EscapeDataString(node.Id)
                    });
                }

                if (node.Level > lastParent.Level)
                {
                    lastParent = node;
                    parents.Add(lastParent);
                }
            }

            return result;
        }

        private static int CalculateForumLevel(string rawText)
        {
            if (String.IsNullOrWhiteSpace(rawText))
                return 0;

            string text = DecodeEntities(rawText).Replace('\u00A0', ' ');
            Match match = Regex.Match(text, @"^\s*(?<dashes>[-–—]+)");
            if (match.Success)
                return Math.Max(0, match.Groups["dashes"].Value.Length / 2);

            int spaces = 0;
            while (spaces < text.Length && Char.IsWhiteSpace(text[spaces]))
                spaces++;

            return Math.Max(0, spaces / 4);
        }

        private static string RemoveForumIndent(string rawText)
        {
            if (String.IsNullOrWhiteSpace(rawText))
                return "";

            string text = DecodeEntities(rawText).Replace('\u00A0', ' ');
            text = Regex.Replace(text, @"^\s*(?:\|\s*)?[-–—]+\s*", "");
            return text.Trim();
        }

        private static string ExtractForumDescription(string html)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            Match match = Regex.Match(html ?? "", @"<div[^>]*?\bclass\s*=\s*['"" ][^'"" >]*(?:desc|forum_desc|forum_description)[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)</div>", options);
            if (!match.Success)
                return "";

            string text = CleanText(match.Groups["value"].Value);
            return text.Length > 180 ? text.Substring(0, 180).Trim() + "..." : text;
        }

        private static string ExtractModifier(string html)
        {
            Match match = Regex.Match(html ?? "", @"<span[^>]*?\bclass\s*=\s*['"" ][^'"" >]*modifier[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? CleanText(match.Groups["value"].Value) : "";
        }

        private static string ExtractTopicDescription(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"<span[^>]*?\bclass\s*=\s*['"" ][^'"" >]*topic_desc[^'"" >]*['"" ][^>]*>(?<value>[\s\S]*?)(?:<br\s*/?>|</span>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string text = CleanText(match.Groups["value"].Value);
                if (String.IsNullOrWhiteSpace(text))
                    continue;

                if (text.StartsWith("автор", StringComparison.OrdinalIgnoreCase))
                    continue;

                return text.Length > 150 ? text.Substring(0, 150).Trim() + "..." : text;
            }

            return "";
        }

        private static string ExtractTopicAuthor(string html)
        {
            MatchCollection matches = Regex.Matches(html ?? "", @"<span[^>]*topic_desc[^>]*>(?<value>[\s\S]*?)(?:<br\s*/?>|</span>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string value = match.Groups["value"].Value;
                string text = CleanText(value);
                if (IndexOfIgnoreCase(text, "автор", 0) < 0)
                    continue;

                Match user = Regex.Match(value, @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (user.Success)
                {
                    string name = CleanText(user.Groups["name"].Value);
                    if (!String.IsNullOrWhiteSpace(name))
                        return name;
                }

                string afterLabel = ExtractTextAfterLabel(text, "автор");
                if (!String.IsNullOrWhiteSpace(afterLabel))
                    return afterLabel;
            }

            return "";
        }

        private static string ExtractLastTopicUser(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            int marker = IndexOfIgnoreCase(html, "getlastpost", 0);
            if (marker < 0)
                marker = IndexOfIgnoreCase(html, "Послед", 0);

            string scope = marker >= 0 ? html.Substring(marker) : html;
            MatchCollection matches = Regex.Matches(scope, @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0)
                return "";

            string name = CleanText(matches[0].Groups["name"].Value);
            return String.IsNullOrWhiteSpace(name) ? "" : name;
        }

        private static List<UserLink> ExtractUserLinks(string html)
        {
            var result = new List<UserLink>();
            MatchCollection matches = Regex.Matches(html ?? "", @"showuser=(?<id>\d+)[^>]*>(?<name>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string name = CleanText(match.Groups["name"].Value);
                if (String.IsNullOrWhiteSpace(name))
                    continue;

                result.Add(new UserLink
                {
                    Id = match.Groups["id"].Value,
                    Name = name,
                    EndIndex = match.Index + match.Length
                });
            }

            return result;
        }

        private static string ExtractLastDate(string html, int startIndex)
        {
            if (String.IsNullOrWhiteSpace(html) || startIndex < 0 || startIndex >= html.Length)
                return "";

            string tail = html.Substring(startIndex);
            Match match = Regex.Match(tail, @"^(?<value>[^<]{1,100})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return "";

            string text = CleanText(match.Groups["value"].Value);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 80)
                text = text.Substring(0, 80).Trim();

            return text;
        }

        private static string BuildTopicFlags(bool isNew, bool isPoll, bool isClosed)
        {
            var values = new List<string>();
            if (isNew)
                values.Add("новое");
            if (isPoll)
                values.Add("опрос");
            if (isClosed)
                values.Add("закрыта");

            return JoinNonEmpty(", ", values.ToArray());
        }

        private static string Prefix(string prefix, string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "" : prefix + value;
        }

        private static string ExtractForumId(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";

            string decoded = DecodeEntities(value);
            Match match = Regex.Match(decoded, @"(?:showforum|f)=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;

            match = Regex.Match(decoded, @"^\d+$", RegexOptions.IgnoreCase);
            return match.Success ? match.Value : "";
        }

        private static string NormalizeTopicUrl(string href, string id)
        {
            string url = NormalizeUrl(href);
            if (String.IsNullOrWhiteSpace(id))
                return url;

            if (String.IsNullOrWhiteSpace(url) || HasStartParameter(url))
                return ForumBaseUrl + "?showtopic=" + Uri.EscapeDataString(id);

            return url;
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

            Uri baseUri = url.IndexOf("forum/", StringComparison.OrdinalIgnoreCase) >= 0
                ? new Uri("https://" + Host + "/forum/")
                : new Uri("https://" + Host + "/forum/index.php");

            return new Uri(baseUri, url).ToString();
        }

        private static List<HtmlLink> ExtractLinks(string html)
        {
            var result = new List<HtmlLink>();
            MatchCollection matches = Regex.Matches(html ?? "", @"<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                string attrs = match.Groups["attrs"].Value;
                string href = GetAttribute(attrs, "href");
                if (String.IsNullOrWhiteSpace(href))
                    continue;

                result.Add(new HtmlLink
                {
                    Href = href,
                    TitleAttribute = GetAttribute(attrs, "title"),
                    Text = CleanText(match.Groups["text"].Value),
                    Html = match.Value,
                    Index = match.Index
                });
            }

            return result;
        }

        private static string GetAttribute(string attributes, string name)
        {
            if (String.IsNullOrWhiteSpace(attributes) || String.IsNullOrWhiteSpace(name))
                return "";

            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            string escaped = Regex.Escape(name);
            Match match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*""(?<value>[^""]*)""", options);
            if (!match.Success)
                match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*'(?<value>[^']*)'", options);
            if (!match.Success)
                match = Regex.Match(attributes, @"\b" + escaped + @"\s*=\s*(?<value>[^\s>]+)", options);

            return match.Success ? DecodeEntities(match.Groups["value"].Value) : "";
        }

        private static string GetHtmlWindow(string html, int index, int radius)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            if (index < 0)
                index = 0;

            int start = Math.Max(0, index - radius);
            int end = Math.Min(html.Length, index + radius);
            return html.Substring(start, end - start);
        }

        private static string StripTags(string html)
        {
            if (String.IsNullOrEmpty(html))
                return "";

            string withoutTags = Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return DecodeEntities(withoutTags);
        }

        private static string NormalizePlainText(string value)
        {
            string text = DecodeEntities(value ?? "");
            text = text.Replace("\u00A0", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string prepared = Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);

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

            text = text.Replace("\u00A0", " ");
            text = Regex.Replace(text, @"\s+", " ");
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
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .Replace("&raquo;", "»")
                    .Replace("&#187;", "»");
            }
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return String.Join(separator, values.Where(v => !String.IsNullOrWhiteSpace(v)));
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

        private sealed class ForumStructureItem
        {
            public string Id { get; set; }
            public string ParentId { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public bool HasTopics { get; set; }
            public bool HasForums { get; set; }
            public int Level { get; set; }
        }

        private sealed class HtmlLink
        {
            public string Href { get; set; }
            public string TitleAttribute { get; set; }
            public string Text { get; set; }
            public string Html { get; set; }
            public int Index { get; set; }
        }

        private sealed class TopicTitleLink
        {
            public string Href { get; set; }
            public string Title { get; set; }
        }

        private sealed class TopicLinkCandidate
        {
            public string Id { get; set; }
            public string Href { get; set; }
            public string Title { get; set; }
            public string Context { get; set; }
            public int Score { get; set; }
            public int Index { get; set; }
        }

        private sealed class ForumLinkCandidate
        {
            public string Id { get; set; }
            public string Href { get; set; }
            public string Title { get; set; }
            public string Context { get; set; }
            public int Score { get; set; }
            public int Index { get; set; }
        }

        private sealed class ForumSearchNode
        {
            public string Id { get; set; }
            public string ParentId { get; set; }
            public string Title { get; set; }
            public int Level { get; set; }
        }

        private sealed class ForumPageState
        {
            public string ForumId { get; set; }
            public int Start { get; set; }
        }

        public enum PageNavigationKind
        {
            Number,
            Previous,
            Next
        }

        public sealed class PageNavigationItem
        {
            public PageNavigationKind Kind { get; set; }
            public string Label { get; set; }
            public string Url { get; set; }
            public int Start { get; set; }
            public bool IsEnabled { get; set; }
        }

        private sealed class ForumData
        {
            public ForumData()
            {
                Items = new List<ForumListItem>();
                Pages = new List<PageNavigationItem>();
            }

            public string Title { get; set; }
            public List<ForumListItem> Items { get; private set; }
            public List<PageNavigationItem> Pages { get; set; }

            public bool HasKind(ForumItemKind kind)
            {
                return Items.Any(item => item.Kind == kind);
            }

            public void AddUnique(ForumListItem item)
            {
                if (item == null)
                    return;

                foreach (ForumListItem existing in Items)
                {
                    if (existing.Kind == item.Kind && !String.IsNullOrWhiteSpace(existing.Id) && String.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!String.IsNullOrWhiteSpace(existing.Url) && String.Equals(existing.Url, item.Url, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                Items.Add(item);
            }

            public void InsertForumsAtTop(IEnumerable<ForumListItem> forums)
            {
                var toInsert = new List<ForumListItem>();
                foreach (ForumListItem forum in forums)
                {
                    bool exists = Items.Any(item => item.Kind == ForumItemKind.Forum && String.Equals(item.Id, forum.Id, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                        toInsert.Add(forum);
                }

                for (int i = toInsert.Count - 1; i >= 0; i--)
                    Items.Insert(0, toInsert[i]);
            }
        }

        private sealed class UserLink
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int EndIndex { get; set; }
        }

        public enum ForumItemKind
        {
            Forum,
            Topic,
            Announce
        }

        public sealed class ForumListItem
        {
            public ForumItemKind Kind { get; set; }
            public string TypeLabel { get; set; }
            public string Id { get; set; }
            public string Title { get; set; }
            public string Info { get; set; }
            public string Url { get; set; }
            public bool IsPinned { get; set; }

            public Visibility InfoVisibility
            {
                get { return String.IsNullOrWhiteSpace(Info) ? Visibility.Collapsed : Visibility.Visible; }
            }
        }
    }
}
