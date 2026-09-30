using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Services
{
    // 监听目录自动解压: 下载/浏览器写完文件才动手，避免解到半截包。
    // 只监听目录本身（不递归），嵌套包交给队列自己的多级解压处理。
    public sealed class WatchFolderService : IDisposable
    {
        static readonly string[] PartialSuffixes =
            { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

        private readonly ISettingsService _settings;
        private readonly ArchiveWorker _worker;
        private readonly object _sync = new object();
        private FileSystemWatcher _watcher;
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cts;

        public WatchFolderService(ISettingsService settings, ArchiveWorker worker)
        {
            _settings = settings;
            _worker = worker;
        }

        public bool IsRunning { get { lock (_sync) return _watcher != null; } }
        public string Folder { get { lock (_sync) return _watcher?.Path; } }

        // 设置保存后或启动时调用，按当前配置重建监听
        public void Apply()
        {
            var s = _settings.Current;
            var folder = s.WatchFolder;
            if (!s.WatchEnabled || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Stop();
                return;
            }
            lock (_sync)
            {
                if (_watcher != null && string.Equals(_watcher.Path, folder, StringComparison.OrdinalIgnoreCase)) return;
            }
            Stop();
            lock (_sync)
            {
                _cts = new CancellationTokenSource();
                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _watcher.Created += (s2, e) => HandlePath(e.FullPath);
                _watcher.Renamed += (s2, e) => HandlePath(e.FullPath);
            }
        }

        public void Stop()
        {
            FileSystemWatcher w;
            CancellationTokenSource cts;
            lock (_sync)
            {
                w = _watcher; cts = _cts;
                _watcher = null; _cts = null;
            }
            if (w != null) { w.EnableRaisingEvents = false; w.Dispose(); }
            cts?.Cancel();
            cts?.Dispose();
        }

        void HandlePath(string path)
        {
            if (!ShouldHandle(path)) return;
            CancellationToken ct;
            lock (_sync)
            {
                if (_watcher == null || !_inFlight.Add(path)) return;
                _cts ??= new CancellationTokenSource();
                ct = _cts.Token;
            }
            _ = PumpAsync(path, ct);
        }

        async Task PumpAsync(string path, CancellationToken ct)
        {
            try
            {
                if (await WaitUntilStableAsync(path, ct).ConfigureAwait(false))
                    _worker.EnqueueExtract(new[] { path });
            }
            catch { /* 监听目录里的异常不能拖垮主程序 */ }
            finally
            {
                lock (_sync) _inFlight.Remove(path);
            }
        }

        public static bool ShouldHandle(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var name = Path.GetFileName(path);
            if (name.Length == 0) return false;
            if (IsPartialDownloadName(name)) return false;
            return ArchiveInspector.CanExtractByExtension(path);
        }

        // 浏览器写盘时的中间名: .crdownload / .part / xxx.zip.tmp 等
        public static bool IsPartialDownloadName(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("~", StringComparison.Ordinal)) return true;
            foreach (var s in PartialSuffixes)
                if (lower.EndsWith(s, StringComparison.Ordinal)) return true;
            // .zip.001 之类分卷的中间态仍带 .part，其余交给稳定判定
            return lower.EndsWith(".download.zip", StringComparison.Ordinal);
        }

        // 大小连续两次不变 + 能独占打开 = 写完。超时返回 false，宁可漏一次也不解半截包。
        public static async Task<bool> WaitUntilStableAsync(string path, CancellationToken ct,
            int pollMs = 300, int stableRounds = 2, int timeoutMs = 60000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            long last = -1;
            int stable = 0;
            while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                if (!File.Exists(path))
                {
                    // 下载完成后常被改名换扩展，短暂不存在就放弃这一条
                    if (last == -2) return false;
                    last = -2;
                }
                else
                {
                    long len;
                    try { len = new FileInfo(path).Length; }
                    catch (IOException) { len = last; }
                    catch (UnauthorizedAccessException) { len = last; }

                    if (len == last && IsFreeToRead(path))
                    {
                        stable++;
                        if (stable >= stableRounds) return true;
                    }
                    else
                    {
                        stable = 0;
                        last = len;
                    }
                }
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
            }
            return false;
        }

        static bool IsFreeToRead(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public void Dispose() => Stop();
    }
}
