using System;
using System.IO;
using System.Linq;
using System.Threading;
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

        // Changed 必须"松手后再播"：持锁派发会让订阅者的同步 UI 派发与工作线程互相咬死
        // （主页密码纸计数订阅即为 _ui.Invoke，命中密码本时整进程冻结）
        [Fact]
        public void Changed_Is_Raised_Without_Holding_Store_Lock()
        {
            var svc = NewService();
            var reentered = new ManualResetEventSlim(false);
            Exception readerError = null;
            svc.Changed += () =>
            {
                // 事件派发期间，另一线程必须能立刻读到密码本（锁已放开）
                var t = new Thread(() =>
                {
                    try
                    {
                        if (svc.Book.Count + svc.Paper.Count >= 0) reentered.Set();
                    }
                    catch (Exception ex) { readerError = ex; }
                }) { IsBackground = true };
                t.Start();
                Assert.True(t.Join(3000), "Changed 派发期间密码本锁仍被占用，订阅者读取会死锁");
            };
            svc.AddBook("死锁探针", "pw-deadlock");
            Assert.Null(readerError);
            Assert.True(reentered.IsSet);
        }

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

        // 一批密码链会对每个档案重复取外链密码；每次都联网等于把断网的超时摊到每个档案上
        [Fact]
        public void ExternalPasswords_Mode3_Http_is_cached_within_the_window()
        {
            int calls = 0;
            var svc = NewService();
            svc.HttpProvider = url => { calls++; return new[] { "http1", "http2" }; };
            _settings.Save(s => { s.ReadPasswordMode = 3; s.PWUrl = "http://example.invalid/pw.txt"; });

            Assert.Equal(new[] { "http1", "http2" }, svc.ExternalPasswords().ToArray());
            Assert.Equal(new[] { "http1", "http2" }, svc.ExternalPasswords().ToArray());
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Failed_http_is_cached_negative_so_a_batch_is_not_blocked_again_and_again()
        {
            int calls = 0;
            var svc = NewService();
            svc.HttpProvider = url => { calls++; throw new InvalidOperationException("offline"); };
            _settings.Save(s => { s.ReadPasswordMode = 3; s.PWUrl = "http://example.invalid/pw.txt"; });

            Assert.Empty(svc.ExternalPasswords());
            Assert.Empty(svc.ExternalPasswords());
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Cache_refetches_after_the_window_expires()
        {
            int calls = 0;
            var svc = NewService();
            svc.HttpProvider = url => { calls++; return new[] { "p" + calls }; };
            svc.ExternalCacheMs = 25;
            _settings.Save(s => { s.ReadPasswordMode = 3; s.PWUrl = "http://example.invalid/pw.txt"; });

            Assert.Equal("p1", svc.ExternalPasswords().Single());
            Thread.Sleep(60);
            Assert.Equal("p2", svc.ExternalPasswords().Single());
            Assert.Equal(2, calls);
        }

        [Fact]
        public void Mode4_uses_the_configured_url_and_never_a_borrowed_one()
        {
            string seen = null;
            var svc = NewService();
            svc.HttpProvider = url => { seen = url; return new[] { "p" }; };
            _settings.Save(s => { s.ReadPasswordMode = 4; s.PWUrl = "http://example.invalid/hidden.txt"; });

            Assert.Equal(new[] { "p" }, svc.ExternalPasswords().ToArray());
            Assert.Equal("http://example.invalid/hidden.txt", seen);
        }

        [Fact]
        public void Mode4_without_a_url_fetches_nothing()
        {
            bool called = false;
            var svc = NewService();
            svc.HttpProvider = url => { called = true; return new[] { "nope" }; };
            _settings.Save(s => { s.ReadPasswordMode = 4; s.PWUrl = ""; });

            Assert.Empty(svc.ExternalPasswords());
            Assert.False(called);
        }

        [Fact]
        public void ImportBook_Transfers_Existing_Cipher_Verbatim()
        {
            var cipher = DpapiHelper.Encode("migrated");
            var svc = NewService();
            svc.ImportBook(new[] { new PasswordEntry { Cipher = cipher, SuccessCount = 3 } });
            var reloaded = NewService();
            Assert.Equal("migrated", reloaded.Book[0].Text);
            Assert.Equal(3, reloaded.Book[0].SuccessCount);
        }

        [Fact]
        public void Cached_Text_Follows_Both_Ways()
        {
            var entry = new PasswordEntry { Cipher = DpapiHelper.Encode("first") };
            Assert.Equal("first", entry.Text);
            Assert.Equal("first", entry.Text);      // 第二次走缓存
            entry.Text = "second";
            Assert.Equal("second", entry.Text);     // 改完不能还吐旧值
            entry.Cipher = DpapiHelper.Encode("third");
            Assert.Equal("third", entry.Text);      // 直接换密文也要重解
        }

        [Fact]
        public void ReportResult_Only_Writes_When_It_Actually_Changed()
        {
            var svc = NewService();
            svc.AddBook("论坛A", "hit");
            int saves = 0;
            svc.Changed += () => saves++;

            svc.ReportResult("miss", true);     // 不在库里
            svc.ReportResult("hit", false);     // 失败不改计数
            svc.ReportResult("", true);
            Assert.Equal(0, saves);

            svc.ReportResult("hit", true);
            Assert.Equal(1, saves);
            Assert.Equal(1, NewService().Book[0].SuccessCount);
        }

        [Fact]
        public void PasteToPaper_Writes_Once_Per_Paste()
        {
            var svc = NewService();
            int saves = 0;
            svc.Changed += () => saves++;

            Assert.Equal(3, svc.PasteToPaper("a\nb\nc\n"));
            Assert.Equal(1, saves);
            Assert.Equal(3, NewService().Paper.Count);

            Assert.Equal(0, svc.PasteToPaper("a\na\n"));   // 全重复: 不落盘
            Assert.Equal(1, saves);
        }
    }
}
