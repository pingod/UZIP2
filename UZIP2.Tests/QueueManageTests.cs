using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    // 主页任务列表管理：排序权重、精准移除、批量清除、自动回收、列表镜像。
    // 无效路径的作业会快速走到 Failed（不需要 7z），用它驱动"终态"场景；
    // 涉及 Success 的场景用真实 7z，和其余流水线测试一样按 SevenZipPath 跳过。
    public class QueueManageTests : IDisposable
    {
        private readonly string _root;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _client;

        public QueueManageTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipQueueTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private ArchiveWorker NewWorker() => new ArchiveWorker(_client, _passwords, _settings);

        private string[] MissingArchives(int n) =>
            Enumerable.Range(0, n)
                .Select(i => Path.Combine(_root, "missing-" + i + "-" + Guid.NewGuid().ToString("N") + ".zip"))
                .ToArray();

        private HomeViewModel NewVm(ArchiveWorker worker) =>
            new HomeViewModel(worker, _settings, _client, _passwords,
                new ClipboardService(_passwords, _settings));

        // ---------- 纯模型：状态权重与排序 ----------

        [Fact]
        public void JobEntry_Terminal_And_SortRank_Map_Every_Status()
        {
            var j = new JobEntry();
            j.Status = JobStatus.Running;   Assert.False(j.IsTerminal); Assert.Equal(0, j.SortRank);
            j.Status = JobStatus.Queued;    Assert.False(j.IsTerminal); Assert.Equal(1, j.SortRank);
            j.Status = JobStatus.Failed;    Assert.True(j.IsTerminal);  Assert.Equal(2, j.SortRank);
            j.Status = JobStatus.Success;   Assert.True(j.IsTerminal);  Assert.Equal(3, j.SortRank);
            j.Status = JobStatus.Cancelled; Assert.True(j.IsTerminal);  Assert.Equal(4, j.SortRank);
        }

        [Fact]
        public void JobEntry_Status_Change_Notifies_Derived_Properties()
        {
            var j = new JobEntry();
            var changed = new List<string>();
            j.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            j.Status = JobStatus.Success;
            Assert.Contains(nameof(JobEntry.IsTerminal), changed);
            Assert.Contains(nameof(JobEntry.SortRank), changed);
        }

        [Fact]
        public void JobOrder_Compare_Follows_Mode()
        {
            var a = new JobEntry { Id = 1, Archive = @"C:\x\b.zip", Status = JobStatus.Success };
            var b = new JobEntry { Id = 2, Archive = @"C:\x\a.zip", Status = JobStatus.Running };

            Assert.True(JobOrder.Compare(a, b, JobOrder.AddOrder) < 0);
            Assert.True(JobOrder.Compare(a, b, JobOrder.NewestFirst) > 0);
            Assert.True(JobOrder.Compare(a, b, JobOrder.StatusFirst) > 0);  // Success(3) 排在 Running(0) 后
            Assert.True(JobOrder.Compare(a, b, JobOrder.ByName) > 0);       // "b.zip" > "a.zip"
            Assert.Equal(0, JobOrder.Compare(a, a, JobOrder.StatusFirst));
        }

        [Fact]
        public void JobOrder_InsertIndex_Computes_Sorted_Position()
        {
            var list = new List<JobEntry>
            {
                new JobEntry { Id = 1, Archive = "a.zip" },
                new JobEntry { Id = 3, Archive = "b.zip" },
                new JobEntry { Id = 5, Archive = "c.zip" },
            };
            Assert.Equal(0, JobOrder.InsertIndex(list, new JobEntry { Id = 0 }, JobOrder.AddOrder));
            Assert.Equal(2, JobOrder.InsertIndex(list, new JobEntry { Id = 4 }, JobOrder.AddOrder));
            Assert.Equal(3, JobOrder.InsertIndex(list, new JobEntry { Id = 9 }, JobOrder.AddOrder));

            // NewestFirst 模式下列表本身须按该模式有序（[5,3,1]），插入位置才有意义
            var newest = new List<JobEntry>
            {
                new JobEntry { Id = 5, Archive = "c.zip" },
                new JobEntry { Id = 3, Archive = "b.zip" },
                new JobEntry { Id = 1, Archive = "a.zip" },
            };
            Assert.Equal(0, JobOrder.InsertIndex(newest, new JobEntry { Id = 6 }, JobOrder.NewestFirst));
            Assert.Equal(1, JobOrder.InsertIndex(newest, new JobEntry { Id = 4 }, JobOrder.NewestFirst));
            Assert.Equal(3, JobOrder.InsertIndex(newest, new JobEntry { Id = 0 }, JobOrder.NewestFirst));

            // Move 语义：跳过自身后按"先移除后插入"坐标计算
            var me = list[1];
            Assert.Equal(1, JobOrder.InsertIndex(list, me, JobOrder.AddOrder, 1));   // 原地不动

            // 状态排序：rank 变好上浮、变差下沉，同 rank 按 Id
            var up = new JobEntry { Id = 4, Archive = "up.zip", Status = JobStatus.Running };
            var sorted = new List<JobEntry>
            {
                new JobEntry { Id = 1, Archive = "s0.zip", Status = JobStatus.Success },
                up,
                new JobEntry { Id = 3, Archive = "s1.zip", Status = JobStatus.Failed },
            };
            up.Status = JobStatus.Success;   // rank 3：应落到 Id=1 之后、Id=3 之前？
            // Success(3) 与 Id=1 同 rank(3)，Id=4 > 1 → 在其后；Id=3 是 Failed(2)，rank 更小 → 在其前
            Assert.Equal(2, JobOrder.InsertIndex(sorted, up, JobOrder.StatusFirst, 1));
        }

        // ---------- Worker：移除 / 回收 ----------

        [Fact]
        public async Task RemoveTerminal_Removes_Matching_Terminal_Jobs()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(3));
            await worker.WhenIdleAsync();

            Assert.Equal(3, worker.Jobs.Count);
            Assert.All(worker.Jobs, j => Assert.True(j.IsTerminal));

            Assert.Equal(3, worker.RemoveTerminal(j => j.Status == JobStatus.Failed));
            Assert.Empty(worker.Jobs);
            Assert.Equal(0, worker.RemoveTerminal(null));   // 空表上再清也是 0
        }

        [Fact]
        public async Task Remove_Immunizes_Active_Jobs_And_Works_After_Terminal()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(2));

            // 刚入队必然是活动态（最坏 7z 启动也要几十毫秒），单卡移除必须被免疫
            var job = worker.Jobs[0];
            Assert.False(job.IsTerminal);
            Assert.False(worker.Remove(job));
            Assert.Equal(2, worker.Jobs.Count);
            Assert.Equal(0, worker.RemoveTerminal(null));   // 活动项整体免疫

            await worker.WhenIdleAsync();
            Assert.True(worker.Remove(worker.Jobs[0]));
            Assert.Single(worker.Jobs);
        }

        [Fact]
        public async Task TrimTerminal_Keeps_Newest_And_Reports_Removed()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(5));
            await worker.WhenIdleAsync();
            var ids = worker.Jobs.Select(j => j.Id).ToList();   // 加入顺序

            Assert.Equal(3, worker.TrimTerminal(2));
            Assert.Equal(2, worker.Jobs.Count);
            Assert.Equal(ids.TakeLast(2), worker.Jobs.Select(j => j.Id));
            Assert.Equal(0, worker.TrimTerminal(2));            // 已在上限内，再裁无动作
        }

        [Fact]
        public async Task RemoveTerminal_Predicates_Split_Finished_And_Failed()
        {
            if (_client.SevenZipPath == null) return;
            var outDir = Path.Combine(_root, "out");
            Directory.CreateDirectory(outDir);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = outDir;

            var src = Path.Combine(_root, "ok.bin");
            File.WriteAllBytes(src, new byte[4096]);
            var zip = Path.Combine(_root, "ok.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zip, null, 0, 0,
                false, null, CancellationToken.None)).Success);

            var worker = NewWorker();
            worker.EnqueueExtract(new[] { zip });
            worker.EnqueueExtract(MissingArchives(1));
            await worker.WhenIdleAsync();

            Assert.Equal(JobStatus.Success, worker.Jobs.Single(j => j.Archive == zip).Status);

            // "清除已完成"只清 Success/Cancelled，失败项原样保留
            Assert.Equal(1, worker.RemoveTerminal(
                j => j.Status == JobStatus.Success || j.Status == JobStatus.Cancelled));
            var left = Assert.Single(worker.Jobs);
            Assert.Equal(JobStatus.Failed, left.Status);
        }

        // ---------- ViewModel：命令与镜像 ----------

        [Fact]
        public async Task Vm_ClearFailed_Command_Notifies_CanExecute_On_Failure_And_Clear()
        {
            var worker = NewWorker();
            var vm = NewVm(worker);   // 先建 VM：订阅须在作业失败之前装好
            bool notified = false;
            vm.ClearFailedCommand.CanExecuteChanged += (s, e) => notified = true;

            worker.EnqueueExtract(MissingArchives(1));
            await worker.WhenIdleAsync();

            // 实机冒烟抓到的缺口：失败到达时若不发通知，WPF 按钮会一直停在禁用态
            Assert.True(notified);
            Assert.True(vm.ClearFailedCommand.CanExecute(null));

            notified = false;
            vm.ClearFailedCommand.Execute(null);
            Assert.Equal(0, vm.FailedCount);
            Assert.True(notified);
        }

        [Fact]
        public async Task Vm_ClearFailed_Removes_Only_Failed_And_Updates_Summary()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(3));
            await worker.WhenIdleAsync();

            var vm = NewVm(worker);
            Assert.Equal(3, vm.Jobs.Count);
            Assert.Equal(3, vm.FailedCount);
            Assert.True(vm.HasFailures);
            Assert.True(vm.HasJobs);
            Assert.True(vm.ClearFailedCommand.CanExecute(null));
            Assert.False(vm.ClearFinishedCommand.CanExecute(null));
            Assert.False(vm.CancelAllJobsCommand.CanExecute(null));

            vm.ClearFailedCommand.Execute(null);

            Assert.Empty(vm.Jobs);
            Assert.Empty(worker.Jobs);
            Assert.Equal(0, vm.FailedCount);
            Assert.False(vm.HasJobs);
            Assert.False(vm.ClearFailedCommand.CanExecute(null));
        }

        [Fact]
        public async Task Vm_RemoveJob_Only_Works_On_Terminal_Cards()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(1));
            var vm = NewVm(worker);

            var job = Assert.Single(vm.Jobs);
            vm.RemoveJobCommand.Execute(job);      // 活动态被免疫
            Assert.Single(vm.Jobs);

            await worker.WhenIdleAsync();
            vm.RemoveJobCommand.Execute(job);
            Assert.Empty(vm.Jobs);
            Assert.Empty(worker.Jobs);
        }

        [Fact]
        public async Task Vm_SortMode_Rebuilds_Mirror_And_Persists()
        {
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(3));
            await worker.WhenIdleAsync();

            var vm = NewVm(worker);
            Assert.Equal(JobOrder.AddOrder, vm.SortMode);
            var asc = vm.Jobs.Select(j => j.Id).ToList();
            Assert.Equal(asc.OrderBy(x => x), asc);

            vm.SortMode = JobOrder.NewestFirst;
            var desc = vm.Jobs.Select(j => j.Id).ToList();
            Assert.Equal(desc.OrderByDescending(x => x), desc);
            Assert.Equal(JobOrder.NewestFirst, _settings.Current.JobSort);   // 已持久化

            // 新作业在"最新在前"下插到队首
            worker.EnqueueExtract(MissingArchives(1));
            await worker.WhenIdleAsync();
            Assert.Equal(4, vm.Jobs.Count);
            Assert.True(vm.Jobs[0].Id > vm.Jobs[1].Id);
        }

        [Fact]
        public async Task Vm_CancelAll_Cancels_Everything_And_ClearFinished_Empties()
        {
            _settings.Current.ParallelExtract = 1;
            var worker = NewWorker();
            worker.EnqueueExtract(MissingArchives(3));
            var vm = NewVm(worker);

            Assert.True(vm.CancelAllJobsCommand.CanExecute(null));
            vm.CancelAllJobsCommand.Execute(null);
            await worker.WhenIdleAsync();

            // 排队中的作业在任一时序下都必须被取消（含读取队列与占用名额之间被标记的那一个）；
            // 只有占住名额的首个作业允许因启动竞态停在 Failed——取消是协作式的，跑赢了就不回滚。
            var jobs = worker.Jobs.ToList();
            Assert.All(jobs, j => Assert.True(j.IsTerminal));
            Assert.All(jobs.Skip(1), j => Assert.Equal(JobStatus.Cancelled, j.Status));
            Assert.Contains(jobs[0].Status, new[] { JobStatus.Cancelled, JobStatus.Failed });

            // 已取消计入可清除的"已完成"，失败项走"清除失败项"
            int cancelled = jobs.Count(j => j.Status == JobStatus.Cancelled);
            int failed = jobs.Count(j => j.Status == JobStatus.Failed);
            Assert.Equal(cancelled, vm.FinishedCount);
            Assert.Equal(failed, vm.FailedCount);
            Assert.Equal(3, vm.FinishedCount + vm.FailedCount);

            Assert.True(vm.ClearFinishedCommand.CanExecute(null));
            vm.ClearFinishedCommand.Execute(null);
            Assert.Equal(failed, vm.Jobs.Count);
            if (failed > 0)
            {
                Assert.True(vm.ClearFailedCommand.CanExecute(null));
                vm.ClearFailedCommand.Execute(null);
            }
            Assert.Empty(vm.Jobs);
            Assert.Empty(worker.Jobs);
            Assert.False(vm.ClearFinishedCommand.CanExecute(null));
            Assert.False(vm.ClearFailedCommand.CanExecute(null));
        }

        [Fact]
        public async Task Vm_StatusFirst_Repositions_On_Status_Change()
        {
            if (_client.SevenZipPath == null) return;
            var outDir = Path.Combine(_root, "out");
            Directory.CreateDirectory(outDir);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = outDir;

            var src = Path.Combine(_root, "st.bin");
            File.WriteAllBytes(src, new byte[4096]);
            var zip = Path.Combine(_root, "st.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zip, null, 0, 0,
                false, null, CancellationToken.None)).Success);

            _settings.Current.JobSort = JobOrder.StatusFirst;
            var worker = NewWorker();
            worker.EnqueueExtract(new[] { zip });
            worker.EnqueueExtract(MissingArchives(1));
            await worker.WhenIdleAsync();

            var vm = NewVm(worker);
            Assert.Equal(JobOrder.StatusFirst, vm.SortMode);
            // Failed(2) 应排在 Success(3) 之前；同 rank 内部按加入顺序
            var statuses = vm.Jobs.Select(j => j.Status).ToList();
            Assert.Equal(new[] { JobStatus.Failed, JobStatus.Success }, statuses);
        }
    }
}
