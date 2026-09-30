using System;
using System.IO;
using System.Text;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class CompressLogServiceTests : IDisposable
    {
        private readonly string _dir;

        public CompressLogServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipLogTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Log_appends_legacy_format()
        {
            var svc = new CompressLogService(_dir);
            svc.Log(@"D:\out\pack.7z", "pw123");
            svc.Log(@"D:\out\nopw.zip");

            var text = File.ReadAllText(svc.LogPath, Encoding.UTF8);
            Assert.Contains("    文件名称：pack.7z    解压密码：pw123\n输出目录：D:\\out\\pack.7z\n", text);
            Assert.Contains("    文件名称：nopw.zip    解压密码：-\n输出目录：D:\\out\\nopw.zip\n", text);
            // 每条记录以 CRLF 结尾(旧 StreamWriter.WriteLine 语义)
            Assert.EndsWith("\n\r\n", text);
        }

        [Fact]
        public void Log_creates_missing_directory_and_keeps_no_bom()
        {
            var sub = Path.Combine(_dir, "cfg");
            var svc = new CompressLogService(sub);
            svc.Log("a.zip", "p");
            byte[] raw = File.ReadAllBytes(svc.LogPath);
            Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
        }

        [Fact]
        public void Log_failures_are_swallowed()
        {
            // 配置目录路径被同名文件阻挡, 创建失败也不应抛异常
            var blocker = Path.Combine(_dir, "blocker");
            File.WriteAllText(blocker, "x");
            var svc = new CompressLogService(Path.Combine(blocker, "sub"));
            var ex = Record.Exception(() => svc.Log("x.zip", "p"));
            Assert.Null(ex);
        }
    }
}
