using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace _4PDA
{
    public static class ForumCookieHelper
    {
        public const string ForumRoot = "https://4pda.to/";
        public const string ForumUrl = "https://4pda.to/forum/";

        // Важно: этот UA совпадает с WebView/IE Mobile на WP 8.1.
        // cf_clearance обычно привязан к User-Agent, поэтому HTTP-запросы скачивания должны идти с тем же UA,
        // с которым пользователь прошёл проверку в WebView.
        public const string UserAgent = "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko";
        public const string AcceptLanguage = "ru-RU,ru;q=0.8,en-US;q=0.6,en;q=0.4";

        private static readonly string[] KnownCookieNames = new string[]
        {
            "ngx_mb",
            "member_id",
            "pass_hash",
            "session_id",
            "anonymous",
            "cf_clearance"
        };

        public static void ApplyDefaultHeaders(HttpClient client)
        {
            if (client == null)
                return;

            try { client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent); } catch { }
            try { client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU"); } catch { }
            try { client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru;q=0.8"); } catch { }
            try { client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US;q=0.6"); } catch { }
            try { client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.4"); } catch { }
        }

        public static void ApplyDefaultHeaders(HttpRequestMessage request)
        {
            if (request == null)
                return;

            try { request.Headers.UserAgent.ParseAdd(UserAgent); } catch { }
            try { request.Headers.AcceptLanguage.ParseAdd("ru-RU"); } catch { }
            try { request.Headers.AcceptLanguage.ParseAdd("ru;q=0.8"); } catch { }
            try { request.Headers.AcceptLanguage.ParseAdd("en-US;q=0.6"); } catch { }
            try { request.Headers.AcceptLanguage.ParseAdd("en;q=0.4"); } catch { }
            TryAppendHeader(request, "Accept-Language", AcceptLanguage);
        }

        public static void TryAppendHeader(HttpRequestMessage request, string name, string value)
        {
            if (request == null || String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(value))
                return;

            try
            {
                request.Headers.TryAppendWithoutValidation(name, value);
            }
            catch
            {
                try { request.Headers.Append(name, value); } catch { }
            }
        }

        public static string GetCookieHeaderForUrl(Uri uri)
        {
            Dictionary<string, string> cookies = GetKnownCookies();
            if (!cookies.ContainsKey("ngx_mb"))
                cookies["ngx_mb"] = "1";

            // Не уводим приватные 4PDA cookies на внешние сайты.
            if (uri != null && uri.Host != null && uri.Host.IndexOf("4pda", StringComparison.OrdinalIgnoreCase) < 0)
            {
                cookies.Remove("member_id");
                cookies.Remove("pass_hash");
                cookies.Remove("session_id");
                cookies.Remove("anonymous");
                cookies.Remove("cf_clearance");
            }

            List<string> pairs = new List<string>();
            foreach (string name in KnownCookieNames)
            {
                string value;
                if (cookies.TryGetValue(name, out value) && !String.IsNullOrWhiteSpace(value))
                    pairs.Add(name + "=" + value);
            }

            foreach (KeyValuePair<string, string> pair in cookies)
            {
                if (Array.IndexOf(KnownCookieNames, pair.Key) >= 0)
                    continue;
                if (!String.IsNullOrWhiteSpace(pair.Value))
                    pairs.Add(pair.Key + "=" + pair.Value);
            }

            return String.Join("; ", pairs.ToArray());
        }

        public static Dictionary<string, string> GetKnownCookies()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["ngx_mb"] = "1";

            ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
            foreach (KeyValuePair<string, object> item in settings.Values)
            {
                string key = item.Key ?? "";
                string value = item.Value == null ? "" : item.Value.ToString();
                if (String.IsNullOrWhiteSpace(value))
                    continue;

                if (key.StartsWith("cookie_", StringComparison.OrdinalIgnoreCase))
                {
                    string name = key.Substring("cookie_".Length);
                    string cookieValue = ExtractCookieValue(name, value);
                    if (!String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(cookieValue))
                        result[name] = cookieValue;
                }
                else if (Array.IndexOf(KnownCookieNames, key) >= 0)
                {
                    result[key] = value;
                }
            }

            return result;
        }

        public static bool HasCookie(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                return false;
            Dictionary<string, string> cookies = GetKnownCookies();
            string value;
            return cookies.TryGetValue(name, out value) && !String.IsNullOrWhiteSpace(value);
        }

        public static string DescribeKnownCookies()
        {
            return DescribeCookieHeader(GetCookieHeaderForUrl(new Uri(ForumRoot)));
        }

        public static string DescribeCookieHeader(string cookieHeader)
        {
            if (String.IsNullOrWhiteSpace(cookieHeader))
                return "<none>";

            List<string> parts = new List<string>();
            string[] pairs = cookieHeader.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in pairs)
            {
                string pair = raw.Trim();
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                string name = pair.Substring(0, eq).Trim();
                string value = pair.Substring(eq + 1).Trim();
                parts.Add(name + "(" + value.Length.ToString() + ")");
            }

            return parts.Count == 0 ? "<none>" : String.Join(", ", parts.ToArray());
        }

        public static void SaveForumCookiesFromSystemCookieManager(string reason)
        {
            SaveForumCookiesFromSystemCookieManager(new Uri(ForumRoot), reason);
            SaveForumCookiesFromSystemCookieManager(new Uri(ForumUrl), reason);
        }

        private static void SaveForumCookiesFromSystemCookieManager(Uri uri, string reason)
        {
            try
            {
                HttpBaseProtocolFilter filter = new HttpBaseProtocolFilter();
                HttpCookieCollection cookies = filter.CookieManager.GetCookies(uri);
                foreach (HttpCookie cookie in cookies)
                    SaveKnownCookie(cookie, uri, reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[4PDA.Auth] " + DateTime.Now.ToString("HH:mm:ss.fff") + " SaveForumCookiesFromSystemCookieManager failed uri=" + uri + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void SaveKnownCookie(HttpCookie cookie, Uri sourceUri, string reason)
        {
            if (cookie == null || String.IsNullOrWhiteSpace(cookie.Name) || String.IsNullOrWhiteSpace(cookie.Value))
                return;

            if (!IsKnownForumCookie(cookie.Name))
                return;

            SaveCookieValue(cookie.Name, cookie.Value, sourceUri == null ? ForumRoot : sourceUri.AbsoluteUri, reason);
        }

        public static void SaveCookieValue(string name, string value, string url, string reason)
        {
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(value))
                return;

            try
            {
                string normalizedUrl = String.IsNullOrWhiteSpace(url) ? ForumRoot : url;
                string stored = normalizedUrl + "|:|" + name + "=" + value;
                ApplicationData.Current.LocalSettings.Values["cookie_" + name] = stored;
                if (name.Equals("member_id", StringComparison.OrdinalIgnoreCase))
                    ApplicationData.Current.LocalSettings.Values["member_id"] = value;

                Debug.WriteLine("[4PDA.Auth] " + DateTime.Now.ToString("HH:mm:ss.fff") + " saved cookie_" + name + "(" + value.Length.ToString() + ") reason=" + reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[4PDA.Auth] " + DateTime.Now.ToString("HH:mm:ss.fff") + " SaveCookieValue failed name=" + name + " error=" + ex.Message);
            }
        }

        private static bool IsKnownForumCookie(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                return false;
            return Array.IndexOf(KnownCookieNames, name) >= 0;
        }

        private static string ExtractCookieValue(string expectedName, string raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
                return "";

            string value = raw.Trim();
            int split = value.IndexOf("|:|", StringComparison.Ordinal);
            if (split >= 0)
                value = value.Substring(split + 3);

            int semicolon = value.IndexOf(';');
            string firstPair = semicolon >= 0 ? value.Substring(0, semicolon) : value;
            int eq = firstPair.IndexOf('=');
            if (eq >= 0)
            {
                string name = firstPair.Substring(0, eq).Trim();
                string cookieValue = firstPair.Substring(eq + 1).Trim();
                if (String.IsNullOrWhiteSpace(expectedName) || name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    return cookieValue;
            }

            return value;
        }
    }
}
