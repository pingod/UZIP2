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
    // 队列的入队面：历史页/监听目录/CLI 只需要往队列里丢作业，
    // 抽成接口后重跑逻辑可以脱离真实 7z 单测。
    public interface IJobQueue
    {
        void EnqueueExtract(IReadOnlyList<string> archives, string outputDir = null,
            List<string> onlyEntries = null, bool flat = false, string manualPassword = null);
        void EnqueueCompress(IReadOnlyList<string> files, string outDir = null, string manualPassword = null);
    }

    // 任务队列 worker：解压/压缩各一条独立通道、按设置的并行度并发跑，
    // 完整复刻旧版 ExtractProcessAsync / CompressProcessAsync 的行为语义
    // （无密码优先、外部密码、文件名密码+提纯、密码本按成功次数、密码纸消耗、
    //  分卷纠正、过滤、智能建目录、多级解压、删源）。
    // 并发安全: 每个作业写私有 temp 目录，只有落到共享输出目录的尾段按目录加锁。
    public sealed class ArchiveWorker : IJobQueue
    {
        const int MultiLevelDepthLimit = 8;
        const int MaxParallelism = 8;

        private readonly IArchiveEngine _zip;
        private readonly PasswordService _passwords;
        private readonly ISettingsService _settings;
        private readonly IFileLogger _logger;
        private readonly CompressLogService _compressLog;
        private readonly IHistoryService _history;

        private readonly Channel<JobEntry> _extractChannel = Channel.CreateUnbounded<JobEntry>();
        private readonly Channel<JobEntry> _compressChannel = Channel.CreateUnbounded<JobEntry>();
        private readonly Dictionary<string, SemaphoreSlim> _dirGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<JobEntry> _jobs = new ObservableCollection<JobEntry>();
        private readonly ReadOnlyObservableCollection<JobEntry> _jobsView;
        private readonly Dictionary<long, CancellationTokenSource> _runners = new Dictionary<long, CancellationTokenSource>();
        // 主卷 -> 真实结果。同批的其余分卷等这个 TaskCompletionSource，不再凭空报成功。
        private readonly Dictionary<string, TaskCompletionSource<(JobStatus, string)>> _volumeClaims =
            new Dictionary<string, TaskCompletionSource<(JobStatus, string)>>(StringComparer.OrdinalIgnoreCase);
        private readonly SlotPool _extractGate;
        private readonly SlotPool _compressGate;
        private readonly object _sync = new object();
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private long _seq;
        private int _active;
        private int _pending;

        // 运行期观测到的最大同时在跑作业数，用于验证并行设置真的生效
        public int PeakActive { get; private set; }

        public event Action<JobEntry> JobFinished;

        public ReadOnlyObservableCollection<JobEntry> Jobs => _jobsView;

        public int MaxConcurrentExtract => _extractGate.Limit;
        public int MaxConcurrentCompress => _compressGate.Limit;

        /// <summary>队列已空：没有排队中的作业，也没有运行中的作业。一批结束的判断用它。</summary>
        public bool IsIdle { get { lock (_sync) return IsIdleLocked(); } }

        // dispatcher 注入接缝：单测要能复现"调度线程从不泵消息"的 headless 处境（见 IArchiveEngine 同类接缝）。
        public ArchiveWorker(IArchiveEngine zip, PasswordService passwords,
            ISettingsService settings, IFileLogger logger = null, CompressLogService compressLog = null,
            IHistoryService history = null,
            System.Windows.Threading.Dispatcher dispatcher = null)
        {
            _zip = zip;
            _passwords = passwords;
            _settings = settings;
            _logger = logger;
            _compressLog = compressLog ?? new CompressLogService(settings.ConfigDirectory, settings);
            _history = history;
            _jobsView = new ReadOnlyObservableCollection<JobEntry>(_jobs);
            _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher;
            try { BindingOperations.EnableCollectionSynchronization(_jobs, _sync); } catch { }

            var s = settings.Current;
            _extractGate = new SlotPool(Clamp(s.ParallelExtract));
            _compressGate = new SlotPool(Clamp(s.ParallelCompress));
            settings.Changed += OnSettingsChanged;

            Task.Run(() => DispatchLoopAsync(_extractChannel, isExtract: true));
            Task.Run(() => DispatchLoopAsync(_compressChannel, isExtract: false));
        }

        static int Clamp(int v) => v < 1 ? 1 : (v > MaxParallelism ? MaxParallelism : v);

        // 并行度改完立即生效: 放大立刻放行等待中的作业，缩小只限制新作业，不打断正在跑的
        void OnSettingsChanged(AppSettings s)
        {
            if (s == null) return;
            _extractGate.SetLimit(Clamp(s.ParallelExtract));
            _compressGate.SetLimit(Clamp(s.ParallelCompress));
        }

        // ---------- 入队 ----------

        public void EnqueueExtract(IReadOnlyList<string> archives, string outputDir = null,
            List<string> onlyEntries = null, bool flat = false, string manualPassword = null)
        {
            foreach (var a in archives)
                AddJob(new JobEntry
                {
                    Kind = "Extract",
                    Archive = a,
                    Target = outputDir,
                    SelectedEntries = onlyEntries,
                    Flat = flat,
                    ManualPassword = manualPassword
                });
        }

        public void EnqueueCompress(IReadOnlyList<string> files, string outDir = null, string manualPassword = null)
        {
            if (files == null || files.Count == 0) return;
            var s = _settings.Current;
            if (s.CompressAlone)
            {
                foreach (var f in files)
                    AddJob(new JobEntry { Kind = "Compress", Archive = f, Target = outDir, ManualPassword = manualPassword });
            }
            else
            {
                AddJob(new JobEntry
                {
                    Kind = "Compress",
                    Archive = files[0],
                    Target = outDir,
                    Sources = files.ToList(),
                    ManualPassword = manualPassword
                });
            }
        }

        // 格式互转: 一个包一条作业，目标格式下标见 ArchiveFormat。
        // 口令走 EnqueueCompress 同样的"人工指定"通道：转换加密包时必须带上原口令，
        // 否则解不开源包，也就无从判断产物该不该继续加密。
        // 返回作业对象本身：headless（CLI）拿不到 Jobs 集合——它只能往调度线程里塞，
        // 而 CLI 正把那个线程堵着。
        public IReadOnlyList<JobEntry> EnqueueConvert(IReadOnlyList<string> archives, int type,
            string outDir = null, string manualPassword = null)
        {
            var added = new List<JobEntry>();
            foreach (var a in archives ?? Array.Empty<string>())
            {
                var job = new JobEntry
                {
                    Kind = "Convert", Archive = a, Target = outDir, ConvertType = type, ManualPassword = manualPassword
                };
                AddJob(job);
                added.Add(job);
            }
            return added;
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
                // 异步派发：多级解压的后续入队在解压线程上，用 Invoke 会把 worker 挂进 UI 队列，
                // UI 一忙整批作业跟着停摆。
                _dispatcher.BeginInvoke(() => { lock (_sync) _jobs.Add(job); });
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
            // 状态判定和 cts 查找必须同锁：派发循环也是在锁里把 Queued 翻成 Running 的，
            // 分开读就会给"刚启动的一瞬"漏下 CancelRequested 标记而永远取消不掉。
            lock (_sync)
            {
                if (job.Status == JobStatus.Queued)
                {
                    job.CancelRequested = true;
                    return;
                }
                if (_runners.TryGetValue(job.Id, out var cts)) cts.Cancel();
            }
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
            // 同 Cancel: 判定与复位必须原子，否则会在"刚终态但尚未收尾"的窗口里重复入队
            lock (_sync)
            {
                if (job.Status == JobStatus.Queued || job.Status == JobStatus.Running) return;
                job.ManualPassword = manualPassword;
                job.Percent = null;
                job.CurrentFile = null;
                job.Diagnosis = null;
                job.Status = JobStatus.Queued;
                Enqueue(job);
            }
        }

        public int RetryAllFailed(string manualPassword = null)
        {
            List<JobEntry> failed;
            lock (_sync) failed = _jobs.Where(j => j.Status == JobStatus.Failed).ToList();
            foreach (var job in failed) Retry(job, manualPassword);
            return failed.Count;
        }

        // ---------- 移除 / 回收 ----------

        // 从列表移除匹配的终态作业（活动作业免疫），返回移除数量。
        public int RemoveTerminal(Func<JobEntry, bool> match)
        {
            if (_dispatcher == null || _dispatcher.CheckAccess()) return RemoveTerminalCore(match);
            return _dispatcher.Invoke(() => RemoveTerminalCore(match));
        }

        int RemoveTerminalCore(Func<JobEntry, bool> match)
        {
            List<JobEntry> doomed;
            lock (_sync)
            {
                doomed = _jobs.Where(j => j.IsTerminal && (match == null || match(j))).ToList();
                foreach (var j in doomed) _jobs.Remove(j);
            }
            return doomed.Count;
        }

        public bool Remove(JobEntry job) =>
            job != null && RemoveTerminal(j => ReferenceEquals(j, job)) > 0;

        // 列表瘦身：终态卡片超过 maxKeep 个时裁掉最旧的（_jobs 按加入顺序）
        public int TrimTerminal(int maxKeep)
        {
            if (_dispatcher == null || _dispatcher.CheckAccess()) return TrimTerminalCore(maxKeep);
            return _dispatcher.Invoke(() => TrimTerminalCore(maxKeep));
        }

        int TrimTerminalCore(int maxKeep)
        {
            int removed = 0;
            lock (_sync)
            {
                int terminal = _jobs.Count(j => j.IsTerminal);
                foreach (var j in _jobs.ToList())
                {
                    if (terminal <= maxKeep) break;
                    if (!j.IsTerminal) continue;
                    _jobs.Remove(j);
                    terminal--;
                    removed++;
                }
            }
            return removed;
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
                var gate = isExtract ? _extractGate : _compressGate;
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                // 二次检查：读取队列与占用名额之间有空窗，等待名额期间可能刚被"全部取消"标记。
                // 漏掉这一步，排队中的下一个作业会在取消生效后又跑起来。
                CancellationTokenSource cts = null;
                lock (_sync)
                {
                    if (job.CancelRequested)
                    {
                        _pending--;
                    }
                    else
                    {
                        cts = new CancellationTokenSource();
                        _runners[job.Id] = cts;
                        _active++;
                        if (_active > PeakActive) PeakActive = _active;
                        job.Status = JobStatus.Running;
                        job.StartedUtc = DateTime.UtcNow;
                    }
                }
                if (cts == null)
                {
                    gate.Release();
                    job.Status = JobStatus.Cancelled;
                    FireFinished(job);
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (job.Kind == "Convert") await RunConvertAsync(job, cts.Token).ConfigureAwait(false);
                        else if (isExtract) await RunExtractAsync(job, cts.Token).ConfigureAwait(false);
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
                            // 先公布真实结果，再判空闲：影子作业还挂在 _pending 上时不会误清表
                            PublishVolumeClaim(job);
                            _runners.Remove(job.Id);
                            _active--;
                            _pending--;
                            // 整批完成后清掉分卷去重表，下一批可重新处理同名档案
                            if (IsIdleLocked())
                            {
                                _volumeClaims.Clear();
                                _dirGates.Clear();
                            }
                        }
                        // 名额在锁外归还：唤醒等待者不该把 _sync 一起带上
                        if (isExtract) _extractGate.Release(); else _compressGate.Release();
                        cts.Dispose();
                        FireFinished(job);
                    }
                });
            }
        }

        void FireFinished(JobEntry job)
        {
            try { JobFinished?.Invoke(job); } catch { }
            try { _history?.Record(job); } catch { }   // 落持久化历史，失败绝不影响主流程
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

        // 认领主卷：true = 本作业是处理者，false = 已有处理者，结果等它的 Task
        bool ClaimVolume(string key, out Task<(JobStatus, string)> outcome)
        {
            lock (_sync)
            {
                TaskCompletionSource<(JobStatus, string)> tcs;
                if (_volumeClaims.TryGetValue(key, out tcs))
                {
                    outcome = tcs.Task;
                    return false;
                }
                tcs = new TaskCompletionSource<(JobStatus, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _volumeClaims[key] = tcs;
                outcome = tcs.Task;
                return true;
            }
        }

        // 调用方需持 _sync：把真实终态交给同批的分卷影子作业
        void PublishVolumeClaim(JobEntry job)
        {
            var key = job.VolumeClaimKey;
            if (key == null) return;
            job.VolumeClaimKey = null;
            TaskCompletionSource<(JobStatus, string)> tcs;
            if (_volumeClaims.TryGetValue(key, out tcs)) tcs.TrySetResult((job.Status, job.Diagnosis));
        }

        // 影子作业：等主卷的真实结果，不做二次解压。主卷失败我也失败，绝不报绿。
        async Task MirrorVolumeAsync(JobEntry job, Task<(JobStatus, string)> owner, CancellationToken ct)
        {
            var aborted = new TaskCompletionSource<bool>();
            using (ct.Register(() => aborted.TrySetResult(true)))
            {
                var first = await Task.WhenAny(owner, aborted.Task).ConfigureAwait(false);
                if (first == aborted.Task)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Diagnosis = "任务已取消";
                    return;
                }
            }

            var (status, diagnosis) = await owner.ConfigureAwait(false);
            job.Status = status;
            job.Diagnosis = status == JobStatus.Success ? "已随分卷主文件处理" : diagnosis;
        }

        // ---------- 解压 ----------

        // session temp 一旦建出来就必须被删：中途失败、取消、抛异常都算。
        // 漏一次就在用户的输出目录里留一个 UZipTemp_*，重启也只会清程序目录那批。
        private async Task RunExtractAsync(JobEntry job, CancellationToken ct)
        {
            var s = _settings.Current;
            string outDir = ResolveExtractOutDir(s, job.Archive, job.Target);
            string temp = TempManager.CreateSessionTemp(outDir, job.Archive, job.Id.ToString(), _settings.ConfigDirectory);
            try
            {
                await RunExtractCoreAsync(job, ct, s, outDir, temp).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteTemp(temp);
            }
        }

        private async Task RunExtractCoreAsync(JobEntry job, CancellationToken ct, AppSettings s,
            string outDir, string temp)
        {
            var info = ArchiveInspector.Inspect(job.Archive);
            string f = job.Archive;
            bool isVolume = info.Volume.IsVolume;
            if (isVolume && File.Exists(info.Volume.MainVolumePath)) f = info.Volume.MainVolumePath;

            // 分卷去重: 同批中主卷已被处理则静默完成(并发下必须原子判重)
            bool isOwner = true;
            Task<(JobStatus, string)> ownerOutcome = null;
            if (isVolume)
            {
                isOwner = ClaimVolume(f, out ownerOutcome);
                if (isOwner) job.VolumeClaimKey = f;
            }
            if (!isOwner)
            {
                await MirrorVolumeAsync(job, ownerOutcome, ct).ConfigureAwait(false);
                return;
            }

            // 格式门槛（旧: 扩展名不可解 && 未开解压未知 && 非分卷 → 跳过）
            if (!ArchiveInspector.CanExtractByExtension(job.Archive) && !s.ExtractUnknow && !isVolume)
            {
                job.Status = JobStatus.Failed;
                job.Diagnosis = "不支持的格式";
                return;
            }

            string fname = Path.GetFileNameWithoutExtension(job.Archive);

            // ---- 密码链: 候选口令直接拿去解，解进私有 temp，错了清空再试下一条 ----
            // 旧流程是"每条口令先 t 一遍全量校验、命中后再 x 一遍"，一个加密包至少读两遍盘；
            // 命中那条现在只读一遍，清单已确认加密的包连空口令那遍都省了。
            var enc = await _zip.ProbeEncryptionAsync(f, ct).ConfigureAwait(false);
            string namePw = s.NameToPassword && !string.IsNullOrEmpty(s.NameFilter) && !isVolume
                ? PasswordFromNameService.SplitString(fname, s.NameFilter)
                : null;
            var chain = BuildPasswordChain(enc == EncryptionState.Encrypted, job.ManualPassword,
                _passwords.ExternalPasswords(), namePw,
                _passwords.Book.OrderByDescending(e => e.SuccessCount).Select(e => e.Text),
                _passwords.Paper.Select(e => e.Text));

            var progress = new Progress<SevenZipProgress>(p =>
            {
                job.Percent = p.Percent;
                if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                if (p.DoneCount > 0) job.Done = p.DoneCount;
            });

            SevenZipResult res = null;
            string usedPassword = null;
            bool fromPaper = false;
            string firstFailureOutput = null;

            foreach (var c in chain)
            {
                if (ct.IsCancellationRequested) break;
                res = await _zip.ExtractAsync(f, temp.TrimEnd('\\'), c.Password, progress, ct,
                    s.ExtractCoverMode, job.SelectedEntries).ConfigureAwait(false);
                if (res.Success)
                {
                    usedPassword = c.Password;
                    fromPaper = c.FromPaper;
                    if (c.FromName) fname = PasswordFromNameService.PurifyName(fname, s.NameFilter, c.Password);
                    break;
                }
                if (firstFailureOutput == null) firstFailureOutput = res.Output;
                // 错口令解出来的半截文件绝不能跟着下一条的结果一起落进用户目录
                ClearTempContents(temp);
            }

            job.Percent = res != null && res.Success ? 100 : job.Percent;
            if (res != null && res.Success) job.Done = job.Total;

            if (res == null || !res.Success)
            {
                if (ct.IsCancellationRequested || (res != null && res.Error == SevenZipError.Cancelled))
                {
                    job.Status = JobStatus.Cancelled;
                    job.Diagnosis = "任务已取消";
                }
                else if (res != null && IsHardFailure(res.Error))
                {
                    // 磁盘满/路径过长这类硬伤不能被说成"密码不对"
                    job.Status = JobStatus.Failed;
                    job.Diagnosis = res.Diagnosis ?? "解压失败";
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.Diagnosis = DescribePasswordChainFailure(firstFailureOutput);
                }
                return;
            }

            // 口令全部试完仍失败时，第一条失败输出决定"缺密码"还是"包坏了"
            // ---- 成功后处理 ----
            if (usedPassword != null)
            {
                _passwords.ReportResult(usedPassword, true);
                if (fromPaper) _passwords.ConsumePaper(usedPassword);
            }
            job.UsedPassword = usedPassword;

            // 文件过滤：删掉的条目要跟着"删除到回收站"设置走，否则用户勾了回收站仍被硬删
            FilterService.Apply(temp, s.ExtractFilter, s.DeleteToRecycle);

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
                if (job.Flat)
                {
                    FilterService.MoveFolder(temp, outDir, s.ExtractCoverMode);
                }
                else if (s.CreateNewFolder || s.CreateNameFolder)
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
                        AddJob(new JobEntry { Kind = "Extract", Archive = p, Target = job.Target, Flat = job.Flat, Depth = job.Depth + 1 });
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

        // 7z 的 a 是边压边写：取消或中途失败会留下一个头部完整、数据不全的档案。
        // 留着它，用户下次双击只会得到"文件已损坏"，不如当场清掉。
        static void TryDeletePartialCompress(string outArchive, string volume)
        {
            if (volume == null)
            {
                TryDeleteFile(outArchive);
                return;
            }
            // 分卷产出 name.7z.001/.002…，按序删到第一个缺口
            for (int i = 1; i <= 999; i++)
            {
                string part = outArchive + "." + i.ToString("D3");
                if (!File.Exists(part)) break;
                TryDeleteFile(part);
            }
            TryDeleteFile(outArchive);
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // 密码链跑完仍失败: 空密码那次的输出才能区分"缺密码"和"包本身坏了"，
        // 后面带密码的 test 一律报 Data error，会把坏包说成密码不对。
        public static string DescribePasswordChainFailure(string emptyPasswordTestOutput)
        {
            switch (SevenZipClient.Classify(emptyPasswordTestOutput ?? "", 1, false))
            {
                case SevenZipError.WrongPassword: return "需要密码，但密码本/密码纸中未找到正确密码";
                case SevenZipError.Corrupt: return "文件已损坏（校验失败）";
                case SevenZipError.UnsupportedFormat: return "文件已损坏或不是可识别的压缩包";
                default: return "密码本/密码纸中未找到正确密码";
            }
        }

        // ---------- 压缩 ----------

        private async Task RunCompressAsync(JobEntry job, CancellationToken ct)
        {
            var s = _settings.Current;
            var sources = job.Sources ?? new List<string> { job.Archive };

            string outDir = ResolveCompressOutDir(s, sources[0], job.Target);
            // 历史页重跑会带上当时用的口令，否则重跑出来的是另一个（无口令的）包
            string password = job.ManualPassword;
            if (password == null)
            {
                password = PickCompressPassword(s, out int randomLen);
                if (randomLen > 0) password = PasswordGenerator.New(randomLen);
            }

            string pwSign = null;
            if (s.PasswordToName && password != null)
                pwSign = string.IsNullOrEmpty(s.NameFilter2) ? " " : s.NameFilter2;

            // 目录预设优先于全局设置；未命中的项按字段回落到全局
            var plan = CompressPresetResolver.Plan(s, CompressPresetResolver.Find(sources[0], s.CompressPresets));

            string outArchive = BuildArchivePath(plan.Type, sources, outDir, pwSign, password);

            string volume = null;
            if (!string.IsNullOrWhiteSpace(plan.Volume) && !VolumeSize.TryNormalize(plan.Volume, out volume))
            {
                job.Status = JobStatus.Failed;
                job.Diagnosis = $"分卷大小格式不正确: {plan.Volume}（示例: 700m / 1g / 102400）";
                return;
            }

            var progress = new Progress<SevenZipProgress>(p =>
            {
                job.Percent = p.Percent;
                if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                if (p.DoneCount > 0) job.Done = p.DoneCount;
            });

            var res = await _zip.CompressAsync(sources, outArchive, password, plan.Type, plan.Level,
                plan.EncryptHeaders, progress, ct, FilterService.ParseRules(s.CompressFilter), volume,
                s.CompressSolid, s.CompressThreads).ConfigureAwait(false);

            if (!res.Success)
            {
                job.Status = res.Error == SevenZipError.Cancelled || ct.IsCancellationRequested
                    ? JobStatus.Cancelled : JobStatus.Failed;
                job.Diagnosis = job.Status == JobStatus.Cancelled ? "任务已取消" : res.Diagnosis ?? "压缩失败";
                TryDeletePartialCompress(outArchive, volume);
                return;
            }

            // 分卷时主文件名不存在，第一卷才是真文件
            job.OutputDir = volume == null ? outArchive : outArchive + ".001";
            job.UsedPassword = password;

            // 自检必须在删源文件之前：坏包若等源没了才暴露，用户两头空
            if (s.VerifyAfterCompress)
            {
                var check = await _zip.TestAsync(job.OutputDir, password, ct).ConfigureAwait(false);
                if (!check.Success)
                {
                    job.Status = check.Error == SevenZipError.Cancelled || ct.IsCancellationRequested
                        ? JobStatus.Cancelled : JobStatus.Failed;
                    job.Diagnosis = job.Status == JobStatus.Cancelled
                        ? "任务已取消"
                        : "压缩产物校验未通过（源文件已保留）：" + (check.Diagnosis ?? "归档损坏");
                    return;
                }
            }

            if (s.DeleteCompressFinish)
                foreach (var src in sources)
                    FilterService.Delete(src, s.DeleteToRecycle);

            _compressLog.Log(outArchive, password);
            job.Percent = 100;
            job.Done = job.Total;
            job.Status = JobStatus.Success;
        }

        // 格式互转: 解进私有 temp，再按目标格式重打包。
        // 原包一律保留——重打包会丢包内元数据（注释、时间戳精度、固实布局），
        // 而"转换"最常见的失败恰是解不开源包，这时删了源就是双重损失。
        private async Task RunConvertAsync(JobEntry job, CancellationToken ct)
        {
            var s = _settings.Current;
            if (!ArchiveFormat.IsCreatable(job.ConvertType))
            {
                job.Status = JobStatus.Failed;
                job.Diagnosis = "不支持的目标格式（可用: zip / 7z / bz2 / gz / tar / wim / xz）";
                return;
            }
            if (!File.Exists(job.Archive))
            {
                job.Status = JobStatus.Failed;
                job.Diagnosis = "文件不存在";
                return;
            }

            string outDir = TempFix(job.Target ?? Path.GetDirectoryName(job.Archive));
            string outArchive = ArchiveFormat.TargetPath(job.Archive, outDir, job.ConvertType);
            string temp = TempManager.CreateSessionTemp(outDir, job.Archive, job.Id.ToString(), _settings.ConfigDirectory);
            try
            {
                var enc = await _zip.ProbeEncryptionAsync(job.Archive, ct).ConfigureAwait(false);
                string namePw = s.NameToPassword && !string.IsNullOrEmpty(s.NameFilter)
                    ? PasswordFromNameService.SplitString(Path.GetFileNameWithoutExtension(job.Archive), s.NameFilter)
                    : null;
                var chain = BuildPasswordChain(enc == EncryptionState.Encrypted, job.ManualPassword,
                    _passwords.ExternalPasswords(), namePw,
                    _passwords.Book.OrderByDescending(e => e.SuccessCount).Select(e => e.Text),
                    _passwords.Paper.Select(e => e.Text));

                // 转换是"解 + 压"两段流水线，百分比各占一半，否则进度条会瞬跳 100
                var unpack = new Progress<SevenZipProgress>(p =>
                {
                    job.Percent = p.Percent * 0.5;
                    if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                });

                SevenZipResult res = null;
                string password = null;
                foreach (var c in chain)
                {
                    if (ct.IsCancellationRequested) break;
                    res = await _zip.ExtractAsync(job.Archive, temp.TrimEnd('\\'), c.Password, unpack, ct, "-aos")
                              .ConfigureAwait(false);
                    if (res.Success) { password = c.Password; break; }
                    if (res.Error != SevenZipError.WrongPassword) break;
                }
                if (res == null || !res.Success)
                {
                    bool cancelled = ct.IsCancellationRequested || (res != null && res.Error == SevenZipError.Cancelled);
                    job.Status = cancelled ? JobStatus.Cancelled : JobStatus.Failed;
                    job.Diagnosis = cancelled ? "任务已取消"
                        : "转换失败：解不开源包（原包已保留）" + (res != null ? "：" + (res.Diagnosis ?? res.Error.ToString()) : "");
                    return;
                }
                job.UsedPassword = password;

                var items = Directory.GetFileSystemEntries(temp);
                if (items.Length == 0)
                {
                    job.Status = JobStatus.Failed;
                    job.Diagnosis = "包内没有文件";
                    return;
                }
                // bz2/gz/xz 是单流格式，只能装一个文件。让 7z 去报这句话，
                // 用户看到的是一句英文 "Sub items data error"，不如在这里讲明白。
                if (ArchiveFormat.IsSingleStream(job.ConvertType) && items.Length != 1)
                {
                    job.Status = JobStatus.Failed;
                    job.Diagnosis = "该格式只能装单个文件，包内有 " + items.Length
                        + " 个条目；要整包转换请选 7z / zip / tar（原包已保留）";
                    return;
                }
                job.Total = items.Length;

                var pack = new Progress<SevenZipProgress>(p =>
                {
                    job.Percent = 50 + p.Percent * 0.5;
                    if (p.CurrentFile != null) job.CurrentFile = p.CurrentFile;
                    if (p.DoneCount > 0) job.Done = p.DoneCount;
                });
                var cRes = await _zip.CompressAsync(items, outArchive, password, job.ConvertType,
                    s.CompressLevel, false, pack, ct).ConfigureAwait(false);
                if (!cRes.Success)
                {
                    job.Status = cRes.Error == SevenZipError.Cancelled || ct.IsCancellationRequested
                        ? JobStatus.Cancelled : JobStatus.Failed;
                    job.Diagnosis = job.Status == JobStatus.Cancelled ? "任务已取消"
                        : "转换失败：" + (cRes.Diagnosis ?? cRes.Error.ToString()) + "（原包已保留）";
                    TryDeletePartialCompress(outArchive, null);
                    return;
                }
                job.OutputDir = outArchive;

                if (s.VerifyAfterCompress)
                {
                    var check = await _zip.TestAsync(outArchive, password, ct).ConfigureAwait(false);
                    if (!check.Success)
                    {
                        job.Status = check.Error == SevenZipError.Cancelled || ct.IsCancellationRequested
                            ? JobStatus.Cancelled : JobStatus.Failed;
                        job.Diagnosis = job.Status == JobStatus.Cancelled ? "任务已取消"
                            : "转换产物校验未通过（原包已保留）：" + (check.Diagnosis ?? "归档损坏");
                        return;
                    }
                }

                _compressLog.Log(outArchive, password);
                job.Percent = 100;
                job.Done = job.Total;
                job.Status = JobStatus.Success;
            }
            finally
            {
                TryDeleteTemp(temp);
            }
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
        static string BuildArchivePath(int compressType, List<string> sources, string outDir, string pwSign, string password)
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
            string ext = ArchiveExtension(compressType);

            int num = 0;
            string path;
            do
            {
                path = outDir + baseName + (num == 0 ? "" : "-New" + num) + sign + ext;
                num++;
            } while (File.Exists(path));
            return path;
        }

        static string ArchiveExtension(int compressType) => ArchiveFormat.Extension(compressType);

        static IEnumerable<string> Dedup(IEnumerable<string> src)
        {
            var seen = new HashSet<string>();
            foreach (var x in src)
                if (!string.IsNullOrEmpty(x) && seen.Add(x))
                    yield return x;
        }

        public sealed class PasswordCandidate
        {
            public string Password;     // null = 空口令
            public bool FromPaper;      // 命中后要从密码纸划掉
            public bool FromName;       // 命中后目录名要去掉口令尾巴
        }

        // 纯函数：口令尝试的顺序、去重、以及"清单已确认加密就别先试空口令"全部定死在这里。
        public static List<PasswordCandidate> BuildPasswordChain(bool knownEncrypted, string manualPassword,
            IEnumerable<string> externalPasswords, string namePassword, IEnumerable<string> book,
            IEnumerable<string> paper)
        {
            var list = new List<PasswordCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!knownEncrypted) list.Add(new PasswordCandidate { Password = null });

            void Add(string pw, bool fromPaper, bool fromName)
            {
                if (string.IsNullOrEmpty(pw) || !seen.Add(pw)) return;
                list.Add(new PasswordCandidate { Password = pw, FromPaper = fromPaper, FromName = fromName });
            }

            Add(manualPassword, false, false);
            foreach (var p in externalPasswords ?? Enumerable.Empty<string>()) Add(p, false, false);
            Add(namePassword, false, true);
            foreach (var p in book ?? Enumerable.Empty<string>()) Add(p, false, false);
            foreach (var p in paper ?? Enumerable.Empty<string>()) Add(p, true, false);
            return list;
        }

        // 这些失败跟口令无关，不能套进"密码本里没找到正确密码"
        static bool IsHardFailure(SevenZipError err) =>
            err == SevenZipError.DiskFull || err == SevenZipError.PathTooLong
            || err == SevenZipError.Occupied || err == SevenZipError.NotFound;

        // 下一条口令开始前，把上一条的残留倒干净（temp 目录本身留着复用）
        static void ClearTempContents(string dir)
        {
            string[] files = Array.Empty<string>(), subDirs = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); } catch { }
            foreach (var f in files)
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                TryDeleteFile(f);
            }
            try { subDirs = Directory.GetDirectories(dir); } catch { subDirs = Array.Empty<string>(); }
            foreach (var d in subDirs)
                try { Directory.Delete(d, true); } catch { }
        }
    }
}
