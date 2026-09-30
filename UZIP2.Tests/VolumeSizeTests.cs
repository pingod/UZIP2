using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class VolumeSizeTests
    {
        [Theory]
        [InlineData("700m", "700m", 700L * 1024 * 1024)]
        [InlineData("700M", "700m", 700L * 1024 * 1024)]
        [InlineData(" 1 g ", "1g", 1024L * 1024 * 1024)]
        [InlineData("4480k", "4480k", 4480L * 1024)]
        [InlineData("102400", "102400", 102400)]
        [InlineData("10b", "10b", 10)]
        public void Parse_accepts_7z_units(string input, string arg, long bytes)
        {
            Assert.True(VolumeSize.TryParse(input, out var got, out long gotBytes));
            Assert.Equal(arg, got);
            Assert.Equal(bytes, gotBytes);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("0m")]
        [InlineData("abc")]
        [InlineData("700mb")]
        [InlineData("700 t")]
        [InlineData("m")]
        [InlineData("1.5m")]
        [InlineData("-100")]
        [InlineData("999999999999999999999999g")]
        public void Parse_rejects_unusable(string input)
        {
            Assert.False(VolumeSize.TryParse(input, out _, out _));
        }

        [Fact]
        public void Parse_rejects_over_one_terabyte()
        {
            Assert.False(VolumeSize.TryParse("2048g", out _, out _));
            Assert.True(VolumeSize.TryParse("1024g", out var a, out long b));
            Assert.Equal(1L << 40, b);
            Assert.Equal("1024g", a);
        }

        [Fact]
        public void Normalize_is_null_for_empty()
        {
            Assert.False(VolumeSize.TryNormalize("  ", out var arg));
            Assert.Null(arg);
        }
    }

    // 真跑 7z: 分卷产物命名 + 拖回第一卷能还原
    public class VolumeCompressTests : IDisposable
    {
        readonly string _root, _src, _out;
        readonly SettingsService _settings;
        readonly SevenZipClient _client;
        readonly ArchiveWorker _worker;

        public VolumeCompressTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipVolumeTests_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            _out = Path.Combine(_root, "out");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_out);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.ExtractOutMode = 3;
            _settings.Current.LastExtractPath = _out;
            _settings.Current.CompressOutMode = 1;
            var passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
            Assert.NotNull(_client.SevenZipPath);
            _worker = new ArchiveWorker(_client, passwords, _settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string MakeFile(string name, int kb)
        {
            var buf = new byte[kb * 1024];
            new Random(7).NextBytes(buf);
            var p = Path.Combine(_src, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        [Theory]
        [InlineData(0)]  // zip
        [InlineData(1)]  // 7z
        public async Task Volume_split_creates_numbered_files_and_round_trips(int compressType)
        {
            var f = MakeFile("blob.bin", 1200);
            _settings.Current.CompressType = compressType;
            _settings.Current.CompressVolume = "500k";

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            string first = job.OutputDir;
            Assert.EndsWith(".001", first);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(first.Replace(".001", ".002")));
            Assert.True(File.Exists(first.Replace(".001", ".003")));
            // 7z 分卷时主文件名不会落地
            Assert.False(File.Exists(first.Substring(0, first.Length - 4)));

            _worker.EnqueueExtract(new[] { first });
            await _worker.WhenIdleAsync();

            var back = _worker.Jobs[_worker.Jobs.Count - 1];
            Assert.Equal(JobStatus.Success, back.Status);
            var restored = Path.Combine(_out, "blob.bin");
            Assert.True(File.Exists(restored));
            Assert.Equal(new FileInfo(f).Length, new FileInfo(restored).Length);
        }

        [Fact]
        public async Task Invalid_volume_setting_fails_before_running_7z()
        {
            var f = MakeFile("blob.bin", 8);
            _settings.Current.CompressVolume = "700mb";

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("分卷大小", job.Diagnosis);
            Assert.Equal(0, Directory.GetFiles(_src, "blob.zip*").Length);
        }

        [Fact]
        public async Task Empty_volume_setting_keeps_single_archive()
        {
            var f = MakeFile("solo.bin", 8);
            _settings.Current.CompressVolume = "";

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.EndsWith(".zip", job.OutputDir);
            Assert.True(File.Exists(job.OutputDir));
            Assert.Equal(0, Directory.GetFiles(_src, "*.001").Length);
        }
    }
}
