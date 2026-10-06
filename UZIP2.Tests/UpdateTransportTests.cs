using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 更新检查的传输层：代理可选（direct / NO_PROXY）+ 代理失败退避直连。
    // 本机实测过：api.github.com 走代理出口会被限流 403，直连却是 200，
    // 只会"用代理"或"只用系统代理"的写法会把更新提示静默吞掉。
    [Collection("proxy-env")]
    public class UpdateTransportTests : IDisposable
    {
        const string Url = "https://api.github.com/repos/pingod/UZIP2/releases/latest";

        readonly Dictionary<string, string> _saved = new Dictionary<string, string>();

        public UpdateTransportTests()
        {
            foreach (var name in new[]
                     {
                         "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy",
                         "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy"
                     })
            {
                _saved[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            UpdateService.Transport = null;
        }

        public void Dispose()
        {
            foreach (var kv in _saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            UpdateService.Transport = null;
        }

        static void Env(string name, string value) => Environment.SetEnvironmentVariable(name, value);

        sealed class Recorder
        {
            public readonly List<bool> UsedProxy = new List<bool>();
        }

        static void UseTransport(Recorder rec, Exception failFirstWith = null, string body = "{\"tag_name\":\"v9.9.9\"}")
        {
            UpdateService.Transport = (url, useProxy) =>
            {
                int n = rec.UsedProxy.Count;
                rec.UsedProxy.Add(useProxy);
                if (n == 0 && failFirstWith != null) throw failFirstWith;
                return Task.FromResult(body);
            };
        }

        // ---------- UsesProxyFor ----------

        [Fact]
        public void Uses_system_proxy_when_environment_says_nothing()
            => Assert.True(UpdateService.UsesProxyFor(Url));

        [Theory]
        [InlineData("HTTPS_PROXY", "direct")]
        [InlineData("https_proxy", "DIRECT")]
        [InlineData("ALL_PROXY", "direct")]
        public void Explicit_direct_opts_out(string name, string value)
        {
            Env(name, value);
            Assert.False(UpdateService.UsesProxyFor(Url));
        }

        // 按 curl 的惯例，HTTP_PROXY 只管 http，不该顺手关掉 https 的代理
        [Fact]
        public void Http_direct_does_not_leak_into_https()
        {
            Env("HTTP_PROXY", "direct");
            Assert.True(UpdateService.UsesProxyFor(Url));
            Assert.False(UpdateService.UsesProxyFor("http://example.com/x"));
        }

        [Theory]
        [InlineData("api.github.com")]
        [InlineData(".github.com")]
        [InlineData("github.com")]
        [InlineData("*.github.com")]
        [InlineData("*")]
        [InlineData("example.com, api.github.com")]
        public void No_proxy_binds_its_own_hosts(string noProxy)
        {
            Env("NO_PROXY", noProxy);
            Assert.False(UpdateService.UsesProxyFor(Url));
        }

        [Theory]
        [InlineData("example.com")]
        [InlineData("mygithub.com")]      // 后缀必须按域名边界匹配，不能当子串
        public void No_proxy_leaves_other_hosts_alone(string noProxy)
        {
            Env("NO_PROXY", noProxy);
            Assert.True(UpdateService.UsesProxyFor(Url));
        }

        [Fact]
        public void Port_and_case_in_the_host_do_not_confuse_the_match()
        {
            Env("NO_PROXY", "API.GitHub.com");
            Assert.False(UpdateService.UsesProxyFor("https://api.github.com:443/repos/x"));
        }

        // ---------- FetchWithFallbackAsync ----------

        [Fact]
        public async Task Success_on_the_first_try_makes_exactly_one_call()
        {
            var rec = new Recorder();
            UseTransport(rec);
            var body = await UpdateService.FetchWithFallbackAsync(Url);
            Assert.Contains("9.9.9", body);
            Assert.Equal(new[] { true }, rec.UsedProxy);
        }

        [Fact]
        public async Task Proxy_failure_retries_direct()
        {
            var rec = new Recorder();
            UseTransport(rec, new HttpRequestException("403 rate limited"));
            var body = await UpdateService.FetchWithFallbackAsync(Url);
            Assert.Contains("9.9.9", body);
            Assert.Equal(new[] { true, false }, rec.UsedProxy);
        }

        [Fact]
        public async Task Timeout_retries_direct()
        {
            var rec = new Recorder();
            UseTransport(rec, new TaskCanceledException());
            await UpdateService.FetchWithFallbackAsync(Url);
            Assert.Equal(new[] { true, false }, rec.UsedProxy);
        }

        [Fact]
        public async Task Socket_failure_retries_direct()
        {
            var rec = new Recorder();
            UseTransport(rec, new IOException("connection refused"));
            await UpdateService.FetchWithFallbackAsync(Url);
            Assert.Equal(new[] { true, false }, rec.UsedProxy);
        }

        [Fact]
        public async Task Already_direct_does_not_retry_direct()
        {
            Env("NO_PROXY", "api.github.com");
            var rec = new Recorder();
            UseTransport(rec, new HttpRequestException("offline"));
            await Assert.ThrowsAsync<HttpRequestException>(() => UpdateService.FetchWithFallbackAsync(Url));
            Assert.Equal(new[] { false }, rec.UsedProxy);
        }

        [Fact]
        public async Task Non_transport_failure_is_not_retried()
        {
            var rec = new Recorder();
            UseTransport(rec, new InvalidOperationException("bug"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.FetchWithFallbackAsync(Url));
            Assert.Single(rec.UsedProxy);
        }

        [Fact]
        public async Task Second_failure_propagates_so_the_caller_stays_silent()
        {
            var rec = new Recorder();
            UpdateService.Transport = (url, useProxy) =>
            {
                rec.UsedProxy.Add(useProxy);
                throw new HttpRequestException("all routes down");
            };
            await Assert.ThrowsAsync<HttpRequestException>(() => UpdateService.FetchWithFallbackAsync(Url));
            Assert.Equal(new[] { true, false }, rec.UsedProxy);
        }

        [Fact]
        public async Task CheckAsync_falls_back_when_the_proxy_is_rate_limited()
        {
            var rec = new Recorder();
            UseTransport(rec, new HttpRequestException("403"));
            var info = await UpdateService.CheckAsync();
            Assert.NotNull(info);
            Assert.Equal("9.9.9", info.Version);
            Assert.Equal(new[] { true, false }, rec.UsedProxy);
        }
    }
}
