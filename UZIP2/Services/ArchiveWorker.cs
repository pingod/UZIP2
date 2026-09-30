using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Data;
using UZIP2.Models;

namespace UZIP2.Services
{
    // 任务队列 worker：解压/压缩各一条独立通道、按设置的并行度并发跑，
    // 完整复刻旧版 ExtractProcessAsync / CompressProcessAsync 的行为语义
    // （无密码优先、外部密码、文件名密码+提纯、密码本按成功次数、密码纸消耗、
    //  分卷纠正、过滤、智能建目录、多级解压、删源）。
    // 并发安全: 每个作业写私有 temp 目录，只有落到共享输出目录的尾段按目录加锁。
    public sealed class ArchiveWorker
    {
        const int MultiLevelDepthLimit = 8;
        const int MaxParallelism = 8;

        private readonly SevenZipClient _zip;
        private readonly PasswordService _passwords;
        private readonly ISettingsService _settings;
        private readonly IFileLogger _logger;
        private readonly CompressLogService _compressLog;

        private readonly Channel<JobEntry> _extractChannel = Channel.CreateUnbounded<JobEntry>();
        private readonly Channel<JobEntry> _compressChannel = Channel.CreateUnbounded<JobEntry>();
        private readonly Dictionary<string, SemaphoreSlim> _dirGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<JobEntry> _jobs = new ObservableCollection<JobEntry>();
        private readonly ReadOnlyObservableCollection<JobEntry> _jobsView;
        private readonly Dictionary<long, CancellationTokenSource> _runners = new Dictionary<long, CancellationTokenSource>();
        private readonly HashSet<string> _handledTargets = new HashSet<string>();
        private readonly object _sync = new object();
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private long _seq;
        private int _active;
        private int _pending;
        private int _runExtract;
        private int _runCompress;
        private int _extractSlots;
        private int _compressSlots;

        // 运行期观测到的最大同时在跑作业数，用于验证并行设置真的生效
        public int PeakActive { get; private set; }

        public event Action<JobEntry> JobFinished;

        public ReadOnlyObservableCollection<JobEntry> Jobs => _jobsView;

        public int MaxConcurrentExtract { get; }
        public int MaxConcurrentCompress { get; }

        public ArchiveWorker(SevenZipClient zip, PasswordService passwords,
            ISettingsService settings, IFileLogger logger = null, CompressLogService compressLog = null)
        {
            _zip = zip;
            _passwords = passwords;
            _settings = settings;
            _logger = logger;
            _compressLog = compressLog ?? new CompressLogService(settings.ConfigDirectory);
            _jobsView = new ReadOnlyObservableCollection<JobEntry>(_jobs);
            _dispatcher = System.Windows.Application.Current?.Dispatcher;
            try { BindingOperations.EnableCollectionSynchronization(_jobs, _sync); } catch { }

            var s = settings.Current;
            MaxConcurrentExtract = Clamp(s.ParallelExtract);
            MaxConcurrentCompress = Clamp(s.ParallelCompress);
            _extractSlots = MaxConcurrentExtract;
            _compressSlots = MaxConcurrentCompress;
            settings.Changed += OnSettingsChanged;

            Task.Run(() => DispatchLoopAsync(_extractChannel, isExtract: true));
            Task.Run(() => DispatchLoopAsync(_compressChannel, isExtract: false));
        }

        static int Clamp(int v) => v < 1 ? 1 : (v > MaxParallelism ? MaxParallelism : v);

        // 并行度改完立即生效: 放大立刻可用，缩小只限制新作业，不打断正在跑的
        void OnSettingsChanged(AppSettings s)
        {
            if (s == null) return;
            Volatile.Write(ref _extractSlots, Clamp(s.ParallelExtract));
            Volatile.Write(ref _compressSlots, Clamp(s.ParallelCompress));
        }

        // ---------- 入队 ----------

        public void EnqueueExtract(IReadOnlyList<string> archives, string outputDir = null,
            List<string> onlyEntries = null)
        {
            foreach (var a in archives)
                AddJob(new JobEntry { Kind = "Extract", Archive = a, Target = outputDir, SelectedEntries = onlyEntries });
        }

