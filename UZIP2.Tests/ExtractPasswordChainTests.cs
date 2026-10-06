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
    // 密码链的耗时账本：解一个加密包不该先把整包读两遍（t 一遍、x 一遍）。
    // 计数装饰器包住真实 client，语义还是真 7z，只是把"起了几遍全盘读"变成可断言的数。
    public class ExtractPasswordChainTests : IDisposable
    {
        readonly string _root, _src, _out;
        readonly SettingsService _settings;
        readonly PasswordService _passwords;
        readonly SevenZipClient _real;
        readonly CountingEngine _engine;
        readonly ArchiveWorker _worker;

        public ExtractPasswordChainTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipPwChain_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            _out = Path.Combine(_root, "out");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_out);

            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = _out;
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _real = new SevenZipClient(_settings);
            Assert.NotNull(_real.SevenZipPath);
            _engine = new CountingEngine(_real);
            _worker = new ArchiveWorker(_engine, _passwords, _settings);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        string MakeSource(string name, int kb = 64)
        {
            var rnd = new Random(1);
            var buf = new byte[kb * 1024];
            rnd.NextBytes(buf);
            var p = Path.Combine(_src, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        async Task<string> MakeEncryptedZip(string password, int type = 0)
        {
            var f = MakeSource("d.bin");
            var zip = Path.Combine(_root, "pw" + type + (type == 1 ? ".7z" : ".zip"));
            var r = await _real.CompressAsync(new[] { f }, zip, password, type, 3, false, null, CancellationToken.None);
            Assert.True(r.Success, r.Diagnosis ?? r.Output);
            return zip;
        }

        [Fact]
        public async Task Book_password_hit_extracts_without_a_second_full_pass()
        {
            var zip = await MakeEncryptedZip("bookpw");
            _passwords.AddBook("常用", "bookpw");

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("bookpw", job.UsedPassword);
            Assert.True(File.Exists(Path.Combine(_out, "d.bin")));
            Assert.Equal(0, _engine.TestCalls);
            Assert.Equal(1, _engine.ExtractCalls);
        }

        // 历史页/重试带来的口令就是答案本身，空口令那一遍全量 test 纯属白花
        [Fact]
        public async Task Manual_password_goes_straight_to_extract()
        {
            var zip = await MakeEncryptedZip("manual9");

            _worker.EnqueueExtract(new[] { zip }, manualPassword: "manual9");
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal("manual9", job.UsedPassword);
            Assert.Equal(0, _engine.TestCalls);
            Assert.Equal(1, _engine.ExtractCalls);
        }

        [Fact]
        public async Task Plain_archive_does_not_burn_a_test_pass()
        {
            var zip = await MakeEncryptedZip(null);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, Assert.Single(_worker.Jobs).Status);
            Assert.Equal(0, _engine.TestCalls);
            Assert.Equal(1, _engine.ExtractCalls);
        }

        // 换到正确口令之前，失败的那些次必须把自己的残留清干净：
        // 否则错口令解出来的半截文件会跟真结果一起落进用户目录。
        [Fact]
        public async Task Failed_password_attempts_leave_nothing_behind()
        {
            var zip = await MakeEncryptedZip("right9");
            _passwords.AddBook("错的第一批", "wrong1");
            _passwords.AddBook("对的", "right9");
            // 让错的那条排在前面（密码本按成功次数降序，先把对的次数压下去）
            _passwords.ReportResult("wrong1", true);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            var files = Directory.GetFiles(_out, "*", SearchOption.AllDirectories);
            Assert.Single(files);
            Assert.Equal("d.bin", Path.GetFileName(files[0]));
            Assert.Empty(Directory.GetDirectories(_out, "UZipTemp_*"));
        }

        // 坏包不能被说成"密码不对"：链上第一次失败的输出来定性
        [Fact]
        public async Task Corrupt_archive_is_reported_as_corrupt_not_as_wrong_password()
        {
            var zip = await MakeEncryptedZip(null);     // 明文包
            _passwords.AddBook("无关", "other");
            // 截断：尾部数据没了，7z 报的是"包坏了"而不是"缺密码"
            var len = new FileInfo(zip).Length;
            using (var fs = new FileStream(zip, FileMode.Open))
                fs.SetLength(len * 3 / 5);

            _worker.EnqueueExtract(new[] { zip });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("损坏", job.Diagnosis);
        }

        // 顺序与跳过规则是纯函数，单独钉住：谁在谁前面、什么时候不试空口令
        [Fact]
        public void Chain_skips_empty_password_when_listing_says_encrypted()
        {
            var chain = ArchiveWorker.BuildPasswordChain(true, null, new[] { "外1" }, null,
                new[] { "本1" }, new[] { "纸1" });

            Assert.Equal(new[] { "外1", "本1", "纸1" }, chain.Select(c => c.Password).ToArray());
            Assert.True(chain[2].FromPaper);
        }

        [Fact]
        public void Chain_keeps_empty_password_first_when_state_is_unknown()
        {
            var chain = ArchiveWorker.BuildPasswordChain(false, "手动", null, null, new[] { "手动", "本1" }, null);

            Assert.Null(chain[0].Password);
            Assert.Equal("手动", chain[1].Password);   // 人工指定排在密码本前
            Assert.Equal("本1", chain[2].Password);
            Assert.Equal(3, chain.Count);              // 重复的"手动"只试一次
        }

        [Fact]
        public void Chain_marks_name_derived_password_for_directory_rename()
        {
            var chain = ArchiveWorker.BuildPasswordChain(false, null, null, "从名", new[] { "本1" }, null);

            Assert.True(chain.Single(c => c.Password == "从名").FromName);
        }

        class CountingEngine : IArchiveEngine
        {
            readonly SevenZipClient _inner;
            public int TestCalls, ExtractCalls;

            public CountingEngine(SevenZipClient inner) { _inner = inner; }

            public Task<SevenZipResult> TestAsync(string archive, string password, CancellationToken ct)
            {
                Interlocked.Increment(ref TestCalls);
                return _inner.TestAsync(archive, password, ct);
            }

            public Task<SevenZipResult> ExtractAsync(string archive, string dest, string password,
                IProgress<SevenZipProgress> progress, CancellationToken ct, string coverMode = "-aos",
                IReadOnlyList<string> onlyEntries = null)
            {
                Interlocked.Increment(ref ExtractCalls);
                return _inner.ExtractAsync(archive, dest, password, progress, ct, coverMode, onlyEntries);
            }

            public Task<EncryptionState> ProbeEncryptionAsync(string archive, CancellationToken ct)
                => _inner.ProbeEncryptionAsync(archive, ct);

            public Task<ArchiveListing> ListEntriesAsync(string archive, string password, CancellationToken ct)
                => _inner.ListEntriesAsync(archive, password, ct);

            public Task<SevenZipResult> CompressAsync(IReadOnlyList<string> files, string outArchive, string password,
                int compressType, int level, bool hideContent, IProgress<SevenZipProgress> progress, CancellationToken ct,
                IReadOnlyList<string> excludeFilters = null, string volumeSize = null,
                string solid = null, string threads = null)
                => _inner.CompressAsync(files, outArchive, password, compressType, level, hideContent, progress, ct,
                    excludeFilters, volumeSize, solid, threads);
        }
    }
}
