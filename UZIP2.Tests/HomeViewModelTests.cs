using System;
using System.Collections.Generic;
using IO = System.IO;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    public class HomeViewModelTests : IDisposable
    {
        private readonly string _dir;

        public HomeViewModelTests()
        {
            _dir = IO.Path.Combine(IO.Path.GetTempPath(), "UZIP2_HomeVm_" + Guid.NewGuid().ToString("N"));
            IO.Directory.CreateDirectory(_dir);
        }

        private string Make(string name)
        {
            var p = IO.Path.Combine(_dir, name);
            IO.File.WriteAllText(p, "x");
            return p;
        }

        public void Dispose()
        {
            try { IO.Directory.Delete(_dir, true); } catch { }
        }

        // ---- 模式常量: 0=自动 1=仅解压 2=仅压缩 ----

        [Fact]
        public void Auto_MixedArchiveAndFiles_PreviewCountsBoth()
        {
            var zip = Make("a.zip");
            var f1 = Make("b.txt");
            var f2 = Make("c.txt");
            var p = HomeViewModel.PreviewFor(0, new List<string> { zip, f1, f2 });
            Assert.False(p.IsWarning);
            Assert.Contains("解压 1", p.Text);
            Assert.Contains("压缩 2", p.Text);
        }

        [Fact]
        public void Auto_OnlyArchives_PreviewExtractOnly()
        {
            var zip = Make("a.zip");
            var r = Make("b.rar");
            var p = HomeViewModel.PreviewFor(0, new List<string> { zip, r });
            Assert.False(p.IsWarning);
            Assert.Contains("解压 2", p.Text);
            Assert.DoesNotContain("、压缩", p.Text);
        }

        [Fact]
        public void CompressMode_Folder_IsLegal()
        {
            var sub = IO.Path.Combine(_dir, "folder");
            IO.Directory.CreateDirectory(sub);
            var p = HomeViewModel.PreviewFor(2, new List<string> { sub });
            Assert.False(p.IsWarning);
            Assert.Contains("压缩 1", p.Text);
        }

        [Fact]
        public void ExtractMode_OnlyFolders_Warns()
        {
            var sub = IO.Path.Combine(_dir, "folder");
            IO.Directory.CreateDirectory(sub);
            var p = HomeViewModel.PreviewFor(1, new List<string> { sub });
            Assert.True(p.IsWarning);
        }

        [Fact]
        public void ExtractMode_Mixed_WarnsAndCountsIgnored()
        {
            var zip = Make("a.zip");
            var txt = Make("note.txt");
            var sub = IO.Path.Combine(_dir, "folder");
            IO.Directory.CreateDirectory(sub);
            var p = HomeViewModel.PreviewFor(1, new List<string> { zip, txt, sub });
            Assert.True(p.IsWarning);
            Assert.Contains("解压 1", p.Text);
            Assert.Contains("忽略", p.Text);
        }

        [Fact]
        public void Auto_EmptyDrop_NoValidFiles()
        {
            var p = HomeViewModel.PreviewFor(0, new List<string>());
            Assert.True(p.IsWarning);
            Assert.Contains("无有效文件", p.Text);
        }

        [Fact]
        public void Archive_WithUnknownExtension_NotCountedAsArchive()
        {
            var f = Make("data.bin");
            var p = HomeViewModel.PreviewFor(0, new List<string> { f });
            // 自动模式下 .bin 不是压缩包 -> 归入压缩侧
            Assert.Contains("压缩 1", p.Text);
        }

        [Fact]
        public void VolumePart_CountedAsArchive()
        {
            var v = Make("big.zip.001");
            var p = HomeViewModel.PreviewFor(1, new List<string> { v });
            Assert.False(p.IsWarning);
            Assert.Contains("解压 1", p.Text);
        }
    }
}
