using System;
using System.IO;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    public class UpdateServiceTests
    {
        [Fact]
        public void ParseRelease_reads_tag_url_and_notes()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.1.0\",\"html_url\":\"https://gh/x/releases/tag/v3.1.0\",\"body\":\"修了两个 bug\"}");
            Assert.NotNull(info);
            Assert.Equal("3.1.0", info.Version);
            Assert.Equal("https://gh/x/releases/tag/v3.1.0", info.Url);
            Assert.Equal("修了两个 bug", info.Notes);
        }

        [Fact]
        public void ParseRelease_without_html_url_falls_back_to_release_page()
        {
            var info = UpdateService.ParseRelease("{\"tag_name\":\"3.1.0\"}");
            Assert.Equal(UpdateService.ReleasePageUrl, info.Url);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("\"3.1.0\"")]
        [InlineData("{}")]
        [InlineData("{\"tag_name\":\"\"}")]
        [InlineData("{\"tag_name\":\"v\"}")]
        [InlineData("{\"tag_name\":\"release-3.1\"}")]
        public void ParseRelease_refuses_junk(string json)
            => Assert.Null(UpdateService.ParseRelease(json));

        [Theory]
        [InlineData("v3.1.0-beta", "3.1.0")]
        [InlineData("V3.1.0", "3.1.0")]
        [InlineData("3.1", "3.1")]
        [InlineData(" 3.1.0 ", "3.1.0")]
        [InlineData("3.1.0-rc1", "3.1.0")]
        public void Tag_is_normalized_to_numbers(string tag, string expected)
            => Assert.Equal(expected, UpdateService.ParseRelease("{\"tag_name\":\"" + tag + "\"}").Version);

        [Theory]
        [InlineData("3.0.0", "3.1.0", true)]
        [InlineData("3.0.0", "3.0.1", true)]
        [InlineData("3.0.0", "4.0.0", true)]
        [InlineData("3.0.0", "3.0.0", false)]
        [InlineData("3.0.0", "2.9.9", false)]
        [InlineData("3.0", "3.0.0", false)]
        [InlineData("3.0.0", "v3.0.1", true)]
        [InlineData("3.0.0", "3.1", true)]
        [InlineData("3.0.0", "abc", false)]
        [InlineData("3.0.0", "", false)]
        [InlineData(null, "3.0.0", false)]
        [InlineData("3.0.0", "3.0.0.1", false)]
        public void IsNewer_compares_three_numeric_parts(string current, string candidate, bool expected)
            => Assert.Equal(expected, UpdateService.IsNewer(current, candidate));

        [Fact]
        public void CurrentVersion_looks_like_a_release_number()
            => Assert.Matches(@"^\d+\.\d+\.\d+$", UpdateService.CurrentVersion());

        [Fact]
        public async Task CheckAsync_uses_the_injected_fetch()
        {
            string called = null;
            var info = await UpdateService.CheckAsync(url =>
            {
                called = url;
                return Task.FromResult("{\"tag_name\":\"v9.9.9\"}");
            });
            Assert.Equal(UpdateService.ApiUrl, called);
            Assert.Equal("9.9.9", info.Version);
        }

        [Fact]
        public async Task CheckAsync_swallows_transport_failures()
        {
            var info = await UpdateService.CheckAsync(_ => throw new HttpRequestFail());
            Assert.Null(info);
        }

        [Fact]
        public async Task CheckAsync_survives_garbage_body()
        {
            var info = await UpdateService.CheckAsync(_ => Task.FromResult("<html>rate limited</html>"));
            Assert.Null(info);
        }

        sealed class HttpRequestFail : Exception { }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void Switch_off_silences_the_window_check(bool enabled, bool expected)
        {
            var last = new DateTime(2026, 9, 20, 8, 0, 0);
            var now = new DateTime(2026, 9, 30, 23, 0, 0);
            Assert.Equal(expected, HomeViewModel.ShouldCheck(enabled, last, now));
        }

        [Fact]
        public void Never_checked_before_gets_checked_now()
            => Assert.True(HomeViewModel.ShouldCheck(true, default, new DateTime(2026, 9, 30)));

        [Fact]
        public void Within_the_day_window_it_stays_quiet()
        {
            var now = new DateTime(2026, 9, 30, 12, 0, 0);
            Assert.False(HomeViewModel.ShouldCheck(true, now.AddHours(-15), now));
        }

        [Fact]
        public void Past_the_window_gets_checked_again()
        {
            var now = new DateTime(2026, 9, 30, 12, 0, 0);
            Assert.True(HomeViewModel.ShouldCheck(true, now.AddHours(-21), now));
        }

        [Theory]
        [InlineData("http://127.0.0.1:9565", true)]
        [InlineData("  http://127.0.0.1:9565  ", true)]
        [InlineData("https://proxy.internal:8443", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("direct", false)]
        [InlineData("127.0.0.1:9565", false)]      // 缺方案不猜，避免拼错时静默走错路
        [InlineData("socks5://127.0.0.1:7891", false)]
        [InlineData("not a url", false)]
        [InlineData(null, false)]
        public void Env_proxy_is_accepted_only_when_it_is_usable(string value, bool expected)
            => Assert.Equal(expected, UpdateService.TryProxy(value, out var proxy) && proxy != null);

        [Fact]
        public void Banner_bindings_resolve_against_the_view_model()
        {
            var t = typeof(HomeViewModel);
            Assert.NotNull(t.GetProperty("HasUpdate"));
            Assert.NotNull(t.GetProperty("UpdateMessage"));
            Assert.NotNull(t.GetProperty("OpenUpdatePageCommand"));
            Assert.NotNull(t.GetProperty("DismissUpdateCommand"));
            // 一键就地更新按钮：命令 + 可见性 + 进度文案 + 忙碌态
            Assert.NotNull(t.GetProperty("ApplyUpdateCommand"));
            Assert.NotNull(t.GetProperty("CanAutoUpdate"));
            Assert.NotNull(t.GetProperty("UpdateStatus"));
            Assert.NotNull(t.GetProperty("IsUpdating"));
        }

        [Fact]
        public void Last_check_time_survives_a_reload()
        {
            string dir = Path.Combine(Path.GetTempPath(), "UZipUpdate_" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = new SettingsService(dir, null);
                Assert.True(settings.Current.CheckUpdateOnStartup);   // 默认开启

                var stamp = new DateTime(2026, 9, 30, 12, 34, 56);
                settings.Save(s =>
                {
                    s.CheckUpdateOnStartup = false;
                    s.LastUpdateCheck = stamp;
                    s.LatestSeenVersion = "3.0.1";
                });

                var reloaded = new SettingsService(dir, null).Current;
                Assert.False(reloaded.CheckUpdateOnStartup);
                Assert.Equal(stamp, reloaded.LastUpdateCheck);
                Assert.Equal("3.0.1", reloaded.LatestSeenVersion);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ---------- 自更新直链：从 assets 里挑 UZIP2.exe ----------

        [Fact]
        public void ParseRelease_picks_the_framework_dependent_asset()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.3.0\",\"assets\":["
                + "{\"name\":\"UZIP-3.3.0-win-x64-selfcontained.zip\",\"browser_download_url\":\"https://gh/sc.zip\",\"size\":68000000},"
                + "{\"name\":\"UZIP2.exe\",\"browser_download_url\":\"https://gh/UZIP2.exe\",\"size\":8400000}"
                + "]}");
            Assert.Equal("https://gh/UZIP2.exe", info.DownloadUrl);
            Assert.Equal(8400000, info.Size);
        }

        [Fact]
        public void Asset_matching_is_case_insensitive()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.3.0\",\"assets\":[{\"name\":\"uzip2.EXE\",\"browser_download_url\":\"https://gh/a.exe\",\"size\":1}]}");
            Assert.Equal("https://gh/a.exe", info.DownloadUrl);
        }

        [Fact]
        public void No_matching_asset_leaves_download_url_null()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.3.0\",\"assets\":[{\"name\":\"UZIP-3.3.0-win-x64-selfcontained.zip\",\"browser_download_url\":\"https://gh/sc.zip\",\"size\":68000000}]}");
            Assert.Null(info.DownloadUrl);
            Assert.Equal(0, info.Size);
        }

        [Fact]
        public void Missing_assets_array_leaves_download_url_null()
        {
            var info = UpdateService.ParseRelease("{\"tag_name\":\"v3.3.0\"}");
            Assert.Null(info.DownloadUrl);
        }

        [Fact]
        public void Non_numeric_size_is_ignored()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.3.0\",\"assets\":[{\"name\":\"UZIP2.exe\",\"browser_download_url\":\"https://gh/a.exe\"}]}");
            Assert.Equal("https://gh/a.exe", info.DownloadUrl);
            Assert.Equal(0, info.Size);
        }

        // ---------- 发布校验值：同名 .sha256 侧车 ----------

        [Fact]
        public void ParseRelease_picks_the_sha256_sidecar()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.6.0\",\"assets\":["
                + "{\"name\":\"UZIP2.exe\",\"browser_download_url\":\"https://gh/UZIP2.exe\",\"size\":8400000},"
                + "{\"name\":\"UZIP2.exe.sha256\",\"browser_download_url\":\"https://gh/UZIP2.exe.sha256\",\"size\":65}"
                + "]}");
            Assert.Equal("https://gh/UZIP2.exe", info.DownloadUrl);
            Assert.Equal("https://gh/UZIP2.exe.sha256", info.Sha256Url);
        }

        [Fact]
        public void Release_without_a_sidecar_has_no_checksum_url()
        {
            var info = UpdateService.ParseRelease(
                "{\"tag_name\":\"v3.5.0\",\"assets\":[{\"name\":\"UZIP2.exe\",\"browser_download_url\":\"https://gh/a.exe\",\"size\":1}]}");
            Assert.Equal("https://gh/a.exe", info.DownloadUrl);
            Assert.Null(info.Sha256Url);   // 老版本没发布校验值：更新照做，只是跳过这一步
        }
    }
}
