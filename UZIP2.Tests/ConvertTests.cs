using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 格式名 ↔ 7z 开关 ↔ 扩展名：这是 compress/convert 共用的唯一真值表
    public class ArchiveFormatTests
    {
        [Theory]
        [InlineData("zip", 0)]
        [InlineData("7z", 1)]
        [InlineData("bz2", 2)]
        [InlineData("bzip2", 2)]
        [InlineData("gz", 3)]
        [InlineData("gzip", 3)]
        [InlineData("tar", 4)]
        [InlineData("wim", 5)]
        [InlineData("xz", 6)]
        // ISO 与拼错的格式都不给下标：ISO 只能读，rar 本程序不创建
        [InlineData("iso", -1)]
        [InlineData("img", -1)]
        [InlineData("rar", -1)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        public void Index_only_maps_creatable_formats(string name, int expected)
            => Assert.Equal(expected, ArchiveFormat.Index(name));

        // -t 开关的拼写是 7z 定的: -tbzip2 / -tgz 实测直接报错，只有 -tbz2→"bzip2" 可用；
        // 文件名后缀却要用 .bz2 / .gz。两张表混用就会产出打不开的文件名或报错的命令。
        [Theory]
        [InlineData(0, "zip", ".zip")]
        [InlineData(1, "7z", ".7z")]
        [InlineData(2, "bzip2", ".bz2")]
        [InlineData(3, "gzip", ".gz")]
        [InlineData(4, "tar", ".tar")]
        [InlineData(5, "wim", ".wim")]
        [InlineData(6, "xz", ".xz")]
        public void Switch_name_and_extension_are_separate_tables(int type, string name, string ext)
        {
            Assert.Equal(name, ArchiveFormat.TypeName(type));
            Assert.Equal(ext, ArchiveFormat.Extension(type));
        }

        // bz2/gz/xz 是单流格式，一个包只装得下一个文件；多条目转换必须提前拦住
        [Theory]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(6, true)]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(4, false)]
        [InlineData(5, false)]
        public void Single_stream_formats_are_named(int type, bool expected)
            => Assert.Equal(expected, ArchiveFormat.IsSingleStream(type));

        [Fact]
        public void Read_only_formats_get_told_instead_of_a_confusing_7z_error()
        {
            Assert.Null(ArchiveFormat.CreateBlockMessage("7z"));
            Assert.Contains("ISO", ArchiveFormat.CreateBlockMessage("iso"));
            Assert.Contains("只能读取", ArchiveFormat.CreateBlockMessage("img"));
            Assert.Contains("未知", ArchiveFormat.CreateBlockMessage("rar"));
            Assert.Contains("未知", ArchiveFormat.CreateBlockMessage(""));
        }

        [Fact]
        public void Target_path_reuses_the_stem_and_drops_the_old_extension()
        {
            Assert.Equal(@"D:\o\a.7z", ArchiveFormat.TargetPath(@"D:\src\a.zip", @"D:\o\", 1));
            // 复合后缀要一路剥干净，别留下 a.tar.7z
            Assert.Equal(@"D:\o\a.7z", ArchiveFormat.TargetPath(@"D:\src\a.tar.gz", @"D:\o", 1));
        }

        [Fact]
        public void Target_path_avoids_an_existing_file_with_the_New_suffix()
        {
            var dir = Path.Combine(Path.GetTempPath(), "UZipFmt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "a.7z"), new byte[4]);
                Assert.Equal(Path.Combine(dir, "a-New1.7z"), ArchiveFormat.TargetPath(@"D:\src\a.zip", dir, 1));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    // 真实 7z 的互转流水线：解到私有 temp → 重新打包 → 原包必须留着
    public class ConvertWorkerTests : IDisposable
    {
        readonly string _root;
        readonly SettingsService _settings;
        readonly PasswordService _passwords;
        readonly SevenZipClient _client;

        public ConvertWorkerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipConvertTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        string MakeFile(string name, int kb = 4)
        {
            var buf = new byte[kb * 1024];
            new Random(name.Length).NextBytes(buf);
            var dir = Path.Combine(_root, "src");
            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, name);
            File.WriteAllBytes(full, buf);
            return full;
        }

        async Task<string> PackZip(params string[] files)
        {
            var outZip = Path.Combine(_root, "pack.zip");
            var res = await _client.CompressAsync(files, outZip, null, 0, 5, false, null, CancellationToken.None);
            Assert.True(res.Success, res.Diagnosis ?? "打包失败");
            return outZip;
        }

        [Fact]
        public async Task Convert_zip_to_7z_yields_a_working_archive_and_keeps_the_source()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("c1.bin"), MakeFile("c2.bin"));
            var outDir = Path.Combine(_root, "conv");

            var worker = new ArchiveWorker(_client, _passwords, _settings);
            worker.EnqueueConvert(new[] { zip }, 1, outDir);
            await worker.WhenIdleAsync();

            var produced = Path.Combine(outDir, "pack.7z");
            Assert.True(File.Exists(produced), "应产出 7z: " + produced);
            Assert.True(File.Exists(zip), "转换绝不能删原包");

            var check = await _client.TestAsync(produced, null, CancellationToken.None);
            Assert.True(check.Success, check.Diagnosis ?? "转换产物应能通过自检");
        }

        // CLI 在线程池上等结果，而绑定用的 Jobs 集合只允许调度线程修改：headless 时 UI 线程
        // 正阻塞在 CLI 结果上，BeginInvoke 永远不会被泵，作业集合一条也不会长的进去。
        // 所以 EnqueueConvert 必须把作业对象本身交回调用方，否则 convert 跑完了也报不出结果。
        [Fact]
        public async Task EnqueueConvert_reports_its_own_jobs_even_when_the_dispatcher_is_blocked()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("c1.bin"));
            var outDir = Path.Combine(_root, "convNoUi");

            var worker = new ArchiveWorker(_client, _passwords, _settings, null, null, null, BlockedDispatcher());
            var jobs = worker.EnqueueConvert(new[] { zip }, 1, outDir);
            await worker.WhenIdleAsync();

            var job = Assert.Single(jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.True(File.Exists(Path.Combine(outDir, "pack.7z")), job.Diagnosis);
            Assert.Empty(worker.Jobs);   // 调度线程没跑，UI 侧集合确实一直是空的
        }

        // 一个建好但从不泵消息的 Dispatcher：精确复现 CLI 里"UI 线程被 GetResult 卡住"的处境
        static System.Windows.Threading.Dispatcher BlockedDispatcher()
        {
            System.Windows.Threading.Dispatcher result = null;
            var gate = new ManualResetEventSlim();
            var hold = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                result = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                gate.Set();
                hold.Wait();   // 永不 Run，队列里的 BeginInvoke 就永不执行
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            gate.Wait(TimeSpan.FromSeconds(5));
            return result;
        }

        [Fact]
        public async Task Convert_reuses_the_source_password_for_the_new_package()
        {
            if (_client.SevenZipPath == null) return;
            var zip = Path.Combine(_root, "sec.zip");
            var res = await _client.CompressAsync(new[] { MakeFile("s.bin") }, zip, "pw123", 0, 1, false,
                null, CancellationToken.None);
            Assert.True(res.Success, res.Diagnosis);

            var outDir = Path.Combine(_root, "convPw");
            var worker = new ArchiveWorker(_client, _passwords, _settings);
            worker.EnqueueConvert(new[] { zip }, 1, outDir, "pw123");
            await worker.WhenIdleAsync();

            var produced = Path.Combine(outDir, "sec.7z");
            Assert.True(File.Exists(produced), string.Join(" | ", worker.Jobs.Select(j => j.Diagnosis)));
            // 口令要跟着走：不带口令的自检必须失败，否则等于把加密包转成了明文包
            Assert.False((await _client.TestAsync(produced, null, CancellationToken.None)).Success);
            Assert.True((await _client.TestAsync(produced, "pw123", CancellationToken.None)).Success);
        }

        [Fact]
        public async Task Convert_to_a_single_stream_format_needs_exactly_one_file()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("m1.bin"), MakeFile("m2.bin"));
            var outDir = Path.Combine(_root, "convBz");

            var worker = new ArchiveWorker(_client, _passwords, _settings);
            worker.EnqueueConvert(new[] { zip }, 2, outDir);
            await worker.WhenIdleAsync();

            var job = worker.Jobs.Single();
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("单个文件", job.Diagnosis);
            Assert.Empty(Directory.GetFiles(outDir));
        }

        [Fact]
        public async Task Convert_a_one_file_package_to_bz2_succeeds()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("only.bin"));
            var outDir = Path.Combine(_root, "convBzOk");

            var worker = new ArchiveWorker(_client, _passwords, _settings);
            worker.EnqueueConvert(new[] { zip }, 2, outDir);
            await worker.WhenIdleAsync();

            var job = worker.Jobs.Single();
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.True(File.Exists(Path.Combine(outDir, "pack.bz2")), job.Diagnosis);
        }

        [Fact]
        public async Task Convert_of_an_unknown_target_type_fails_without_touching_the_source()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("u.bin"));
            var worker = new ArchiveWorker(_client, _passwords, _settings);
            worker.EnqueueConvert(new[] { zip }, -1, Path.Combine(_root, "convBad"));
            await worker.WhenIdleAsync();

            var job = worker.Jobs.Single();
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.True(File.Exists(zip));
        }

        // 主页「转格式」按钮：目标格式来自工具条下拉，下标映射必须由 ArchiveFormat 说同一套话
        [Fact]
        public async Task Home_view_model_converts_the_job_archive_to_the_picked_format()
        {
            if (_client.SevenZipPath == null) return;
            var zip = await PackZip(MakeFile("h.bin"));
            var worker = new ArchiveWorker(_client, _passwords, _settings);
            var vm = new UZIP2.ViewModel.HomeViewModel(worker, _settings, _client, _passwords,
                new ClipboardService(_passwords, _settings));

            vm.ConvertTypeIndex = 0;                       // 列表第一项 = 7z
            vm.ConvertJobCommand.Execute(new JobEntry { Kind = "Extract", Archive = zip });
            await worker.WhenIdleAsync();

            // 卡片上的「转格式」不加问号：产物就放在原包旁边
            Assert.True(File.Exists(Path.Combine(_root, "pack.7z")),
                string.Join(" | ", worker.Jobs.Select(j => j.Diagnosis)));
            Assert.NotEmpty(vm.ConvertTypeNames);
            Assert.Equal("7z", vm.ConvertTypeNames[0]);
        }
    }

    // CLI 门面：convert 命令
    public class ConvertCliTests
    {
        [Fact]
        public async Task Cli_convert_turns_a_zip_into_a_7z()
        {
            var root = Path.Combine(Path.GetTempPath(), "UZipConvCli_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            try
            {
                var src = Path.Combine(root, "f.txt");
                File.WriteAllText(src, new string('x', 5000));
                var o = new List<string>(); var e = new List<string>();
                var runner = new UZIP2.Cli.CliRunner(root, Path.Combine(root, "Config"), s => o.Add(s), s => e.Add(s));
                var outDir = Path.Combine(root, "zipout");
                var cc = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "compress", src, "-o", outDir, "--type", "zip", "--no-test" }));
                Assert.True(cc == 0, string.Join("|", e));
                var zip = Path.Combine(outDir, "f.zip");
                Assert.True(File.Exists(zip), string.Join("|", o));

                var convOut = Path.Combine(root, "conv");
                o.Clear(); e.Clear();
                var code = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "convert", zip, "--type", "7z", "-o", convOut }));
                Assert.True(code == 0, string.Join("|", e));
                Assert.True(File.Exists(Path.Combine(convOut, "f.7z")), string.Join("|", o));
                Assert.True(o.Any(s => s.Contains("->") && s.EndsWith("f.7z")), "转换成功必须在标准输出里报出产物");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task Cli_convert_rejects_iso_with_a_clear_message()
        {
            var root = Path.Combine(Path.GetTempPath(), "UZipConvIso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            try
            {
                var src = Path.Combine(root, "g.txt");
                File.WriteAllText(src, "hello");
                var o = new List<string>(); var e = new List<string>();
                var runner = new UZIP2.Cli.CliRunner(root, Path.Combine(root, "Config"), s => o.Add(s), s => e.Add(s));
                await runner.RunAsync(UZIP2.Cli.CliParser.Parse(new[] { "compress", src, "-o", root, "--type", "zip", "--no-test" }));
                var code = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "convert", Path.Combine(root, "g.zip"), "--type", "iso" }));
                Assert.Equal(2, code);
                Assert.Contains(e, s => s.Contains("ISO"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // 旧行为: --type iso 会被静默当成 zip，用户拿到一个装着 ISO 名字的 zip。
        [Fact]
        public async Task Cli_compress_rejects_a_format_7z_cannot_create()
        {
            var root = Path.Combine(Path.GetTempPath(), "UZipCompIso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            try
            {
                var src = Path.Combine(root, "h.txt");
                File.WriteAllText(src, "hello");
                var o = new List<string>(); var e = new List<string>();
                var runner = new UZIP2.Cli.CliRunner(root, Path.Combine(root, "Config"), s => o.Add(s), s => e.Add(s));
                var code = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "compress", src, "-o", root, "--type", "iso" }));
                Assert.Equal(2, code);
                Assert.Contains(e, s => s.Contains("ISO"));
                Assert.False(File.Exists(Path.Combine(root, "h.zip")), "被拒绝的格式不该产出任何包");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task Cli_compress_rejects_a_typo_format()
        {
            var root = Path.Combine(Path.GetTempPath(), "UZipCompTypo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            try
            {
                var src = Path.Combine(root, "t.txt");
                File.WriteAllText(src, "hello");
                var e = new List<string>();
                var runner = new UZIP2.Cli.CliRunner(root, Path.Combine(root, "Config"), s => { }, s => e.Add(s));
                var code = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "compress", src, "-o", root, "--type", "zst" }));
                Assert.Equal(2, code);
                Assert.Contains(e, s => s.Contains("zst"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // --name x.iso 同样不该被当成 zip
        [Fact]
        public async Task Cli_compress_rejects_an_uncreatable_name_extension()
        {
            var root = Path.Combine(Path.GetTempPath(), "UZipCompName_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            try
            {
                var src = Path.Combine(root, "n.txt");
                File.WriteAllText(src, "hello");
                var e = new List<string>();
                var runner = new UZIP2.Cli.CliRunner(root, Path.Combine(root, "Config"), s => { }, s => e.Add(s));
                var code = await runner.RunAsync(UZIP2.Cli.CliParser.Parse(
                    new[] { "compress", src, "--name", Path.Combine(root, "n.iso") }));
                Assert.Equal(2, code);
                Assert.Contains(e, s => s.Contains("ISO"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
