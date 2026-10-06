using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    // HistoryViewModel 的过滤 / 揭示 / 清空 / 重跑 行为（Dispatcher 无关，纯集合逻辑）。
    public class HistoryViewModelTests : IDisposable
    {
        readonly string _dir;

        public HistoryViewModelTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipHistVm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        HistoryService _h;
        HistoryService H => _h ??= new HistoryService(_dir);

        void SeedSuccess(string src, string pw = null) =>
            H.Record(new JobEntry { Kind = "Extract", Archive = src, Status = JobStatus.Success, UsedPassword = pw, Done = 1 });

        void SeedFail(string src, string diag) =>
            H.Record(new JobEntry { Kind = "Extract", Archive = src, Status = JobStatus.Failed, Diagnosis = diag });

        [Fact]
        public void Loads_rows_newest_first()
        {
            SeedSuccess("old.zip");
            SeedSuccess("new.zip");
            var vm = new HistoryViewModel(H);
            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal("new.zip", vm.Rows[0].Source);
            Assert.Equal("old.zip", vm.Rows[1].Source);
            Assert.True(vm.HasRows);
            Assert.False(vm.IsEmpty);
        }

        [Fact]
        public void FailuresOnly_drops_success_rows()
        {
            SeedSuccess("ok.zip");
            SeedFail("bad.zip", "密码错误");
            var vm = new HistoryViewModel(H) { FailuresOnly = true };
            var row = Assert.Single(vm.Rows);
            Assert.Equal("bad.zip", row.Source);
            Assert.True(row.IsFailed);
            Assert.False(string.IsNullOrWhiteSpace(row.Advice));   // 失败行自带建议
        }

        [Fact]
        public void Search_matches_source_error_and_advice()
        {
            SeedFail("第一季.zip", "分卷不完整");
            SeedSuccess("other.zip");
            var byName = new HistoryViewModel(H) { SearchText = "第一季" };
            Assert.Single(byName.Rows);

            // 搜建议里的关键词也能命中（"分卷" 出现在 advice）
            var byAdvice = new HistoryViewModel(H) { SearchText = "分卷" };
            Assert.Contains(byAdvice.Rows, r => r.Source == "第一季.zip");
        }

        [Fact]
        public void ShowPasswords_toggles_row_password_text()
        {
            SeedSuccess("p.zip", "s3cr3t");
            var masked = new HistoryViewModel(H);
            Assert.Equal("••••••", Assert.Single(masked.Rows).PasswordText);

            var revealed = new HistoryViewModel(H) { ShowPasswords = true };
            Assert.Equal("s3cr3t", Assert.Single(revealed.Rows).PasswordText);
        }

        [Fact]
        public void Row_without_password_has_empty_password_text()
        {
            SeedSuccess("plain.zip");           // UsedPassword null
            var row = Assert.Single(new HistoryViewModel(H).Rows);
            Assert.Equal("", row.PasswordText);
            Assert.False(row.HasPassword);
        }

        [Fact]
        public void Clear_removes_all_rows()
        {
            SeedSuccess("a.zip");
            var vm = new HistoryViewModel(H);
            vm.ClearCommand.Execute(null);
            Assert.Empty(vm.Rows);
            Assert.True(vm.IsEmpty);
            Assert.Contains("还没有", vm.EmptyText);
        }

        [Fact]
        public void Retry_without_worker_is_a_noop_and_safe()
        {
            SeedSuccess("a.zip");
            var vm = new HistoryViewModel(H);   // worker = null
            var ex = Record.Exception(() => vm.RetryCommand.Execute(vm.Rows[0]));
            Assert.Null(ex);
        }

        // 校验记录不能当解压重跑：否则"test 失败"变成"真的把包解开了"。
        [Fact]
        public void Test_row_is_not_retryable()
        {
            H.Record(new JobEntry { Kind = "Test", Archive = "pack.zip", Status = JobStatus.Failed, Diagnosis = "数据错误" });
            var row = Assert.Single(new HistoryViewModel(H).Rows);
            Assert.Equal("Test", row.Kind);
            Assert.False(row.CanRetry);
        }

        [Fact]
        public void Extract_and_compress_rows_are_retryable()
        {
            H.Record(new JobEntry { Kind = "Extract", Archive = "a.zip", Status = JobStatus.Failed });
            H.Record(new JobEntry { Kind = "Compress", Archive = "b.txt", Status = JobStatus.Failed });
            var vm = new HistoryViewModel(H);
            Assert.All(vm.Rows, r => Assert.True(r.CanRetry));
        }

        // 重跑必须带上历史里那条记录用的口令：口令不在密码本里时，
        // 裸重跑等于把一次成功的作业变成一个必然失败的作业。
        [Fact]
        public void Retry_passes_recorded_password_to_the_worker()
        {
            var fake = new RecordingWorker();
            H.Record(new JobEntry { Kind = "Extract", Archive = @"C:\p\secret.7z", Status = JobStatus.Success, UsedPassword = "pw9" });
            var vm = new HistoryViewModel(H, fake);
            vm.RetryCommand.Execute(vm.Rows[0]);

            Assert.True(fake.ExtractCalled);
            Assert.Equal("pw9", fake.ExtractPassword);
        }

        [Fact]
        public void Retry_passes_recorded_password_to_compress()
        {
            var fake = new RecordingWorker();
            H.Record(new JobEntry { Kind = "Compress", Archive = @"C:\p\doc.txt", Status = JobStatus.Success, UsedPassword = "cpw" });
            var vm = new HistoryViewModel(H, fake);
            vm.RetryCommand.Execute(vm.Rows[0]);

            Assert.True(fake.CompressCalled);
            Assert.Equal("cpw", fake.CompressPassword);
        }

        [Fact]
        public void Retry_skips_test_rows_even_with_a_worker()
        {
            var fake = new RecordingWorker();
            H.Record(new JobEntry { Kind = "Test", Archive = @"C:\p\pack.zip", Status = JobStatus.Failed });
            var vm = new HistoryViewModel(H, fake);
            vm.RetryCommand.Execute(vm.Rows[0]);

            Assert.False(fake.ExtractCalled);
            Assert.False(fake.CompressCalled);
        }

        class RecordingWorker : IJobQueue
        {
            public bool ExtractCalled, CompressCalled;
            public string ExtractPassword, CompressPassword;

            public void EnqueueExtract(IReadOnlyList<string> archives, string outputDir = null,
                List<string> onlyEntries = null, bool flat = false, string manualPassword = null)
            {
                ExtractCalled = true;
                ExtractPassword = manualPassword;
            }

            public void EnqueueCompress(IReadOnlyList<string> files, string outDir = null, string manualPassword = null)
            {
                CompressCalled = true;
                CompressPassword = manualPassword;
            }
        }
    }
}
