using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 压缩后校验：坏包若等到下次解压才暴露，用户往往已经按"压缩后删除"清掉了源文件。
    // 开关打开时，产物必须先自检通过才算成功，且自检不过不能动源文件。
    public class CompressVerifyTests : IDisposable
    {
        readonly string _root;
        readonly SettingsService _settings;
        readonly PasswordService _passwords;
        readonly StubEngine _engine = new StubEngine();
        readonly ArchiveWorker _worker;

        public CompressVerifyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipCvrfy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.CompressOutMode = 1;        // 产物落在源文件同目录
            _passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _worker = new ArchiveWorker(_engine, _passwords, _settings);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        string MakeSource(string name)
        {
            var p = Path.Combine(_root, name);
            File.WriteAllText(p, "payload");
            return p;
        }

        [Fact]
        public async Task Verification_runs_after_compress_when_the_toggle_is_on()
        {
            _settings.Current.VerifyAfterCompress = true;
            var src = MakeSource("a.bin");

            _worker.EnqueueCompress(new[] { src });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.Equal(1, _engine.TestCalls);
            Assert.Equal(job.OutputDir, _engine.TestedArchive);
        }

        [Fact]
        public async Task Verification_is_skipped_when_the_toggle_is_off()
        {
            _settings.Current.VerifyAfterCompress = false;
            var src = MakeSource("b.bin");

            _worker.EnqueueCompress(new[] { src });
            await _worker.WhenIdleAsync();

            Assert.Equal(0, _engine.TestCalls);
            Assert.Equal(JobStatus.Success, Assert.Single(_worker.Jobs).Status);
        }

        // 加密包自检要带上当次用的口令，否则 t 只会报"缺密码"
        [Fact]
        public async Task Verification_tests_with_the_password_just_written()
        {
            _settings.Current.VerifyAfterCompress = true;
            _settings.Current.PasswordMode = 2;
            _settings.Current.CustomPasswords = new List<string> { "pw123" };
            var src = MakeSource("c.bin");

            _worker.EnqueueCompress(new[] { src });
            await _worker.WhenIdleAsync();

            Assert.Equal("pw123", _engine.TestedPassword);
        }

        [Fact]
        public async Task Broken_product_fails_the_job_before_the_sources_are_deleted()
        {
            _settings.Current.VerifyAfterCompress = true;
            _settings.Current.DeleteCompressFinish = true;
            _engine.TestSucceeds = false;
            var src = MakeSource("d.bin");

            _worker.EnqueueCompress(new[] { src });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("校验", job.Diagnosis);
            Assert.True(File.Exists(src));   // 自检没过，源文件一个都不能少
        }

        [Fact]
        public async Task Cancelled_verification_reports_cancelled_not_a_broken_archive()
        {
            _settings.Current.VerifyAfterCompress = true;
            _engine.TestResult = SevenZipResult.Fail("", SevenZipError.Cancelled, "用户中断");
            var src = MakeSource("e.bin");

            _worker.EnqueueCompress(new[] { src });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.DoesNotContain("校验未通过", job.Diagnosis);
        }

        class StubEngine : IArchiveEngine
        {
            public int TestCalls;
            public string TestedArchive, TestedPassword;
            public bool TestSucceeds = true;
            public SevenZipResult TestResult;

            public Task<SevenZipResult> TestAsync(string archive, string password, CancellationToken ct)
            {
                Interlocked.Increment(ref TestCalls);
                TestedArchive = archive;
                TestedPassword = password;
                if (TestResult != null) return Task.FromResult(TestResult);
                return Task.FromResult(TestSucceeds
                    ? SevenZipResult.Ok("ok", archive)
                    : SevenZipResult.Fail("broken", SevenZipError.Corrupt, "archive is broken", archive));
            }

            public Task<SevenZipResult> CompressAsync(IReadOnlyList<string> files, string outArchive, string password,
                int compressType, int level, bool hideContent, IProgress<SevenZipProgress> progress, CancellationToken ct,
                IReadOnlyList<string> excludeFilters = null, string volumeSize = null,
                string solid = null, string threads = null)
            {
                File.WriteAllText(outArchive, "fake-archive");
                return Task.FromResult(SevenZipResult.Ok("compressed", outArchive));
            }

            public Task<EncryptionState> ProbeEncryptionAsync(string archive, CancellationToken ct)
                => Task.FromResult(EncryptionState.NotEncrypted);

            public Task<SevenZipResult> ExtractAsync(string archive, string dest, string password,
                IProgress<SevenZipProgress> progress, CancellationToken ct, string coverMode = "-aos",
                IReadOnlyList<string> onlyEntries = null)
                => Task.FromResult(SevenZipResult.Ok("extracted", archive));

            public Task<ArchiveListing> ListEntriesAsync(string archive, string password, CancellationToken ct)
                => Task.FromResult(new ArchiveListing(true, SevenZipError.None, null, Array.Empty<ArchiveEntry>()));
        }
    }
}