        public void EnqueueCompress(IReadOnlyList<string> files, string outDir = null)
        {
            if (files == null || files.Count == 0) return;
            var s = _settings.Current;
            if (s.CompressAlone)
            {
                foreach (var f in files)
                    AddJob(new JobEntry { Kind = "Compress", Archive = f, Target = outDir });
            }
            else
            {
                AddJob(new JobEntry { Kind = "Compress", Archive = files[0], Target = outDir, Sources = files.ToList() });
            }
        }

        void AddJob(JobEntry job)
        {
            lock (_sync)
            {
                job.Id = ++_seq;
                job.Status = JobStatus.Queued;
            }
            // 绑定到 UI 后这个集合只能在调度线程改（监听目录、多级解压都从后台线程入队）
            if (_dispatcher == null || _dispatcher.CheckAccess())
            {
                lock (_sync) _jobs.Add(job);
            }
            else
            {
                _dispatcher.Invoke(() => { lock (_sync) _jobs.Add(job); });
            }
            Enqueue(job);
        }

        // _pending 覆盖"已入队但尚未收尾"的全周期，包含还没被派发出去的那段，
        // 否则 WhenIdleAsync 会在"已出队、还在等并行名额"的窗口里误判为空闲。
        void Enqueue(JobEntry job)
        {
            lock (_sync) _pending++;
            ChannelFor(job).Writer.TryWrite(job);
        }

        Channel<JobEntry> ChannelFor(JobEntry job) => job.Kind == "Extract" ? _extractChannel : _compressChannel;

        // ---------- 取消 / 重试 ----------

        public void Cancel(JobEntry job)
        {
            if (job.Status == JobStatus.Queued)
            {
                job.CancelRequested = true;
                return;
            }
            lock (_sync)
                if (_runners.TryGetValue(job.Id, out var cts)) cts.Cancel();
        }

        public void CancelAll()
        {
            lock (_sync)
            {
                foreach (var job in _jobs)
                    if (job.Status == JobStatus.Queued) job.CancelRequested = true;
                foreach (var cts in _runners.Values) cts.Cancel();
            }
        }

        public void Retry(JobEntry job, string manualPassword = null)
        {
            if (job.Status == JobStatus.Queued || job.Status == JobStatus.Running) return;
            job.ManualPassword = manualPassword;
            job.Percent = null;
            job.CurrentFile = null;
            job.Diagnosis = null;
            job.Status = JobStatus.Queued;
            Enqueue(job);
        }

        public int RetryAllFailed(string manualPassword = null)
        {
            List<JobEntry> failed;
            lock (_sync) failed = _jobs.Where(j => j.Status == JobStatus.Failed).ToList();
            foreach (var job in failed) Retry(job, manualPassword);
            return failed.Count;
        }

        // ---------- 预览 ----------

