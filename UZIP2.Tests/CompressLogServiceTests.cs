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
        public void Parse_reads_back_every_field()
        {
            var svc = new CompressLogService(_dir);
            svc.Log(@"D:\out\第一季.7z", "pw 1");
            svc.Log(@"D:\out\nopw.zip");

            var records = svc.ReadAll();
            Assert.Equal(2, records.Count);
            // 新记录排在前面，检索界面第一眼看到最近一次
            Assert.Equal(@"D:\out\nopw.zip", records[0].Path);
            Assert.Null(records[0].Password);
            Assert.Equal("nopw.zip", records[0].FileName);
            Assert.Equal(@"D:\out\第一季.7z", records[1].Path);
            Assert.Equal("pw 1", records[1].Password);
            Assert.NotEqual(default, records[1].Time);
        }

        [Fact]
        public void Parse_tolerates_hand_edited_and_truncated_entries()
        {
            var text = string.Join("\n", new[]
            {
                "2026-01-01 08:00:00    文件名称：a.zip    解压密码：aaa",
                "输出目录：D:\\a.zip",
                "",
                "这一行是用户手工加备注，没有任何字段",
                "D:\\loose.zip",
                "",
                "垃圾时间    文件名称：b.zip    解压密码：bbb",   // 缺输出目录，整条丢弃
                "",
                "2026-01-02 09:00:00    解压密码：ccc",           // 缺文件名称，密码仍可用
                "输出目录：D:\\c.zip",
                "",
            });

            var records = CompressLogService.Parse(text);
            Assert.Equal(3, records.Count);
            // 新记录排在前面
            Assert.Equal("ccc", records[0].Password);
            Assert.Equal(@"D:\c.zip", records[0].Path);
            Assert.Null(records[0].FileName);
            Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0), records[0].Time);
            // 没有字段名的裸行当成路径
            Assert.Equal(@"D:\loose.zip", records[1].Path);
            Assert.Null(records[1].Password);
            Assert.Equal("aaa", records[2].Password);
            Assert.Equal("a.zip", records[2].FileName);
            Assert.NotEqual(default, records[2].Time);
        }

        [Fact]
        public void Parse_handles_empty_input()
        {
            Assert.Empty(CompressLogService.Parse(null));
            Assert.Empty(CompressLogService.Parse("   \n\n "));
        }

        [Fact]
        public void ReadAll_returns_nothing_when_log_missing()
        {
            Assert.Empty(new CompressLogService(Path.Combine(_dir, "never-created")).ReadAll());
        }

        [Fact]
        public void Turning_password_logging_off_masks_the_password()
        {
            var settings = new SettingsService(_dir, null);
            settings.Save(s => s.LogPasswords = false);
            var svc = new CompressLogService(Path.Combine(_dir, "off"), settings);
            svc.Log(@"D:\out\pack.7z", "secret");

            var text = File.ReadAllText(svc.LogPath, Encoding.UTF8);
            Assert.DoesNotContain("secret", text);
            Assert.Contains(@"D:\out\pack.7z", text);
            Assert.Null(Assert.Single(svc.ReadAll()).Password);
        }

        [Fact]
        public void Password_logging_stays_on_by_default()
        {
            var settings = new SettingsService(_dir, null);
            var svc = new CompressLogService(Path.Combine(_dir, "on"), settings);
            svc.Log(@"D:\out\pack.7z", "secret");
            Assert.Contains("secret", File.ReadAllText(svc.LogPath, Encoding.UTF8));
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
