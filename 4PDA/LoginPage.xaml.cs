using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Phone.UI.Input;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace _4PDA
{
    public sealed partial class LoginPage : Page
    {
        private ForumLoginForm _loginForm;
        private bool _loading;

        public LoginPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;

            if (ForumAuthService.IsAuthorized)
            {
                NavigateToUserPageAndRemoveLoginFromBackStack();
                return;
            }

            await LoadLoginFormAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            base.OnNavigatedFrom(e);
        }

        private void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private async void ReloadCaptchaAppBarButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadLoginFormAsync();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            string login = LoginTextBox.Text == null ? "" : LoginTextBox.Text.Trim();
            string password = PasswordTextBox.Password == null ? "" : PasswordTextBox.Password;
            string captcha = CaptchaTextBox.Text == null ? "" : CaptchaTextBox.Text.Trim();

            if (String.IsNullOrWhiteSpace(login) || String.IsNullOrWhiteSpace(password))
            {
                StatusTextBlock.Text = "Введите логин и пароль.";
                return;
            }

            if (_loginForm == null)
            {
                StatusTextBlock.Text = "Форма авторизации ещё не загружена.";
                await LoadLoginFormAsync();
                return;
            }

            if (_loginForm.CaptchaRequired && String.IsNullOrWhiteSpace(captcha))
            {
                StatusTextBlock.Text = "Введите капчу.";
                return;
            }

            SetLoading(true, "Входим...");

            try
            {
                ForumLoginResult result = await ForumAuthService.LoginAsync(
                    _loginForm,
                    login,
                    password,
                    captcha,
                    HiddenCheckBox.IsChecked == true);

                if (result.Success)
                {
                    StatusTextBlock.Text = "Вход выполнен.";
                    NavigateToUserPageAndRemoveLoginFromBackStack();
                    return;
                }

                StatusTextBlock.Text = String.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Не удалось войти. Проверьте логин, пароль и капчу."
                    : result.ErrorMessage;

                await LoadLoginFormAsync();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "Ошибка входа: " + ex.Message;
            }
            finally
            {
                SetLoading(false, StatusTextBlock.Text);
            }
        }

        private void NavigateToUserPageAndRemoveLoginFromBackStack()
        {
            Frame.Navigate(typeof(UserPage));

            for (int i = Frame.BackStack.Count - 1; i >= 0; i--)
            {
                if (Frame.BackStack[i].SourcePageType == typeof(LoginPage))
                    Frame.BackStack.RemoveAt(i);
            }
        }

        private async Task LoadLoginFormAsync()
        {
            if (_loading)
                return;

            SetLoading(true, "Загружаем форму входа...");
            CaptchaTextBox.Text = "";
            CaptchaImage.Source = null;

            try
            {
                _loginForm = await ForumAuthService.GetLoginFormAsync();

                if (_loginForm.CaptchaRequired)
                {
                    CaptchaPanel.Visibility = Visibility.Visible;
                    BitmapImage captchaImage = await ForumAuthService.LoadCaptchaImageAsync(_loginForm);
                    CaptchaImage.Source = captchaImage;

                    if (captchaImage == null)
                        StatusTextBlock.Text = "Форма входа загружена, но картинка капчи пустая.";
                    else
                        StatusTextBlock.Text = "Введите данные аккаунта и капчу.";
                }
                else
                {
                    CaptchaPanel.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Введите данные аккаунта.";
                }
            }
            catch (Exception ex)
            {
                _loginForm = null;
                StatusTextBlock.Text = "Не удалось загрузить форму входа: " + ex.Message;
            }
            finally
            {
                SetLoading(false, StatusTextBlock.Text);
            }
        }

        private void SetLoading(bool value, string message)
        {
            _loading = value;
            CaptchaProgressRing.IsActive = value;
            LoginButton.IsEnabled = !value;
            LoginTextBox.IsEnabled = !value;
            PasswordTextBox.IsEnabled = !value;
            CaptchaTextBox.IsEnabled = !value && (_loginForm == null || _loginForm.CaptchaRequired);
            HiddenCheckBox.IsEnabled = !value;

            if (!String.IsNullOrWhiteSpace(message))
                StatusTextBlock.Text = message;
        }
    }

    internal static class ForumAuthService
    {
        private const string Host = "4pda.to";
        private const string ForumRootUrl = "https://4pda.to/forum/index.php";
        private const string AuthUrl = "https://4pda.to/forum/index.php?act=auth";
        private const string MinimalPageUrl = "https://4pda.to/forum/index.php?showforum=200#afterauth";
        private const string KeyUserId = "ForumUserId";
        private const string KeyUserLogin = "ForumUserLogin";
        private const string KeyUserAvatar = "ForumUserAvatar";
        private const string KeyLogoutK = "ForumLogoutK";

        private static readonly HttpBaseProtocolFilter Filter = new HttpBaseProtocolFilter();
        private static readonly HttpClient Client = new HttpClient(Filter);

        static ForumAuthService()
        {
            try
            {
                Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; Touch; rv:11.0) like Gecko");
            }
            catch
            {
            }

            try
            {
                Client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            }
            catch
            {
            }

            try
            {
                Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            }
            catch
            {
            }
        }

        public static bool IsAuthorized
        {
            get { return !String.IsNullOrWhiteSpace(CurrentUserId); }
        }

        public static string CurrentUserId
        {
            get { return ReadSetting(KeyUserId); }
        }

        public static string CurrentUserLogin
        {
            get
            {
                string login = ReadSetting(KeyUserLogin);
                return String.IsNullOrWhiteSpace(login) ? CurrentUserId : login;
            }
        }

        public static string CurrentUserAvatarUrl
        {
            get { return ReadSetting(KeyUserAvatar); }
        }

        public static async Task<ForumLoginForm> GetLoginFormAsync()
        {
            // ForPDA берёт форму сразу с act=auth. Это основной путь.
            string firstPage = await Client.GetStringAsync(new Uri(AuthUrl, UriKind.Absolute));

            var alreadyLoginResult = new ForumLoginResult();
            CheckLogin(firstPage, alreadyLoginResult);
            if (alreadyLoginResult.Success)
            {
                SaveCurrentUser(alreadyLoginResult);
                throw new InvalidOperationException("Вы уже авторизованы.");
            }

            ForumLoginForm form = TryParseLoginForm(firstPage);
            if (IsUsableLoginForm(form))
                return form;

            // 4pdaClient-plus дополнительно открывает act=auth&k=... . Оставляем это как fallback.
            string k = ExtractValue(firstPage, "act=auth[^\\\"']*[;&]k=(?<value>[^&\\\"']*)");
            var fallbackUrls = new List<string>();

            if (!String.IsNullOrWhiteSpace(k))
                fallbackUrls.Add(AuthUrl + "&k=" + Uri.EscapeDataString(k));

            fallbackUrls.Add(AuthUrl + "&k=" + Uri.EscapeDataString(Guid.NewGuid().ToString()));

            string lastPage = firstPage;
            for (int i = 0; i < fallbackUrls.Count; i++)
            {
                try
                {
                    lastPage = await Client.GetStringAsync(new Uri(fallbackUrls[i], UriKind.Absolute));
                    form = TryParseLoginForm(lastPage);
                    if (IsUsableLoginForm(form))
                        return form;
                }
                catch
                {
                    // Следующий fallback ниже.
                }
            }

            string diagnostic = BuildLoginFormDiagnostic(lastPage);
            throw new InvalidOperationException(diagnostic);
        }

        public static async Task<BitmapImage> LoadCaptchaImageAsync(ForumLoginForm form)
        {
            if (form == null || String.IsNullOrWhiteSpace(form.CaptchaUrl))
                return null;

            HttpResponseMessage response = await Client.GetAsync(new Uri(form.CaptchaUrl, UriKind.Absolute));

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Сервер не отдал капчу: " + response.StatusCode.ToString());

            IBuffer buffer = await response.Content.ReadAsBufferAsync();

            if (buffer == null || buffer.Length == 0)
                throw new InvalidOperationException("Сервер вернул пустую капчу.");

            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(buffer);
            stream.Seek(0);

            BitmapImage image = new BitmapImage();
            image.SetSource(stream);
            return image;
        }

        public static async Task<ForumLoginResult> LoginAsync(ForumLoginForm form, string login, string password, string captcha, bool hidden)
        {
            var result = new ForumLoginResult();

            if (form == null)
            {
                result.ErrorMessage = "Форма авторизации не загружена.";
                return result;
            }

            // ForPDA отправляет не всю HTML-форму, а фиксированный набор полей на act=auth.
            // Важно: login/password должны быть закодированы как windows-1251, как URLEncoder.encode(..., "windows-1251") в ForPDA.
            var values = new List<FormPostField>();

            AddFormValue(values, "captcha-time", form.CaptchaTime, false);
            AddFormValue(values, "captcha-sig", form.CaptchaSig, false);
            AddFormValue(values, "captcha", form.CaptchaRequired ? captcha : "", false);
            AddFormValue(values, "return", MinimalPageUrl, false);
            AddFormValue(values, "login", UrlEncodeWindows1251(login), true);
            AddFormValue(values, "password", UrlEncodeWindows1251(password), true);
            AddFormValue(values, "remember", "1", false);
            AddFormValue(values, "hidden", hidden ? "1" : "0", false);

            string bodyToSend = BuildFormBody(values);
            HttpStringContent content = new HttpStringContent(
                bodyToSend,
                Windows.Storage.Streams.UnicodeEncoding.Utf8,
                "application/x-www-form-urlencoded");

            HttpResponseMessage response = await Client.PostAsync(new Uri(AuthUrl, UriKind.Absolute), content);
            string body = await response.Content.ReadAsStringAsync();

            if (String.IsNullOrWhiteSpace(body))
            {
                result.ErrorMessage = "Сервер вернул пустую страницу после отправки формы.";
                return result;
            }

            string explicitError = TryExtractLoginError(body);
            if (!String.IsNullOrWhiteSpace(explicitError))
            {
                result.ErrorMessage = explicitError;
                return result;
            }

            // Сначала проверяем cookie, потому что на успешном входе 4PDA может редиректить не туда,
            // где старый HTML-шаблон содержит showuser + logout.
            ApplyCookieLoginResult(result, login);
            CheckLogin(body, result);

            // Повторяем проверку на минимальной странице, как в ForPDA: там ожидается showuser + action=logout&k=.
            if (!result.Success)
            {
                try
                {
                    string minimalPage = await Client.GetStringAsync(new Uri(MinimalPageUrl, UriKind.Absolute));
                    string minimalError = TryExtractLoginError(minimalPage);

                    if (String.IsNullOrWhiteSpace(minimalError))
                    {
                        CheckLogin(minimalPage, result);
                        ApplyCookieLoginResult(result, login);
                    }
                    else
                    {
                        result.ErrorMessage = minimalError;
                    }
                }
                catch
                {
                    // Ниже вернём нормальную диагностическую ошибку.
                }
            }

            if (result.Success)
            {
                if (String.IsNullOrWhiteSpace(result.UserLogin))
                    result.UserLogin = login;

                SaveCurrentUser(result);
                return result;
            }

            if (String.IsNullOrWhiteSpace(result.ErrorMessage))
                result.ErrorMessage = BuildAuthFailureDiagnostic(body);

            return result;
        }

        public static async Task LogoutAsync()
        {
            string k = ReadSetting(KeyLogoutK);

            if (!String.IsNullOrWhiteSpace(k))
            {
                try
                {
                    await Client.GetStringAsync(new Uri("https://" + Host + "/forum/index.php?act=Login&CODE=03&k=" + Uri.EscapeDataString(k), UriKind.Absolute));
                }
                catch
                {
                }
            }

            ClearCurrentUser();
        }

        public static async Task<ForumUserProfile> GetCurrentUserProfileAsync()
        {
            if (!IsAuthorized)
                throw new InvalidOperationException("Пользователь не авторизован.");

            string userId = CurrentUserId;
            string page = await Client.GetStringAsync(new Uri("https://" + Host + "/forum/index.php?showuser=" + Uri.EscapeDataString(userId), UriKind.Absolute));
            ForumUserProfile profile = ParseProfile(page);
            profile.Id = userId;

            if (String.IsNullOrWhiteSpace(profile.Nick))
                profile.Nick = CurrentUserLogin;

            if (String.IsNullOrWhiteSpace(profile.AvatarUrl))
                profile.AvatarUrl = CurrentUserAvatarUrl;

            if (!String.IsNullOrWhiteSpace(profile.Nick))
                WriteSetting(KeyUserLogin, profile.Nick);

            if (!String.IsNullOrWhiteSpace(profile.AvatarUrl))
                WriteSetting(KeyUserAvatar, profile.AvatarUrl);

            return profile;
        }

        public static async Task<bool> SaveProfileNoteAsync(string note)
        {
            if (!IsAuthorized)
                throw new InvalidOperationException("Пользователь не авторизован.");

            var values = new List<FormPostField>();
            AddFormValue(values, "note", note == null ? "" : note, false);

            HttpStringContent content = new HttpStringContent(
                BuildFormBody(values),
                Windows.Storage.Streams.UnicodeEncoding.Utf8,
                "application/x-www-form-urlencoded");

            HttpResponseMessage response = await Client.PostAsync(
                new Uri("https://" + Host + "/forum/index.php?act=profile-xhr&action=save-note", UriKind.Absolute),
                content);

            string body = await response.Content.ReadAsStringAsync();
            return body != null && body.Trim() == "1";
        }

        public static void ClearCurrentUser()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(KeyUserId);
            ApplicationData.Current.LocalSettings.Values.Remove(KeyUserLogin);
            ApplicationData.Current.LocalSettings.Values.Remove(KeyUserAvatar);
            ApplicationData.Current.LocalSettings.Values.Remove(KeyLogoutK);
        }

        private static ForumLoginForm TryParseLoginForm(string page)
        {
            if (String.IsNullOrWhiteSpace(page))
                return null;

            string formHtml = ExtractAuthForm(page);
            if (String.IsNullOrWhiteSpace(formHtml))
            {
                if (page.IndexOf("captcha-time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    page.IndexOf("captcha-sig", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    page.IndexOf("name=\"login\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    page.IndexOf("name='login'", StringComparison.OrdinalIgnoreCase) >= 0)
                    formHtml = page;
                else
                    return null;
            }

            ForumLoginForm form = new ForumLoginForm();
            form.PostUrl = NormalizeUrl(HtmlDecode(ExtractAttribute(ExtractFirst(formHtml, "(?<value><form\\b[^>]*>)"), "action")));
            form.Fields = ExtractInputFields(formHtml);
            form.Session = GetFieldValue(form.Fields, "s");
            form.CaptchaTime = GetFieldValue(form.Fields, "captcha-time", "captcha_time");
            form.CaptchaSig = GetFieldValue(form.Fields, "captcha-sig", "captcha_sig");
            form.CaptchaUrl = FindCaptchaImageUrl(formHtml);

            // Regex из ForPDA: captcha-time -> captcha-sig -> src. Используем как дополнительный путь.
            if (String.IsNullOrWhiteSpace(form.CaptchaTime) || String.IsNullOrWhiteSpace(form.CaptchaSig) || String.IsNullOrWhiteSpace(form.CaptchaUrl))
            {
                Match m = Regex.Match(page,
                    "captcha-time[\\\"']?\\s+value=[\\\"'](?<time>[^\\\"']*?)[\\\"'][\\s\\S]*?captcha-sig[\\\"']?\\s+value=[\\\"'](?<sig>[^\\\"']*?)[\\\"'][\\s\\S]*?src=[\\\"'](?<src>[^\\\"']*?)[\\\"']",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (m.Success)
                {
                    if (String.IsNullOrWhiteSpace(form.CaptchaTime))
                        form.CaptchaTime = HtmlDecode(m.Groups["time"].Value);

                    if (String.IsNullOrWhiteSpace(form.CaptchaSig))
                        form.CaptchaSig = HtmlDecode(m.Groups["sig"].Value);

                    if (String.IsNullOrWhiteSpace(form.CaptchaUrl))
                        form.CaptchaUrl = NormalizeUrl(HtmlDecode(m.Groups["src"].Value));
                }
            }

            if (!String.IsNullOrWhiteSpace(form.CaptchaTime) && !form.Fields.ContainsKey("captcha-time"))
                form.Fields["captcha-time"] = form.CaptchaTime;

            if (!String.IsNullOrWhiteSpace(form.CaptchaSig) && !form.Fields.ContainsKey("captcha-sig"))
                form.Fields["captcha-sig"] = form.CaptchaSig;

            return form;
        }

        private static bool IsUsableLoginForm(ForumLoginForm form)
        {
            if (form == null)
                return false;

            bool hasLoginField = form.Fields != null &&
                (form.Fields.ContainsKey("login") || form.Fields.ContainsKey("UserName") || form.Fields.ContainsKey("username"));

            bool hasPasswordField = form.Fields != null &&
                (form.Fields.ContainsKey("password") || form.Fields.ContainsKey("PassWord") || form.Fields.ContainsKey("passwd"));

            bool hasFullCaptcha = !String.IsNullOrWhiteSpace(form.CaptchaTime) &&
                !String.IsNullOrWhiteSpace(form.CaptchaSig) &&
                !String.IsNullOrWhiteSpace(form.CaptchaUrl);

            bool hasPartialCaptcha = !String.IsNullOrWhiteSpace(form.CaptchaTime) ||
                !String.IsNullOrWhiteSpace(form.CaptchaSig) ||
                !String.IsNullOrWhiteSpace(form.CaptchaUrl);

            if (hasFullCaptcha)
                return true;

            if (hasPartialCaptcha)
                return false;

            return hasLoginField && hasPasswordField;
        }

        private static string BuildLoginFormDiagnostic(string page)
        {
            string text = CleanText(page);

            if (text.IndexOf("hcaptcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("h-captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                return "4PDA отдала hCaptcha/антибот-страницу, а не старую форму с captcha-time/captcha-sig.";

            if (text.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                page.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                return "4PDA отдала форму капчи в другом формате: старые поля captcha-time/captcha-sig не найдены.";

            if (text.IndexOf("ошибка", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                return ExtractLoginError(page);

            return "Форма входа не найдена. Возможно, сайт вернул мобильную/антибот-страницу вместо act=auth.";
        }

        private static string ExtractAuthForm(string page)
        {
            MatchCollection forms = Regex.Matches(page, "(?<value><form\\b[\\s\\S]*?</form>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in forms)
            {
                string form = match.Groups["value"].Value;
                string start = ExtractFirst(form, "(?<value><form\\b[^>]*>)");
                string id = ExtractAttribute(start, "id");
                string name = ExtractAttribute(start, "name");
                string action = ExtractAttribute(start, "action");

                if (String.Equals(id, "auth", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(name, "auth", StringComparison.OrdinalIgnoreCase) ||
                    action.IndexOf("act=auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    form.IndexOf("captcha-time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    form.IndexOf("captcha-sig", StringComparison.OrdinalIgnoreCase) >= 0)
                    return form;
            }

            return "";
        }

        private static Dictionary<string, string> ExtractInputFields(string html)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (String.IsNullOrWhiteSpace(html))
                return fields;

            MatchCollection inputs = Regex.Matches(html, "<input\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match input in inputs)
            {
                string attrs = input.Groups["attrs"].Value;
                string name = HtmlDecode(ExtractAttribute(attrs, "name"));
                string value = HtmlDecode(ExtractAttribute(attrs, "value"));

                if (String.IsNullOrWhiteSpace(name))
                    continue;

                if (!fields.ContainsKey(name))
                    fields.Add(name, value);
                else
                    fields[name] = value;
            }

            return fields;
        }

        private static string FindCaptchaImageUrl(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string fallback = "";
            MatchCollection images = Regex.Matches(html, "<img\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match image in images)
            {
                string attrs = image.Groups["attrs"].Value;
                string src = HtmlDecode(ExtractAttribute(attrs, "src"));
                if (String.IsNullOrWhiteSpace(src))
                    continue;

                string all = attrs + " " + src;
                if (all.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    all.IndexOf("antibot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    all.IndexOf("cap", StringComparison.OrdinalIgnoreCase) >= 0)
                    return NormalizeUrl(src);

                if (String.IsNullOrWhiteSpace(fallback))
                    fallback = src;
            }

            if (!String.IsNullOrWhiteSpace(fallback) &&
                (html.IndexOf("captcha-time", StringComparison.OrdinalIgnoreCase) >= 0 || html.IndexOf("captcha-sig", StringComparison.OrdinalIgnoreCase) >= 0))
                return NormalizeUrl(fallback);

            return "";
        }

        private static string GetFieldValue(Dictionary<string, string> fields, params string[] names)
        {
            if (fields == null || names == null)
                return "";

            for (int i = 0; i < names.Length; i++)
            {
                string value;
                if (fields.TryGetValue(names[i], out value))
                    return value == null ? "" : value;
            }

            foreach (KeyValuePair<string, string> pair in fields)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (String.Equals(pair.Key, names[i], StringComparison.OrdinalIgnoreCase))
                        return pair.Value == null ? "" : pair.Value;
                }
            }

            return "";
        }

        private static void SetFormValue(List<KeyValuePair<string, string>> values, string name, string value)
        {
            for (int i = values.Count - 1; i >= 0; i--)
            {
                if (String.Equals(values[i].Key, name, StringComparison.OrdinalIgnoreCase))
                    values.RemoveAt(i);
            }

            values.Add(new KeyValuePair<string, string>(name, value == null ? "" : value));
        }

        private static void AddFormValue(List<FormPostField> values, string name, string value, bool alreadyEncoded)
        {
            values.Add(new FormPostField
            {
                Name = name == null ? "" : name,
                Value = value == null ? "" : value,
                AlreadyEncoded = alreadyEncoded
            });
        }

        private static string BuildFormBody(List<FormPostField> values)
        {
            if (values == null || values.Count == 0)
                return "";

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < values.Count; i++)
            {
                FormPostField field = values[i];

                if (i > 0)
                    builder.Append('&');

                builder.Append(UrlEncodeUtf8(field.Name));
                builder.Append('=');
                builder.Append(field.AlreadyEncoded ? field.Value : UrlEncodeUtf8(field.Value));
            }

            return builder.ToString();
        }

        private static string UrlEncodeUtf8(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            return UrlEncodeBytes(Encoding.UTF8.GetBytes(value));
        }

        private static string UrlEncodeWindows1251(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            try
            {
                return UrlEncodeBytes(Encoding.GetEncoding("windows-1251").GetBytes(value));
            }
            catch
            {
                return UrlEncodeBytes(GetWindows1251BytesFallback(value));
            }
        }

        private static string UrlEncodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                char ch = (char)b;

                if ((ch >= 'A' && ch <= 'Z') ||
                    (ch >= 'a' && ch <= 'z') ||
                    (ch >= '0' && ch <= '9') ||
                    ch == '-' || ch == '_' || ch == '.' || ch == '*')
                {
                    builder.Append(ch);
                }
                else if (ch == ' ')
                {
                    builder.Append('+');
                }
                else
                {
                    builder.Append('%');
                    builder.Append(b.ToString("X2"));
                }
            }

            return builder.ToString();
        }

        private static byte[] GetWindows1251BytesFallback(string value)
        {
            List<byte> bytes = new List<byte>();

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];

                if (ch <= 0x7F)
                {
                    bytes.Add((byte)ch);
                    continue;
                }

                int mapped = MapWindows1251(ch);
                bytes.Add((byte)(mapped >= 0 ? mapped : 0x3F));
            }

            return bytes.ToArray();
        }

        private static int MapWindows1251(char ch)
        {
            if (ch >= '\u0410' && ch <= '\u042F')
                return 0xC0 + (ch - '\u0410');

            if (ch >= '\u0430' && ch <= '\u044F')
                return 0xE0 + (ch - '\u0430');

            if (ch >= '\u00A0' && ch <= '\u00BF')
                return ch;

            switch (ch)
            {
                case '\u0402': return 0x80;
                case '\u0403': return 0x81;
                case '\u201A': return 0x82;
                case '\u0453': return 0x83;
                case '\u201E': return 0x84;
                case '\u2026': return 0x85;
                case '\u2020': return 0x86;
                case '\u2021': return 0x87;
                case '\u20AC': return 0x88;
                case '\u2030': return 0x89;
                case '\u0409': return 0x8A;
                case '\u2039': return 0x8B;
                case '\u040A': return 0x8C;
                case '\u040C': return 0x8D;
                case '\u040B': return 0x8E;
                case '\u040F': return 0x8F;
                case '\u0452': return 0x90;
                case '\u2018': return 0x91;
                case '\u2019': return 0x92;
                case '\u201C': return 0x93;
                case '\u201D': return 0x94;
                case '\u2022': return 0x95;
                case '\u2013': return 0x96;
                case '\u2014': return 0x97;
                case '\u2122': return 0x99;
                case '\u0459': return 0x9A;
                case '\u203A': return 0x9B;
                case '\u045A': return 0x9C;
                case '\u045C': return 0x9D;
                case '\u045B': return 0x9E;
                case '\u045F': return 0x9F;
                case '\u040E': return 0xA1;
                case '\u045E': return 0xA2;
                case '\u0408': return 0xA3;
                case '\u0490': return 0xA5;
                case '\u0401': return 0xA8;
                case '\u0404': return 0xAA;
                case '\u0407': return 0xAF;
                case '\u0406': return 0xB2;
                case '\u0456': return 0xB3;
                case '\u0491': return 0xB4;
                case '\u0451': return 0xB8;
                case '\u0454': return 0xBA;
                case '\u0458': return 0xBC;
                case '\u0405': return 0xBD;
                case '\u0455': return 0xBE;
                case '\u0457': return 0xBF;
            }

            return -1;
        }

        private static ForumUserProfile ParseProfile(string page)
        {
            ForumUserProfile profile = new ForumUserProfile();

            if (String.IsNullOrWhiteSpace(page))
                return profile;

            profile.RawText = CleanText(page);
            profile.Id = ExtractFirst(page, "showuser=(?<value>\\d+)");

            // Основной парсер перенесён по смыслу из ForPDA ProfileParser + patterns.json.
            // Он сначала пытается разобрать старую структуру user-box целиком, затем добирает поля мягкими fallback-ами.
            TryParseProfileMainBlock(page, profile);
            TryParseProfileNote(page, profile);
            TryParseProfileDevices(page, profile);
            TryParseProfileAbout(page, profile);
            TryParseProfileWarnings(page, profile);
            FillProfileFallbacks(page, profile);

            return profile;
        }

        private static void TryParseProfileMainBlock(string page, ForumUserProfile profile)
        {
            string mainPattern = "<div[^>]*?user-box[\\s\\S]*?<img src=\"([^\"]*?)\"[\\s\\S]*?<h1>([^<]*?)</h1>[\\s\\S]*?(?=<span class=\"title\">([^<]*?)</span>| )[\\s\\S]*?<h2>(?:<span style[^>]*?>|)([^\"<]*?)(?:</span>|)</h2>[\\s\\S]*?(<ul[\\s\\S]*?</ul>)[\\s\\S]*?<div class=\"u-note\">([\\s\\S]*?)</div>[^<]*?(?:</li>|<div)[\\s\\S]*?(<ul[\\s\\S]*?</ul>)[\\s\\S]*?(<ul[\\s\\S]*?</ul>)[\\s\\S]*?(<ul[\\s\\S]*?</ul>)[\\s\\S]*?(<ul[\\s\\S]*?</ul>)[\\s\\S]*?(<ul[\\s\\S]*?</ul>)";
            Match main = Regex.Match(page, mainPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!main.Success)
                return;

            profile.AvatarUrl = NormalizeUrl(HtmlDecode(main.Groups[1].Value.Trim()));
            profile.Nick = CleanText(main.Groups[2].Value);
            profile.Status = CleanText(main.Groups[3].Value);
            profile.Group = CleanText(main.Groups[4].Value);

            ParseInfoList(main.Groups[5].Value, profile);

            string signature = main.Groups[6].Value == null ? "" : main.Groups[6].Value.Trim();
            if (!String.Equals(CleanText(signature), "Нет подписи", StringComparison.OrdinalIgnoreCase))
                profile.Signature = CleanText(signature);

            ParsePersonalList(main.Groups[7].Value, profile);
            ParseDevicesList(main.Groups[9].Value, profile);
            ParseSiteStats(main.Groups[10].Value, profile);
            ParseForumStats(main.Groups[11].Value, profile);
        }

        private static void ParseInfoList(string html, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(html))
                return;

            foreach (Match matcher in Regex.Matches(html, "<li[\\s\\S]*?title[^>]*?>([^>]*?)<[\\s\\S]*?div[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string field = CleanText(matcher.Groups[1].Value);
                string value = CleanText(matcher.Groups[2].Value);

                if (field.IndexOf("Рег", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.RegistrationDate = value;
                else if (field.IndexOf("Последнее", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.LastVisit = value;
            }
        }

        private static void ParsePersonalList(string html, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(html))
                return;

            foreach (Match matcher in Regex.Matches(html, "<li[\\s\\S]*?title[^>]*?>([^>]*?)<[\\s\\S]*?(?=<div[^>]*>([^<]*)[\\s\\S]*?</div>|)<", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string field = CleanText(matcher.Groups[1].Value);
                string value = CleanText(matcher.Groups[2].Value);

                if (String.IsNullOrWhiteSpace(value))
                {
                    if (String.IsNullOrWhiteSpace(profile.Gender))
                        profile.Gender = field;
                }
                else if (field.IndexOf("Дата", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.Birthday = value;
                else if (field.IndexOf("Время", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.UserTime = value;
                else if (field.IndexOf("Город", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.City = value;
            }
        }

        private static void ParseSiteStats(string html, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(html))
                return;

            foreach (Match matcher in Regex.Matches(html, "<span class=\"title\">([^<]*?)</span>[\\s\\S]*?<div class=\"area\">[\\s\\S]*?(?:<a[^>]*?href=\"([^\"]*?)\"[^>]*?>)?([\\s\\S]*?)(?:</a>)?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string field = CleanText(matcher.Groups[1].Value);
                string value = CleanText(matcher.Groups[3].Value);

                if (field.IndexOf("Карма", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.SiteKarma = value;
                else if (field.IndexOf("Постов", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.SitePosts = value;
                else if (field.IndexOf("Комментов", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.SiteComments = value;
            }
        }

        private static void ParseForumStats(string html, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(html))
                return;

            foreach (Match matcher in Regex.Matches(html, "<span class=\"title\">([^<]*?)</span>[\\s\\S]*?<div class=\"area\">(?:(\\d+)|[\\s\\S]*?<a[^>]*?href=\"([^\"]*?act=(?:search|rep[^\"]*?view=history)[^\"]*?)\"[^>]*?>(?:<span[^>]*?>)?(-?\\d+)(?:</span>)?</a>)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string field = CleanText(matcher.Groups[1].Value);
                string value = matcher.Groups[4].Success && !String.IsNullOrWhiteSpace(matcher.Groups[4].Value)
                    ? matcher.Groups[4].Value
                    : matcher.Groups[2].Value;

                if (field.IndexOf("Репу", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.Reputation = value;
                else if (field.IndexOf("Тем", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.ForumTopics = value;
                else if (field.IndexOf("Постов", StringComparison.OrdinalIgnoreCase) >= 0)
                    profile.ForumPosts = value;
            }
        }

        private static void ParseDevicesList(string html, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(html) || profile == null)
                return;

            foreach (Match matcher in Regex.Matches(html, "<a[^>]*?href=\"([^\"]*)\"[^>]*?>([\\s\\S]*?)</a>([\\s\\S]*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string url = NormalizeUrl(HtmlDecode(matcher.Groups[1].Value.Trim()));
                string name = CleanText(matcher.Groups[2].Value);
                string accessory = CleanText(matcher.Groups[3].Value);
                AddProfileDevice(profile, name, accessory, url);
            }
        }

        private static void TryParseProfileDevices(string page, ForumUserProfile profile)
        {
            if (String.IsNullOrWhiteSpace(page) || profile == null || profile.Devices.Count > 0)
                return;

            string devicesBlock = ExtractFirst(page, "(?<value><ul[^>]*>[\\s\\S]*?(?:devdb|devices|Устройства|Мои устройства)[\\s\\S]*?</ul>)");
            ParseDevicesList(devicesBlock, profile);

            if (profile.Devices.Count > 0)
                return;

            foreach (Match matcher in Regex.Matches(page, "<a[^>]*?href=\"(?<url>[^\"]*(?:devdb|devices)[^\"]*)\"[^>]*?>(?<name>[\\s\\S]*?)</a>(?<accessory>[\\s\\S]*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string url = NormalizeUrl(HtmlDecode(matcher.Groups["url"].Value.Trim()));
                string name = CleanText(matcher.Groups["name"].Value);
                string accessory = CleanText(matcher.Groups["accessory"].Value);
                AddProfileDevice(profile, name, accessory, url);
            }
        }

        private static void AddProfileDevice(ForumUserProfile profile, string name, string accessory, string url)
        {
            if (profile == null || String.IsNullOrWhiteSpace(name))
                return;

            string cleanName = name.Trim();
            string cleanAccessory = String.IsNullOrWhiteSpace(accessory) ? "" : accessory.Trim();
            string cleanUrl = String.IsNullOrWhiteSpace(url) ? "" : url.Trim();

            for (int i = 0; i < profile.Devices.Count; i++)
            {
                ForumUserDevice existing = profile.Devices[i];
                if (String.Equals(existing.Name, cleanName, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(existing.Url, cleanUrl, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            profile.Devices.Add(new ForumUserDevice
            {
                Name = cleanName,
                Accessory = cleanAccessory,
                Url = cleanUrl
            });
        }

        private static void TryParseProfileNote(string page, ForumUserProfile profile)
        {
            string note = ExtractFirst(page, "<textarea[^>]*?profile-textarea\"[^>]*?>(?<value>[\\s\\S]*?)</textarea>");

            if (String.IsNullOrWhiteSpace(note))
                note = ExtractFirst(page, "<textarea[^>]*?name=['\"]note['\"][^>]*?>(?<value>[\\s\\S]*?)</textarea>");

            profile.Note = HtmlDecode(note).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static void TryParseProfileAbout(string page, ForumUserProfile profile)
        {
            string about = ExtractFirst(page, "<div[^>]*?div-custom-about[^>]*?>(?<value>[\\s\\S]*?)</div>");
            profile.About = CleanText(about);
        }

        private static void TryParseProfileWarnings(string page, ForumUserProfile profile)
        {
            int count = Regex.Matches(page, "<li class=\"wlog-", RegexOptions.IgnoreCase).Count;
            if (count > 0)
                profile.Warnings = count.ToString();
        }

        private static void FillProfileFallbacks(string page, ForumUserProfile profile)
        {
            string text = profile.RawText;

            if (String.IsNullOrWhiteSpace(profile.Nick))
                profile.Nick = CleanText(ExtractFirst(page, @"<h1[^>]*>(?<value>[\\s\\S]*?)</h1>"));

            if (String.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                string imageTag = ExtractFirst(page, @"<img\b(?=[^>]*(avatar|photo|user-box))(?<value>[^>]*)>");
                if (String.IsNullOrWhiteSpace(imageTag))
                    imageTag = ExtractFirst(page, @"<img\b(?<value>[^>]*)>");
                profile.AvatarUrl = NormalizeUrl(HtmlDecode(ExtractAttribute(imageTag, "src")));
            }

            if (String.IsNullOrWhiteSpace(profile.Group))
                profile.Group = ExtractTextField(text, "Группа");

            if (String.IsNullOrWhiteSpace(profile.ForumPosts))
                profile.ForumPosts = ExtractTextField(text, "Сообщений");

            if (String.IsNullOrWhiteSpace(profile.Reputation))
                profile.Reputation = ExtractTextField(text, "Репутация");

            if (String.IsNullOrWhiteSpace(profile.RegistrationDate))
                profile.RegistrationDate = ExtractTextField(text, "Регистрация");

            if (String.IsNullOrWhiteSpace(profile.LastVisit))
            {
                profile.LastVisit = ExtractTextField(text, "Последнее посещение");
                if (String.IsNullOrWhiteSpace(profile.LastVisit))
                    profile.LastVisit = ExtractTextField(text, "Последнее");
            }

            if (String.IsNullOrWhiteSpace(profile.Status))
            {
                profile.Status = ExtractTextField(text, "Статус");
                if (String.IsNullOrWhiteSpace(profile.Status))
                    profile.Status = ExtractTextField(text, "Звание");
            }
        }

        private static void CheckLogin(string pageBody, ForumLoginResult loginResult)
        {
            if (String.IsNullOrWhiteSpace(pageBody) || loginResult == null)
                return;

            // Точная идея из ForPDA: на странице после входа должны быть showuser=ID и logout с k=32.
            Match matcher = Regex.Match(pageBody,
                "showuser=(?<id>\\d+)\"[\\s\\S]*?action=logout[^\"']*?[;&]k=(?<k>[a-z0-9]{32})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!matcher.Success)
            {
                // Более мягкий вариант для других HTML-шаблонов 4PDA.
                matcher = Regex.Match(pageBody,
                    "showuser=(?<id>\\d+)[\\s\\S]*?act=(?:logout|Login)[^\"']*?[;&]k=(?<k>[a-z0-9]{32})",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            if (matcher.Success)
            {
                loginResult.UserId = matcher.Groups["id"].Value;
                loginResult.K = matcher.Groups["k"].Value;
                loginResult.Success = true;
            }
            else
            {
                string k = ExtractValue(pageBody, @"act=(?:logout|Login)[^""']*?[;&]k=(?<value>[a-z0-9]{32})");
                if (!String.IsNullOrWhiteSpace(k))
                    loginResult.K = k;

                string userId = ExtractValue(pageBody, "showuser=(?<value>\\d+)");
                if (!String.IsNullOrWhiteSpace(userId) && !String.IsNullOrWhiteSpace(k))
                {
                    loginResult.UserId = userId;
                    loginResult.Success = true;
                }
            }

            string avatarTag = ExtractFirst(pageBody, @"<img\b(?=[^>]*(avatar|photo))(?<value>[^>]*)>");
            string avatar = NormalizeUrl(HtmlDecode(ExtractAttribute(avatarTag, "src")));
            if (!String.IsNullOrWhiteSpace(avatar))
                loginResult.UserAvatarUrl = avatar;
        }

        private static string ExtractLoginError(string html)
        {
            string error = TryExtractLoginError(html);
            return String.IsNullOrWhiteSpace(error) ? "Не удалось подтвердить авторизацию." : error;
        }

        private static string TryExtractLoginError(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string errorList = ExtractFirst(html, "errors-list[^>]*>(?<value>[\\s\\S]*?)(?:</ul>|</div>|<form|$)");
            if (!String.IsNullOrWhiteSpace(errorList))
                return CleanText(errorList);

            string errorText = ExtractFirst(html, "wr va-m text[^>]*>(?<value>[\\s\\S]*?)(?:</div>|<br|$)");
            if (!String.IsNullOrWhiteSpace(errorText))
                return CleanText(errorText);

            string text = CleanText(html);
            string reason = ExtractValue(text, @"Причина:\s*(?<value>[^\r\n]+)");

            if (!String.IsNullOrWhiteSpace(reason))
                return reason;

            if (text.IndexOf("Неверно введено слово", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("код подтверждения", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Неверно введена капча.";

            if (text.IndexOf("Неверное имя пользователя", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Неверный пароль", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Неправильный логин или пароль.";

            return "";
        }

        private static string BuildAuthFailureDiagnostic(string html)
        {
            string text = CleanText(html);

            if (text.IndexOf("hcaptcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("h-captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                return "4PDA вернула антибот/hCaptcha-страницу. Старый вход через act=auth не может пройти эту проверку.";

            if (text.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("капч", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Авторизация не подтверждена. Скорее всего, неверно введена капча или 4PDA изменила формат проверки.";

            if (text.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("парол", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Авторизация не подтверждена. Проверьте логин и пароль.";

            return "Авторизация не подтверждена: cookie member_id не пришла и ссылка logout не найдена.";
        }

        private static void ApplyCookieLoginResult(ForumLoginResult result, string login)
        {
            if (result == null)
                return;

            string cookieUserId = GetCookieValue("member_id");
            string passHash = GetCookieValue("pass_hash");

            if (!String.IsNullOrWhiteSpace(cookieUserId) &&
                !String.Equals(cookieUserId, "deleted", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(cookieUserId, "0", StringComparison.OrdinalIgnoreCase))
            {
                result.UserId = cookieUserId;
                result.UserLogin = login;
                result.Success = true;
            }

            if (String.Equals(cookieUserId, "deleted", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(passHash, "deleted", StringComparison.OrdinalIgnoreCase))
            {
                result.Success = false;
                result.ErrorMessage = "Неправильный логин, пароль или капча.";
            }
        }

        private static string ExtractInputValue(string html, string name)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string input = ExtractFirst(html, "<input\\b(?=[^>]*\\bname\\s*=\\s*['\"]?" + Regex.Escape(name) + "['\"]?)[^>]*>");
            return ExtractAttribute(input, "value");
        }

        private static string ExtractAttribute(string tag, string name)
        {
            if (String.IsNullOrWhiteSpace(tag))
                return "";

            string pattern = "\\b" + Regex.Escape(name) + "\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))";
            Match match = Regex.Match(tag, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value : "";
        }

        private static string ExtractFirst(string text, string pattern)
        {
            if (String.IsNullOrEmpty(text))
                return "";

            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value : "";
        }

        private static string ExtractValue(string text, string pattern)
        {
            return ExtractFirst(text, pattern);
        }

        private static string ExtractTextField(string text, string title)
        {
            if (String.IsNullOrWhiteSpace(text))
                return "";

            string pattern = Regex.Escape(title) + @"\s*:?\s*(?<value>[^\r\n]+)";
            string value = ExtractValue(text, pattern);
            return value.Trim();
        }

        private static string CleanText(string html)
        {
            if (String.IsNullOrWhiteSpace(html))
                return "";

            string text = Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</\s*(p|div|li|tr|h1|h2|h3|h4|h5|h6)\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\s*\n\s*", "\n");
            text = Regex.Replace(text, @"\n{2,}", "\n");
            return text.Trim();
        }

        private static string HtmlDecode(string value)
        {
            if (String.IsNullOrEmpty(value))
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

            return new Uri(new Uri("https://" + Host + "/"), url).ToString();
        }

        private static string GetCookieValue(string name)
        {
            string[] urls =
            {
                "https://" + Host + "/",
                "https://." + Host + "/",
                "https://" + Host + "/forum/",
                "https://" + Host + "/forum/index.php"
            };

            foreach (string url in urls)
            {
                try
                {
                    foreach (HttpCookie cookie in Filter.CookieManager.GetCookies(new Uri(url, UriKind.Absolute)))
                    {
                        if (String.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase))
                            return cookie.Value;
                    }
                }
                catch
                {
                }
            }

            return "";
        }

        private static void SaveCurrentUser(ForumLoginResult result)
        {
            WriteSetting(KeyUserId, result.UserId);
            WriteSetting(KeyUserLogin, result.UserLogin);
            WriteSetting(KeyUserAvatar, result.UserAvatarUrl);
            WriteSetting(KeyLogoutK, result.K);
        }

        private static string ReadSetting(string key)
        {
            object value;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out value) && value != null)
                return value.ToString();

            return "";
        }

        private static void WriteSetting(string key, string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            else
                ApplicationData.Current.LocalSettings.Values[key] = value;
        }
    }

    internal sealed class FormPostField
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public bool AlreadyEncoded { get; set; }
    }

    internal sealed class ForumLoginForm
    {
        public ForumLoginForm()
        {
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string K { get; set; }
        public string PostUrl { get; set; }
        public string CaptchaUrl { get; set; }
        public string CaptchaTime { get; set; }
        public string CaptchaSig { get; set; }
        public string Session { get; set; }
        public Dictionary<string, string> Fields { get; set; }

        public bool CaptchaRequired
        {
            get
            {
                return !String.IsNullOrWhiteSpace(CaptchaUrl) ||
                    !String.IsNullOrWhiteSpace(CaptchaTime) ||
                    !String.IsNullOrWhiteSpace(CaptchaSig);
            }
        }
    }

    internal sealed class ForumLoginResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string UserId { get; set; }
        public string UserLogin { get; set; }
        public string UserAvatarUrl { get; set; }
        public string K { get; set; }
    }

    internal sealed class ForumUserProfile
    {
        public ForumUserProfile()
        {
            Devices = new List<ForumUserDevice>();
        }

        public string Id { get; set; }
        public string Nick { get; set; }
        public string AvatarUrl { get; set; }
        public string Group { get; set; }
        public string Status { get; set; }
        public string Reputation { get; set; }
        public string ForumPosts { get; set; }
        public string ForumTopics { get; set; }
        public string SiteKarma { get; set; }
        public string SitePosts { get; set; }
        public string SiteComments { get; set; }
        public string RegistrationDate { get; set; }
        public string LastVisit { get; set; }
        public string Birthday { get; set; }
        public string UserTime { get; set; }
        public string City { get; set; }
        public string Gender { get; set; }
        public string Signature { get; set; }
        public string About { get; set; }
        public string Note { get; set; }
        public string Warnings { get; set; }
        public List<ForumUserDevice> Devices { get; set; }
        public string RawText { get; set; }
    }

    internal sealed class ForumUserDevice
    {
        public string Name { get; set; }
        public string Accessory { get; set; }
        public string Url { get; set; }
    }
}
