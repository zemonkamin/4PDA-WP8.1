using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace _4PDA
{
    public sealed partial class ChatPage : Page
    {
        private readonly ObservableCollection<QmsMessage> _messages = new ObservableCollection<QmsMessage>();
        private readonly QmsService _qmsService = new QmsService();
        private QmsThreadNavigationArgs _args;
        private bool _loading;

        public ChatPage()
        {
            this.InitializeComponent();
            MessagesListView.ItemsSource = _messages;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (!ForumAuthService.IsAuthorized)
            {
                Frame.Navigate(typeof(LoginPage));
                return;
            }

            _args = e.Parameter as QmsThreadNavigationArgs;
            if (_args == null)
            {
                ChatStatusTextBlock.Text = "Чат не выбран.";
                return;
            }

            ApplyHeader();
            await LoadMessagesAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }


        private void ApplyHeader()
        {
            string nick = _args == null ? "" : _args.ContactNick;
            string title = _args == null ? "" : _args.ThreadTitle;

            if (String.IsNullOrWhiteSpace(nick))
                nick = "QMS";
            if (String.IsNullOrWhiteSpace(title))
                title = "чат";

            DialogTitleTextBlock.Text = nick + ": " + title;
        }

        private async Task LoadMessagesAsync()
        {
            if (_loading || _args == null)
                return;

            _loading = true;
            SetBusy(true);
            ChatStatusTextBlock.Text = "";

            try
            {
                List<QmsMessage> items = await _qmsService.GetMessagesAsync(
                    _args.ContactId,
                    _args.ThreadId,
                    _args.ContactNick,
                    _args.ThreadTitle,
                    _args.AvatarUrl);

                _messages.Clear();
                foreach (QmsMessage item in items)
                    _messages.Add(item);

                ChatStatusTextBlock.Text = _messages.Count == 0 ? "Сообщения не найдены." : "";
                ScrollToLastMessage();

                var ignoredTileUpdate = LiveTileService.RefreshQmsOnlyAsync();
            }
            catch (Exception ex)
            {
                ChatStatusTextBlock.Text = "Не удалось загрузить чат: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_args == null || _loading)
                return;

            string text = MessageTextBox.Text == null ? "" : MessageTextBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(text))
                return;

            _loading = true;
            SetBusy(true);
            ChatStatusTextBlock.Text = "Отправляем...";
            bool sent = false;

            try
            {
                await _qmsService.SendMessageAsync(_args.ContactId, _args.ThreadId, text);
                MessageTextBox.Text = "";
                ChatStatusTextBlock.Text = "";
                sent = true;
            }
            catch (Exception ex)
            {
                ChatStatusTextBlock.Text = "Не удалось отправить сообщение: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                _loading = false;
            }

            if (sent)
                await LoadMessagesAsync();
        }

        private void ScrollToLastMessage()
        {
            if (_messages.Count == 0)
                return;

            try
            {
                MessagesListView.ScrollIntoView(_messages[_messages.Count - 1]);
            }
            catch
            {
            }
        }

        private void SetBusy(bool busy)
        {
            ChatProgressRing.IsActive = busy;
            MessageTextBox.IsEnabled = !busy;
            SendButton.IsEnabled = !busy;
        }
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackNavigationService.GoBack(Frame);
        }

    }
}
