using System;
using System.Collections.ObjectModel;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace _4PDA
{
    public sealed partial class MainPage : Page
    {
        private ObservableCollection<SectionGroup> _groups;

        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;
            this._groups = CreateGroups();
            groupedItemsViewSource.Source = this._groups;
        }

        private ObservableCollection<SectionGroup> CreateGroups()
        {
            ObservableCollection<SectionGroup> groups = new ObservableCollection<SectionGroup>();

            SectionGroup news = new SectionGroup();
            news.UniqueId = "news";
            news.Title = "Новости";
            news.Items.Add(CreateItem("news-latest", "Лента", "\uE12A"));
            news.Items.Add(CreateItem("news-reviews", "Обзоры", "\uE8A5"));
            news.Items.Add(CreateItem("news-title", "Заголовок новости", "\uE8F1"));
            news.Items.Add(CreateItem("news-articles", "Статьи", "\uE8A5"));
            news.Items.Add(CreateItem("news-popular", "Популярное", "\uE734"));
            news.Items.Add(CreateItem("news-comments", "Комментарии", "\uE134"));
            groups.Add(news);

            SectionGroup forum = new SectionGroup();
            forum.UniqueId = "forum";
            forum.Title = "Форум";
            forum.Items.Add(CreateItem("forum-root", "Разделы", "\uE8B7"));
            forum.Items.Add(CreateItem("forum-topics", "Темы", "\uE8A5"));
            forum.Items.Add(CreateItem("forum-title", "Заголовок темы", "\uE8F1"));
            forum.Items.Add(CreateItem("forum-favorites", "Избранное", "\uE734"));
            forum.Items.Add(CreateItem("forum-my", "Мои темы", "\uE77B"));
            forum.Items.Add(CreateItem("forum-search", "Поиск", "\uE11A"));
            groups.Add(forum);

            SectionGroup messages = new SectionGroup();
            messages.UniqueId = "messages";
            messages.Title = "Сообщения";
            messages.Items.Add(CreateItem("messages-inbox", "Входящие", "\uE119"));
            messages.Items.Add(CreateItem("messages-dialogs", "Диалоги", "\uE15F"));
            messages.Items.Add(CreateItem("messages-unread", "Непрочитанные", "\uE715"));
            messages.Items.Add(CreateItem("messages-contacts", "Контакты", "\uE77B"));
            messages.Items.Add(CreateItem("messages-archive", "Архив", "\uE1D3"));
            messages.Items.Add(CreateItem("messages-account", "Аккаунт", "\uE115"));
            groups.Add(messages);

            return groups;
        }

        private SectionItem CreateItem(string uniqueId, string title, string iconText)
        {
            SectionItem item = new SectionItem();
            item.UniqueId = uniqueId;
            item.Title = title;
            item.IconText = iconText;
            return item;
        }

        private void ItemView_ItemClick(object sender, ItemClickEventArgs e)
        {
            SectionItem item = e.ClickedItem as SectionItem;

            if (item == null || String.IsNullOrWhiteSpace(item.UniqueId))
                return;

            if (item.UniqueId.StartsWith("forum", StringComparison.OrdinalIgnoreCase))
            {
                this.Frame.Navigate(typeof(ForumPage));
                return;
            }

            if (item.UniqueId.StartsWith("messages", StringComparison.OrdinalIgnoreCase))
            {
                this.Frame.Navigate(typeof(LoginPage));
                return;
            }
        }
    }

    public sealed class SectionGroup
    {
        private ObservableCollection<SectionItem> _items = new ObservableCollection<SectionItem>();

        public string UniqueId { get; set; }
        public string Title { get; set; }

        public ObservableCollection<SectionItem> Items
        {
            get { return this._items; }
        }
    }

    public sealed class SectionItem
    {
        public string UniqueId { get; set; }
        public string Title { get; set; }
        public string IconText { get; set; }
    }
}
