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
    public class WatchFolderServiceTests : IDisposable
    {
        private readonly string _root;
        private readonly string _watch;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _client;
        private readonly ArchiveWorker _worker;
        private readonly WatchFolderService _service;

        public WatchFolderServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipWatchTests_" + Guid.NewGuid().ToString("N"));
            _watch = Path.Combine(_root, "watch");
            Directory.CreateDirectory(_watch);
            Directory.CreateDirectory(Path.Combine(_root, "out"));

            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = Path.Combine(_root, "out");
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
            _worker = new ArchiveWorker(_client, _passwords, _settings);
            _service = new WatchFolderService(_settings, _worker);
        }

        public void Dispose()
        {
            _service.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }

        private bool Has7z => _client.SevenZipPath != null;

        [Theory]
        [InlineData(@"C:\dl\pack.zip", true)]
        [InlineData(@"C:\dl\pack.7z", true)]
        [InlineData(@"C:\dl\pack.rar", true)]
        [InlineData(@"C:\dl\pack.zip.crdownload", false)]
        [InlineData(@"C:\dl\pack.zip.part", false)]
        [InlineData(@"C:\dl\pack.tmp", false)]
        [InlineData(@"C:\dl\notes.txt", false)]
        [InlineData(@"C:\dl\movie.mp4", false)]
        [InlineData(@"C:\dl\~temp.zip", false)]
        [InlineData("", false)]
        public void ShouldHandle_filters_archives_and_partial_downloads(string path, bool expected)
            => Assert.Equal(expected, WatchFolderService.ShouldHandle(path));

        [Fact]
        public async Task Stable_file_passes_quickly_locked_file_times_out()
        {
            var ok = Path.Combine(_watch, "ok.bin");
            File.WriteAllText(ok, "data");
            var sw = Stopwatch.StartNew();
            Assert.True(await WatchFolderService.WaitUntilStableAsync(ok, CancellationToken.None, 50));
            Assert.True(sw.ElapsedMilliseconds < 2000, "空闲文件应立刻判定写完，实测 " + sw.ElapsedMilliseconds + " ms");

            var busy = Path.Combine(_watch, "busy.bin");
            File.WriteAllText(busy, "data");
            using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(await WatchFolderService.WaitUntilStableAsync(busy, CancellationToken.None, 50, timeoutMs: 400));
            }
        }

        [Fact]
        public async Task Dropped_archive_is_extracted_without_being_dragged()
        {
            if (!Has7z) return;
            var src = Path.Combine(_root, "payload.bin");
            File.WriteAllBytes(src, Enumerable.Repeat<byte>(7, 2048).ToArray());
            var zip = Path.Combine(_root, "auto.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zip, null, 0, 3, false, null, CancellationToken.None)).Success);

            _settings.Save(s => { s.WatchEnabled = true; s.WatchFolder = _watch; });
            _service.Apply();
            Assert.True(_service.IsRunning);
            Assert.Equal(_watch, _service.Folder);

            File.Copy(zip, Path.Combine(_watch, "auto.zip"));
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 15000 && _worker.Jobs.Count == 0)
                await Task.Delay(100);

            Assert.NotEmpty(_worker.Jobs);
            await _worker.WhenIdleAsync();
            var job = _worker.Jobs.Single();
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.True(File.Exists(Path.Combine(_root, "out", "payload.bin")));
        }

        [Fact]
        public async Task Disabled_or_missing_folder_does_not_watch()
        {
            _settings.Save(s => { s.WatchEnabled = false; s.WatchFolder = _watch; });
            _service.Apply();
            Assert.False(_service.IsRunning);

            _settings.Save(s => { s.WatchEnabled = true; s.WatchFolder = Path.Combine(_root, "nope"); });
            _service.Apply();
            Assert.False(_service.IsRunning);

            _settings.Save(s => { s.WatchEnabled = true; s.WatchFolder = _watch; });
            _service.Apply();
            Assert.True(_service.IsRunning);

            // 重复 Apply 不应重建监听，也不该丢事件
            _service.Apply();
            var src = Path.Combine(_root, "again.txt");
            File.WriteAllText(src, "x");
            var zip = Path.Combine(_root, "again.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zip, null, 0, 0, false, null, CancellationToken.None)).Success);
            File.Copy(zip, Path.Combine(_watch, "again.zip"));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000 && _worker.Jobs.Count == 0)
                await Task.Delay(100);
            Assert.NotEmpty(_worker.Jobs);
        }

        // 下载目录可能在移动盘/网络盘上：目录暂时不在不代表用户不想监听。
        // 以前 Apply() 之后就再也不重试，盘符回来后监听仍是死的。
        [Fact]
        public async Task Missing_folder_is_rearmed_once_it_appears()
        {
            var dir = Path.Combine(_root, "later");
            _settings.Save(s => { s.WatchEnabled = true; s.WatchFolder = dir; });
            _service.Apply();
            Assert.False(_service.IsRunning);

            Directory.CreateDirectory(dir);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && !_service.IsRunning) await Task.Delay(100);

            Assert.True(_service.IsRunning, "目录出现后必须自动重建监听");
            Assert.Equal(dir, _service.Folder);
        }

        // watcher 内部缓冲溢出/目录被删时会抛 Error；不接住就是整个进程跟着崩。
        // 接住以后必须重建出一个"真的在收事件"的新 watcher。
        [Fact]
        public async Task Restart_rearms_a_live_watcher()
        {
            if (!Has7z) return;
            var src = Path.Combine(_root, "rearm.txt");
            File.WriteAllText(src, "x");
            var zip = Path.Combine(_root, "rearm.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zip, null, 0, 0, false, null, CancellationToken.None)).Success);

            _settings.Save(s => { s.WatchEnabled = true; s.WatchFolder = _watch; });
            _service.Apply();
            _service.Restart();          // 等价于 Error 事件后的重建
            Assert.True(_service.IsRunning);

            File.Copy(zip, Path.Combine(_watch, "rearm.zip"));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000 && _worker.Jobs.Count == 0) await Task.Delay(100);
            Assert.NotEmpty(_worker.Jobs);
        }
    }
}
