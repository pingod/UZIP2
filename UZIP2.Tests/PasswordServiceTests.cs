using System;
using System.IO;
using System.Linq;
using UZIP2.Models;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class PasswordServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly SettingsService _settings;

        public PasswordServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uzip2-pw-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settings = new SettingsService(_dir, null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private PasswordService NewService() => new PasswordService(_dir, _settings);

        [Fact]
        public void Dpapi_Roundtrip_And_Persistence()
        {
            var svc = NewService();
            svc.AddBook("论坛A", "secre§词");
            var reloaded = NewService();
            Assert.Single(reloaded.Book);
            Assert.Equal("secre§词", reloaded.Book[0].Text);
            // 落盘文件里不得出现明文
            var raw = File.ReadAllText(svc.StorePath);
            Assert.DoesNotContain("secre§词", raw);
            Assert.Contains("DPAPI:", raw);
        }

        [Fact]
        public void AddBook_Dedups()
        {
            var svc = NewService();
            svc.AddBook("a", "dup");
            svc.AddBook("b", "dup");
            Assert.Single(svc.Book);
        }

        [Fact]
        public void CandidatePasswords_SortsBySuccessDesc()
        {
            var svc = NewService();
            svc.AddBook("n1", "rare");
            svc.AddBook("n2", "常用");
            svc.AddBook("n3", "mid");
            svc.ReportResult("常用", true);
            svc.ReportResult("常用", true);
            svc.ReportResult("mid", true);
            svc.ReportResult("rare", false);

            var order = NewService().CandidatePasswords().ToList();
            Assert.Equal(new[] { "常用", "mid", "rare" }, order);
        }

        [Fact]
        public void Paper_Dedups_Against_Itself_And_Book_And_Trims()
        {
            _settings.Save(s => s.TrimSpace = true);
            var svc = NewService();
            svc.AddBook("b", "bookpw");
            svc.PasteToPaper("bookpw\n  newpw \nnewpw\n");
            Assert.Single(svc.Paper);           // bookpw 与书重复被跳过；newpw 去重；trim 生效
            Assert.Equal("newpw", svc.Paper[0].Text);
        }

        [Fact]
        public void TrimSpace_False_Keeps_Whitespace()
        {
            _settings.Save(s => s.TrimSpace = false);
            var svc = NewService();
            svc.PasteToPaper(" keep me ");
            Assert.Equal(" keep me ", svc.Paper[0].Text);
        }

        [Fact]
        public void ExternalPasswords_Mode1_InternalList()
        {
            _settings.Save(s => s.ReadPasswordMode = 1);
            var svc = NewService();
            Assert.Contains("password1", svc.ExternalPasswords());
        }

        [Fact]
        public void ExternalPasswords_Mode2_File()
        {
            var file = Path.Combine(_dir, "pwlist.txt");
            File.WriteAllText(file, "fromfile1\nfromfile2\n");
            _settings.Save(s => { s.ReadPasswordMode = 2; s.PWUrl = file; });
            var svc = NewService();
            Assert.Equal(new[] { "fromfile1", "fromfile2" }, svc.ExternalPasswords().ToArray());
        }

        [Fact]
        public void ImportBook_Transfers_Existing_Cipher_Verbatim()
        {
            var cipher = Dpapi.Encode("migrated");
            var svc = NewService();
            svc.ImportBook(new[] { new PasswordEntry { Cipher = cipher, SuccessCount = 3 } });
            var reloaded = NewService();
            Assert.Equal("migrated", reloaded.Book[0].Text);
            Assert.Equal(3, reloaded.Book[0].SuccessCount);
        }
    }
}
