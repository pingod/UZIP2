using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    public partial class PasswordView : UserControl
    {
        private const string Mask = "••••••";
        private readonly List<Row> _rows = new List<Row>();

        public PasswordView()
        {
            InitializeComponent();
            Reload();
        }

        public void Reload()
        {
            _rows.Clear();
            foreach (var e in AppHost.Passwords.Book) _rows.Add(new Row(e));
            BookList.ItemsSource = null;
            BookList.ItemsSource = _rows;
            BookCount.Text = $"共 {_rows.Count} 条";

            var paper = AppHost.Passwords.Paper;
            PaperList.ItemsSource = null;
            PaperList.ItemsSource = paper;
            PaperCount.Text = $"{paper.Count} / {PasswordService.PaperLimit}";
        }

        private void Hint(string text) => HintText.Text = text ?? "";

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var plain = PlainBox.Text ?? "";
            if (plain.Length == 0) return;
            AppHost.Passwords.AddBook((NameBox.Text ?? "").Trim(), plain);
            NameBox.Text = "";
            PlainBox.Text = "";
            Reload();
        }

        private void OnToggleReveal(object sender, RoutedEventArgs e)
        {
            var row = (sender as Control)?.DataContext as Row;
            if (row == null) return;
            row.Revealed = !row.Revealed;
        }

        private async void OnCopy(object sender, RoutedEventArgs e)
        {
            var row = (sender as Control)?.DataContext as Row;
            if (row == null) return;
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) { Hint("当前环境没有剪贴板"); return; }
                await clipboard.SetTextAsync(row.PlainText);
                Hint("已复制到剪贴板");
            }
            catch (Exception ex)
            {
                // 无头环境 / 没有剪贴板后端时这里会抛，提示代替崩溃
                Hint("复制到剪贴板失败：" + ex.Message);
            }
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var row = (sender as Control)?.DataContext as Row;
            if (row == null) return;
            AppHost.Passwords.RemoveBook(row.Entry);
            Reload();
        }

        private void OnAddPaper(object sender, RoutedEventArgs e)
        {
            var text = PaperBox.Text ?? "";
            if (text.Trim().Length == 0) return;
            var added = AppHost.Passwords.PasteToPaper(text);
            PaperBox.Text = "";
            Hint(added > 0 ? $"已写入密码纸 (+{added})" : "这些口令已经在密码纸 / 密码本里（或密码纸已满）");
            Reload();
        }

        private void OnClearPaper(object sender, RoutedEventArgs e)
        {
            AppHost.Passwords.ClearPaper();
            Hint("已清空密码纸");
            Reload();
        }

        // 对齐 WPF ClipboardService.PasteFromClipboard（按钮文案"从剪切板贴入"，这里按
        // 当前统一用词写成"从剪贴板粘贴"）：读剪贴板 → 清洗 → 写入密码纸 → 回执。
        // 无头测试环境里 Clipboard 可能为 null 或直接抛，全部在这里兜住。
        private async void OnPasteClipboard(object sender, RoutedEventArgs e)
        {
            string raw;
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                {
                    Hint("当前环境没有剪贴板，无法粘贴");
                    return;
                }
                raw = await clipboard.GetTextAsync();
            }
            catch (Exception ex)
            {
                Hint("读取剪贴板失败：" + ex.Message);
                return;
            }

            var cleaned = Clean(raw, AppHost.Settings.Current.TrimSpace);
            if (cleaned == null)
            {
                Hint("剪贴板里没有可以贴入的密码");
                return;
            }

            var added = AppHost.Passwords.PasteToPaper(cleaned);
            Hint(added > 0
                ? $"已写入密码纸 (+{added})，现有 {AppHost.Passwords.Paper.Count} 个"
                : "密码已在密码纸 / 密码本中（或密码纸已满）");
            Reload();
        }

        /// <summary>
        /// 剪贴板文本清洗：永远去掉制表符与回车残留，再按设置决定是否整体 Trim。
        /// 与 WPF UZIP2/Services/ClipboardService.cs 的 Clean 同一份语义；
        /// 清洗后为空返回 null（表示"没什么可贴的"）。
        /// </summary>
        internal static string Clean(string raw, bool trimSpace)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = Regex.Replace(raw, "[\t\r]", "");
            if (trimSpace) s = s.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>列表行：显示/隐藏只影响这一行，不落盘。</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private bool _revealed;

            public Row(PasswordEntry entry) { Entry = entry; }

            public PasswordEntry Entry { get; }
            public string Name => string.IsNullOrEmpty(Entry.Name) ? "(未命名)" : Entry.Name;
            public string PlainText => Entry.Text ?? "";
            public string DisplayText => _revealed ? PlainText : Mask;
            public string SuccessText => Entry.SuccessCount > 0 ? $"成功 {Entry.SuccessCount} 次" : "";

            public bool Revealed
            {
                get => _revealed;
                set
                {
                    if (_revealed == value) return;
                    _revealed = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revealed)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
