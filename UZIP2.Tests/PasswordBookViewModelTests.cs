using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;
using IO = System.IO;

namespace UZIP2.Tests
{
    public class PasswordBookViewModelTests : IDisposable
    {
        private readonly string _dir;
        private readonly SettingsService _settings;
        private readonly PasswordService _passwords;
        private readonly PasswordBookViewModel _vm;

        public PasswordBookViewModelTests()
        {
            _dir = IO.Path.Combine(IO.Path.GetTempPath(), "UZIP2_PwVm_" + Guid.NewGuid().ToString("N"));
            IO.Directory.CreateDirectory(_dir);
            _settings = new SettingsService(_dir, null);
            _passwords = new PasswordService(_dir, _settings);
            _vm = new PasswordBookViewModel(_passwords, Dispatcher.CurrentDispatcher);
        }

        public void Dispose()
        {
            try { IO.Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void AddBook_AppearsInRows()
        {
            _vm.Add("site-a", "pw123");
            Assert.Single(_vm.Rows);
            Assert.Equal("site-a", _vm.Rows[0].Name);
        }

        [Fact]
        public void Update_ChangesNameAndPlain()
        {
            _vm.Add("old", "aaa");
            var row = _vm.Rows[0];
            _vm.Update(row, "new", "bbb");
            Assert.Equal("new", row.Name);
            Assert.Equal("new", _passwords.Book.Single().Name);
            Assert.Equal("bbb", _passwords.Book.Single().Text);
        }

        [Fact]
        public void Remove_DeletesFromService()
        {
            _vm.Add("x", "1");
            _vm.Add("y", "2");
            _vm.Remove(_vm.Rows.First(r => r.Name == "x"));
            Assert.Equal(1, _passwords.Book.Count);
            Assert.DoesNotContain(_vm.Rows, r => r.Name == "x");
        }

        [Fact]
        public void Search_FiltersRowsByNameOrPassword()
        {
            _vm.Add("github", "abc");
            _vm.Add("gitee", "xyz");
            _vm.SearchText = "git";
            Assert.Equal(2, _vm.VisibleRows.Count);
            _vm.SearchText = "xyz";
            Assert.Single(_vm.VisibleRows);
            Assert.Equal("gitee", _vm.VisibleRows[0].Name);
            _vm.SearchText = "";
            Assert.Equal(2, _vm.VisibleRows.Count);
        }

        [Fact]
        public void Rows_DefaultMasked()
        {
            _vm.Add("k", "secret");
            var row = _vm.Rows[0];
            Assert.False(row.Revealed);
            Assert.DoesNotContain("secret", row.MaskedText);
            Assert.Equal("••••••", row.MaskedText);
        }

        [Fact]
        public void Reveal_ShowsPlainThenAutoHides()
        {
            _vm.Add("k", "secret");
            var row = _vm.Rows[0];
            _vm.Reveal(row);
            Assert.True(row.Revealed);
            Assert.Equal("secret", row.MaskedText);
            // 强制立即回掩码（定时器逻辑单测里不等待真实 3s）
            _vm.HideNow(row);
            Assert.False(row.Revealed);
            Assert.Equal("••••••", row.MaskedText);
        }

        [Fact]
        public void Paper_AddAndClear()
        {
            _vm.AddPaper("p1\np2\np3");
            Assert.Equal(3, _passwords.Paper.Count);
            Assert.Equal(3, _vm.PaperRows.Count);
            _vm.ClearPaper();
            Assert.Empty(_passwords.Paper);
            Assert.Empty(_vm.PaperRows);
        }

        [Fact]
        public void ServiceChanged_ExternalMutation_RefreshesVm()
        {
            _passwords.AddBook("ext", "e1");
            Assert.Single(_vm.Rows);
            Assert.Equal("ext", _vm.Rows[0].Name);
        }
    }
}
