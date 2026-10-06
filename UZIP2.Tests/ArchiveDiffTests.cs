using System;
using System.Collections.Generic;
using System.Linq;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 两个包的清单对比：纯逻辑，输入是 7z 解析出来的条目列表。
    public class ArchiveDiffTests
    {
        static ArchiveEntry F(string path, long size = 10)
            => new ArchiveEntry(path, size, false, "LZMA");

        static ArchiveEntry Dir(string path)
            => new ArchiveEntry(path, 0, true, "");

        [Fact]
        public void Identical_lists_report_no_difference()
        {
            var a = new[] { F("x.txt"), F("y.txt") };
            var b = new[] { F("x.txt"), F("y.txt") };
            var d = ArchiveDiff.Compare(a, b);

            Assert.True(d.Identical);
            Assert.Equal(2, d.SameCount);
            Assert.Contains("一致", d.Summarize());
        }

        [Fact]
        public void Items_present_on_one_side_only_are_listed_on_that_side()
        {
            var a = new[] { F("keep.txt"), F("gone.txt") };
            var b = new[] { F("keep.txt"), F("new.txt") };
            var d = ArchiveDiff.Compare(a, b);

            Assert.Equal(new[] { "gone.txt" }, d.OnlyInLeft);
            Assert.Equal(new[] { "new.txt" }, d.OnlyInRight);
            Assert.Equal(1, d.SameCount);
            Assert.False(d.Identical);
        }

        // 同名不同大小必须算"变化"，否则一次内容替换会被拆成一删一增，看不出是同一个文件
        [Fact]
        public void Same_path_different_size_is_a_change_not_an_add_and_a_delete()
        {
            var a = new[] { F("big.bin", 1024) };
            var b = new[] { F("big.bin", 2048) };
            var d = ArchiveDiff.Compare(a, b);

            Assert.Empty(d.OnlyInLeft);
            Assert.Empty(d.OnlyInRight);
            Assert.Single(d.Changed);
            Assert.Equal("big.bin", d.Changed[0].Path);
            Assert.Equal(1024, d.Changed[0].LeftSize);
            Assert.Equal(2048, d.Changed[0].RightSize);
        }

        // zip 用 /、7z 用 \，且 Windows 路径不区分大小写；分隔符和大小写不能造成假差异
        [Fact]
        public void Separator_and_casing_differences_are_not_reported()
        {
            var a = new[] { F("Docs/Readme.TXT") };
            var b = new[] { F(@"Docs\readme.txt") };
            var d = ArchiveDiff.Compare(a, b);

            Assert.True(d.Identical);
        }

        // 目录条目两边的 Size 恒为 0，列不列出来取决于打包器；带进来只会产生噪音
        [Fact]
        public void Folder_entries_are_skipped()
        {
            var a = new[] { Dir("folder"), F("a.txt") };
            var b = new[] { F("a.txt") };
            var d = ArchiveDiff.Compare(a, b);

            Assert.True(d.Identical);
        }

        [Fact]
        public void Result_lists_are_sorted_so_output_is_stable()
        {
            var a = new[] { F("z.txt"), F("m.txt"), F("a.txt") };
            var d = ArchiveDiff.Compare(a, Array.Empty<ArchiveEntry>());

            Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }, d.OnlyInLeft);
        }

        [Fact]
        public void Summarize_names_all_four_counts()
        {
            var a = new[] { F("only-left.txt"), F("changed.txt", 1), F("same.txt") };
            var b = new[] { F("only-right.txt"), F("changed.txt", 9), F("same.txt") };
            var s = ArchiveDiff.Compare(a, b).Summarize();

            Assert.Contains("仅左 1", s);
            Assert.Contains("仅右 1", s);
            Assert.Contains("大小不同 1", s);
            Assert.Contains("相同 1", s);
        }
    }
}
