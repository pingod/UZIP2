using System;
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
    }
}
