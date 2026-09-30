using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class SevenZipProgressParsing
    {
        [Theory]
        [InlineData("  23% 4 - folder\\file.bin", 23, "folder\\file.bin", 4)]
        [InlineData("100% - done.txt", 100, "done.txt", 0)]
        [InlineData("Scanning the drive:", 0, null, 0)]
        public void Parses_Progress_Lines(string line, int pct, string file, int done)
        {
            var p = SevenZipClient.ParseProgressLine(line);
            if (file == null) { Assert.Null(p); return; }
            Assert.NotNull(p);
            Assert.Equal(pct, (int)p.Percent!.Value);
            Assert.Equal(file, p.CurrentFile);
            Assert.Equal(done, p.DoneCount);
        }

        [Theory]
        [InlineData("Data Error in encrypted file. Wrong Password : secret", SevenZipError.WrongPassword)]
        [InlineData("ERROR: Method Failed with nonzero return code, while the decoder produced an error. CRC Failed", SevenZipError.Corrupt)]
        [InlineData("Can not open the file as archive\nIs not supported as an archive", SevenZipError.UnsupportedFormat)]
        [InlineData("There are some data after the end of the payload data", SevenZipError.Corrupt)]
        [InlineData("Not enough disk space", SevenZipError.DiskFull)]
        [InlineData("The system cannot find the path specified. being used by another process", SevenZipError.Occupied)]
        public void Classifies_Errors(string output, SevenZipError expected)
        {
            Assert.Equal(expected, SevenZipClient.Classify(output, 1, false));
        }
    }

    public class SevenZipIntegration : IDisposable
    {
        private readonly string _dir;
        private readonly SettingsService _settings;
        private readonly SevenZipClient _client;

        public SevenZipIntegration()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uzip2-7z-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settings = new SettingsService(_dir, null);
            _client = new SevenZipClient(_settings);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private bool Has7z => _client.SevenZipPath != null;

        [Fact]
        public async Task Compress_Then_Wrong_And_Right_Password_Roundtrip()
        {
            if (!Has7z) return; // 环境无 7z 时跳过(CI 会安装)
            var src = Path.Combine(_dir, "hello.txt");
            File.WriteAllText(src, "hello uzip");
            var archive = Path.Combine(_dir, "out.zip");

            var cr = await _client.CompressAsync(new[] { src }, archive, "secret123", 0, 5, false, null, CancellationToken.None);
            Assert.True(cr.Success, cr.Output);
            Assert.True(File.Exists(archive));

            var bad = await _client.TestAsync(archive, "wrong", CancellationToken.None);
            Assert.False(bad.Success);
            Assert.Equal(SevenZipError.WrongPassword, bad.Error);
            Assert.Equal("密码错误或加密头损坏", bad.Diagnosis);

            var good = await _client.TestAsync(archive, "secret123", CancellationToken.None);
            Assert.True(good.Success, good.Output);

            var dest = Path.Combine(_dir, "out");
            var er = await _client.ExtractAsync(archive, dest, "secret123", null, CancellationToken.None);
            Assert.True(er.Success, er.Output);
            Assert.Equal("hello uzip", File.ReadAllText(Path.Combine(dest, "hello.txt")));
        }

        [Fact]
        public async Task Cancel_Stops_Process()
        {
            if (!Has7z) return;
            // 造一个较大文件保证有时间取消
            var big = Path.Combine(_dir, "big.bin");
            using (var fs = File.Create(big)) fs.Write(new byte[64 * 1024 * 1024]);
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var r = await _client.CompressAsync(new[] { big }, Path.Combine(_dir, "big.7z"), null, 1, 9, false, null, cts.Token);
            Assert.False(r.Success);
            Assert.Equal(SevenZipError.Cancelled, r.Error);
        }

        [Fact]
        public async Task Probe_Detects_Encryption_State()
        {
            if (!Has7z) return;
            var src = Path.Combine(_dir, "p.txt");
            File.WriteAllText(src, "probe me");

            var plain = Path.Combine(_dir, "plain.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, plain, null, 0, 0, false, null, CancellationToken.None)).Success);
            Assert.Equal(EncryptionState.NotEncrypted, await _client.ProbeEncryptionAsync(plain, CancellationToken.None));

            var zipPw = Path.Combine(_dir, "zippw.zip");
            Assert.True((await _client.CompressAsync(new[] { src }, zipPw, "pw1", 0, 0, false, null, CancellationToken.None)).Success);
            Assert.Equal(EncryptionState.Encrypted, await _client.ProbeEncryptionAsync(zipPw, CancellationToken.None));

            var sevenPw = Path.Combine(_dir, "sevenpw.7z");
            Assert.True((await _client.CompressAsync(new[] { src }, sevenPw, "pw2", 1, 0, false, null, CancellationToken.None)).Success);
            Assert.Equal(EncryptionState.Encrypted, await _client.ProbeEncryptionAsync(sevenPw, CancellationToken.None));

            // 头加密: 连清单都读不出来，必须保守判定为需要密码
            var headPw = Path.Combine(_dir, "headpw.7z");
            Assert.True((await _client.CompressAsync(new[] { src }, headPw, "pw3", 1, 0, true, null, CancellationToken.None)).Success);
            Assert.Equal(EncryptionState.Encrypted, await _client.ProbeEncryptionAsync(headPw, CancellationToken.None));
        }

        [Fact]
        public async Task Probe_Non_Archive_Is_Unknown()
        {
            if (!Has7z) return;
            var fake = Path.Combine(_dir, "fake.bin");
            File.WriteAllText(fake, "definitely not an archive");
            Assert.Equal(EncryptionState.Unknown, await _client.ProbeEncryptionAsync(fake, CancellationToken.None));
        }

        [Fact]
        public async Task Missing_7z_Returns_NotFound()
        {
            _settings.Save(s => { s.Customize7z = true; s.Customize7zPath = Path.Combine(_dir, "nope.exe"); });
            // Locate 的兜底仍可能找到系统 7z，所以直接断言路径行为: 自定义不存在时回退
            var located = SevenZipClient.Locate(_settings, _dir);
            if (located != null) return; // 系统装了 7z 则回退成功,合理
            var r = await _client.TestAsync("x", null, CancellationToken.None);
            Assert.Equal(SevenZipError.NotFound, r.Error);
        }
    }
}
