using System;
using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class ArchiveInspectorTests : IDisposable
    {
        private readonly string _dir;

        public ArchiveInspectorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipInspTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Write(string name, byte[] bytes)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private static byte[] WithUstar(byte[] head)
        {
            // tar 判定要求文件长度 >= 0x106 且 0x101 处为 "ustar"
            var buf = new byte[0x110];
            Array.Copy(head, buf, head.Length);
            buf[0x101] = 0x75; buf[0x102] = 0x73; buf[0x103] = 0x74; buf[0x104] = 0x61; buf[0x105] = 0x72;
            return buf;
        }

        [Fact]
        public void RealExtension_zip_magic()
        {
            var p = Write("a.bin", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 });
            Assert.Equal(".zip", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_rar_magic()
        {
            var p = Write("a.bin", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01 });
            Assert.Equal(".rar", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_7z_magic()
        {
            var p = Write("a.bin", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 });
            Assert.Equal(".7z", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_gz_magic()
        {
            var p = Write("a.bin", new byte[] { 0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0 });
            Assert.Equal(".gz", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_xz_magic()
        {
            var p = Write("a.bin", new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0, 0 });
            Assert.Equal(".xz", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_tar_ustar_at_offset()
        {
            var p = Write("a.bin", WithUstar(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.Equal(".tar", ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_plain_text_returns_null()
        {
            var p = Write("a.txt", new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F });
            Assert.Null(ArchiveInspector.RealExtension(p));
        }

        [Fact]
        public void RealExtension_missing_file_returns_null()
        {
            Assert.Null(ArchiveInspector.RealExtension(Path.Combine(_dir, "nope.zip")));
        }

        [Theory]
        [InlineData("x.zip", true)]
        [InlineData("x.rar", true)]
        [InlineData("x.7z", true)]
        [InlineData("x.001", true)]
        [InlineData("x.txt", false)]
        [InlineData("x.exe", false)]
        [InlineData("noext", false)]
        public void CanExtractByExtension_whitelist(string name, bool expected)
        {
            Assert.Equal(expected, ArchiveInspector.CanExtractByExtension(name));
        }

        [Fact]
        public void Volume_rar_partN_main_is_part1()
        {
            var v = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "movie.part2.rar"));
            Assert.True(v.IsVolume);
            Assert.Equal("rar", v.ZipType);
            Assert.Equal("movie", v.BaseName);
            Assert.Equal(Path.Combine(_dir, "movie.part1.rar"), v.MainVolumePath);
        }

        [Fact]
        public void Volume_rar_first_part_still_volume()
        {
            var v = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "movie.part1.rar"));
            Assert.True(v.IsVolume);
            Assert.Equal(Path.Combine(_dir, "movie.part1.rar"), v.MainVolumePath);
        }

        [Fact]
        public void Volume_zip_split_zNN_main_is_zip()
        {
            var v = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "b.z01"));
            Assert.True(v.IsVolume);
            Assert.Equal("zip-z", v.ZipType);
            Assert.Equal(Path.Combine(_dir, "b.zip"), v.MainVolumePath);
        }

        [Fact]
        public void Volume_numeric_00N_requires_archive_base_ext()
        {
            var v = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "a.zip.002"));
            Assert.True(v.IsVolume);
            Assert.Equal("zip", v.ZipType);
            Assert.Equal("a", v.BaseName);
            Assert.Equal(Path.Combine(_dir, "a.zip.001"), v.MainVolumePath);

            var n = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "doc.txt.002"));
            Assert.False(n.IsVolume);
            Assert.Null(n.MainVolumePath);
        }

        [Fact]
        public void Volume_plain_archive_is_not_volume()
        {
            var v = ArchiveInspector.AnalyzeVolume(Path.Combine(_dir, "x.zip"));
            Assert.False(v.IsVolume);
            Assert.Null(v.MainVolumePath);
            Assert.Equal("x", v.BaseName);
        }

        [Fact]
        public void Inspect_real_zip_with_fake_extension()
        {
            var p = Write("disguise.bin", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 });
            var info = ArchiveInspector.Inspect(p);
            Assert.True(info.IsArchive);
            Assert.Equal(".zip", info.RealExt);
            Assert.False(info.CanExtract); // 扩展名不在白名单
            Assert.Equal(p, info.ExtractTargetPath);
        }

        [Fact]
        public void Inspect_volume_redirects_to_existing_main()
        {
            Write("m.part1.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01 });
            var second = Write("m.part2.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x02 });
            var info = ArchiveInspector.Inspect(second);
            Assert.True(info.Volume.IsVolume);
            Assert.Equal(Path.Combine(_dir, "m.part1.rar"), info.ExtractTargetPath);
        }

        [Fact]
        public void Inspect_volume_missing_main_falls_back_to_self()
        {
            var only = Write("q.part2.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x02 });
            var info = ArchiveInspector.Inspect(only);
            Assert.Equal(only, info.ExtractTargetPath);
        }
    }
}
