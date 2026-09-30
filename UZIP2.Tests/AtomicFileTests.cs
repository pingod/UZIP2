using System;
using System.IO;
using System.Text;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class AtomicFileTests : IDisposable
    {
        readonly string _dir;
        readonly string _path;

        public AtomicFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipAtomicTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "store.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Write_creates_file_and_leaves_no_temp()
        {
            AtomicFile.Write(_path, "{\"a\":1}");
            Assert.Equal("{\"a\":1}", File.ReadAllText(_path));
            Assert.False(File.Exists(_path + ".tmp"));
        }

        [Fact]
        public void Write_overwrites_and_honours_encoding()
        {
            AtomicFile.Write(_path, "第一版");
            AtomicFile.Write(_path, "第二版", new UTF8Encoding(true));
            Assert.Equal("第二版", File.ReadAllText(_path, Encoding.UTF8));
            var raw = File.ReadAllBytes(_path);
            Assert.Equal(0xEF, raw[0]);   // BOM 由调用方决定
        }

        // 连写 200 次是密码纸贴入的真实形状，也是偶发 UnauthorizedAccessException 的场景
        [Fact]
        public void Rapid_rewrites_all_succeed()
        {
            for (int i = 0; i < 200; i++) AtomicFile.Write(_path, "v" + i);
            Assert.Equal("v199", File.ReadAllText(_path));
        }
    }
}
