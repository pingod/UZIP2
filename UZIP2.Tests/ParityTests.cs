using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 行为对等回归（plan Task 15 清单 ③⑤⑥⑦）：全部走真实 7z.exe 端到端。
    public class ParityTests : IDisposable
    {
        private readonly string _root;
        private readonly string _src;
        private readonly string _out;
        private readonly string _config;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _client;
        private readonly ArchiveWorker _worker;

        public ParityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipParity_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            _out = Path.Combine(_root, "out");
            _config = Path.Combine(_root, "Config");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_out);

            _settings = new SettingsService(_config, null);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = _out;
            _passwords = new PasswordService(_config, _settings);
            _client = new SevenZipClient(_settings);
            Assert.NotNull(_client.SevenZipPath);
            _worker = new ArchiveWorker(_client, _passwords, _settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string MakeFile(string dir, string name, int kb = 4)
        {
            Directory.CreateDirectory(dir);
            var buf = new byte[kb * 1024];
            // 不可压缩内容：分卷测试需要真实体积
            new Random(20260930).NextBytes(buf);
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        Task<SevenZipResult> Compress(string archive, string password, int type, params string[] sources)
            => _client.CompressAsync(sources, archive, password, type, 3, false, null, CancellationToken.None);

        // 直接调 7z 以使用 worker 未暴露的开关（-v 分卷）
        void Run7z(params string[] args)
        {
            var psi = new ProcessStartInfo(_client.SevenZipPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                Assert.True(p.ExitCode == 0, "7z 失败: " + string.Join(" ", args));
            }
        }

        // ③ 全部密码试错 → 诊断文案
        [Fact]
        public async Task WrongPassword_all_sources_tried_then_diagnosis()
        {
            var f = MakeFile(_src, "secret.bin");
            var zip = Path.Combine(_root, "locked.zip");
            Assert.True((await Compress(zip, "right-pw-913", 0, f)).Success);

            _passwords.AddBook("book-a", "wrong-a");
            _passwords.AddBook("book-b", "wrong-b");
            _passwords.PasteToPaper("wrong-c");
            _settings.Current.NameToPassword = true;
            _settings.Current.NameFilter = "-";

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Equal("需要密码，但密码本/密码纸中未找到正确密码", job.Diagnosis);
            // 试错不应消耗密码纸
            Assert.Single(_passwords.Paper);
        }

        // ③b 密码纸命中 → 消耗之
        [Fact]
        public async Task Paper_password_hit_consumes_entry()
        {
            var f = MakeFile(_src, "secret2.bin");
            var zip = Path.Combine(_root, "paper.zip");
            Assert.True((await Compress(zip, "paper-pw-77", 0, f)).Success);
            _passwords.PasteToPaper("paper-pw-77");

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("paper-pw-77", job.UsedPassword);
            Assert.Empty(_passwords.Paper);
        }

        // ④ 多级解压嵌套
        [Fact]
        public async Task Nested_archives_extracted_recursively()
        {
            var leaf = MakeFile(_src, "leaf.bin");
            var inner = Path.Combine(_root, "inner.zip");
            Assert.True((await Compress(inner, null, 0, leaf)).Success);
            var outer = Path.Combine(_root, "outer.zip");
            Assert.True((await Compress(outer, null, 0, inner)).Success);

            _worker.EnqueueExtract(new[] { outer });
            await _worker.WhenIdleAsync();
            await _worker.WhenIdleAsync();

            Assert.Equal(2, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            Assert.True(File.Exists(Path.Combine(_out, "leaf.bin")), "内层内容应被二次解压出来");
        }

        // ⑤ 分卷：主卷解压 + 副卷去重
        [Fact]
        public async Task Split_volumes_extract_once_from_main()
        {
            var big = MakeFile(_src, "big.bin", kb: 400);
            var baseArc = Path.Combine(_root, "vol.zip");
            Run7z("a", "-tzip", "-v128k", baseArc, big);
            var parts = Directory.GetFiles(_root, "vol.zip.*").OrderBy(x => x).ToArray();
            Assert.True(parts.Length >= 2, "应产生多个分卷");

            _worker.EnqueueExtract(parts);
            await _worker.WhenIdleAsync();
            await _worker.WhenIdleAsync();

            Assert.Equal(parts.Length, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            Assert.Contains(_worker.Jobs, j => j.Diagnosis == "已随分卷主文件处理");
            Assert.True(File.Exists(Path.Combine(_out, "big.bin")));
        }

        // ⑥ 随机密码 + 密码写入文件名
        [Fact]
        public async Task Random_password_written_into_archive_name()
        {
            var f = MakeFile(_src, "payload.bin");
            _settings.Current.PasswordMode = 6;          // 随机 8 位
            _settings.Current.PasswordToName = true;
            _settings.Current.CompressType = 0;
            _settings.Current.CompressOutMode = 1;

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal(8, job.UsedPassword.Length);
            var name = Path.GetFileName(job.OutputDir);
            Assert.EndsWith(" " + job.UsedPassword + ".zip", name);

            // 文件名里的密码确实能解开该包
            Assert.True((await _client.TestAsync(job.OutputDir, job.UsedPassword, CancellationToken.None)).Success);
        }

        // ⑦ 解压过滤命中项被删；HideZipContent 头加密后无名可读
        [Fact]
        public async Task Extract_filter_removes_ad_files_and_header_encryption_hides_names()
        {
            var keep = MakeFile(_src, "keep.bin");
            var ad = MakeFile(_src, "广告.txt");
            var zip = Path.Combine(_root, "mixed.zip");
            Assert.True((await Compress(zip, null, 0, keep, ad)).Success);

            _settings.Current.ExtractFilter = "广告.txt";
            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, Assert.Single(_worker.Jobs).Status);
            Assert.True(File.Exists(Path.Combine(_out, "keep.bin")));
            Assert.False(File.Exists(Path.Combine(_out, "广告.txt")), "过滤规则应删除命中文件");

            // 头加密：7z + HideZipContent 后，无密码列目录看不到内部文件名
            var secret = MakeFile(_src, "inner-name.dat");
            var enc = Path.Combine(_root, "hidden.7z");
            var r = await _client.CompressAsync(new[] { secret }, enc, "hpw-123", 1, 3, true, null, CancellationToken.None);
            Assert.True(r.Success);
            var listing = _client.ListContent(enc, null);
            Assert.DoesNotContain("inner-name.dat", listing);
        }

        // ⑧ 压缩日志落盘（含密码，供事后找回）
        [Fact]
        public async Task Compress_log_records_archive_and_password()
        {
            var f = MakeFile(_src, "logme.bin");
            _settings.Current.PasswordMode = 2;
            _settings.Current.CustomPasswords = new System.Collections.Generic.List<string> { "logged-pw", "", "" };
            _settings.Current.CompressOutMode = 1;

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);

            var log = Path.Combine(_config, "Compress.log");
            Assert.True(File.Exists(log));
            string content = File.ReadAllText(log);
            Assert.Contains(Path.GetFileName(job.OutputDir), content);
            Assert.Contains("logged-pw", content);
        }

        // ⑨ 调试模式：命令行写日志且密码脱敏
        [Fact]
        public async Task DebugMode_logs_redacted_command_line()
        {
            var logger = new FileLogger(_root);
            var client = new SevenZipClient(_settings, logger);
            _settings.Current.DebugMode = true;

            var f = MakeFile(_src, "dbg.bin");
            var zip = Path.Combine(_root, "dbg.zip");
            Assert.True((await client.CompressAsync(new[] { f }, zip, "top-secret", 0, 3, false, null,
                CancellationToken.None)).Success);

            string content = File.ReadAllText(logger.LatestLogPath);
            Assert.Contains("[7z]", content);
            Assert.DoesNotContain("top-secret", content);
            Assert.Contains("-p***", content);
        }
    }
}