        // 清单读取不读数据，按解压同款密码链试一遍也很便宜
        public async Task<ArchiveListing> PreviewAsync(string archive, string password = null, CancellationToken ct = default)
        {
            var first = await _zip.ListEntriesAsync(archive, password, ct).ConfigureAwait(false);
            if (first.Success || first.Error != SevenZipError.WrongPassword) return first;

            var s = _settings.Current;
            var candidates = new List<string>();
            candidates.AddRange(_passwords.ExternalPasswords());
            candidates.AddRange(_passwords.Book.OrderByDescending(e => e.SuccessCount).Select(e => e.Text));
            candidates.AddRange(_passwords.Paper.Select(e => e.Text));
            if (s.NameToPassword && !string.IsNullOrEmpty(s.NameFilter))
                candidates.Add(PasswordFromNameService.SplitString(Path.GetFileNameWithoutExtension(archive), s.NameFilter));

            foreach (var c in Dedup(candidates))
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(c)) continue;
                var r = await _zip.ListEntriesAsync(archive, c, ct).ConfigureAwait(false);
                if (r.Success) return r;
            }
            return first;
        }

        // 测试/命令行模式等待整批完成
        public async Task WhenIdleAsync(CancellationToken ct = default)
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                bool idle;
                lock (_sync) idle = IsIdleLocked();
                if (idle) return;
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }

        // 调用方必须持有 _sync
        bool IsIdleLocked()
            => _active == 0 && _pending == 0 && _extractChannel.Reader.Count == 0 && _compressChannel.Reader.Count == 0;

        // ---------- 消费循环 ----------

        private async Task DispatchLoopAsync(Channel<JobEntry> channel, bool isExtract)
        {
            await foreach (var job in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (job.CancelRequested)
                {
                    lock (_sync) _pending--;
                    job.Status = JobStatus.Cancelled;
                    FireFinished(job);
                    continue;
                }
                await AwaitSlotAsync(isExtract).ConfigureAwait(false);
                var cts = new CancellationTokenSource();
                lock (_sync)
                {
                    _runners[job.Id] = cts;
                    _active++;
                    if (isExtract) _runExtract++; else _runCompress++;
                    if (_active > PeakActive) PeakActive = _active;
                    job.Status = JobStatus.Running;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (isExtract) await RunExtractAsync(job, cts.Token).ConfigureAwait(false);
                        else await RunCompressAsync(job, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        job.Status = JobStatus.Cancelled;
                        job.Diagnosis = "任务已取消";
                    }
                    catch (Exception ex)
                    {
                        job.Status = JobStatus.Failed;
                        job.Diagnosis = ex.Message;
                        _logger?.Error("作业异常 " + job.Archive, ex);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _runners.Remove(job.Id);
                            _active--;
                            _pending--;
                            if (isExtract) _runExtract--; else _runCompress--;
                            // 整批完成后清掉分卷去重表，下一批可重新处理同名档案
                            if (IsIdleLocked())
                            {
                                _handledTargets.Clear();
                                _dirGates.Clear();
                            }
                        }
                        cts.Dispose();
                        FireFinished(job);
                    }
                });
            }
        }

        // 单条通道只有一个派发者，因此这里只需等自己的名额被释放
        private async Task AwaitSlotAsync(bool isExtract)
        {
            while (true)
            {
                int running = isExtract ? Volatile.Read(ref _runExtract) : Volatile.Read(ref _runCompress);
                int target = isExtract ? Volatile.Read(ref _extractSlots) : Volatile.Read(ref _compressSlots);
                if (running < target) return;
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        void FireFinished(JobEntry job)
        {
            try { JobFinished?.Invoke(job); } catch { }
        }

        SemaphoreSlim DirGate(string dir)
        {
            var key = (dir ?? "").TrimEnd('\\').ToLowerInvariant();
            lock (_sync)
            {
                SemaphoreSlim g;
                if (!_dirGates.TryGetValue(key, out g)) _dirGates[key] = g = new SemaphoreSlim(1, 1);
                return g;
            }
        }

        // ---------- 解压 ----------

        private async Task RunExtractAsync(JobEntry job, CancellationToken ct)
        {
            var s = _settings.Current;
            string outDir = ResolveExtractOutDir(s, job.Archive, job.Target);
            string temp = TempManager.CreateSessionTemp(outDir, job.Archive, job.Id.ToString());
            var info = ArchiveInspector.Inspect(job.Archive);
            string f = job.Archive;
            bool isVolume = info.Volume.IsVolume;
            if (isVolume && File.Exists(info.Volume.MainVolumePath)) f = info.Volume.MainVolumePath;

            // 分卷去重: 同批中主卷已被处理则静默完成(并发下必须原子判重)
            bool claimed;
            lock (_sync) claimed = _handledTargets.Add(f);
            if (isVolume && !claimed)
            {
                TryDeleteTemp(temp);
                job.Status = JobStatus.Success;
                job.Diagnosis = "已随分卷主文件处理";
                return;
            }

            // 格式门槛（旧: 扩展名不可解 && 未开解压未知 && 非分卷 → 跳过）
            if (!ArchiveInspector.CanExtractByExtension(job.Archive) && !s.ExtractUnknow && !isVolume)
            {
                TryDeleteTemp(temp);
                job.Status = JobStatus.Failed;
                job.Diagnosis = "不支持的格式";
                return;
            }

            string fname = Path.GetFileNameWithoutExtension(job.Archive);

            // ---- 密码尝试链: 无密码 → 人工指定 → 外部 → 文件名 → 密码本(次数降序) → 密码纸 ----
            string usedPassword = null;
            bool fromPaper = false;
            string lastTestOutput = null;

            // 清单已确认未加密的包直接空密码解压，省掉一整遍全量读盘
            var enc = await _zip.ProbeEncryptionAsync(f, ct).ConfigureAwait(false);
            bool ok = enc == EncryptionState.NotEncrypted;
            if (!ok)
            {
                var ok0 = await _zip.TestAsync(f, null, ct).ConfigureAwait(false);
                ok = ok0.Success;
                if (!ok) lastTestOutput = ok0.Output;
            }

            if (!ok && !string.IsNullOrEmpty(job.ManualPassword))
            {
                ok = (await _zip.TestAsync(f, job.ManualPassword, ct).ConfigureAwait(false)).Success;
                if (ok) usedPassword = job.ManualPassword;
            }

            foreach (var pw in Dedup(_passwords.ExternalPasswords()))
            {
                if (ct.IsCancellationRequested) break;
                if (ok) break;
                if ((await _zip.TestAsync(f, pw, ct).ConfigureAwait(false)).Success)
                {
                    usedPassword = pw;
                    ok = true;
                }
            }

            if (!ok && s.NameToPassword && !string.IsNullOrEmpty(s.NameFilter) && !isVolume)
            {
                string np = PasswordFromNameService.SplitString(fname, s.NameFilter);
                if (np != null && (await _zip.TestAsync(f, np, ct).ConfigureAwait(false)).Success)
                {
                    usedPassword = np;
                    ok = true;
                    fname = PasswordFromNameService.PurifyName(fname, s.NameFilter, np);
                }
            }

            if (!ok)
            {
                foreach (var e in _passwords.Book.OrderByDescending(e => e.SuccessCount))
                {
                    if (ct.IsCancellationRequested) break;
                    var t = e.Text;
                    if (string.IsNullOrEmpty(t)) continue;
                    if ((await _zip.TestAsync(f, t, ct).ConfigureAwait(false)).Success)
                    {
                        usedPassword = t;
                        ok = true;
                        break;
                    }
                }
            }

            if (!ok)
            {
                foreach (var e in _passwords.Paper)
                {
                    if (ct.IsCancellationRequested) break;
                    var t = e.Text;
                    if (string.IsNullOrEmpty(t)) continue;
                    if ((await _zip.TestAsync(f, t, ct).ConfigureAwait(false)).Success)
                    {
                        usedPassword = t;
                        fromPaper = true;
                        ok = true;
                        break;
                    }
                }
            }

            if (!ok)
            {
                TryDeleteTemp(temp);
                job.Status = ct.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Failed;
                job.Diagnosis = ct.IsCancellationRequested
                    ? "任务已取消"
                    : SevenZipClient.Classify(lastTestOutput ?? "", 1, false) == SevenZipError.WrongPassword
                        ? "需要密码，但密码本/密码纸中未找到正确密码"
                        : "密码本/密码纸中未找到正确密码";
                return;
            }

            // ---- 正式解压到临时目录 ----
            var progress = new Progress<SevenZipProgress>(p =>
            {
                job.Percent = p.Percent;
                if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                if (p.DoneCount > 0) job.Done = p.DoneCount;
            });

            var res = await _zip.ExtractAsync(f, temp.TrimEnd('\\'), usedPassword, progress, ct,
                s.ExtractCoverMode, job.SelectedEntries).ConfigureAwait(false);
            job.Percent = res.Success ? 100 : job.Percent;
            if (res.Success) job.Done = job.Total;

            if (!res.Success)
            {
                if (res.Error == SevenZipError.Cancelled || ct.IsCancellationRequested)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Diagnosis = "任务已取消";
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.Diagnosis = res.Diagnosis ?? "解压失败";
                }
                return;
            }

            // ---- 成功后处理 ----
            if (usedPassword != null)
            {
                _passwords.ReportResult(usedPassword, true);
                if (fromPaper) _passwords.ConsumePaper(usedPassword);
            }
            job.UsedPassword = usedPassword;

            // 文件过滤(旧版为硬删除)
            if (FilterService.ParseRules(s.ExtractFilter) != null)
                FilterService.Apply(temp, s.ExtractFilter);

            // 多级解压: 临时目录第一层中仍是压缩包的文件，移动到新位置后继续解压
            var nestedNames = Directory.GetFiles(temp, "*", SearchOption.TopDirectoryOnly)
                .Where(x => ArchiveInspector.CanExtractByExtension(x))
                .Select(Path.GetFileName)
                .ToList();

            // 智能建目录(移植旧 CreateNewFolder/CreateNameFolder 三分支)
            // 并发作业只在"落到共享输出目录"这一段按目录串行，避免同名目录判定与互相覆盖打架
            string dest = outDir;
            var tail = DirGate(outDir);
            await tail.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var di = new DirectoryInfo(temp);
                if (s.CreateNewFolder || s.CreateNameFolder)
                {
                    if (di.GetDirectories().Length + di.GetFiles().Length <= 1 && !s.CreateNameFolder)
                    {
                        FilterService.MoveFolder(temp, outDir, s.ExtractCoverMode);
                    }
                    else
                    {
                        string newPath = Path.Combine(outDir, fname);
                        string finalPath = newPath + Path.DirectorySeparatorChar;
                        int n = 1;
                        while (Directory.Exists(finalPath))
                        {
                            finalPath = newPath + "-New" + n + Path.DirectorySeparatorChar;
                            n++;
                        }
                        Directory.CreateDirectory(finalPath);
                        dest = finalPath;
                        FilterService.MoveFolder(temp, finalPath, null);
                    }
                }
                else
                {
                    FilterService.MoveFolder(temp, outDir, s.ExtractCoverMode);
                }
            }
            finally { tail.Release(); }
            TryDeleteTemp(temp);
            job.OutputDir = dest;

            // 删除原文件(分卷删整组)
            if (s.DeleteFinishFile && File.Exists(f))
            {
                if (isVolume) ArchiveInspector.DeleteVolumeSet(info.Volume, s.DeleteToRecycle);
                else FilterService.Delete(f, s.DeleteToRecycle);
            }

            job.Status = JobStatus.Success;

            if (nestedNames.Count > 0 && job.Depth < MultiLevelDepthLimit)
            {
                foreach (var name in nestedNames.Distinct())
                {
                    var p = Path.Combine(dest, name);
                    if (File.Exists(p))
                        AddJob(new JobEntry { Kind = "Extract", Archive = p, Target = job.Target, Depth = job.Depth + 1 });
                }
            }
        }

        static string ResolveExtractOutDir(AppSettings s, string archivePath, string explicitOverride)
        {
            if (!string.IsNullOrWhiteSpace(explicitOverride))
                return TempFix(explicitOverride);
            string archiveDir = Path.GetDirectoryName(archivePath);
            switch (s.ExtractOutMode)
            {
                case 1: return TempFix(archiveDir);                       // File: 档案所在目录
                case 3: return TempFix(Pick(s.LastExtractPath, archiveDir)); // Browse: 上次浏览目录
                case 0: return TempFix(Pick(s.LastExtractPath, archiveDir)); // Last: 上次输出目录
                default:
                    int idx = s.ExtractOutMode - 5;                        // Customize1-8 = 5..12
                    if (idx >= 0 && idx < s.CustomizeFolders.Count && Directory.Exists(s.CustomizeFolders[idx].Path))
                        return TempFix(s.CustomizeFolders[idx].Path);
                    return TempFix(archiveDir);
            }
        }

        static string Pick(string preferred, string fallback)
            => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

        static string TempFix(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir.EndsWith("\\") ? dir : dir + "\\";
        }

        static void TryDeleteTemp(string temp)
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp.TrimEnd('\\'), true); } catch { }
        }

        // ---------- 压缩 ----------

        private async Task RunCompressAsync(JobEntry job, CancellationToken ct)
        {
            var s = _settings.Current;
            var sources = job.Sources ?? new List<string> { job.Archive };

            string outDir = ResolveCompressOutDir(s, sources[0], job.Target);
            string password = PickCompressPassword(s, out int randomLen);
            if (randomLen > 0) password = PasswordGenerator.New(randomLen);

            string pwSign = null;
            if (s.PasswordToName && password != null)
                pwSign = string.IsNullOrEmpty(s.NameFilter2) ? " " : s.NameFilter2;

            string outArchive = BuildArchivePath(s, sources, outDir, pwSign, password);

            var progress = new Progress<SevenZipProgress>(p =>
            {
                job.Percent = p.Percent;
                if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                if (p.DoneCount > 0) job.Done = p.DoneCount;
            });

            var res = await _zip.CompressAsync(sources, outArchive, password, s.CompressType, s.CompressLevel,
                s.HideZipContent, progress, ct, FilterService.ParseRules(s.CompressFilter)).ConfigureAwait(false);

            if (!res.Success)
            {
                job.Status = res.Error == SevenZipError.Cancelled || ct.IsCancellationRequested
                    ? JobStatus.Cancelled : JobStatus.Failed;
                job.Diagnosis = job.Status == JobStatus.Cancelled ? "任务已取消" : res.Diagnosis ?? "压缩失败";
                return;
            }

            if (s.DeleteCompressFinish)
                foreach (var src in sources)
                    FilterService.Delete(src, s.DeleteToRecycle);

            _compressLog.Log(outArchive, password);
            job.Percent = 100;
            job.Done = job.Total;
            job.UsedPassword = password;
            job.OutputDir = outArchive;
            job.Status = JobStatus.Success;
        }

        static string ResolveCompressOutDir(AppSettings s, string firstSource, string explicitOverride)
        {
            if (!string.IsNullOrWhiteSpace(explicitOverride))
                return TempFix(explicitOverride);
            string srcDir = Path.GetDirectoryName(firstSource);
            if (s.CompressOutMode == 1) return TempFix(srcDir); // File
            return TempFix(Pick(s.LastCompressPath, srcDir));   // Browse/Last
        }

        static string PickCompressPassword(AppSettings s, out int randomLength)
        {
            randomLength = 0;
            switch (s.PasswordMode)
            {
                case 2: return NullIfEmpty(s.CustomPasswords, 0);
                case 3: return NullIfEmpty(s.CustomPasswords, 1);
                case 4: return NullIfEmpty(s.CustomPasswords, 2);
                case 6: randomLength = 8; return null;
                case 7: randomLength = 16; return null;
                case 8: randomLength = 32; return null;
                default: return null;
            }
        }

        static string NullIfEmpty(List<string> list, int i)
            => list != null && i < list.Count && !string.IsNullOrEmpty(list[i]) ? list[i] : null;

        // 输出档案命名(移植 UCmd.CompressFile 命名循环: 去 -New 尾巴、-NewN 避让、密码写文件名)
        static string BuildArchivePath(AppSettings s, List<string> sources, string outDir, string pwSign, string password)
        {
            string baseName;
            bool combined = sources.Count > 1;
            if (combined)
            {
                baseName = Path.GetFileNameWithoutExtension(Path.GetDirectoryName(sources[0]));
                if (string.IsNullOrEmpty(baseName)) baseName = "NewArchive";
            }
            else
            {
                baseName = Path.GetFileNameWithoutExtension(sources[0]);
                int n = baseName.IndexOf("-New", StringComparison.Ordinal);
                if (n > 0) baseName = baseName.Remove(n);
            }

            string sign = pwSign != null && password != null ? pwSign + password : "";
            string ext = ArchiveExtension(s.CompressType);

            int num = 0;
            string path;
            do
            {
                path = outDir + baseName + (num == 0 ? "" : "-New" + num) + sign + ext;
                num++;
            } while (File.Exists(path));
            return path;
        }

        static string ArchiveExtension(int compressType)
        {
            switch (compressType)
            {
                case 0: return ".zip";
                case 1: return ".7z";
                case 2: return ".bz2";
                case 3: return ".gz";
                case 4: return ".tar";
                case 5: return ".wim";
                case 6: return ".xz";
                default: return ".zip";
            }
        }

        static IEnumerable<string> Dedup(IEnumerable<string> src)
        {
            var seen = new HashSet<string>();
            foreach (var x in src)
                if (!string.IsNullOrEmpty(x) && seen.Add(x))
                    yield return x;
        }
    }
}
