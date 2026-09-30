using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Services;
using UZIP2.ViewModel;

namespace UZIP2.Shell.Pages
{
    public partial class PasswordBookView : UserControl
    {
        private PasswordBookViewModel Vm => DataContext as PasswordBookViewModel;
        private PasswordBookViewModel.PasswordRow _editingRow;

        public PasswordBookView()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<PasswordBookViewModel>();
            Vm.PropertyChanged += OnVmChanged;
            RefreshList();
        }

        void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PasswordBookViewModel.VisibleRows))
                RefreshList();
        }

        void RefreshList()
        {
            BookList.ItemsSource = Vm?.VisibleRows;
        }

        void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (Vm != null) Vm.SearchText = SearchBox.Text ?? "";
        }

        // ---- 添加 / 编辑 ----

        void OnAddBook(object sender, RoutedEventArgs e)
        {
            _editingRow = null;
            EditTitle.Text = "添加密码";
            EditName.Text = "";
            EditPlain.Text = "";
            EditOverlay.Visibility = Visibility.Visible;
            EditName.Focus();
        }

        void OnEdit(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            _editingRow = row;
            EditTitle.Text = "编辑密码";
            EditName.Text = row.Name;
            EditPlain.Text = row.Entry.Text;
            EditOverlay.Visibility = Visibility.Visible;
            EditPlain.Focus();
        }

        void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            Vm?.Remove(row);
        }

        void OnReveal(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            Vm?.Reveal(row);
        }

        void OnEditSave(object sender, RoutedEventArgs e)
        {
            var name = EditName.Text.Trim();
            var plain = EditPlain.Text;
            if (_editingRow == null)
                Vm?.Add(name, plain);
            else
                Vm?.Update(_editingRow, name, plain);
            CloseOverlay();
        }

        void OnEditCancel(object sender, RoutedEventArgs e) => CloseOverlay();

        void OnOverlayClick(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, EditOverlay)) CloseOverlay();
        }

        void OnOverlayBodyClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

        void CloseOverlay()
        {
            EditOverlay.Visibility = Visibility.Collapsed;
            _editingRow = null;
        }

        // ---- 密码纸 ----

        void OnPastePaper(object sender, RoutedEventArgs e)
        {
            if (Vm != null) Vm.PasteFromClipboardCommand.Execute(null);
        }

        void OnClearPaper(object sender, RoutedEventArgs e)
        {
            Vm?.ClearPaper();
        }

        // ---- 密码库跨机导出/导入 ----

        enum VaultMode { Export, Import }

        VaultMode _vaultMode;
        string _vaultJson;

        static PasswordService Passwords => App.Services.GetRequiredService<PasswordService>();

        void OnExportVault(object sender, RoutedEventArgs e)
        {
            int count = Passwords.DumpAll().Count;
            if (count == 0)
            {
                VaultInfo("密码本和密码纸都是空的，没有可导出的条目。");
                return;
            }
            _vaultMode = VaultMode.Export;
            VaultTitle.Text = "导出密码库";
            VaultHint.Text = $"共 {count} 条。口令用来加密导出件，导入时要原样再输一次——忘了口令这个文件就再也打不开。";
            VaultConfirmRow.Visibility = Visibility.Visible;
            VaultAccept.Content = "导出…";
            OpenVaultOverlay();
        }

        void OnImportVault(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择密码库导出件",
                Filter = "UZIP 密码库|*.uzip.json|JSON 文件|*.json|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            string json;
            try { json = File.ReadAllText(dlg.FileName); }
            catch (Exception ex) { VaultInfo("读取失败: " + ex.Message); return; }

            _vaultMode = VaultMode.Import;
            _vaultJson = json;
            VaultTitle.Text = "导入密码库";
            VaultHint.Text = "输入这份导出件的口令。导入只做合并，不会清空现有密码。";
            VaultConfirmRow.Visibility = Visibility.Collapsed;
            VaultAccept.Content = "导入";
            OpenVaultOverlay();
        }

        void OpenVaultOverlay()
        {
            VaultPass1.Clear();
            VaultPass2.Clear();
            VaultOverlay.Visibility = Visibility.Visible;
            VaultPass1.Focus();
        }

        void OnVaultAccept(object sender, RoutedEventArgs e)
        {
            var pass = VaultPass1.Password;
            if (_vaultMode == VaultMode.Export)
            {
                if (!string.Equals(pass, VaultPass2.Password, StringComparison.Ordinal))
                {
                    VaultInfo("两次输入的口令不一致。");
                    return;
                }
                if (FinishExport(pass)) CloseVaultOverlay();
            }
            else if (FinishImport(pass))
            {
                CloseVaultOverlay();
            }
        }

        bool FinishExport(string pass)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出密码库",
                Filter = "UZIP 密码库|*.uzip.json",
                FileName = "uzip-vault-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".uzip.json",
            };
            if (dlg.ShowDialog() != true) return true;   // 用户自己取消，弹层跟着关
            try
            {
                var entries = Passwords.DumpAll();
                File.WriteAllText(dlg.FileName, VaultTransfer.Export(entries, pass), new UTF8Encoding(false));
                VaultInfo($"已导出 {entries.Count} 条到\n{dlg.FileName}\n\n请把文件和口令分开存放。");
                return true;
            }
            catch (VaultException ex) { VaultInfo(ex.Message); return false; }
            catch (Exception ex) { VaultInfo("写入失败: " + ex.Message); return true; }
        }

        bool FinishImport(string pass)
        {
            int bookBefore = Passwords.Book.Count, paperBefore = Passwords.Paper.Count;
            try
            {
                var entries = VaultTransfer.Import(_vaultJson, pass);
                Passwords.ImportBook(entries.Where(x => !x.IsPaper).ToList());
                Passwords.ImportPaper(entries.Where(x => x.IsPaper).ToList());
                VaultInfo($"文件里 {entries.Count} 条；新增密码本 {Passwords.Book.Count - bookBefore} 条、"
                    + $"密码纸 {Passwords.Paper.Count - paperBefore} 条。");
                return true;
            }
            catch (VaultException ex)
            {
                VaultInfo(ex.Message);
                return false;   // 口令错了留在弹层里，直接重输
            }
        }

        void VaultInfo(string text)
            => MessageBox.Show(text, "UZIP 密码库", MessageBoxButton.OK, MessageBoxImage.Information);

        void OnVaultCancel(object sender, RoutedEventArgs e) => CloseVaultOverlay();

        void CloseVaultOverlay()
        {
            VaultOverlay.Visibility = Visibility.Collapsed;
            _vaultJson = null;
            VaultPass1.Clear();
            VaultPass2.Clear();
        }

        void OnVaultOverlayClick(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, VaultOverlay)) CloseVaultOverlay();
        }
    }
}
