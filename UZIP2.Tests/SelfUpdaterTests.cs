using System;
using System.Diagnostics;
using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class SelfUpdaterTests
    {
        static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "UZipSelfUpd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------- CanApplyInPlace ----------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void CanApply_rejects_blank(string path)
            => Assert.False(SelfUpdater.CanApplyInPlace(path));

        [Fact]
        public void CanApply_rejects_missing_file()
            => Assert.False(SelfUpdater.CanApplyInPlace(
                Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N") + ".exe")));

        [Fact]
        public void CanApply_accepts_a_small_real_exe()
        {
            string dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "UZIP2.exe");
                File.WriteAllBytes(f, new byte[100 * 1024]);
                Assert.True(SelfUpdater.CanApplyInPlace(f));
            }
            finally { TryRmDir(dir); }
        }

        [Fact]
        public void CanApply_rejects_self_contained_size()
        {
            string dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "UZIP2.exe");
                using (var fs = new FileStream(f, FileMode.Create))
                    fs.SetLength(SelfUpdater.SelfContainedThresholdBytes + 1);
                Assert.False(SelfUpdater.CanApplyInPlace(f));
            }
            finally { TryRmDir(dir); }
        }

        [Fact]
        public void CanApply_rejects_single_file_cache_dir()
        {
            // 模拟 %TEMP%\.net\UZIP2\<hash>\ 的单文件解压缓存路径
            string dir = Path.Combine(Path.GetTempPath(), ".net", "UZIP2", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "UZIP2.exe");
                File.WriteAllBytes(f, new byte[100 * 1024]);
                Assert.False(SelfUpdater.CanApplyInPlace(f));
            }
            finally { TryRmDir(dir); }
        }

        // ---------- Verify ----------

        [Fact]
        public void Verify_rejects_missing()
        {
            var r = SelfUpdater.Verify(Path.Combine(Path.GetTempPath(), "gone_" + Guid.NewGuid().ToString("N") + ".exe"), "1.0.0");
            Assert.False(r.Ok);
            Assert.Contains("不存在", r.Error);
        }

        [Fact]
        public void Verify_rejects_too_small()
        {
            string dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "tiny.exe");
                File.WriteAllBytes(f, new byte[1024]);
                Assert.False(SelfUpdater.Verify(f, "1.0.0").Ok);
            }
            finally { TryRmDir(dir); }
        }

        [Fact]
        public void Verify_rejects_non_pe()
        {
            string dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "fake.exe");
                var bytes = new byte[128 * 1024];   // 全 0，够大但不是 MZ
                File.WriteAllBytes(f, bytes);
                var r = SelfUpdater.Verify(f, "1.0.0");
                Assert.False(r.Ok);
                Assert.Contains("不是可执行程序", r.Error);
            }
            finally { TryRmDir(dir); }
        }

        [Fact]
        public void Verify_rejects_MZ_but_not_a_real_pe()
        {
            string dir = TempDir();
            try
            {
                var f = Path.Combine(dir, "mzonly.exe");
                var bytes = new byte[128 * 1024];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                File.WriteAllBytes(f, bytes);
                // 有 MZ 头，但读不到版本信息 → 拒绝
                Assert.False(SelfUpdater.Verify(f, "1.0.0").Ok);
            }
            finally { TryRmDir(dir); }
        }

        static string RealPe() => typeof(SelfUpdaterTests).Assembly.Location;

        [Fact]
        public void Verify_accepts_a_real_assembly_with_blank_expected()
        {
            var r = SelfUpdater.Verify(RealPe(), "");
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void Verify_accepts_the_real_assembly_own_version()
        {
            var pv = FileVersionInfo.GetVersionInfo(RealPe());
            var v = pv.FileVersion ?? pv.ProductVersion;
            if (string.IsNullOrEmpty(v)) return;   // 环境没有版本资源则跳过
            var r = SelfUpdater.Verify(RealPe(), v);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void Verify_rejects_version_mismatch()
        {
            var r = SelfUpdater.Verify(RealPe(), "0.0.1");
            Assert.False(r.Ok);
            Assert.Contains("不一致", r.Error);
        }

        // ---------- BuildRelayScript（纯字符串）----------

        [Fact]
        public void Relay_waits_on_the_giving_process()
        {
            var s = SelfUpdater.BuildRelayScript(4321, @"C:\app\UZIP2.exe", @"C:\app\UZIP2.exe.update.new", "");
            Assert.Contains("PID eq 4321", s);
            Assert.Contains("goto wait", s);
        }

        [Fact]
        public void Relay_copies_new_over_exe_then_launches()
        {
            var s = SelfUpdater.BuildRelayScript(1, @"C:\a\b\UZIP2.exe", @"C:\a\b\UZIP2.exe.update.new", "");
            Assert.Contains("copy /y \"C:\\a\\b\\UZIP2.exe.update.new\" \"C:\\a\\b\\UZIP2.exe\"", s);
            Assert.Contains("start \"\" \"C:\\a\\b\\UZIP2.exe\"", s);
        }

        [Fact]
        public void Relay_deletes_itself()
            => Assert.Contains("del /f /q \"%~f0\"", SelfUpdater.BuildRelayScript(1, "a", "b", ""));

        [Fact]
        public void Relay_quotes_paths_with_spaces()
        {
            var s = SelfUpdater.BuildRelayScript(7, @"D:\Sync Public\UZip\UZIP2.exe", @"D:\Sync Public\UZip\UZIP2.exe.update.new", "");
            Assert.Contains("\"D:\\Sync Public\\UZip\\UZIP2.exe\"", s);
        }

        [Fact]
        public void Relay_passes_launch_args_through()
            => Assert.Contains("--show-updated", SelfUpdater.BuildRelayScript(1, "a", "b", "--show-updated"));

        // ---------- 发布校验值（.sha256 侧车）----------

        // 64 位十六进制：sha256 摘要的长度就是这道门槛，短一格说明读错了字段
        const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string Other = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        [Fact]
        public void ExtractSha256_reads_a_coreutils_sidecar_line()
            => Assert.Equal(Hex, SelfUpdater.ExtractSha256(Hex + "  UZIP2.exe\r\n"));

        [Fact]
        public void ExtractSha256_lowercases_a_labelled_hash()
            => Assert.Equal(Hex, SelfUpdater.ExtractSha256("SHA256(UZIP2.exe)= " + Hex.ToUpperInvariant()));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("没有哈希")]
        [InlineData("0123456789abcdef")]              // 太短：像是 md5 或被截断的一行
        public void ExtractSha256_returns_null_without_a_full_digest(string text)
            => Assert.Null(SelfUpdater.ExtractSha256(text));

        [Fact]
        public void VerifySha256_accepts_the_published_digest()
        {
            var file = Path.Combine(TempDir(), "payload.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            var expected = System.BitConverter.ToString(
                System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).Replace("-", "").ToLowerInvariant();

            var r = SelfUpdater.VerifySha256(file, expected);
            Assert.True(r.Ok);
            Assert.Null(r.Error);
        }

        [Fact]
        public void VerifySha256_rejects_a_tampered_payload()
        {
            var file = Path.Combine(TempDir(), "payload.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

            var r = SelfUpdater.VerifySha256(file, Other);
            Assert.False(r.Ok);
            Assert.Contains("校验", r.Error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("   ")]
        public void VerifySha256_skips_a_release_without_a_published_digest(string expected)
            => Assert.True(SelfUpdater.VerifySha256(Path.Combine(TempDir(), "whatever"), expected).Ok);

        static void TryRmDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ---------- 受控端到端换体（用真实框架依赖单文件；无产物则跳过）----------

        static string FindFdExe()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UZIP2.sln")))
                dir = dir.Parent;
            if (dir == null) return null;
            var exe = Path.Combine(dir.FullName, "_rel", "fd", "UZIP2.exe");
            return File.Exists(exe) ? exe : null;
        }

        [Fact]
        public async Task Relay_end_to_end_swaps_real_exe_and_cleans_up()
        {
            string fd = FindFdExe();
            if (fd == null) return;                       // 没有发布产物（如 CI）：跳过，逻辑由其它用例覆盖

            byte[] clean = File.ReadAllBytes(fd);         // 干净可运行版，作为"新"体
            string dir = TempDir();
            string target = Path.Combine(dir, "UZIP2.exe");
            string newFile = target + ".update.new";
            try
            {
                // "旧"体 = 干净版 + 尾部标记字节（内容不同、仍是可运行 PE）
                var dirty = new byte[clean.Length + 5];
                Array.Copy(clean, dirty, clean.Length);
                dirty[clean.Length] = (byte)'X';
                File.WriteAllBytes(target, dirty);
                File.WriteAllBytes(newFile, clean);

                int deadPid = 999_999_999;                // 不存在的 PID → 中继脚本跳过等待直接换体
                string script = SelfUpdater.BuildRelayScript(deadPid, target, newFile, "--version");
                string scriptPath = SelfUpdater.RelayScriptPath();
                File.WriteAllText(scriptPath, script, System.Text.Encoding.ASCII);

                SelfUpdater.StartDetached(scriptPath);

                // 轮询：target 长度回到干净版、.new 与脚本都被删除，即换体 + 清理成功
                var deadline = DateTime.UtcNow.AddSeconds(30);
                bool swapped = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (new FileInfo(target).Length == clean.Length
                        && !File.Exists(newFile) && !File.Exists(scriptPath))
                    { swapped = true; break; }
                    await Task.Delay(250);
                }

                Assert.True(swapped, "中继脚本未在限定时间内完成换体/清理");
                Assert.Equal(clean.Length, new FileInfo(target).Length);   // 尾部标记已被覆盖掉
                Assert.False(File.Exists(newFile));
                Assert.False(File.Exists(scriptPath));
            }
            finally
            {
                // 被 start 拉起的 UZIP2.exe --version 会短暂占用 target；等它退出再删
                var end = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < end)
                {
                    try { Directory.Delete(dir, true); break; }
                    catch { System.Threading.Thread.Sleep(300); }
                }
            }
        }
    }
}
