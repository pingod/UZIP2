using System;
using System.IO;
using System.Linq;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class MigratorTests : IDisposable
    {
        private readonly string _dir;
        public MigratorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uzip2-mig-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string MainConfig = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""AppMode"" value=""1"" />
    <add key=""ExtractCoverMode"" value=""-aoa"" />
    <add key=""CompressType"" value=""1"" />
    <add key=""CompressLevel"" value=""7"" />
    <add key=""Customize7z"" value=""True"" />
    <add key=""Customize7zPath"" value=""C:\7-Zip\7z.exe"" />
    <add key=""WindowLeft"" value=""12.5"" />
    <add key=""WindowTop"" value=""-1"" />
    <add key=""TrimSpace"" value=""True"" />
    <add key=""AutoOpenAfterExtract"" value=""False"" />
    <add key=""CustomizeFolderPath2"" value=""D:\MOV"" />
    <add key=""CustomizeFolderName2"" value=""电影"" />
    <add key=""CustomizePassword1"" value=""myzip123"" />
    <add key=""BackPageNum"" value=""4"" />
    <add key=""PWUrl"" value=""http://example.com/pw.txt"" />
    <add key=""ReadPasswordMode"" value=""3"" />
  </appSettings>
</configuration>";

        private const string NoteConfig = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""PWNote0"" value=""plainbookpw"" />
    <add key=""PWNote1"" value=""{0}"" />
    <add key=""Score_PWNote1"" value=""{1}"" />
  </appSettings>
</configuration>";

        private const string PageConfig = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""PWPaper0"" value=""{0}"" />
  </appSettings>
</configuration>";

        [Fact]
        public void Migrates_Settings_Books_And_Baks_Files()
        {
            // 用本机 DPAPI 造一条真密文(测试与运行同用户，可解)
            var dpapiBook = Dpapi.Encode("encbookpw");
            var scoreValue = Dpapi.Encode("encbookpw") + ":7";
            var dpapiPage = Dpapi.Encode("paperpw");

            File.WriteAllText(Path.Combine(_dir, "UZip.config"), MainConfig);
            File.WriteAllText(Path.Combine(_dir, "PasswordNote.config"),
                string.Format(NoteConfig, dpapiBook, scoreValue));
            File.WriteAllText(Path.Combine(_dir, "PasswordPage.config"),
                string.Format(PageConfig, dpapiPage));

            var settings = new SettingsService(_dir, null);
            var passwords = new PasswordService(_dir, settings);

            var result = LegacyConfigMigrator.TryMigrate(_dir, settings, passwords);

            Assert.True(result.Performed);
            Assert.True(result.Success, result.Reason);

            var s = settings.Current;
            Assert.Equal(1, s.AppMode);
            Assert.Equal("-aoa", s.ExtractCoverMode);
            Assert.Equal(1, s.CompressType);
            Assert.Equal(7, s.CompressLevel);
            Assert.True(s.Customize7z);
            Assert.Equal(@"C:\7-Zip\7z.exe", s.Customize7zPath);
            Assert.Equal(12.5, s.WindowLeft);
            Assert.True(s.TrimSpace);
            Assert.False(s.AutoOpenAfterExtract);
            Assert.Equal("myzip123", s.CustomPasswords[0]);
            Assert.Equal(3, s.ReadPasswordMode);
            Assert.Equal("http://example.com/pw.txt", s.PWUrl);
            var folder = Assert.Single(s.CustomizeFolders);
            Assert.Equal("电影", folder.Name);
            Assert.Equal(@"D:\MOV", folder.Path);

            var reloaded = new PasswordService(_dir, settings);
            Assert.Equal(2, reloaded.Book.Count);
            var texts = reloaded.Book.Select(b => b.Text).ToArray();
            Assert.Contains("plainbookpw", texts);
            Assert.Contains("encbookpw", texts);
            var scored = reloaded.Book.First(b => b.Text == "encbookpw");
            Assert.Equal(7, scored.SuccessCount);
            Assert.Equal("paperpw", Assert.Single(reloaded.Paper).Text);
            // 试密码顺序: 有 7 次成功记录的排第一
            Assert.Equal("encbookpw", reloaded.CandidatePasswords()[0]);

            // 旧文件改名 .bak，新文件生成
            Assert.False(File.Exists(Path.Combine(_dir, "UZip.config")));
            Assert.True(File.Exists(Path.Combine(_dir, "UZip.config.bak")));
            Assert.True(File.Exists(Path.Combine(_dir, "PasswordNote.config.bak")));
            Assert.True(File.Exists(Path.Combine(_dir, "PasswordPage.config.bak")));
            Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(_dir, "passwords.json")));

            // 幂等: 再跑一次不动作
            var again = LegacyConfigMigrator.TryMigrate(_dir, settings, reloaded);
            Assert.False(again.Performed);
        }

        [Fact]
        public void No_Legacy_Files_Does_Nothing()
        {
            var settings = new SettingsService(_dir, null);
            var passwords = new PasswordService(_dir, settings);
            var r = LegacyConfigMigrator.TryMigrate(_dir, settings, passwords);
            Assert.False(r.Performed);
        }
    }
}
