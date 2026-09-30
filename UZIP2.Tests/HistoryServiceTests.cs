using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class HistoryServiceTests : IDisposable
    {
        readonly string _dir;

        public HistoryServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipHistTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        static JobEntry Extract(JobStatus st, string diag = null, string pw = null) =>
            new JobEntry { Kind = "Extract", Archive = @"D:\a.zip", Status = st, Diagnosis = diag, UsedPassword = pw, Done = 3, OutputDir = @"D:\out" };

        [Fact]
        public void Record_then_ReadAll_roundtrips_a_success()
        {
            var svc = new HistoryService(_dir);
            svc.Record(Extract(JobStatus.Success, pw: "secret"));

            var all = svc.ReadAll();
            var e = Assert.Single(all);
            Assert.Equal("Extract", e.Kind);
            Assert.Equal("Success", e.Status);
            Assert.Equal(@"D:\a.zip", e.Source);
            Assert.Equal(@"D:\out", e.Destination);
            Assert.Null(e.Error);
            Assert.Null(e.Advice);
            Assert.Equal(3, e.Count);
            Assert.True(e.Timestamp > 0);
            Assert.True(e.DurationMs >= 0);
        }

        [Fact]
        public void Failed_record_carries_error_and_next_step_advice()
        {
            var svc = new HistoryService(_dir);
            svc.Record(Extract(JobStatus.Failed, diag: "密码错误，无法打开加密归档"));

            var e = Assert.Single(svc.ReadAll());
            Assert.Equal("Failed", e.Status);
            Assert.Equal("密码错误，无法打开加密归档", e.Error);
            Assert.False(string.IsNullOrWhiteSpace(e.Advice));
            Assert.Contains("密码", e.Advice);
        }

        [Fact]
        public void Cancelled_and_running_jobs_are_not_recorded()
        {
            var svc = new HistoryService(_dir);
            svc.Record(Extract(JobStatus.Cancelled));
            svc.Record(Extract(JobStatus.Running));
            svc.Record(Extract(JobStatus.Queued));
            Assert.Empty(svc.ReadAll());
        }

        [Fact]
        public void ReadAll_returns_newest_first()
        {
            var svc = new HistoryService(_dir);
            var a = new JobEntry { Kind = "Extract", Archive = "a.zip", Status = JobStatus.Success };
            var b = new JobEntry { Kind = "Compress", Archive = "b.zip", Status = JobStatus.Success };
            svc.Record(a);
            svc.Record(b);

            var all = svc.ReadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("b.zip", all[0].Source);
            Assert.Equal("a.zip", all[1].Source);
        }

        [Fact]
        public void Password_recorded_when_logging_on()
        {
            var svc = new HistoryService(_dir, new SettingsService(_dir, null));
            svc.Record(Extract(JobStatus.Success, pw: "hunter2"));
            Assert.Equal("hunter2", Assert.Single(svc.ReadAll()).Password);
        }

        [Fact]
        public void Password_not_recorded_nor_written_when_logging_off()
        {
            var settings = new SettingsService(_dir, null);
            settings.Save(s => s.LogPasswords = false);
            var svc = new HistoryService(Path.Combine(_dir, "off"), settings);
            svc.Record(Extract(JobStatus.Success, pw: "hunter2"));

            Assert.Null(Assert.Single(svc.ReadAll()).Password);
            Assert.DoesNotContain("hunter2", File.ReadAllText(svc.HistoryPath, Encoding.UTF8));
        }

        [Fact]
        public void Failed_jobs_never_store_a_password()
        {
            var svc = new HistoryService(_dir);
            svc.Record(Extract(JobStatus.Failed, diag: "密码错误", pw: "shouldnotappear"));
            Assert.Null(Assert.Single(svc.ReadAll()).Password);
        }

        [Fact]
        public void Caps_history_and_drops_oldest()
        {
            var svc = new HistoryService(_dir);
            for (int i = 0; i < HistoryService.MaxEntries + 40; i++)
                svc.Record(new JobEntry { Kind = "Extract", Archive = "f" + i + ".zip", Status = JobStatus.Success });

            var all = svc.ReadAll();
            Assert.Equal(HistoryService.MaxEntries, all.Count);
            // newest (f<max+39>) first; oldest f0..f39 dropped
            Assert.Contains("f" + (HistoryService.MaxEntries + 39) + ".zip", all[0].Source);
            Assert.DoesNotContain(all, e => e.Source == "f0.zip");
        }

        [Fact]
        public void Persists_across_instances()
        {
            var sub = Path.Combine(_dir, "cfg");
            new HistoryService(sub).Record(Extract(JobStatus.Success, pw: "p"));
            var reloaded = new HistoryService(sub).ReadAll();
            Assert.Single(reloaded);
            Assert.Equal("p", reloaded[0].Password);
        }

        [Fact]
        public void Corrupt_file_is_quarantined_and_service_recovers()
        {
            var sub = Path.Combine(_dir, "bad");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "history.json"), "{ this is not valid json ][");

            var svc = new HistoryService(sub);
            Assert.Empty(svc.ReadAll());                 // 坏文件不抛异常，读成空
            svc.Record(Extract(JobStatus.Success));       // 仍能继续写
            Assert.Single(svc.ReadAll());
            Assert.Contains(Directory.GetFiles(sub), f => f.Contains("history.json.bad-"));
        }

        [Fact]
        public void Clear_removes_everything()
        {
            var svc = new HistoryService(_dir);
            svc.Record(Extract(JobStatus.Success));
            svc.Clear();
            Assert.Empty(svc.ReadAll());
            Assert.False(File.Exists(svc.HistoryPath));
        }

        [Fact]
        public void Chinese_survives_roundtrip_readably()
        {
            var svc = new HistoryService(_dir);
            svc.Record(new JobEntry { Kind = "Extract", Archive = @"D:\资料\第一季.zip", Status = JobStatus.Failed, Diagnosis = "分卷不完整" });
            Assert.Equal(@"D:\资料\第一季.zip", Assert.Single(svc.ReadAll()).Source);
            // RelaxedJsonEscaping: 中文直接可读，不应被转成 \uXXXX
            Assert.Contains("第一季", File.ReadAllText(svc.HistoryPath, Encoding.UTF8));
        }
    }
}
