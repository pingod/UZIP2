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
    // 并发队列: 并行度必须真的生效，且不能突破设置上限；
    // 并发落到同一输出目录时建目录/搬移必须串行，不能互相踩。
    public class ParallelQueueTests : IDisposable
    {
        private readonly string _root;
        private readonly string _src;
        private readonly string _out;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _client;

        public ParallelQueueTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipParallelTests_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            _out = Path.Combine(_root, "out");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_out);

            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = _out;
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeIncompressible(string dir, string name, int mb)
        {
            Directory.CreateDirectory(dir);
            var buf = new byte[mb * 1024 * 1024];
            new Random(mb).NextBytes(buf);
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        private async Task<string> MakeZip(string zipPath, string sourceFile)
        {
            Assert.True((await _client.CompressAsync(new[] { sourceFile }, zipPath, null, 0, 0,
                false, null, CancellationToken.None)).Success);
            return zipPath;
        }

        [Fact]
        public async Task Parallel_Extract_Actually_Overlaps()
        {
            if (_client.SevenZipPath == null) return;
            _settings.Current.ParallelExtract = 4;
            var worker = new ArchiveWorker(_client, _passwords, _settings);

            var zips = new List<string>();
            for (int i = 0; i < 8; i++)
                zips.Add(await MakeZip(Path.Combine(_root, "pkg" + i + ".zip"),
                    MakeIncompressible(_src, "payload" + i + ".bin", 6)));

            worker.EnqueueExtract(zips);
            await worker.WhenIdleAsync();

            Assert.Equal(8, worker.Jobs.Count(j => j.Status == JobStatus.Success));
            for (int i = 0; i < 8; i++)
                Assert.True(File.Exists(Path.Combine(_out, "payload" + i + ".bin")), "payload" + i);
            Assert.True(worker.PeakActive >= 2, "并发未生效，PeakActive=" + worker.PeakActive);
            Assert.True(worker.PeakActive <= 4, "并发突破上限，PeakActive=" + worker.PeakActive);
        }

        [Fact]
        public async Task Parallel_One_Serializes_And_Still_Succeeds()
        {
            if (_client.SevenZipPath == null) return;
            _settings.Current.ParallelExtract = 1;
            var worker = new ArchiveWorker(_client, _passwords, _settings);

            var zips = new List<string>();
            for (int i = 0; i < 4; i++)
                zips.Add(await MakeZip(Path.Combine(_root, "s" + i + ".zip"),
                    MakeIncompressible(_src, "sp" + i + ".bin", 3)));

            worker.EnqueueExtract(zips);
            await worker.WhenIdleAsync();

            Assert.Equal(4, worker.Jobs.Count(j => j.Status == JobStatus.Success));
            Assert.Equal(1, worker.PeakActive);
        }

        // 同名不同目录的包，智能建目录全部争抢 out\dup\，避让必须串行
        [Fact]
        public async Task Concurrent_Extracts_With_Same_Target_FolderName_Avoid_Collision()
        {
            if (_client.SevenZipPath == null) return;
            _settings.Current.ParallelExtract = 4;
            _settings.Current.CreateNameFolder = true;
            var worker = new ArchiveWorker(_client, _passwords, _settings);

            var zips = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var f = MakeIncompressible(Path.Combine(_src, "u" + i), "content" + i + ".bin", 2);
                var dir = Path.Combine(_root, "z" + i);
                Directory.CreateDirectory(dir);
                zips.Add(await MakeZip(Path.Combine(dir, "dup.zip"), f));
            }

            worker.EnqueueExtract(zips);
            await worker.WhenIdleAsync();

            Assert.Equal(6, worker.Jobs.Count(j => j.Status == JobStatus.Success));
            var folders = Directory.GetDirectories(_out)
                .Where(d => !Path.GetFileName(d).StartsWith("UZipTemp_"))
                .ToList();
            Assert.Equal(6, folders.Count);
            Assert.Contains(folders, d => Path.GetFileName(d) == "dup");
            // 每个目录只收到 1 个文件，说明没有互相搬错地方
            Assert.Equal(6, folders.Count(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length == 1));
        }

        // 未加密包必须走"清单探测→直接解压"，不再跑整包 t
        [Fact]
        public async Task Unencrypted_Archive_Extracts_Without_Test_Pass()
        {
            if (_client.SevenZipPath == null) return;
            _settings.Current.DebugMode = true;
            var log = new CollectingLogger();
            var worker = new ArchiveWorker(new SevenZipClient(_settings, log), _passwords, _settings);
            var zip = await MakeZip(Path.Combine(_root, "plain.zip"),
                MakeIncompressible(_src, "plain.bin", 1));

            worker.EnqueueExtract(new[] { zip });
            await worker.WhenIdleAsync();

            var job = Assert.Single(worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Null(job.UsedPassword);
            Assert.True(File.Exists(Path.Combine(_out, "plain.bin")));

            Assert.Contains(log.Lines, l => l.Contains(" l " + zip));
            Assert.DoesNotContain(log.Lines, l => l.Contains(" t " + zip));
            Assert.Contains(log.Lines, l => l.Contains(" x " + zip));
        }

        // WhenIdle 必须覆盖"已出队但还在等并行名额"的窗口，不能提前返回
        [Fact]
        public async Task WhenIdle_Waits_Until_Every_Job_Is_Terminal()
        {
            if (_client.SevenZipPath == null) return;
            _settings.Current.ParallelExtract = 1;
            var worker = new ArchiveWorker(_client, _passwords, _settings);

            var zips = new List<string>();
            for (int i = 0; i < 6; i++)
                zips.Add(await MakeZip(Path.Combine(_root, "w" + i + ".zip"),
                    MakeIncompressible(_src, "wp" + i + ".bin", 2)));

            worker.EnqueueExtract(zips);
            await worker.WhenIdleAsync();

            Assert.All(worker.Jobs, j => Assert.True(
                j.Status == JobStatus.Success || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled,
                "空闲时仍有未收尾作业: " + j.Id + "/" + j.Status));
            Assert.Equal(6, worker.Jobs.Count(j => j.Status == JobStatus.Success));
            Assert.Equal(0, System.IO.Directory.GetDirectories(_out, "UZipTemp_*").Length);
        }

        class CollectingLogger : IFileLogger
        {
            public readonly List<string> Lines = new List<string>();
            public void Info(string message) { lock (Lines) Lines.Add(message); }
            public void Warn(string message) { lock (Lines) Lines.Add(message); }
            public void Error(string message, Exception exception = null) { lock (Lines) Lines.Add(message); }
            public string LatestLogPath => "";
        }
    }
}
