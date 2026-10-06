using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UZIP2.Models;

namespace UZIP2.Services
{
    public sealed class PasswordEntry
    {
        public string Name { get; set; } = "";
        public string Cipher { get; set; } = "";   // DPAPI base64（DpapiHelper.Encode 格式，含 "DPAPI:" 前缀）
        public int SuccessCount { get; set; }
        public bool IsPaper { get; set; }

        // 试密码时每个候选都要比对/排序，逐次解出明文会把 DPAPI 打成热路径。
        // 密文变了才重新解；同一份密文只解一次。
        private string _plain;
        private string _plainFor;

        [JsonIgnore]
        public string Text
        {
            get
            {
                if (_plainFor != Cipher)
                {
                    _plain = DpapiHelper.Decode(Cipher);
                    _plainFor = Cipher;
                }
                return _plain;
            }
            set => Cipher = DpapiHelper.Encode(value);
        }
    }

    public interface IPasswordStore
    {
        void ImportBook(IEnumerable<PasswordEntry> entries);
        void ImportPaper(IEnumerable<PasswordEntry> entries);
        IReadOnlyList<PasswordEntry> DumpAll();
    }

    // 密码本(永久) + 密码纸(一次性)。存储于 Config/passwords.json，条目文本 DPAPI 加密。
    public sealed class PasswordService : IPasswordStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly string _storePath;
        private readonly ISettingsService _settings;
        private readonly object _sync = new object();
        private readonly List<PasswordEntry> _book = new List<PasswordEntry>();
        private readonly List<PasswordEntry> _paper = new List<PasswordEntry>();

        public event Action Changed;

        public string StorePath => _storePath;
        public IReadOnlyList<PasswordEntry> Book { get { lock (_sync) return _book.ToList(); } }
        public IReadOnlyList<PasswordEntry> Paper { get { lock (_sync) return _paper.ToList(); } }

