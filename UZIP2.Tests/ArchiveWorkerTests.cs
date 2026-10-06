using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class ArchiveWorkerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _src;
        private readonly string _out;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _client;
        private readonly ArchiveWorker _worker;

        public ArchiveWorkerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipWorkerTests_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            _out = Path.Combine(_root, "out");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_out);

            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            // 解压统一输出到 _out (Browse 模式 + 预设目录)
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = _out;
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
            Assert.NotNull(_client.SevenZipPath); // 依赖本机 7-Zip
            _worker = new ArchiveWorker(_client, _passwords, _settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeFile(string dir, string name, int kb = 4, byte fill = 65)
        {
            Directory.CreateDirectory(dir);
            var buf = new byte[kb * 1024];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(fill ^ (i & 0x1F));
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        private Task<SevenZipResult> CompressRaw(string outArchive, string password, int type, params string[] sources)
            => _client.CompressAsync(sources, outArchive, password, type, 3, false, null, CancellationToken.None);

        // ---------- 解压 ----------

        [Fact]
        public async Task Extract_plain_zip_success_and_no_temp_left()
        {
            var f = MakeFile(_src, "data.bin");
            var zip = Path.Combine(_root, "plain.zip");
            Assert.True((await CompressRaw(zip, null, 0, f)).Success);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Null(job.UsedPassword);
            Assert.True(File.Exists(Path.Combine(_out, "data.bin")));
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        [Fact]
        public async Task Extract_book_password_success_and_scores()
        {
            var f = MakeFile(_src, "d1.bin");
            var zip = Path.Combine(_root, "locked.zip");
            Assert.True((await CompressRaw(zip, "bookpw", 0, f)).Success);
            _passwords.AddBook("常用", "bookpw");

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("bookpw", job.UsedPassword);
            Assert.True(File.Exists(Path.Combine(_out, "d1.bin")));
            Assert.Equal(1, _passwords.Book.Single().SuccessCount);
        }

        [Fact]
        public async Task Paper_password_is_consumed_into_recycle()
        {
            var f = MakeFile(_src, "d2.bin");
            var zip = Path.Combine(_root, "paper.zip");
            Assert.True((await CompressRaw(zip, "paperpw", 0, f)).Success);
            _passwords.PasteToPaper("paperpw");

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, _worker.Jobs.Single().Status);
            Assert.Empty(_passwords.Paper);
            Assert.Contains("paperpw", _passwords.Recycle);
        }

        [Fact]
        public async Task Extract_without_any_password_fails_with_diagnosis()
        {
            var f = MakeFile(_src, "d3.bin");
            var zip = Path.Combine(_root, "locked2.zip");
            Assert.True((await CompressRaw(zip, "hidden", 0, f)).Success);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("未找到正确密码", job.Diagnosis);
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        [Fact]
        public async Task Name_password_extract_and_purified_folder()
        {
            var f = MakeFile(_src, "d4.bin");
            var zip = Path.Combine(_root, "电影#pw99#.7z");
            Assert.True((await CompressRaw(zip, "pw99", 1, f)).Success);

            _settings.Current.NameToPassword = true;
            _settings.Current.NameFilter = "#";
            _settings.Current.CreateNameFolder = true;
            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("pw99", job.UsedPassword);
            Assert.True(Directory.Exists(Path.Combine(_out, "电影")), "应使用提纯后的文件夹名");
            Assert.True(File.Exists(Path.Combine(_out, "电影", "d4.bin")));
        }

        // "解压到当前文件夹"动词必须无视建目录设置，即使同名文件夹开关打开
        [Fact]
        public async Task Flat_extract_overrides_create_name_folder()
        {
            var f = MakeFile(_src, "d5.bin");
            var zip = Path.Combine(_root, "here.zip");
            Assert.True((await CompressRaw(zip, null, 0, f)).Success);

            _settings.Current.CreateNameFolder = true;
            _worker.EnqueueExtract(new[] { zip }, _out, null, true);
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, Assert.Single(_worker.Jobs).Status);
            Assert.True(File.Exists(Path.Combine(_out, "d5.bin")), "文件应直接落在输出目录");
            Assert.False(Directory.Exists(Path.Combine(_out, "here")), "不应建立同名文件夹");
        }

        [Fact]
        public async Task MultiLevel_extracts_nested_archive()
        {
            var innerFile = MakeFile(_src, "inner.bin", 2);
            var inner = Path.Combine(_root, "inner.7z");
            Assert.True((await CompressRaw(inner, "nest99", 1, innerFile)).Success);

            var outer = Path.Combine(_root, "outer.zip");
            Assert.True((await CompressRaw(outer, null, 0, inner)).Success);

            _passwords.AddBook("内层", "nest99");
            _worker.EnqueueExtract(new[] { outer });
            await _worker.WhenIdleAsync();

            Assert.Equal(2, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            Assert.True(File.Exists(Path.Combine(_out, "inner.bin")), "内层压缩包应被再次解压");
        }

        [Fact]
        public async Task MultiLevel_chains_three_levels_to_the_innermost_file()
        {
            var leaf = MakeFile(_src, "deep.bin", 2);
            var l2 = Path.Combine(_root, "l2.7z");
            Assert.True((await CompressRaw(l2, null, 1, leaf)).Success);
            var l1 = Path.Combine(_root, "l1.zip");
            Assert.True((await CompressRaw(l1, null, 0, l2)).Success);
            var l0 = Path.Combine(_root, "l0.7z");
            Assert.True((await CompressRaw(l0, null, 1, l1)).Success);

            _worker.EnqueueExtract(new[] { l0 });
            await _worker.WhenIdleAsync();

            Assert.Equal(3, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            Assert.True(File.Exists(Path.Combine(_out, "deep.bin")), "第三层内容应被解出");
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        // 递归必须有上界：没有 MultiLevelDepthLimit 时自我包含/超深嵌套会把任务队列无限撑大。
        // 第 9 层（Depth==8）之后不再派生新作业，最深的包留在原地等用户手动处理。
        [Fact]
        public async Task MultiLevel_stops_at_the_depth_limit()
        {
            var pending = MakeFile(_src, "core.bin", 1);
            string current = null;
            for (int level = 0; level < 10; level++)
            {
                var archive = Path.Combine(_root, "chain" + level + ".7z");
                Assert.True((await CompressRaw(archive, null, 1, pending)).Success);
                current = archive;
                pending = archive;
            }

            _worker.EnqueueExtract(new[] { current });
            await _worker.WhenIdleAsync();

            Assert.Equal(9, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            Assert.True(File.Exists(Path.Combine(_out, "chain0.7z")), "截断处的包应保留");
            Assert.False(File.Exists(Path.Combine(_out, "core.bin")), "超过深度上限后不再解压");
        }

        [Fact]
        public async Task Volumes_are_redirected_to_main_and_deduped()
        {
            // 随机数据不可压缩, 确保 -v200k 真正分成多卷
            var rnd = new Random(1234);
            var buf = new byte[400 * 1024];
            rnd.NextBytes(buf);
            var big = Path.Combine(_src, "bigfile.bin");
            File.WriteAllBytes(big, buf);
            var psi = new ProcessStartInfo(_client.SevenZipPath) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add(Path.Combine(_root, "big.7z"));
            psi.ArgumentList.Add(big);
            psi.ArgumentList.Add("-v200k");
            psi.ArgumentList.Add("-y");
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                Assert.True(p.ExitCode == 0);
            }
            Assert.True(File.Exists(Path.Combine(_root, "big.7z.001")));
            Assert.True(File.Exists(Path.Combine(_root, "big.7z.002")));

            _worker.EnqueueExtract(new[]
            {
                Path.Combine(_root, "big.7z.001"),
                Path.Combine(_root, "big.7z.002"),
            });
            await _worker.WhenIdleAsync();

            Assert.Equal(2, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.Equal(JobStatus.Success, j.Status));
            // 并发下两卷谁先认领不确定，只断言"恰好有一个被判为随主卷处理"
            Assert.Contains(_worker.Jobs, j => (j.Diagnosis ?? "").Contains("已随分卷主文件处理"));
            Assert.True(File.Exists(Path.Combine(_out, "bigfile.bin")));
        }

        // 分卷的"影子作业"以前固定报成功：主卷其实是坏包时，队列里显示两个绿勾，
        // 而用户一个文件都没拿到。影子必须照抄主作业的真实结果。
        [Fact]
        public async Task Volume_shadow_job_mirrors_the_winner_failure()
        {
            var rnd = new Random(7);
            var buf = new byte[400 * 1024];
            rnd.NextBytes(buf);
            var big = Path.Combine(_src, "sick.bin");
            File.WriteAllBytes(big, buf);

            var psi = new ProcessStartInfo(_client.SevenZipPath) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add(Path.Combine(_root, "sick.7z"));
            psi.ArgumentList.Add(big);
            psi.ArgumentList.Add("-v200k");
            psi.ArgumentList.Add("-y");
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                Assert.Equal(0, p.ExitCode);
            }

            // 抹掉首卷头部数据，让这一组必然解不动
            var main = Path.Combine(_root, "sick.7z.001");
            using (var fs = new FileStream(main, FileMode.Open))
            {
                fs.Seek(64, SeekOrigin.Begin);
                fs.Write(new byte[4096], 0, 4096);
            }

            _worker.EnqueueExtract(new[] { main, Path.Combine(_root, "sick.7z.002") });
            await _worker.WhenIdleAsync();

            Assert.Equal(2, _worker.Jobs.Count);
            Assert.All(_worker.Jobs, j => Assert.NotEqual(JobStatus.Success, j.Status));
        }

        [Fact]
        public async Task Volumes_single_part2_redirects_to_missing_main_and_fails_gracefully()
        {
            // 只存在 .002 而无 .001 时, 按原文件尝试 → 失败但不崩溃
            var fake = Path.Combine(_root, "miss.7z.002");
            File.WriteAllBytes(fake, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 });
            _worker.EnqueueExtract(new[] { fake });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
        }

        [Fact]
        public async Task Retry_after_adding_password_succeeds()
        {
            var f = MakeFile(_src, "d5.bin");
            var zip = Path.Combine(_root, "late.zip");
            Assert.True((await CompressRaw(zip, "late99", 0, f)).Success);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();
            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);

            _passwords.AddBook("迟到", "late99");
            _worker.Retry(job);
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("late99", job.UsedPassword);
        }

        // ---------- 预览 / 勾选解压 ----------

        [Fact]
        public async Task Preview_lists_entries_and_finds_password_without_extracting()
        {
            var a = MakeFile(_src, "one.bin", 1);
            var b = MakeFile(_src, "two.bin", 1, 66);
            var zip = Path.Combine(_root, "pv.zip");
            Assert.True((await CompressRaw(zip, "pv-pw", 0, a, b)).Success);
            _passwords.AddBook("pv", "pv-pw");

            var listing = await _worker.PreviewAsync(zip);
            Assert.True(listing.Success, listing.Diagnosis);
            Assert.Equal(2, listing.Entries.Count(e => !e.IsFolder));
            // 预览只读清单，不应产生任何解压输出
            Assert.Empty(_worker.Jobs);
            Assert.Empty(Directory.GetFiles(_out, "*.bin"));
        }

        [Fact]
        public async Task Selected_entries_only_extract_the_checked_files()
        {
            var dir = Path.Combine(_root, "pvsrc");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "KEEP");
            File.WriteAllText(Path.Combine(dir, "drop.txt"), "DROP");
            var zip = Path.Combine(_root, "pick.zip");
            Assert.True((await CompressRaw(zip, null, 0, dir)).Success);

            var listing = await _worker.PreviewAsync(zip);
            Assert.True(listing.Success, listing.Diagnosis);
            var pick = listing.Entries.Where(e => !e.IsFolder && e.Path.EndsWith("keep.txt"))
                                      .Select(e => e.Path).ToList();

            _worker.EnqueueExtract(new[] { zip }, null, pick);
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, _worker.Jobs.Single().Status);
            var dropped = Directory.GetFiles(_out, "*", SearchOption.AllDirectories);
            Assert.Contains("keep.txt", dropped.Select(Path.GetFileName));
            Assert.DoesNotContain("drop.txt", dropped.Select(Path.GetFileName));
        }

        // ---------- 压缩 ----------

        [Fact]
        public async Task Compress_alone_with_custom_password_to_name_and_log()
        {
            var f = MakeFile(_src, "one.txt", 1);
            var s = _settings.Current;
            s.CompressAlone = true;
            s.CompressType = 1;                       // 7z
            s.PasswordMode = 2;                       // 自定义1
            s.CustomPasswords[0] = "cust1";
            s.PasswordToName = true;
            s.NameFilter2 = "@";
            s.CompressOutMode = 1;                    // 输出到源所在目录
            s.DeleteCompressFinish = true;
            s.DeleteToRecycle = false;

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("cust1", job.UsedPassword);
            Assert.True(File.Exists(Path.Combine(_src, "one@cust1.7z")));
            Assert.False(File.Exists(f));             // 删源
            var log = File.ReadAllText(Path.Combine(_root, "Config", "Compress.log"), Encoding.UTF8);
            Assert.Contains("解压密码：cust1", log);
            Assert.Contains("one@cust1.7z", log);
        }

        [Fact]
        public async Task Compress_combined_uses_directory_name()
        {
            var pack = Path.Combine(_src, "pack");
            var a = MakeFile(pack, "a.txt", 1);
            var b = MakeFile(pack, "b.txt", 1);
            var s = _settings.Current;
            s.CompressAlone = false;
            s.CompressType = 0;
            s.CompressOutMode = 1;
            s.PasswordMode = 0;

            _worker.EnqueueCompress(new[] { a, b });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.True(File.Exists(Path.Combine(pack, "pack.zip")));
        }

        [Fact]
        public async Task Compress_random_password_generates_and_logs()
        {
            var f = MakeFile(_src, "rnd.txt", 1);
            var s = _settings.Current;
            s.CompressAlone = true;
            s.CompressType = 1;
            s.PasswordMode = 6; // 随机8位
            s.CompressOutMode = 1;

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal(8, (job.UsedPassword ?? "").Length);
            var log = File.ReadAllText(Path.Combine(_root, "Config", "Compress.log"), Encoding.UTF8);
            Assert.Contains(job.UsedPassword, log);
        }

        [Fact]
        public async Task CancelAll_never_fails_jobs()
        {
            var f = MakeFile(_src, "c1.bin");
            var zip = Path.Combine(_root, "c1.zip");
            Assert.True((await CompressRaw(zip, null, 0, f)).Success);

            _worker.EnqueueExtract(new[] { zip });
            _worker.CancelAll();
            await _worker.WhenIdleAsync();

            Assert.All(_worker.Jobs, j => Assert.Contains(j.Status.ToString(), new[] { "Success", "Cancelled" }));
        }

        // ---------- 收尾清理 ----------

        // temp 目录建在输出目录下，任何"已经建好 temp 又中途失败"的分支都必须删掉它，
        // 否则用户的下载目录会攒下 UZipTemp_* 隐藏垃圾
        [Fact]
        public async Task Extract_failure_after_temp_created_leaves_no_temp_dir()
        {
            // 包里有一个顶层目录 pack，输出目录预先放一个同名"文件" → 移动尾段必然抛异常
            var pack = Path.Combine(_src, "pack");
            MakeFile(pack, "inside.bin", 1);
            var zip = Path.Combine(_root, "clash.zip");
            Assert.True((await CompressRaw(zip, null, 0, pack)).Success);
            File.WriteAllText(Path.Combine(_out, "pack"), "I am a file, not the folder 7z wants to create");

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        // 取消发生在解压进行中时，同样不允许留下 temp。
        // 用"加密包 + 20 条错密码"把作业拖到秒级：进度行在单文件包上几乎不出现，
        // 所以判定条件只能是"temp 已建出来"，不能是 Percent。
        [Fact]
        public async Task Cancel_during_extract_leaves_no_temp_dir()
        {
            var big = MakeRandomFile(Path.Combine(_src, "big.bin"), 24);
            var store = Path.Combine(_root, "bigstore.7z");
            Assert.True((await CompressRaw(store, "never-guess-me", 1, big)).Success);
            for (int i = 0; i < 20; i++) _passwords.AddBook("错密码" + i, "wrong-" + i);

            _worker.EnqueueExtract(new[] { store });
            await WaitUntil(j => j.Status == JobStatus.Running
                                 && Directory.GetDirectories(_out, "UZipTemp_*").Length > 0);
            var job = _worker.Jobs.Single();
            _worker.Cancel(job);
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        // 压缩被取消/失败时，半截产物必须删掉：留着它既是坏包，又会让重试被改名成 -New1
        [Fact]
        public async Task Cancel_during_compress_removes_partial_archive()
        {
            var big = MakeRandomFile(Path.Combine(_src, "p.bin"), 200);
            var s = _settings.Current;
            s.CompressAlone = true;
            s.CompressType = 1;                       // 7z + 默认级别，随机数据要压好几秒
            s.PasswordMode = 0;
            s.LastCompressPath = _out;

            _worker.EnqueueCompress(new[] { big });
            await WaitUntil(j => j.Status == JobStatus.Running
                                 && Directory.GetFiles(_out, "*.7z").Length > 0);
            var job = _worker.Jobs.Single();
            _worker.Cancel(job);
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Empty(Directory.GetFiles(_out, "*.7z"));
        }

        async Task WaitUntil(Func<JobEntry, bool> ready)
        {
            for (int i = 0; i < 3000; i++)
            {
                if (_worker.Jobs.Any(ready)) return;
                await Task.Delay(1);
            }
            throw new TimeoutException("作业没有进入预期的进行中状态，用例的时序假设不成立");
        }

        static string MakeRandomFile(string path, int mb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var rnd = new Random(20261006);
            var buf = new byte[4 * 1024 * 1024];
            using (var fs = File.Create(path, 1 << 20))
                for (int left = mb; left > 0; left -= 4)
                {
                    rnd.NextBytes(buf);
                    fs.Write(buf, 0, Math.Min(4, left) * 1024 * 1024);
                }
            return path;
        }
    }
}
