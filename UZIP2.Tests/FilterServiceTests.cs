using System;
using System.IO;
using System.Linq;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class FilterServiceTests : IDisposable
    {
        private readonly string _dir;

        public FilterServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipFilterTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Make(string rel)
        {
            var p = Path.Combine(_dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, "x");
            return p;
        }

        [Fact]
        public void ParseRules_normalizes_semicolons_and_spaces()
        {
            Assert.Equal(new[] { "广告.txt", "*.url" }, FilterService.ParseRules(" 广告.txt ;;  *.url; "));
            Assert.Null(FilterService.ParseRules(";;;"));
            Assert.Null(FilterService.ParseRules(null));
        }

        [Fact]
        public void FindFiles_recursive_with_chinese_and_wildcard()
        {
            Make("广告.txt");
            Make("sub\\游戏说明.txt");
            Make("sub\\keep.bin");
            Make("root.url");

            var hit = FilterService.FindFiles(_dir, "*.txt");
            Assert.Equal(2, hit.Count);
            Assert.Contains(hit, f => f.EndsWith("广告.txt"));
            Assert.Contains(hit, f => f.EndsWith("游戏说明.txt"));

            Assert.Single(FilterService.FindFiles(_dir, "*.url"));
        }

        [Fact]
        public void Apply_deletes_matched_and_counts()
        {
            Make("a.url");
            Make("deep\\inner\\b.url");
            Make("safe.txt");

            int n = FilterService.Apply(_dir, "*.url");
            Assert.Equal(2, n);
            Assert.False(File.Exists(Path.Combine(_dir, "a.url")));
            Assert.True(File.Exists(Path.Combine(_dir, "safe.txt")));
        }

        [Fact]
        public void NextFree_path_appends_two_digit_suffix()
        {
            var target = Make("out\\file.dat");
            var next = FilterService.NextFreePath(target, "-New");
            Assert.EndsWith("file-New01.dat", next);

            File.WriteAllText(next, "y");
            var next2 = FilterService.NextFreePath(target, "-New");
            Assert.EndsWith("file-New02.dat", next2);
        }

        [Fact]
        public void MoveFolder_pass_skips_existing()
        {
            var src = Path.Combine(_dir, "src");
            var dst = Path.Combine(_dir, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "x.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "x.txt"), "old");

            FilterService.MoveFolder(src, dst, CoverModes.Pass);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "x.txt")));
            // 旧语义: Pass 不移动同名文件，源文件留待调用方随临时目录整体删除
            Assert.True(File.Exists(Path.Combine(src, "x.txt")));
        }

        [Fact]
        public void MoveFolder_cover_overwrites()
        {
            var src = Path.Combine(_dir, "src");
            var dst = Path.Combine(_dir, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "x.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "x.txt"), "old");

            FilterService.MoveFolder(src, dst, CoverModes.Cover);
            Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "x.txt")));
        }

        [Fact]
        public void MoveFolder_rename_old_keeps_both()
        {
            var src = Path.Combine(_dir, "src");
            var dst = Path.Combine(_dir, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "x.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "x.txt"), "old");

            FilterService.MoveFolder(src, dst, CoverModes.RenameOld);
            Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "x.txt")));
            Assert.True(File.Exists(Path.Combine(dst, "x-Old01.txt")));
        }

        [Fact]
        public void MoveFolder_recurses_subdirs_across_created_dest()
        {
            Make("src\\sub\\nested.dat");
            var dst = Path.Combine(_dir, "dst");
            FilterService.MoveFolder(Path.Combine(_dir, "src"), dst);
            Assert.True(File.Exists(Path.Combine(dst, "sub", "nested.dat")));
        }

        [Fact]
        public void Delete_removes_file_and_directory()
        {
            var f = Make("del\\trash.bin");
            FilterService.Delete(f);
            Assert.False(File.Exists(f));
            var d = Path.Combine(_dir, "del");
            FilterService.Delete(d);
            Assert.False(Directory.Exists(d));
        }
    }
}
