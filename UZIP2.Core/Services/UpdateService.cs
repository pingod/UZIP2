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
        // 框架依赖单文件资产的直链（自更新下载用）；没有匹配资产时为 null
        public string DownloadUrl { get; set; }
        // 同名 UZIP2.exe.sha256 侧车的直链；老版本没发布校验值时为 null（更新时跳过这一步）
        public string Sha256Url { get; set; }
        public long Size { get; set; }
    }

    // GitHub Releases 更新检查。整条链路都允许失败：离线、代理不通、被限流
    // 都不该影响解压，最多是这次不提示。
    public static class UpdateService
    {
        public const string Repo = "pingod/UZIP2";
        public static string ApiUrl => "https://api.github.com/repos/" + Repo + "/releases/latest";
        public static string ReleasePageUrl => "https://github.com/" + Repo + "/releases/latest";

        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        // 自更新下载体积上限：框架依赖单文件约 8.5MB，这里给到 64MB 的宽裕上限，
        // 既能挡下"直链被换成别的大文件"，又远小于 170MB 的自包含版（不该走就地更新）。
        // 0 或负数表示不限制。
        public const long MaxDownloadBytes = 64L * 1024 * 1024;

        public static string CurrentVersion()
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        public static async Task<UpdateInfo> CheckAsync(Func<string, Task<string>> fetch = null, CancellationToken ct = default)
        {
            try
            {
                fetch ??= FetchWithFallbackAsync;
                return ParseRelease(await fetch(ApiUrl).ConfigureAwait(false));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>测试替身：(url, useProxy) → 响应体；抛出异常代表这一趟没走通。</summary>
        public static Func<string, bool, Task<string>> Transport;

        /// <summary>
        /// 这条 URL 该不该套代理。两个逃生口：
        /// ① 变量值写成 direct（本机的 mihomo 出口会被 GitHub 限流，直连反而是 200）；
        /// ② NO_PROXY 命中该域名。都不设时沿用 .NET 的系统/环境代理解析。
        /// </summary>
        public static bool UsesProxyFor(string url)
        {
            var host = HostOf(url);
            foreach (var name in SchemeEnvNames(url))
                if (string.Equals((Environment.GetEnvironmentVariable(name) ?? "").Trim(),
                        "direct", StringComparison.OrdinalIgnoreCase))
                    return false;
            return !MatchesNoProxy(host);
        }

        static string[] SchemeEnvNames(string url)
        {
            bool https = (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                         || (url ?? "").IndexOf("https://", StringComparison.OrdinalIgnoreCase) == 0;
            return https
                ? new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" }
                : new[] { "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" };
        }

        static string HostOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)) return u.Host;
            // 容忍 "api.github.com:443/path" 这类没有 scheme 的写法
            var text = (url ?? "").Trim();
            int slash = text.IndexOf('/');
            if (slash >= 0) text = text.Substring(0, slash);
            int colon = text.LastIndexOf(':');
            if (colon > 0 && char.IsDigit(text[colon + 1])) text = text.Substring(0, colon);
            return text;
        }

        // NO_PROXY 支持 * 、.github.com 、github.com 、逗号/空格分隔；按域名边界匹配，不当子串用
        static bool MatchesNoProxy(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var list = (Environment.GetEnvironmentVariable("NO_PROXY")
                        ?? Environment.GetEnvironmentVariable("no_proxy") ?? "").Trim();
            if (list.Length == 0) return false;
            host = host.TrimEnd('.').ToLowerInvariant();
            foreach (var raw in list.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token == "*") return true;
                var entry = token.TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
                if (entry.Length == 0) continue;
                if (host == entry) return true;
                if (host.Length > entry.Length && host.EndsWith(entry, StringComparison.Ordinal)
                    && host[host.Length - entry.Length - 1] == '.') return true;
            }
            return false;
        }

        /// <summary>网络类失败才值得换条路重试；代码 bug 抛出来该让它响。</summary>
        public static bool IsRetryable(Exception ex)
            => ex is HttpRequestException || ex is TaskCanceledException
               || ex is System.IO.IOException || ex is System.Net.Sockets.SocketException;

        /// <summary>先按代理策略走一趟；代理里的网络失败就直连重试一次。</summary>
        public static async Task<string> FetchWithFallbackAsync(string url)
        {
            bool useProxy = UsesProxyFor(url);
            try
            {
                return await GetOnceAsync(url, useProxy).ConfigureAwait(false);
            }
            catch (Exception ex) when (useProxy && IsRetryable(ex))
            {
                return await GetOnceAsync(url, useProxy: false).ConfigureAwait(false);
            }
        }

        static Task<string> GetOnceAsync(string url, bool useProxy)
            => Transport != null ? Transport(url, useProxy) : GetAsStringAsync(url, useProxy);

        static async Task<string> GetAsStringAsync(string url, bool useProxy)
        {
            using var http = NewClient(Timeout, useProxy);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return await http.GetStringAsync(url).ConfigureAwait(false);
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
                PickAsset(root, info);
                return string.IsNullOrEmpty(info.Version) ? null : info;
            }
            catch (JsonException) { return null; }
        }

        // 我们发布的框架依赖单文件就叫 UZIP2.exe；命中它才有直下链接，命中不到
        // （比如只挂了 selfcontained.zip）就留 null，让上层回落到"打开下载页"。
        const string FallbackAssetName = "UZIP2.exe";

        public static void PickAsset(JsonElement root, UpdateInfo info)
        {
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(a, "name");
                if (string.Equals(name, FallbackAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    info.DownloadUrl = GetString(a, "browser_download_url");
                    if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                        info.Size = sz.GetInt64();
                }
                else if (info.Sha256Url == null
                         && string.Equals(name, FallbackAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    info.Sha256Url = GetString(a, "browser_download_url");
                }
            }
        }

        // .NET 默认只看系统代理；CN 网络下很多人是靠环境变量走本地代理的，这里补上。
        static IWebProxy ProxyFromEnvironment()
        {
            foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
                if (TryProxy(Environment.GetEnvironmentVariable(name), out var proxy)) return proxy;
            return null;
        }

        // 带环境代理 + UA 的 HttpClient，检查更新与自更新下载共用同一套代理策略。
        public static HttpClient NewClient(TimeSpan timeout) => NewClient(timeout, useProxy: true);

        public static HttpClient NewClient(TimeSpan timeout, bool useProxy)
        {
            var handler = new HttpClientHandler { UseProxy = useProxy, AllowAutoRedirect = true };
            if (useProxy)
            {
                var proxy = ProxyFromEnvironment();
                if (proxy != null) handler.Proxy = proxy;
            }
            else
            {
                handler.Proxy = null;
            }
            var http = new HttpClient(handler) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UZIP2");
            return http;
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
