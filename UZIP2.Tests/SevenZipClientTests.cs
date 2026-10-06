using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // 7z 26.03 对随机字节伪装的 .zip 的真实输出
        [InlineData("bad3.zip\nOpen ERROR: Cannot open the file as [zip] archive\n\nERRORS:\nIs not archive", SevenZipError.UnsupportedFormat)]
        [InlineData("There are some data after the end of the payload data", SevenZipError.Corrupt)]
        [InlineData("Not enough disk space", SevenZipError.DiskFull)]
        [InlineData("The system cannot find the path specified. being used by another process", SevenZipError.Occupied)]
        public void Classifies_Errors(string output, SevenZipError expected)
        {
            Assert.Equal(expected, SevenZipClient.Classify(output, 1, false));
        }

        // 输出行里没有可辨认关键字时，用 7z 官方退出码表兜底
        // (0 正常 / 1 非致命 / 2 致命 / 3 方法不支持 / 5 数据错误 / 7 不是压缩包 / 255 用户中止)
        [Theory]
        [InlineData("", 0, SevenZipError.None)]
        [InlineData("", 5, SevenZipError.Corrupt)]
        [InlineData("", 7, SevenZipError.UnsupportedFormat)]
        [InlineData("", 3, SevenZipError.UnsupportedFormat)]
        [InlineData("", 255, SevenZipError.Cancelled)]
        [InlineData("", 2, SevenZipError.Corrupt)]
        [InlineData("", 1, SevenZipError.Unknown)]
        [InlineData("", 6, SevenZipError.Unknown)]
        public void Classifies_by_exit_code_when_output_gives_no_hint(string output, int exit, SevenZipError expected)
            => Assert.Equal(expected, SevenZipClient.Classify(output, exit, false));

        // 成功与否只看退出码：7z 的 "Everything is Ok" 是文案，
        // 哪天改一个字就会把真正成功的作业判成失败。
        [Theory]
        [InlineData(0, "", true)]
        [InlineData(0, "Everything is Ok", true)]
        [InlineData(1, "Everything is Ok", false)]
        [InlineData(2, "", false)]
        public void Success_is_decided_by_exit_code(int exit, string output, bool expected)
            => Assert.Equal(expected, SevenZipClient.IsSuccess(exit, output));
    }

    public class SevenZipStreamTests : IDisposable
    {
        readonly string _dir;
        readonly SevenZipClient _client;

        public SevenZipStreamTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipStreamTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _client = new SevenZipClient(new SettingsService(Path.Combine(_dir, "Config"), null));
            Assert.NotNull(_client.SevenZipPath);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // 实测 7z 带 -sccUTF-8 时 stderr 同样是 UTF-8；不设 StandardErrorEncoding
        // 就按系统 ANSI(GBK) 解，中文包名在诊断里变成乱码，关键字匹配也一起失效。
        [Fact]
        public async Task Error_stream_is_decoded_as_utf8()
        {
            var missing = Path.Combine(_dir, "不存在的中文包.7z");
            var r = await _client.TestAsync(missing, null, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Contains("不存在的中文包", r.Output);
        }
    }

    public class ArchiveListingParsing
    {
        // 7z 26.03 `l -slt` 真实片段: 头块有 Path 无 Folder，必须被丢弃
        const string Sample = @"7-Zip 26.03 (x64)

Listing archive: test.zip

--
Path = test.zip
Type = zip
Physical Size = 936

----------
Path = a.txt
Folder = -
Size = 1
Packed Size = 1
Attributes = A
Method = Store
Offset = 0

Path = deep
Folder = +
Size = 0
Attributes = D

Path = deep\nest\c[1].dat
Folder = -
Size = 2
Method = Deflate

Errors: 0
";

        [Fact]
        public void Keeps_Only_Entry_Blocks()
        {
            var e = SevenZipClient.ParseEntries(Sample);
            Assert.Equal(3, e.Count);
            Assert.Equal("a.txt", e[0].Path);
            Assert.False(e[0].IsFolder);
            Assert.Equal(1, e[0].Size);
            Assert.Equal("Store", e[0].Method);
            Assert.True(e[1].IsFolder);
            Assert.Equal("deep\\nest\\c[1].dat", e[2].Path);
            Assert.Equal("Deflate", e[2].Method);
        }

        [Fact]
        public void Empty_Input_Yields_No_Entries()
        {
            Assert.Empty(SevenZipClient.ParseEntries(null));
            Assert.Empty(SevenZipClient.ParseEntries(""));
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
        public async Task List_Entries_And_Selective_Extract()
        {
            if (!Has7z) return;
            var root = Path.Combine(_dir, "src");
            Directory.CreateDirectory(Path.Combine(root, "中文目录"));
            Directory.CreateDirectory(Path.Combine(root, "deep", "nest"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "A");
            File.WriteAllText(Path.Combine(root, "deep", "nest", "c[1].dat"), "BB");
            File.WriteAllText(Path.Combine(root, "中文目录", "说明.txt"), "CCC");

            var archive = Path.Combine(_dir, "multi.zip");
            Assert.True((await _client.CompressAsync(new[] { root }, archive, null, 0, 0, false, null, CancellationToken.None)).Success);

            var listing = await _client.ListEntriesAsync(archive, null, CancellationToken.None);
            Assert.True(listing.Success, listing.Diagnosis);
            Assert.Contains(listing.Entries, x => x.Path.EndsWith("c[1].dat") && !x.IsFolder);
            Assert.Contains(listing.Entries, x => x.Path.Contains("中文目录") && !x.IsFolder);
            Assert.DoesNotContain(listing.Entries, x => x.Path.EndsWith("multi.zip"));

            var pick = listing.Entries
                .Where(x => !x.IsFolder && x.Path.Contains("中文"))
                .Select(x => x.Path).ToList();
            var outDir = Path.Combine(_dir, "out");
            var r = await _client.ExtractAsync(archive, outDir, null, null, CancellationToken.None, "-aos", pick);
            Assert.True(r.Success, r.Output);

            var dropped = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
            Assert.Single(dropped);
            Assert.Equal("说明.txt", Path.GetFileName(dropped[0]));
        }

        [Fact]
        public async Task Listing_Encrypted_Archive_Reports_Wrong_Password()
        {
            if (!Has7z) return;
            var src = Path.Combine(_dir, "s.txt");
            File.WriteAllText(src, "hidden");
            var archive = Path.Combine(_dir, "enc.7z");
            Assert.True((await _client.CompressAsync(new[] { src }, archive, "pw", 1, 0, true, null, CancellationToken.None)).Success);

            var bad = await _client.ListEntriesAsync(archive, "nope", CancellationToken.None);
            Assert.False(bad.Success);
            Assert.Equal(SevenZipError.WrongPassword, bad.Error);
            Assert.NotEmpty(bad.Diagnosis);

            var good = await _client.ListEntriesAsync(archive, "pw", CancellationToken.None);
            Assert.True(good.Success, good.Diagnosis);
            Assert.Contains(good.Entries, x => x.Path.EndsWith("s.txt"));
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
