using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.ViewModel
{
    // 历史页：列出持久化的解压/压缩作业，失败项显示"下一步建议"，可重跑 / 清空 / 只看失败 / 搜索 / 揭示口令。
    public partial class HistoryViewModel : ObservableObject
    {
        const string Mask = "••••••";

        readonly IHistoryService _history;
        readonly ArchiveWorker _worker;

        public HistoryViewModel(IHistoryService history, ArchiveWorker worker = null)
        {
            _history = history;
            _worker = worker;
            Reload();
        }

        public ObservableCollection<HistoryRow> Rows { get; } = new ObservableCollection<HistoryRow>();

        [ObservableProperty] private string searchText = "";
        [ObservableProperty] private bool failuresOnly;
        [ObservableProperty] private bool showPasswords;

        partial void OnSearchTextChanged(string value) => Reload();
        partial void OnFailuresOnlyChanged(bool value) => Reload();
        partial void OnShowPasswordsChanged(bool value) => Reload();

        public void Reload()
        {
            var q = SearchText?.Trim();
            Rows.Clear();
            foreach (var e in _history.ReadAll())      // newest first
            {
                if (FailuresOnly && e.Status != "Failed") continue;
                if (!string.IsNullOrEmpty(q))
                {
                    bool hit = (e.Source ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (e.Kind ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (e.Error ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (e.Advice ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                Rows.Add(new HistoryRow(e, ShowPasswords, Mask));
            }
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyText));
        }

        public bool HasRows => Rows.Count > 0;
        public bool IsEmpty => Rows.Count == 0;

        public string EmptyText => FailuresOnly
            ? "没有失败的记录。"
            : "还没有历史。解压或压缩一次后，这里会留下记录与失败建议。";

        [RelayCommand]
        void Refresh() => Reload();

        [RelayCommand]
        void Clear()
        {
            _history.Clear();
            Reload();
        }

        [RelayCommand]
        void Retry(HistoryRow row)
        {
            if (row == null || _worker == null || string.IsNullOrEmpty(row.Source)) return;
            if (string.Equals(row.Kind, "Compress", StringComparison.OrdinalIgnoreCase))
                _worker.EnqueueCompress(new[] { row.Source });
            else
                _worker.EnqueueExtract(new[] { row.Source });
        }

        public sealed class HistoryRow : ObservableObject
        {
            public HistoryEntry Entry { get; }

            public HistoryRow(HistoryEntry e, bool revealPassword, string mask)
            {
                Entry = e;
                PasswordText = string.IsNullOrEmpty(e.Password)
                    ? ""
                    : (revealPassword ? e.Password : mask);
            }

            public string Kind => Entry.Kind;
            public string Source => Entry.Source;
            public string Destination => Entry.Destination;
            public string Status => Entry.Status;
            public string Error => Entry.Error;
            public string Advice => Entry.Advice;
            public int Count => Entry.Count;
            public bool IsFailed => Entry.Status == "Failed";
            public string PasswordText { get; }
            public bool HasPassword => Entry.Password != null;

            public string TimeText
            {
                get
                {
                    try { return DateTimeOffset.FromUnixTimeMilliseconds(Entry.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"); }
                    catch { return ""; }
                }
            }

            public string DurationText => Entry.DurationMs > 0 ? Entry.DurationMs + " ms" : "";
        }
    }
}
