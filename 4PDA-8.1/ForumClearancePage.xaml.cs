using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace _4PDA
{
    public sealed partial class ForumClearancePage : Page
    {
        private string _url = ForumCookieHelper.ForumUrl;

        public ForumClearancePage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            string parameter = e.Parameter as string;
            if (!String.IsNullOrWhiteSpace(parameter))
                _url = parameter;

            NavigateToClearanceUrl();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("ForumClearancePage.OnNavigatedFrom");
            base.OnNavigatedFrom(e);
        }

        private void NavigateToClearanceUrl()
        {
            try
            {
                if (String.IsNullOrWhiteSpace(_url))
                    _url = ForumCookieHelper.ForumUrl;

                StatusTextBlock.Text = "Открываем проверку 4PDA...";
                ClearanceWebView.Navigate(new Uri(_url, UriKind.Absolute));
            }
            catch
            {
                _url = ForumCookieHelper.ForumUrl;
                ClearanceWebView.Navigate(new Uri(_url, UriKind.Absolute));
            }
        }

        private void ClearanceWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("ForumClearancePage.NavigationCompleted");

            if (ForumCookieHelper.HasCookie("cf_clearance"))
                StatusTextBlock.Text = "cf_clearance сохранён. Нажмите «готово» и повторите скачивание.";
            else
                StatusTextBlock.Text = "Если видите проверку — пройдите её. Если страница форума открылась нормально, нажмите «готово».";
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToClearanceUrl();
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            ForumCookieHelper.SaveForumCookiesFromSystemCookieManager("ForumClearancePage.Done");
            if (Frame != null && Frame.CanGoBack)
                Frame.GoBack();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackNavigationService.GoBack(Frame);
        }

    }
}
