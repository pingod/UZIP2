using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    // 失败报告必须能拿去求助，所以绝不能带上密码。
    public class FailureReportTests : IDisposable
    {
        private readonly string _root;

        public FailureReportTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipFailReportTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        static JobEntry Job(JobStatus status, string kind = "Extract", string archive = null)
            => new JobEntry { Status = status, Kind = kind, Archive = archive };

        [Fact]
        public void Failed_keeps_only_failed_jobs()
        {
            var jobs = new[]
            {
                Job(JobStatus.Success), Job(JobStatus.Failed), Job(JobStatus.Queued),
                Job(JobStatus.Failed), Job(JobStatus.Cancelled),
            };
            Assert.Equal(2, FailureReport.Failed(jobs).Count);
            Assert.Empty(FailureReport.Failed(null));
        }

        [Fact]
        public void Build_without_failures_says_so()
        {
            var text = FailureReport.Build(new[] { Job(JobStatus.Success) });
            Assert.Contains("失败 0 / 共 1", text);
            Assert.Contains("当前没有失败任务。", text);
        }

        [Fact]
        public void Build_reports_reason_progress_and_source_file()
        {
            var src = Path.Combine(_root, "pack.7z");
            File.WriteAllBytes(src, new byte[1234]);
            var job = Job(JobStatus.Failed, "Compress", src);
            job.Diagnosis = "  物理卷不匹配  ";
            job.Total = 10;
            job.Done = 3;
            job.Percent = 30.0;
            job.CurrentFile = @"src\big.bin";
            job.Sources = new List<string> { src, Path.Combine(_root, "b.bin") };

            var text = FailureReport.Build(new[] { job }, @"D:\7-Zip\7z.exe");

            Assert.Contains("失败 1 / 共 1", text);
            Assert.Contains("[1] 压缩  " + src, text);
            Assert.Contains("原因: 物理卷不匹配", text);
            Assert.Contains("进度: 3/10", text);
            Assert.Contains("(30%)", text);
            Assert.Contains("中断于: src\\big.bin", text);
            Assert.Contains("字节，修改于", text);
            Assert.Contains("合并来源 2 项", text);
            Assert.Contains(@"7z: D:\7-Zip\7z.exe", text);
        }

        [Fact]
        public void Build_marks_missing_source_and_blank_fields()
        {
            var job = Job(JobStatus.Failed, "Extract", Path.Combine(_root, "gone.zip"));
            var text = FailureReport.Build(new[] { job });
            Assert.Contains("不存在（可能已被删除或移动）", text);
            Assert.Contains("原因: 未记录", text);
            Assert.DoesNotContain("7z:", text);
        }

        [Fact]
        public void Build_never_leaks_passwords()
        {
            var job = Job(JobStatus.Failed, "Extract", @"D:\a.zip");
            job.UsedPassword = "s3cret-pw";
            var text = FailureReport.Build(new[] { job });
            Assert.DoesNotContain("s3cret-pw", text);
            Assert.Contains("[1] 解压  D:\\a.zip", text);
        }

        [Fact]
        public void Write_emits_utf8_bom_so_notedepad_reads_chinese()
        {
            var out_ = Path.Combine(_root, "report.txt");
            FailureReport.Write(out_, FailureReport.Build(new[] { Job(JobStatus.Failed) }));
            var raw = File.ReadAllBytes(out_);
            Assert.True(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
            Assert.Contains("UZIP 失败报告", File.ReadAllText(out_, Encoding.UTF8));
            Assert.Contains("失败 1 / 共 1", File.ReadAllText(out_, Encoding.UTF8));
        }
    }

    // 失败条的可见性/按钮可用性完全由 FailedCount 驱动
    public class HomeViewModelFailureBarTests : IDisposable
    {
        private readonly string _root;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly SevenZipClient _zip;
        private readonly ArchiveWorker _worker;
        private readonly HomeViewModel _vm;

        public HomeViewModelFailureBarTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipFailBarTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Config"));
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _zip = new SevenZipClient(_settings);
            _worker = new ArchiveWorker(_zip, _passwords, _settings);
            _vm = new HomeViewModel(_worker, _settings, _zip, _passwords,
                new ClipboardService(_passwords, _settings));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void Bar_hidden_and_commands_disabled_without_failures()
        {
            Assert.Equal(0, _vm.FailedCount);
            Assert.False(_vm.HasFailures);
            Assert.False(_vm.RetryAllFailedCommand.CanExecute(null));
            Assert.False(_vm.ExportFailuresCommand.CanExecute(null));
        }

        [Fact]
        public void Bar_shown_and_commands_enabled_once_a_job_fails()
        {
            int visibilityChanges = 0;
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(HomeViewModel.HasFailures)) visibilityChanges++;
            };

            _vm.FailedCount = 2;

            Assert.True(_vm.HasFailures);
            Assert.True(_vm.RetryAllFailedCommand.CanExecute(null));
            Assert.True(_vm.ExportFailuresCommand.CanExecute(null));
            Assert.Equal(1, visibilityChanges);

            _vm.FailedCount = 0;
            Assert.False(_vm.HasFailures);
            Assert.False(_vm.RetryAllFailedCommand.CanExecute(null));
        }
    }

    // 坏包不能被说成"密码不对"，否则用户会一直换密码
    public class PasswordChainDiagnosisTests
    {
        [Theory]
        [InlineData("ERROR: Wrong password", "需要密码，但密码本/密码纸中未找到正确密码")]
        [InlineData("ERROR: Cannot open encrypted file as archive", "需要密码，但密码本/密码纸中未找到正确密码")]
        [InlineData("ERROR: Data error ", "文件已损坏（校验失败）")]
        [InlineData("ERROR: Cannot open the file as archive", "文件已损坏或不是可识别的压缩包")]
        [InlineData("Open ERROR: Cannot open the file as [zip] archive\nIs not archive", "文件已损坏或不是可识别的压缩包")]
        [InlineData("", "密码本/密码纸中未找到正确密码")]
        [InlineData(null, "密码本/密码纸中未找到正确密码")]
        public void Empty_password_test_output_drives_the_message(string output, string expected)
            => Assert.Equal(expected, ArchiveWorker.DescribePasswordChainFailure(output));
    }
}
