using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(row.PlainText);
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
            AppHost.Passwords.PasteToPaper(text);
            PaperBox.Text = "";
            Reload();
        }

        private void OnClearPaper(object sender, RoutedEventArgs e)
        {
            AppHost.Passwords.ClearPaper();
            Reload();
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
