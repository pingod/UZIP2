using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UZIP2.Services;

namespace UZIP2.ViewModel
{
    // 密码本页：上=密码本(行内编辑/揭示)，下=密码纸(一次性)。
    public partial class PasswordBookViewModel : ObservableObject
    {
        private const string Mask = "••••••";
        private static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(3);

        private readonly PasswordService _passwords;
        private readonly Dispatcher _dispatcher;
        private readonly ClipboardService _clipboard;
        private readonly Dictionary<PasswordEntry, DispatcherTimer> _timers
            = new Dictionary<PasswordEntry, DispatcherTimer>();

        public PasswordBookViewModel(PasswordService passwords, Dispatcher dispatcher, ClipboardService clipboard = null)
        {
            _passwords = passwords;
            _dispatcher = dispatcher;
            _clipboard = clipboard;
            _passwords.Changed += OnPasswordsChanged;
            Reload();
        }

        public ObservableCollection<PasswordRow> Rows { get; } = new ObservableCollection<PasswordRow>();
        public ObservableCollection<string> PaperRows { get; } = new ObservableCollection<string>();

        [ObservableProperty] private string searchText = "";

        public List<PasswordRow> VisibleRows
        {
            get
            {
                var q = SearchText?.Trim();
                if (string.IsNullOrEmpty(q)) return Rows.ToList();
                return Rows.Where(r =>
                    (r.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Entry.Text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(VisibleRows));
        }

        public int PaperCount => PaperRows.Count;
        public int PaperLimit => PasswordService.PaperLimit;

        void OnPasswordsChanged()
        {
            if (_dispatcher.CheckAccess()) Reload();
            else _dispatcher.BeginInvoke(Reload);
        }

        void Reload()
        {
            Rows.Clear();
            foreach (var e in _passwords.Book)
                Rows.Add(new PasswordRow(e));
            OnPropertyChanged(nameof(VisibleRows));

            PaperRows.Clear();
            foreach (var p in _passwords.Paper)
                PaperRows.Add(p.Text);
            OnPropertyChanged(nameof(PaperCount));
        }

        // ---- 密码本 CRUD ----

        public void Add(string name, string plain)
        {
            if (string.IsNullOrEmpty(plain)) return;
            _passwords.AddBook(name ?? "", plain);
        }

        public void Update(PasswordRow row, string name, string plain)
        {
            if (row == null) return;
            _passwords.UpdateBook(row.Entry, name ?? "", string.IsNullOrEmpty(plain) ? row.Entry.Text : plain);
        }

        public void Remove(PasswordRow row)
        {
            if (row == null) return;
            _passwords.RemoveBook(row.Entry);
        }

        // ---- 揭示/回掩码 ----

        public void Reveal(PasswordRow row)
        {
            if (row == null) return;
            row.Revealed = true;
            if (_timers.TryGetValue(row.Entry, out var old))
                old.Stop();
            var timer = new DispatcherTimer { Interval = RevealDuration };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _timers.Remove(row.Entry);
                row.Revealed = false;
            };
            _timers[row.Entry] = timer;
            timer.Start();
        }

        public void HideNow(PasswordRow row)
        {
            if (row == null) return;
            if (_timers.TryGetValue(row.Entry, out var timer))
            {
                timer.Stop();
                _timers.Remove(row.Entry);
            }
            row.Revealed = false;
        }

        // ---- 密码纸 ----

        public void AddPaper(string multiLine)
        {
            if (!string.IsNullOrWhiteSpace(multiLine))
                _passwords.PasteToPaper(multiLine);
        }

        public void ClearPaper() => _passwords.ClearPaper();

        [RelayCommand]
        void PasteFromClipboard()
        {
            _clipboard?.PasteFromClipboard();
        }

        public sealed class PasswordRow : ObservableObject
        {
            public PasswordEntry Entry { get; }

            public PasswordRow(PasswordEntry entry)
            {
                Entry = entry;
            }

            public string Name => Entry.Name;
            public int SuccessCount => Entry.SuccessCount;

            public bool Revealed
            {
                get => _revealed;
                set
                {
                    if (_revealed == value) return;
                    _revealed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MaskedText));
                }
            }
            private bool _revealed;

            public string MaskedText => Revealed ? Entry.Text : Mask;
        }
    }
}
