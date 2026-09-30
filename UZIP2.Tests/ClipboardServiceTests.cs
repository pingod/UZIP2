using System;
using System.IO;
using System.Linq;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class ClipboardServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly ClipboardService _clipboard;

        public ClipboardServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UZipClipTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settings = new SettingsService(_dir, null);
            _passwords = new PasswordService(_dir, _settings);
            _clipboard = new ClipboardService(_passwords, _settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Theory]
        [InlineData(null, false, null)]
        [InlineData("", false, null)]
        [InlineData("  pw1 \t\r\n", true, "pw1")]
        [InlineData("pw2\tX", false, "pw2X")]           // 制表符总是被清除
        [InlineData(" a \n b ", false, " a \n b ")]    // TrimSpace=false 保留首尾空格
        public void Clean_rules(string raw, bool trim, string expected)
        {
            Assert.Equal(expected, ClipboardService.Clean(raw, trim));
        }

        [Fact]
        public void PasteToPaper_splits_lines_dedups_and_counts()
        {
            _settings.Current.TrimSpace = true;
            int added = _passwords.PasteToPaper("p1\np2\np1\n\n  p3  ");
            Assert.Equal(3, added);
            Assert.Equal(new[] { "p1", "p2", "p3" }, _passwords.Paper.Select(p => p.Text).ToArray());

            // 再贴同一批: 全去重, 新增 0
            Assert.Equal(0, _passwords.PasteToPaper("p1\np2\np3"));
        }

        [Fact]
        public void PasteToPaper_respects_200_limit()
        {
            var many = string.Join("\n", Enumerable.Range(1, 250).Select(i => "pw" + i));
            int added = _passwords.PasteToPaper(many);
            Assert.Equal(PasswordService.PaperLimit, added);
            Assert.Equal(200, _passwords.Paper.Count);
        }

        [Fact]
        public void Book_entries_block_paper_duplicates()
        {
            _passwords.AddBook("永久", "samepw");
            Assert.Equal(0, _passwords.PasteToPaper("samepw"));
        }
    }
}
