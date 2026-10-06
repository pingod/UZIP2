using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

        // GUI 和 CLI 是两个进程，同一份 Config 会被并发写。固定 .tmp 名时，
        // 甲的 Move 会把乙刚写好的 .tmp 搬走，乙随后 Move 一个不存在的文件直接抛异常。
        [Fact]
        public void Concurrent_writers_never_throw_or_leave_temp()
        {
            var payloads = new[] { "aaaa", "bbbb", "cccc", "dddd" };
            var errors = new List<Exception>();
            var threads = payloads.Select(p => new Thread(() =>
            {
                try { for (int i = 0; i < 80; i++) AtomicFile.Write(_path, p); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            Assert.Empty(errors);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            Assert.Contains(File.ReadAllText(_path), payloads);
        }
    }
}
