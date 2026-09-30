using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Services
{
    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Notes { get; set; }
    }

    // GitHub Releases 更新检查。整条链路都允许失败：离线、代理不通、被限流
    // 都不该影响解压，最多是这次不提示。
    public static class UpdateService
    {
        public const string Repo = "pingod/UZIP2";
        public static string ApiUrl => "https://api.github.com/repos/" + Repo + "/releases/latest";
        public static string ReleasePageUrl => "https://github.com/" + Repo + "/releases/latest";

        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        public static string CurrentVersion()
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        public static async Task<UpdateInfo> CheckAsync(Func<string, Task<string>> fetch = null, CancellationToken ct = default)
        {
            try
            {
                fetch ??= GetAsStringAsync;
                return ParseRelease(await fetch(ApiUrl).ConfigureAwait(false));
            }
            catch (Exception)
            {
                return null;
            }
        }

        static async Task<string> GetAsStringAsync(string url)
        {
            using var handler = new HttpClientHandler { UseProxy = true };
            var proxy = ProxyFromEnvironment();
            if (proxy != null) handler.Proxy = proxy;
            using var http = new HttpClient(handler) { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UZIP2");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return await http.GetStringAsync(url).ConfigureAwait(false);
        }

        // .NET 默认只看系统代理；CN 网络下很多人是靠环境变量走本地代理的，这里补上。
        static IWebProxy ProxyFromEnvironment()
        {
            foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
                if (TryProxy(Environment.GetEnvironmentVariable(name), out var proxy)) return proxy;
            return null;
        }

        public static bool TryProxy(string value, out IWebProxy proxy)
        {
            proxy = null;
            var text = (value ?? "").Trim();
            if (text.Length == 0) return false;
            if (string.Equals(text, "direct", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            proxy = new WebProxy(uri);
            return true;
        }

        public static UpdateInfo ParseRelease(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                var info = new UpdateInfo
                {
                    Version = GetString(root, "tag_name"),
                    Url = GetString(root, "html_url") ?? ReleasePageUrl,
                    Notes = GetString(root, "body"),
                };
                if (string.IsNullOrWhiteSpace(info.Version)) return null;
                info.Version = NormalizeVersion(info.Version);
                return string.IsNullOrEmpty(info.Version) ? null : info;
            }
            catch (JsonException) { return null; }
        }

        static string GetString(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static string NormalizeVersion(string tag)
        {
            tag = (tag ?? "").Trim().TrimStart('v', 'V');
            int cut = tag.Length;
            for (int i = 0; i < tag.Length; i++)
            {
                if (char.IsDigit(tag[i]) || tag[i] == '.') continue;
                cut = i;
                break;
            }
            tag = tag.Substring(0, cut).Trim('.');
            return tag.Length == 0 ? null : tag;
        }

        // 只有拿到更高的版本号才提示；解析不了的写法一律当作"不提示"
        public static bool IsNewer(string current, string candidate)
        {
            var a = Parse(current);
            var b = Parse(candidate);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
            {
                if (b[i] != a[i]) return b[i] > a[i];
            }
            return false;
        }

        static int[] Parse(string version)
        {
            var parts = (version ?? "").Trim().TrimStart('v', 'V').Split('.');
            if (parts.Length == 0 || parts.Length > 4) return null;
            var nums = new int[3];
            for (int i = 0; i < parts.Length && i < 3; i++)
                if (!int.TryParse(parts[i], out nums[i])) return null;
            return nums;
        }
    }
}