        // 外链密码缓存。一批档案会逐个重建密码链，每次都联网等于把断网超时摊进整批。
        // 独立锁：网络 I/O 绝不攥着密码本的 _sync。
        private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromMilliseconds(3000) };
        private readonly object _extSync = new object();
        private string _extUrl;
        private string[] _extLines;
        private bool _extFailed;
        private long _extAtMs;

        /// <summary>外链结果复用窗口（毫秒），<=0 表示每次现取。</summary>
        public int ExternalCacheMs { get; set; } = 60_000;

        /// <summary>测试/离线接缝：替换掉真实 HTTP 取行。</summary>
        public Func<string, string[]> HttpProvider { get; set; }

        public PasswordService(string configDirectory, ISettingsService settings)
        {
            _storePath = Path.Combine(configDirectory, "passwords.json");
            _settings = settings;
            Load();
        }

        private void Load()
        {
            lock (_sync)
            {
                _book.Clear();
                _paper.Clear();
                if (!File.Exists(_storePath)) return;
                try
                {
                    var all = JsonSerializer.Deserialize<List<PasswordEntry>>(File.ReadAllText(_storePath));
                    if (all == null) return;
                    foreach (var e in all)
                        (e.IsPaper ? _paper : _book).Add(e);
                }
                catch { /* 损坏视为空，保存时覆盖 */ }
            }
        }

        // 调用方必须持有 _sync。Changed 不在这里派发：订阅者会在回调里读 Book/Paper，
        // 而锁还攥在调用方手里，锁内派发等于让回调等自己——必死锁。
        private void Save()
        {
            var all = _book.Concat(_paper).ToList();
            AtomicFile.Write(_storePath, JsonSerializer.Serialize(all, JsonOptions));
        }

        public void ImportBook(IEnumerable<PasswordEntry> entries)
        {
            lock (_sync)
            {
                foreach (var e in entries)
                {
                    e.IsPaper = false;
                    e.Cipher = DpapiHelper.Encode(e.Text); // 明文条目自动加密
                    if (!string.IsNullOrEmpty(e.Text) && !_book.Any(b => b.Text == e.Text))
                        _book.Add(e);
                }
                Save();
            }
            Changed?.Invoke();
        }

        public void ImportPaper(IEnumerable<PasswordEntry> entries)
        {
            lock (_sync)
            {
                foreach (var e in entries)
                {
                    e.IsPaper = true;
                    e.Cipher = DpapiHelper.Encode(e.Text);
                    if (!string.IsNullOrEmpty(e.Text) && !_paper.Any(p => p.Text == e.Text))
                        _paper.Add(e);
                }
                Save();
            }
            Changed?.Invoke();
        }

        public IReadOnlyList<PasswordEntry> DumpAll()
        {
            lock (_sync) return _book.Concat(_paper).ToList();
        }

        public void AddBook(string name, string plain)
        {
            lock (_sync)
            {
                if (_book.Any(b => b.Text == plain)) return; // 去重
                _book.Add(new PasswordEntry { Name = name ?? "", Cipher = DpapiHelper.Encode(plain) });
                Save();
            }
            Changed?.Invoke();
        }

        public void RemoveBook(PasswordEntry entry)
        {
            lock (_sync)
            {
                _book.RemoveAll(b => b.Cipher == entry.Cipher);
                Save();
            }
            Changed?.Invoke();
        }

        public void UpdateBook(PasswordEntry entry, string name, string plain)
        {
            lock (_sync)
            {
                entry.Name = name ?? entry.Name;
                entry.Cipher = DpapiHelper.Encode(plain);
                Save();
            }
            Changed?.Invoke();
        }

        public const int PaperLimit = 200;

        // 逐行导入密码纸: TrimSpace 开关控制裁剪、去空行、跨密码纸/密码本去重、上限200。返回实际新增数。
        public int PasteToPaper(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return 0;
            int added = 0;
            lock (_sync)
            {
                foreach (var line in plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = ShouldTrimSpace() ? line.Trim() : line;
                    if (trimmed.Length == 0) continue;
                    if (_paper.Count >= PaperLimit) break;
                    if (_paper.Any(p => p.Text == trimmed) || _book.Any(b => b.Text == trimmed)) continue;
                    _paper.Add(new PasswordEntry { IsPaper = true, Cipher = DpapiHelper.Encode(trimmed) });
                    added++;
                }
                // 一次粘贴只落盘一次
                if (added > 0) Save();
            }
            if (added > 0) Changed?.Invoke();
            return added;
        }

        public void ClearPaper()
        {
            lock (_sync)
            {
                _paper.Clear();
                Save();
            }
            Changed?.Invoke();
        }

        // 会话级密码纸废纸篓(关闭程序即清空，与旧 PWRecycle 语义一致)
        private readonly List<string> _recycle = new List<string>();
        public IReadOnlyList<string> Recycle { get { lock (_sync) return _recycle.ToList(); } }

        // 解压成功后消耗掉所用密码纸: 移入废纸篓并从密码纸删除
        public void ConsumePaper(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return;
            lock (_sync)
            {
                var hit = _paper.FirstOrDefault(p => p.Text == plain);
                if (hit == null) return;
                _paper.Remove(hit);
                _recycle.Add(plain);
                Save();
            }
            Changed?.Invoke();
        }

        private bool ShouldTrimSpace()
        {
            try { return _settings == null || _settings.Current.TrimSpace; }
            catch { return true; }
        }

        // 试密码顺序(与旧版一致): 外部来源 -> 文件名提取 -> 密码本(按成功次数降序) -> 密码纸
        public IReadOnlyList<string> CandidatePasswords(string namePassword = null)
        {
            List<PasswordEntry> book;
            List<PasswordEntry> paper;
            lock (_sync)
            {
                book = _book.ToList();
                paper = _paper.ToList();
            }
            book.Sort((a, b) => b.SuccessCount.CompareTo(a.SuccessCount));
            var result = new List<string>();
            foreach (var extra in ExternalPasswords())
                if (!result.Contains(extra)) result.Add(extra);
            if (!string.IsNullOrEmpty(namePassword)) result.Add(namePassword);
            foreach (var t in book.Select(e => e.Text).Concat(paper.Select(e => e.Text)))
                if (!string.IsNullOrEmpty(t) && !result.Contains(t)) result.Add(t);
            return result;
        }

        public void ReportResult(string passwordUsed, bool success)
        {
            if (string.IsNullOrEmpty(passwordUsed) || !success) return;
            lock (_sync)
            {
                var entry = _book.FirstOrDefault(b => b.Text == passwordUsed)
                         ?? _paper.FirstOrDefault(p => p.Text == passwordUsed);
                if (entry == null) return;
                entry.SuccessCount++;
                Save();
            }
            Changed?.Invoke();
        }

        // 旧 Mypassword.cs 的四种来源语义（模式2=文件 模式3=http 模式4=隐藏http，逐字保持）
        public IReadOnlyList<string> ExternalPasswords()
        {
            var mode = _settings?.Current.ReadPasswordMode ?? 0;
            var url = _settings?.Current.PWUrl ?? "";
            string[] lines = null;
            switch (mode)
            {
                case 0: return Array.Empty<string>();
                case 1: lines = _settings.Current.InternalPasswords?.ToArray(); break;
                case 2: if (!string.IsNullOrEmpty(url)) lines = ReadFileLines(url); break;
                case 3: case 4:
                    if (!string.IsNullOrEmpty(url)) lines = ReadHttpCached(url);
                    break;
            }
            if (lines == null) return Array.Empty<string>();
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToArray();
        }

        private string[] ReadHttpCached(string url)
        {
            lock (_extSync)
            {
                if (string.Equals(_extUrl, url, StringComparison.Ordinal)
                    && Environment.TickCount64 - _extAtMs < ExternalCacheMs)
                    return _extFailed ? null : _extLines;
            }

            string[] lines = null;
            try { lines = (HttpProvider ?? FetchHttpLines)(url); }
            catch { lines = null; }

            lock (_extSync)
            {
                _extUrl = url;
                _extLines = lines;
                _extFailed = lines == null;   // 失败也要记住，否则每个档案再等一次超时
                _extAtMs = Environment.TickCount64;
            }
            return lines;
        }

        private static string[] FetchHttpLines(string url)
        {
            var body = SharedHttp.GetStringAsync(url).GetAwaiter().GetResult();
            return body.Replace("\r\n", "\n").Split('\n');
        }

        private static string[] ReadFileLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllLines(path, Encoding.UTF8);
            }
            catch { return null; }
        }
    }
}
