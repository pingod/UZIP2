using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class SettingsServiceTests : IDisposable
    {
        private readonly string _dir;

        public SettingsServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uzip2-settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private SettingsService NewService() => new SettingsService(_dir, null);

        [Fact]
        public void Missing_File_Creates_Defaults()
        {
            var svc = NewService();
            Assert.Equal("-aos", svc.Current.ExtractCoverMode);
            Assert.True(svc.Current.AutoOpenAfterExtract);
            Assert.False(svc.Current.TrimSpace);
            Assert.True(File.Exists(svc.SettingsPath));
        }

        [Fact]
        public void Save_Reload_Preserves_Values()
        {
            var svc = NewService();
            svc.Save(s =>
            {
                s.ExtractFilter = "广告*.txt";
                s.AppMode = 2;
                s.CustomizeFolders.Add(new CustomFolder { Name = "电影", Path = @"D:\MOV" });
            });

            var reloaded = NewService();
            Assert.Equal("广告*.txt", reloaded.Current.ExtractFilter);
            Assert.Equal(2, reloaded.Current.AppMode);
            Assert.Single(reloaded.Current.CustomizeFolders);
            Assert.Equal(@"D:\MOV", reloaded.Current.CustomizeFolders[0].Path);
        }

        [Fact]
        public void Changed_Fires_After_Save()
        {
            var svc = NewService();
            AppSettings? received = null;
            svc.Changed += s => received = s;
            svc.Save(s => s.WindowOnTop = true);
            Assert.NotNull(received);
            Assert.True(received.WindowOnTop);
        }

        [Fact]
        public void Concurrent_Saves_Keep_Json_Valid()
        {
            var svc = NewService();
            Parallel.For(0, 32, i => svc.Save(s => s.ExtractOutMode = i % 4));

            var text = File.ReadAllText(svc.SettingsPath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(text);
            Assert.NotNull(parsed);
        }

        [Fact]
        public void Corrupt_File_Is_Backup_And_Recreated()
        {
            File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json !!");
            var svc = NewService();
            Assert.Equal("-aos", svc.Current.ExtractCoverMode); // 回退默认值
            Assert.NotEmpty(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        }
    }
}
