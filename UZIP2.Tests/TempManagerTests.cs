using System;
using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class TempManagerTests : IDisposable
    {
        private readonly string _dir;

        public TempManagerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipTempTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void CreateSessionTemp_hidden_named_path()
        {
            var temp = TempManager.CreateSessionTemp(_dir, Path.Combine(_dir, "movie.rar"));
            Assert.EndsWith("UZipTemp_movie\\", temp);
            Assert.True(Directory.Exists(temp));
            var attrs = File.GetAttributes(Path.Combine(_dir, "UZipTemp_movie"));
            Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        }

        [Fact]
        public void CreateSessionTemp_same_name_reuses_directory()
        {
            var t1 = TempManager.CreateSessionTemp(_dir, "a\\x.zip");
            File.WriteAllText(t1 + "keep.txt", "1");
            var t2 = TempManager.CreateSessionTemp(_dir, "b\\x.zip");
            Assert.Equal(t1, t2);
            Assert.True(File.Exists(t1 + "keep.txt"));
        }

        [Fact]
        public void CreateSessionTemp_token_splits_same_named_archives()
        {
            var t1 = TempManager.CreateSessionTemp(_dir, "a\\x.zip", "11");
            var t2 = TempManager.CreateSessionTemp(_dir, "b\\x.zip", "12");
            Assert.NotEqual(t1, t2);
            Assert.EndsWith("UZipTemp_x_11\\", t1);
            Assert.True(Directory.Exists(t1) && Directory.Exists(t2));
            Assert.True(File.GetAttributes(t1.TrimEnd('\\')).HasFlag(FileAttributes.Hidden));
        }

        [Fact]
        public void CreateSessionTemp_accepts_trailing_slash_output()
        {
            var withSlash = TempManager.CreateSessionTemp(_dir + "\\", Path.Combine(_dir, "n.zip"));
            Assert.EndsWith("UZipTemp_n\\", withSlash);
            Assert.True(Directory.Exists(withSlash));
        }

        [Fact]
        public void CleanupOnStartup_removes_only_prefixed_dirs()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "UZipTemp_left"));
            File.WriteAllText(Path.Combine(_dir, "UZipTemp_left", "f.txt"), "x");
            Directory.CreateDirectory(Path.Combine(_dir, "Normal"));

            int cleaned = TempManager.CleanupOnStartup(_dir);

            Assert.Equal(1, cleaned);
            Assert.False(Directory.Exists(Path.Combine(_dir, "UZipTemp_left")));
            Assert.True(Directory.Exists(Path.Combine(_dir, "Normal")));
        }

        [Fact]
        public void CleanupOnStartup_missing_base_dir_is_silent()
        {
            Assert.Equal(0, TempManager.CleanupOnStartup(Path.Combine(_dir, "nope")));
        }
    }
}
