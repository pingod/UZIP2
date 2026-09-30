using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UZIP2.Cli;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // headless 跑 CliRunner：临时 baseDir/configDir + 真实 7z，输出用 sink 捕获。
    public class CliRunnerTests : IAsyncLifetime, IDisposable
    {
        string _root = "";
        string _base = "";
        string _config = "";
        string _src = "";

        public Task InitializeAsync()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipCliTests_" + Guid.NewGuid().ToString("N"));
            _base = Path.Combine(_root, "app");
            _config = Path.Combine(_base, "Config");
            _src = Path.Combine(_root, "src");
            Directory.CreateDirectory(_config);
            Directory.CreateDirectory(_src);
            Assert.NotNull(new SevenZipClient(new SettingsService(_config, null)).SevenZipPath);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        string MakeFile(string name, int kb = 3, byte fill = 66)
        {
            var buf = new byte[kb * 1024];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(fill ^ (i & 0x1F));
            var p = Path.Combine(_src, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        static async Task<(int code, List<string> o, List<string> e)> Run(string baseDir, string configDir, params string[] args)
        {
            var o = new List<string>(); var e = new List<string>();
            var runner = new CliRunner(baseDir, configDir, s => o.Add(s), s => e.Add(s));
            var code = await runner.RunAsync(CliParser.Parse(args));
            return (code, o, e);
        }

        // ---------- 基础 ----------

        [Fact]
        public async Task Version_prints_uzip_three()
        {
            var (code, o, _) = await Run(_base, _config, "version");
            Assert.Equal(0, code);
            Assert.Contains("UZIP 3", o.Last());
        }

        [Fact]
        public async Task Help_prints_usage()
        {
            var (code, o, _) = await Run(_base, _config, "help");
            Assert.Equal(0, code);
            Assert.Contains(o, s => s.Contains("UZIP 命令行"));
        }

        [Fact]
        public async Task List_without_file_is_usage_error()
        {
            var (code, _, _) = await Run(_base, _config, "list");
            Assert.Equal(2, code);
        }

        // ---------- compress / list / extract 往返 ----------

        [Fact]
        public async Task Compress_then_list_then_extract_roundtrip_zip()
        {
            var f = MakeFile("data.bin");
            var outDir = Path.Combine(_root, "out");
            var (cc, co, ce) = await Run(_base, _config, "compress", f, "-o", outDir);
            Assert.True(cc == 0, string.Join("|", ce));
            var archive = co.Last();
            Assert.True(File.Exists(archive), "应产出压缩包: " + archive);

            var (lc, lo, _) = await Run(_base, _config, "list", archive);
            Assert.Equal(0, lc);
            Assert.Contains(lo, s => s.Contains("data.bin"));

            var dest = Path.Combine(_root, "de");
            var (ec, _, ee) = await Run(_base, _config, "extract", archive, "-o", dest);
            Assert.True(ec == 0, string.Join("|", ee));
            Assert.True(File.Exists(Path.Combine(dest, "data.bin")));
        }

        [Fact]
        public async Task Extract_here_lands_next_to_archive()
        {
            var f = MakeFile("h.bin");
            var outDir = Path.Combine(_root, "out2");
            var (_, co, _) = await Run(_base, _config, "compress", f, "-o", outDir, "--type", "7z");
            var archive = co.Last();

            var (ec, _, ee) = await Run(_base, _config, "extract", archive, "--here");
            Assert.True(ec == 0, string.Join("|", ee));
            Assert.True(File.Exists(Path.Combine(outDir, "h.bin")), "应解到包所在目录");
        }

        [Fact]
        public async Task Encrypted_extract_needs_password_or_auto()
        {
            var f = MakeFile("secret.bin");
            var outDir = Path.Combine(_root, "out3");
            var (cc, co, ce) = await Run(_base, _config, "compress", f, "-o", outDir, "--type", "7z", "--password", "s3cr3t");
            Assert.True(cc == 0, string.Join("|", ce));
            var archive = co.Last();

            // 无密码无 auto → 失败
            var dest1 = Path.Combine(_root, "d1");
            var (ec1, _, _) = await Run(_base, _config, "extract", archive, "-o", dest1);
            Assert.Equal(1, ec1);

            // 显式密码 → 成功
            var dest2 = Path.Combine(_root, "d2");
            var (ec2, _, ee2) = await Run(_base, _config, "extract", archive, "-o", dest2, "--password", "s3cr3t");
            Assert.True(ec2 == 0, string.Join("|", ee2));
            Assert.True(File.Exists(Path.Combine(dest2, "secret.bin")));

            // --auto 命中密码本 → 成功
            await Run(_base, _config, "vault", "add", "常用", "s3cr3t");
            var dest3 = Path.Combine(_root, "d3");
            var (ec3, _, ee3) = await Run(_base, _config, "extract", archive, "-o", dest3, "--auto");
            Assert.True(ec3 == 0, string.Join("|", ee3));
            Assert.True(File.Exists(Path.Combine(dest3, "secret.bin")));
        }

        [Fact]
        public async Task Extract_selected_entries_only()
        {
            var a = MakeFile("a.txt"); var b = MakeFile("b.txt");
            var outDir = Path.Combine(_root, "out4");
            var (_, co, _) = await Run(_base, _config, "compress", a, b, "-o", outDir, "--name", "two.zip");
            var archive = co.Last();

            var dest = Path.Combine(_root, "d4");
            var (ec, _, ee) = await Run(_base, _config, "extract", archive, "-o", dest, "--entries", "a.txt");
            Assert.True(ec == 0, string.Join("|", ee));
            Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "b.txt")), "未勾选的不应解出");
        }

        // ---------- checksum ----------

        [Fact]
        public async Task Checksum_write_then_verify_passes_and_detects_tamper()
        {
            var f = MakeFile("dl.bin");
            var (wc, _, we) = await Run(_base, _config, "checksum", f, "--write", "sha256");
            Assert.True(wc == 0, string.Join("|", we));
            Assert.True(File.Exists(f + ".sha256"));

            var (vc, vo, _) = await Run(_base, _config, "checksum", f, "--verify");
            Assert.Equal(0, vc);
            Assert.Contains(vo, s => s.Contains("OK"));

            File.AppendAllText(f, "tamper");
            var (vc2, _, _) = await Run(_base, _config, "checksum", f, "--verify");
            Assert.Equal(1, vc2);
        }

        [Fact]
        public async Task Checksum_plain_prints_sha256_hex()
        {
            var f = MakeFile("hex.bin");
            var (code, o, _) = await Run(_base, _config, "checksum", f);
            Assert.Equal(0, code);
            Assert.Matches(@"[0-9a-f]{64}  hex\.bin", o.Last());
        }

        // ---------- vault ----------

        [Fact]
        public async Task Vault_add_list_remove()
        {
            await Run(_base, _config, "vault", "add", "wifi", "hunter2");
            var (lc, lo, _) = await Run(_base, _config, "vault", "list", "--show-passwords");
            Assert.Equal(0, lc);
            Assert.Contains(lo, s => s.Contains("wifi") && s.Contains("hunter2"));

            // 默认不显示明文
            var (_, lo2, _) = await Run(_base, _config, "vault", "list");
            Assert.DoesNotContain(lo2, s => s.Contains("hunter2"));

            await Run(_base, _config, "vault", "remove", "wifi");
            var (_, lo3, _) = await Run(_base, _config, "vault", "list");
            Assert.DoesNotContain(lo3, s => s.Contains("wifi"));
        }

        [Fact]
        public async Task Vault_export_import_roundtrip_no_plaintext_in_file()
        {
            await Run(_base, _config, "vault", "add", "bank", "P@ssw0rd-12345");
            var exp = Path.Combine(_root, "vault.json");
            var (ec, _, ee) = await Run(_base, _config, "vault", "export", exp, "--passphrase", "verylongpassphrase");
            Assert.True(ec == 0, string.Join("|", ee));

            var text = File.ReadAllText(exp);
            Assert.DoesNotContain("P@ssw0rd-12345", text);
            Assert.DoesNotContain("verylongpassphrase", text);

            // 导入到全新的空库
            var config2 = Path.Combine(_root, "app2", "Config");
            Directory.CreateDirectory(config2);
            var (ic, _, ie) = await Run(Path.Combine(_root, "app2"), config2, "vault", "import", exp, "--passphrase", "verylongpassphrase");
            Assert.True(ic == 0, string.Join("|", ie));
            var (_, lo, _) = await Run(Path.Combine(_root, "app2"), config2, "vault", "list", "--show-passwords");
            Assert.Contains(lo, s => s.Contains("bank") && s.Contains("P@ssw0rd-12345"));
        }

        // ---------- config ----------

        [Fact]
        public async Task Config_set_then_get()
        {
            var (sc, _, _) = await Run(_base, _config, "config", "set", "theme", "Dark");
            Assert.Equal(0, sc);
            var (_, go, _) = await Run(_base, _config, "config", "get", "theme");
            Assert.Equal("Dark", go.Last());

            await Run(_base, _config, "config", "set", "parallelExtract", "5");
            var (_, gp, _) = await Run(_base, _config, "config", "get", "parallelExtract");
            Assert.Equal("5", gp.Last());
        }

        [Fact]
        public async Task Config_unknown_key_is_error()
        {
            var (code, _, _) = await Run(_base, _config, "config", "get", "notARealKey");
            Assert.Equal(1, code);
        }

        // ---------- log ----------

        [Fact]
        public async Task Compress_log_masks_password_by_default()
        {
            var f = MakeFile("lg.bin");
            var outDir = Path.Combine(_root, "out5");
            await Run(_base, _config, "compress", f, "-o", outDir, "--type", "7z", "--password", "topsecret");

            var (_, masked, _) = await Run(_base, _config, "log", "compress");
            Assert.Contains(masked, s => s.Contains("***"));
            Assert.DoesNotContain(masked, s => s.Contains("topsecret"));

            var (_, shown, _) = await Run(_base, _config, "log", "compress", "--show-passwords");
            Assert.Contains(shown, s => s.Contains("topsecret"));
        }

        // ---------- shell ----------

        [Fact]
        public async Task Shell_status_reports_state_without_touching_registry()
        {
            var (code, o, _) = await Run(_base, _config, "shell", "status");
            Assert.NotEqual(2, code);              // 只会是 0/1，绝不会写注册表
            Assert.Contains(o, s => s.Contains("状态"));
        }

        // ---------- update（注入 seam，离线可控）----------

        string AppLikeExe()
        {
            var p = Path.Combine(_root, "UZIP2.exe");
            File.WriteAllBytes(p, new byte[100 * 1024]);   // 100KB < 40MB 阈值
            return p;
        }

        async Task<(int code, List<string> o, List<string> e)> RunUpdate(
            UpdateInfo info, string exePath,
            Func<UpdateInfo, string, IProgress<long>, CancellationToken, Task<(bool Ok, string Error)>> stage,
            params string[] args)
        {
            var o = new List<string>(); var e = new List<string>();
            var runner = new CliRunner(_base, _config, s => o.Add(s), s => e.Add(s))
            {
                UpdateCheck = _ => Task.FromResult(info),
                ExePathProvider = () => exePath,
                StageApply = stage,
            };
            var code = await runner.RunAsync(CliParser.Parse(args));
            return (code, o, e);
        }

        static UpdateInfo New(string ver, string url = "https://gh/x", string dl = "https://gh/UZIP2.exe", long size = 8_400_000)
            => new UpdateInfo { Version = ver, Url = url, DownloadUrl = dl, Size = size };

        [Fact]
        public async Task Update_check_reports_available_update_and_hints_apply()
        {
            var (code, o, _) = await RunUpdate(New("99.0.0"), AppLikeExe(), null, "update");
            Assert.Equal(0, code);
            Assert.Contains(o, s => s.Contains("99.0.0"));
            Assert.Contains(o, s => s.Contains("update --apply"));
        }

        [Fact]
        public async Task Update_check_says_latest_when_not_newer()
        {
            var (code, o, _) = await RunUpdate(New("0.0.1"), AppLikeExe(), null, "update");
            Assert.Equal(0, code);
            Assert.Contains(o, s => s.Contains("已是最新"));
        }

        [Fact]
        public async Task Update_json_includes_download_url_and_can_apply()
        {
            var (code, o, _) = await RunUpdate(New("99.0.0"), AppLikeExe(), null, "update", "--json");
            Assert.Equal(0, code);
            var json = string.Join("\n", o);
            Assert.Contains("\"updateAvailable\": true", json);
            Assert.Contains("\"canApply\": true", json);
            Assert.Contains("https://gh/UZIP2.exe", json);
        }

        [Fact]
        public async Task Update_apply_skips_when_already_latest()
        {
            bool called = false;
            var (code, o, _) = await RunUpdate(New("0.0.1"), AppLikeExe(),
                (i, e2, p, c) => { called = true; return Task.FromResult((true, "")); }, "update", "--apply");
            Assert.Equal(0, code);
            Assert.False(called);
            Assert.Contains(o, s => s.Contains("无需更新"));
        }

        [Fact]
        public async Task Update_apply_errors_when_no_direct_asset()
        {
            var (code, _, e) = await RunUpdate(New("99.0.0", dl: null), AppLikeExe(), null, "update", "--apply");
            Assert.Equal(1, code);
            Assert.Contains(e, s => s.Contains("没有框架依赖直链"));
        }

        [Fact]
        public async Task Update_apply_errors_when_in_place_not_possible()
        {
            // exe 指向不存在的路径 -> CanApplyInPlace=false
            var (code, _, e) = await RunUpdate(New("99.0.0"), Path.Combine(_root, "missing.exe"),
                null, "update", "--apply");
            Assert.Equal(1, code);
            Assert.Contains(e, s => s.Contains("不支持就地更新"));
        }

        [Fact]
        public async Task Update_apply_stages_and_reports_ready()
        {
            bool called = false;
            var (code, o, _) = await RunUpdate(New("99.0.0"), AppLikeExe(),
                (i, e2, p, c) => { called = true; return Task.FromResult((true, "")); }, "update", "--apply");
            Assert.Equal(0, code);
            Assert.True(called);
            Assert.Contains(o, s => s.Contains("更新已就绪"));
        }

        [Fact]
        public async Task Update_apply_surfaces_stage_failure()
        {
            var (code, _, e) = await RunUpdate(New("99.0.0"), AppLikeExe(),
                (i, e2, p, c) => Task.FromResult((false, "校验失败: 版本不一致")), "update", "--apply");
            Assert.Equal(1, code);
            Assert.Contains(e, s => s.Contains("版本不一致"));
        }
    }
}
