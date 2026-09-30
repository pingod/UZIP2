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
    public class CompressArgsTests
    {
        static List<string> Build(int type, bool hide = false, string solid = null,
            string threads = null, string volume = null)
            => SevenZipClient.BuildCompressArgs(new[] { "src.bin" }, "out.arc", null, type, 5, hide,
                null, volume, solid, threads);

        [Fact]
        public void Empty_options_emit_no_extra_switches()
        {
            var args = Build(1, solid: "", threads: "", volume: "");
            Assert.DoesNotContain(args, a => a.StartsWith("-ms", StringComparison.Ordinal));
            Assert.DoesNotContain(args, a => a.StartsWith("-mmt", StringComparison.Ordinal));
            Assert.DoesNotContain(args, a => a.StartsWith("-v", StringComparison.Ordinal));
            Assert.DoesNotContain(args, a => a.StartsWith("-mhe", StringComparison.Ordinal));
        }

        [Fact]
        public void Solid_and_header_encryption_only_reach_7z()
        {
            Assert.Contains("-ms=off", Build(1, solid: "off"));
            Assert.DoesNotContain("-ms=off", Build(0, solid: "off"));
            Assert.Contains("-mhe=on", Build(1, hide: true));
            Assert.DoesNotContain("-mhe=on", Build(0, hide: true));
        }

        [Fact]
        public void Threads_reach_every_format()
        {
            Assert.Contains("-mmt=off", Build(1, threads: "off"));
            Assert.Contains("-mmt=2", Build(0, threads: " 2 "));
        }

        [Fact]
        public void Volume_and_password_still_present()
        {
            var args = SevenZipClient.BuildCompressArgs(new[] { "a.bin" }, "o.7z", "pw1", 1, 9, false,
                new[] { "*.tmp" }, "700m", null, null);
            Assert.Contains("-v700m", args);
            Assert.Contains("-ppw1", args);
            Assert.Contains("-xr!*.tmp", args);
            Assert.Contains("-mx9", args);
        }
    }

    // 真跑 7z: 高级参数确实改变产物
    public class CompressOptionsTests : IDisposable
    {
        readonly string _root, _src;
        readonly SettingsService _settings;
        readonly SevenZipClient _client;

        public CompressOptionsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipOptTests_" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            Directory.CreateDirectory(_src);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _client = new SevenZipClient(_settings);
            Assert.NotNull(_client.SevenZipPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // 两份高度相似的文件: 固实能跨文件建字典，非固实只能各自压
        string MakeSimilar(string name, char fill)
        {
            var p = Path.Combine(_src, name);
            File.WriteAllText(p, new string(fill, 200000));
            return p;
        }

        async Task<long> CompressAndGet(string arc, string solid, string threads)
        {
            var a = MakeSimilar("a.txt", 'a');
            var b = MakeSimilar("b.txt", 'b');
            var r = await _client.CompressAsync(new[] { a, b }, Path.Combine(_root, arc), null, 1, 5,
                false, null, CancellationToken.None, null, null, solid, threads);
            Assert.True(r.Success, r.Diagnosis);
            return new FileInfo(Path.Combine(_root, arc)).Length;
        }

        [Fact]
        public async Task Solid_off_produces_bigger_archive_than_on()
        {
            long solid = await CompressAndGet("solid.7z", "on", null);
            long loose = await CompressAndGet("loose.7z", "off", null);
            Assert.True(loose > solid, $"非固实 {loose} 应大于固实 {solid}");
        }

        [Fact]
        public async Task Limited_threads_still_compress_and_extract()
        {
            long size = await CompressAndGet("mt.7z", null, "2");
            Assert.True(size > 0);
            var r = await _client.TestAsync(Path.Combine(_root, "mt.7z"), null, CancellationToken.None);
            Assert.True(r.Success, r.Diagnosis);
        }

        [Fact]
        public async Task Zip_ignores_solid_switch_and_still_works()
        {
            var f = MakeSimilar("z.txt", 'z');
            var arc = Path.Combine(_root, "plain.zip");
            var r = await _client.CompressAsync(new[] { f }, arc, null, 0, 5, false, null,
                CancellationToken.None, null, null, "off", "off");
            Assert.True(r.Success, r.Diagnosis);
            Assert.True((await _client.TestAsync(arc, null, CancellationToken.None)).Success);
        }
    }
}
