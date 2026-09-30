using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Cli
{
    // 把解析后的命令映射到既有服务，全程 headless（不建窗口、不碰单实例总线）。
    // 退出码: 0 成功 / 1 操作失败 / 2 用法错误。
    public sealed class CliRunner
    {
        readonly Action<string> _out;
        readonly Action<string> _err;
        readonly SettingsService _settings;
        readonly PasswordService _passwords;
        readonly SevenZipClient _zip;
        readonly CompressLogService _compressLog;

        public CliRunner(string baseDir, string configDir, Action<string> output, Action<string> error)
        {
            _out = output ?? (s => Console.WriteLine(s));
            _err = error ?? (s => Console.Error.WriteLine(s));
            var logger = new FileLogger(baseDir);
            _settings = new SettingsService(configDir, logger);
            _passwords = new PasswordService(configDir, _settings);
            _zip = new SevenZipClient(_settings, logger);
            _compressLog = new CompressLogService(configDir, _settings);
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public async Task<int> RunAsync(CliRequest r, CancellationToken ct = default)
        {
            if (r.HasError) { _err(r.Error); _err(""); _err(Usage.OneLine); return 2; }
            if (_zip.SevenZipPath == null && Needs7z(r.Command))
            { _err("未找到 7z.exe，请安装 7-Zip 或用 config set customize7zPath <路径>"); return 1; }

            try
            {
                switch (r.Command)
                {
                    case CliCommand.Help: _out(Usage.Text); return 0;
                    case CliCommand.Version: return DoVersion(r);
                    case CliCommand.List: return await DoList(r, ct);
                    case CliCommand.Test: return await DoTest(r, ct);
                    case CliCommand.Extract: return await DoExtract(r, ct);
                    case CliCommand.Compress: return await DoCompress(r, ct);
                    case CliCommand.Checksum: return await DoChecksum(r, ct);
                    case CliCommand.Vault: return DoVault(r);
                    case CliCommand.Config: return DoConfig(r);
                    case CliCommand.Log: return DoLog(r);
                    case CliCommand.Shell: return DoShell(r);
                    case CliCommand.Watch: return await DoWatch(r, ct);
                    case CliCommand.Update: return await DoUpdate(ct);
                    default: _out(Usage.Text); return 0;
                }
            }
            catch (VaultException ex) { _err(ex.Message); return 1; }
            catch (Exception ex) { _err("错误: " + ex.Message); return 1; }
        }

        static bool Needs7z(CliCommand c)
            => c == CliCommand.List || c == CliCommand.Test || c == CliCommand.Extract || c == CliCommand.Compress || c == CliCommand.Watch;

        // ---------- version / 通用工具 ----------

        int DoVersion(CliRequest r)
        {
            var v = UpdateService.CurrentVersion();
            if (r.Json) _out(JsonSerializer.Serialize(new { version = v }, Json));
            else _out("UZIP " + v);
            return 0;
        }

        // 试密码链候选: 空密码优先，其次显式密码，再 --auto 时走密码本/密码纸/文件名密码
        List<string> Candidates(string archive, CliRequest r)
        {
            var list = new List<string> { null };
            if (!string.IsNullOrEmpty(r.Password)) { list[0] = r.Password; return list; }
            if (r.Auto)
            {
                var s = _settings.Current;
                string namePw = null;
                if (s.NameToPassword)
                    namePw = PasswordFromNameService.Extract(Path.GetFileNameWithoutExtension(archive), s.NameFilter);
                foreach (var c in _passwords.CandidatePasswords(namePw))
                    if (!list.Contains(c)) list.Add(c);
            }
            return list;
        }

        async Task<(SevenZipResult res, string used)> TryExtractAsync(string archive, string dest, CliRequest r, CancellationToken ct)
        {
            foreach (var pw in Candidates(archive, r))
            {
                var res = await _zip.ExtractAsync(archive, dest, pw, null, ct, r.Cover ?? "-aos", r.Entries).ConfigureAwait(false);
                if (res.Success) { if (pw != null) _passwords.ReportResult(pw, true); return (res, pw); }
                if (res.Error != SevenZipError.WrongPassword) return (res, pw);
            }
            return (await _zip.ExtractAsync(archive, dest, null, null, ct, r.Cover ?? "-aos", r.Entries).ConfigureAwait(false), null);
        }

        // ---------- list ----------

        async Task<int> DoList(CliRequest r, CancellationToken ct)
        {
            if (r.Args.Count == 0) { _err("用法: uzip2 list <压缩包> [--password pw|--auto] [--json]"); return 2; }
            int code = 0;
            foreach (var f in r.Args)
            {
                if (!File.Exists(f)) { _err(f + ": 文件不存在"); code = 1; continue; }
                ArchiveListing list = null; string used = null;
                foreach (var pw in Candidates(f, r))
                {
                    list = await _zip.ListEntriesAsync(f, pw, ct).ConfigureAwait(false);
                    if (list.Success || list.Error != SevenZipError.WrongPassword) { used = pw; break; }
                }
                if (!list.Success) { _err(f + ": " + (list.Diagnosis ?? "读取失败")); code = 1; continue; }
                if (r.Json)
                    _out(JsonSerializer.Serialize(new { archive = f, password = used, entries = list.Entries.Select(e => new { path = e.Path, size = e.Size, folder = e.IsFolder, method = e.Method }) }, Json));
                else
                {
                    _out(f);
                    foreach (var e in list.Entries)
                        _out("  " + (e.IsFolder ? "d" : "-") + " " + e.Size.ToString().PadLeft(12) + "  " + e.Path);
                    _out("  " + list.Entries.Count + " 项");
                }
            }
            return code;
        }

        // ---------- test ----------

        async Task<int> DoTest(CliRequest r, CancellationToken ct)
        {
            if (r.Args.Count == 0) { _err("用法: uzip2 test <压缩包...> [--password pw|--auto]"); return 2; }
            int code = 0;
            foreach (var f in r.Args)
            {
                if (!File.Exists(f)) { _err(f + ": 文件不存在"); code = 1; continue; }
                SevenZipResult res = null;
                foreach (var pw in Candidates(f, r))
                {
                    res = await _zip.TestAsync(f, pw, ct).ConfigureAwait(false);
                    if (res.Success || res.Error != SevenZipError.WrongPassword) break;
                }
                if (res.Success) _out(f + ": 正常");
                else { _err(f + ": " + (res.Diagnosis ?? "校验失败")); code = 1; }
            }
            return code;
        }

        // ---------- extract ----------

        async Task<int> DoExtract(CliRequest r, CancellationToken ct)
        {
            var files = r.Args.Where(File.Exists).ToList();
            if (files.Count == 0) { _err("用法: uzip2 extract <压缩包...> [-o DIR|--here] [--password pw|--auto] [--entries a;b]"); return files.Count == 0 && r.Args.Count > 0 ? 1 : 2; }
            foreach (var miss in r.Args.Where(a => !File.Exists(a))) _err(miss + ": 文件不存在");
            int code = 0;
            foreach (var f in files)
            {
                string dest = ResolveDest(f, r);
                var (res, used) = await TryExtractAsync(f, dest, r, ct).ConfigureAwait(false);
                if (res.Success) _out(f + " -> " + dest.TrimEnd('\\'));
                else { _err(f + ": " + (res.Diagnosis ?? "解压失败")); code = 1; }
            }
            return code;
        }

        string ResolveDest(string archive, CliRequest r)
        {
            string dest = !string.IsNullOrWhiteSpace(r.Output) ? r.Output
                        : r.Here ? Path.GetDirectoryName(archive)
                        : Environment.CurrentDirectory;
            Directory.CreateDirectory(dest);
            return dest.EndsWith("\\") || dest.EndsWith("/") ? dest : dest + Path.DirectorySeparatorChar;
        }

        // ---------- compress ----------

        async Task<int> DoCompress(CliRequest r, CancellationToken ct)
        {
            var sources = r.Args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
            if (sources.Count == 0) { _err("用法: uzip2 compress <文件或目录...> [-o DIR|--name x.7z] [--type 7z|zip] [--level N] [--password pw] [--volume 700m] [--solid on|off] [--threads N|off] [--headers]"); return 2; }
            foreach (var miss in r.Args.Where(a => !File.Exists(a) && !Directory.Exists(a))) _err(miss + ": 不存在");

            int type = ResolveType(r, sources[0]);
            int level = r.Level ?? 5;
            bool headers = r.Headers || (type == 1 && r.Headers);
            string volume = null;
            if (!string.IsNullOrWhiteSpace(r.Volume) && !VolumeSize.TryNormalize(r.Volume, out volume))
            { _err("分卷大小格式不正确: " + r.Volume + "（示例: 700m / 1g / 102400）"); return 2; }

            string outDir = ResolveCompressOutDir(r, sources[0]);
            string full = ResolveArchivePath(r, sources, outDir, type);

            var res = await _zip.CompressAsync(sources, full, r.Password, type, level, headers, null, ct,
                r.Exclude, volume, r.Solid, r.Threads).ConfigureAwait(false);
            if (!res.Success) { _err("压缩失败: " + (res.Diagnosis ?? res.Error.ToString())); return 1; }

            string produced = volume == null ? full : full + ".001";
            _compressLog.Log(full, r.Password);
            if (r.DeleteSource)
                foreach (var s2 in sources)
                    try { if (File.Exists(s2)) File.Delete(s2); } catch { }
            _out(produced);
            return 0;
        }

        static int ResolveType(CliRequest r, string firstSource)
        {
            if (!string.IsNullOrWhiteSpace(r.Type)) return TypeIndex(r.Type);
            // 从 --name 扩展名推断
            if (!string.IsNullOrWhiteSpace(r.Name))
            {
                var t = TypeIndexFromExt(Path.GetExtension(r.Name));
                if (t >= 0) return t;
            }
            return 0; // zip
        }

        static int TypeIndex(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "zip": return 0;
                case "7z": return 1;
                case "bz2": case "bzip2": return 2;
                case "gz": case "gzip": return 3;
                case "tar": return 4;
                case "wim": return 5;
                case "xz": return 6;
                default: return 0;
            }
        }

        static int TypeIndexFromExt(string ext)
        {
            switch ((ext ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "zip": return 0;
                case "7z": return 1;
                case "bz2": return 2;
                case "gz": return 3;
                case "tar": return 4;
                case "wim": return 5;
                case "xz": return 6;
                default: return -1;
            }
        }

        static string ResolveCompressOutDir(CliRequest r, string firstSource)
        {
            string dir = !string.IsNullOrWhiteSpace(r.Output) ? r.Output : Path.GetDirectoryName(firstSource);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            Directory.CreateDirectory(dir);
            return dir.EndsWith("\\") || dir.EndsWith("/") ? dir : dir + Path.DirectorySeparatorChar;
        }

        static string ResolveArchivePath(CliRequest r, List<string> sources, string outDir, int type)
        {
            if (!string.IsNullOrWhiteSpace(r.Name))
                return Path.IsPathRooted(r.Name) ? r.Name : outDir + r.Name;

            string baseName;
            if (sources.Count > 1)
            {
                baseName = Path.GetFileName(Path.GetDirectoryName(sources[0].TrimEnd('\\', '/')));
                if (string.IsNullOrEmpty(baseName)) baseName = "NewArchive";
            }
            else
            {
                bool isDir = Directory.Exists(sources[0]);
                baseName = isDir ? Path.GetFileName(sources[0].TrimEnd('\\', '/'))
                                 : Path.GetFileNameWithoutExtension(sources[0]);
                int n = baseName.IndexOf("-New", StringComparison.Ordinal);
                if (n > 0) baseName = baseName.Substring(0, n);
            }
            string ext = ArchiveExt(type);
            int num = 0; string path;
            do { path = outDir + baseName + (num == 0 ? "" : "-New" + num) + ext; num++; }
            while (File.Exists(path) && !(num > 9000));
            return path;
        }

        static string ArchiveExt(int type)
        {
            switch (type)
            {
                case 1: return ".7z";
                case 2: return ".bz2";
                case 3: return ".gz";
                case 4: return ".tar";
                case 5: return ".wim";
                case 6: return ".xz";
                default: return ".zip";
            }
        }

        // ---------- checksum ----------

        async Task<int> DoChecksum(CliRequest r, CancellationToken ct)
        {
            var files = r.Args.Where(File.Exists).ToList();
            if (files.Count == 0) { _err("用法: uzip2 checksum <文件...> [--verify|--write sha256|md5|sha1] [--json]"); return 2; }
            foreach (var miss in r.Args.Where(a => !File.Exists(a))) _err(miss + ": 文件不存在");

            if (r.Verify)
            {
                int code = 0;
                var all = new List<object>();
                foreach (var f in files)
                {
                    var rep = await ChecksumService.VerifyAsync(f, ct).ConfigureAwait(false);
                    bool ok = rep.Sha256 != null && rep.Sidecars.Any(s => s.SelectedCovered && s.SelectedOk) ||
                              rep.Sidecars.Count == 0;
                    bool allPassed = rep.Sidecars.All(s => s.PassedAll) && !rep.Sidecars.Any(s => s.SelectedCovered && !s.SelectedOk);
                    if (!allPassed) code = 1;
                    if (r.Json)
                        all.Add(new { file = f, sha256 = rep.Sha256, error = rep.Error, sidecars = rep.Sidecars.Select(s => new { sidecar = s.Sidecar, total = s.Total, passed = s.Passed, mismatched = s.Mismatched, missing = s.Missing, detail = s.Detail }) });
                    else
                    {
                        _out(f);
                        _out("  SHA-256: " + (rep.Sha256 ?? rep.Error));
                        foreach (var s in rep.Sidecars)
                            _out("  [" + (s.PassedAll ? "OK" : "FAIL") + "] " + s.Sidecar + " — " + s.Detail);
                        if (rep.Sidecars.Count == 0) _out("  (无旁挂校验文件，仅计算哈希)");
                    }
                }
                if (r.Json) _out(JsonSerializer.Serialize(all, Json));
                return code;
            }

            if (!string.IsNullOrWhiteSpace(r.Write))
            {
                string algo = r.Write.ToLowerInvariant();
                string ext = algo == "md5" ? ".md5" : algo == "sha1" ? ".sha1" : ".sha256";
                foreach (var f in files)
                {
                    string hex = await Task.Run(() => ChecksumService.Compute(f, algo), ct).ConfigureAwait(false);
                    string side = f + ext;
                    File.WriteAllText(side, hex + "  " + Path.GetFileName(f) + Environment.NewLine);
                    _out(side);
                }
                return 0;
            }

            foreach (var f in files)
            {
                string hex = await Task.Run(() => ChecksumService.Sha256File(f), ct).ConfigureAwait(false);
                if (r.Json) _out(JsonSerializer.Serialize(new { file = f, sha256 = hex }, Json));
                else _out(hex + "  " + Path.GetFileName(f));
            }
            return 0;
        }

        // ---------- vault ----------

        int DoVault(CliRequest r)
        {
            var sub = r.Args.Count > 0 ? r.Args[0].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "list":
                    if (r.Json)
                        _out(JsonSerializer.Serialize(_passwords.DumpAll().Select(e => new { name = e.Name, hits = e.SuccessCount, paper = e.IsPaper, text = r.ShowPasswords ? e.Text : null }), Json));
                    else
                    {
                        foreach (var e in _passwords.Book)
                            _out("本 " + e.Name + (string.IsNullOrEmpty(e.Name) ? "(无名)" : "") + "  [" + e.SuccessCount + "]" + (r.ShowPasswords ? "  " + e.Text : ""));
                        foreach (var e in _passwords.Paper)
                            _out("纸      [" + e.SuccessCount + "]" + (r.ShowPasswords ? "  " + e.Text : ""));
                    }
                    return 0;
                case "add":
                    if (r.Args.Count < 3) { _err("用法: uzip2 vault add <名称> <密码>"); return 2; }
                    _passwords.AddBook(r.Args[1], r.Args[2]); _out("已加入密码本"); return 0;
                case "remove":
                    if (r.Args.Count < 2) { _err("用法: uzip2 vault remove <名称>"); return 2; }
                    var hit = _passwords.Book.FirstOrDefault(b => string.Equals(b.Name, r.Args[1], StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { _err("未找到: " + r.Args[1]); return 1; }
                    _passwords.RemoveBook(hit); _out("已删除"); return 0;
                case "clear-paper":
                    _passwords.ClearPaper(); _out("密码纸已清空"); return 0;
                case "gen":
                    _out(PasswordGenerator.New(Math.Max(1, Math.Min(r.Length, 64)))); return 0;
                case "export":
                    if (r.Args.Count < 2) { _err("用法: uzip2 vault export <文件> --passphrase <口令>"); return 2; }
                    File.WriteAllText(r.Args[1], VaultTransfer.Export(_passwords.DumpAll(), r.Passphrase));
                    _out("已导出: " + r.Args[1]); return 0;
                case "import":
                    if (r.Args.Count < 2) { _err("用法: uzip2 vault import <文件> --passphrase <口令>"); return 2; }
                    var entries = VaultTransfer.Import(File.ReadAllText(r.Args[1]), r.Passphrase);
                    var book = entries.Where(e => !e.IsPaper); var paper = entries.Where(e => e.IsPaper);
                    _passwords.ImportBook(book); _passwords.ImportPaper(paper);
                    _out("已导入 " + entries.Count + " 条"); return 0;
                default: _err("未知 vault 子命令: " + sub); return 2;
            }
        }

        // ---------- config ----------

        int DoConfig(CliRequest r)
        {
            var props = typeof(AppSettings).GetProperties();
            var sub = r.Args.Count > 0 ? r.Args[0].ToLowerInvariant() : "show";
            if (sub == "show")
            {
                if (r.Json) _out(JsonSerializer.Serialize(_settings.Current, Json));
                else foreach (var p in props) _out(p.Name + " = " + Render(p.GetValue(_settings.Current)));
                return 0;
            }
            if (sub == "get")
            {
                if (r.Args.Count < 2) { _err("用法: uzip2 config get <键>"); return 2; }
                var p = Find(props, r.Args[1]);
                if (p == null) { _err("未知配置项: " + r.Args[1]); return 1; }
                var v = p.GetValue(_settings.Current);
                if (r.Json) _out(JsonSerializer.Serialize(new { key = p.Name, value = v }, Json));
                else _out(Render(v));
                return 0;
            }
            if (sub == "set")
            {
                if (r.Args.Count < 3) { _err("用法: uzip2 config set <键> <值>"); return 2; }
                var p = Find(props, r.Args[1]);
                if (p == null) { _err("未知配置项: " + r.Args[1]); return 1; }
                if (!TryConvert(r.Args[2], p.PropertyType, out var val))
                { _err("值类型不匹配 " + p.PropertyType.Name + ": " + r.Args[2]); return 2; }
                _settings.Save(x => p.SetValue(x, val));
                _out(p.Name + " = " + Render(val));
                return 0;
            }
            _err("未知 config 子命令: " + sub); return 2;
        }

        static System.Reflection.PropertyInfo Find(System.Reflection.PropertyInfo[] props, string key)
            => props.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

        static string Render(object v) => v == null ? "" : v is string s ? s : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);

        static bool TryConvert(string text, Type t, out object value)
        {
            value = null; Type tt = Nullable.GetUnderlyingType(t) ?? t;
            if (tt == typeof(bool)) { bool b = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("on", StringComparison.OrdinalIgnoreCase);
                if (!(b || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text.Equals("off", StringComparison.OrdinalIgnoreCase))) return false;
                value = b; return true; }
            if (tt == typeof(int)) { if (int.TryParse(text, out var i)) { value = i; return true; } return false; }
            if (tt == typeof(double)) { if (double.TryParse(text, out var d)) { value = d; return true; } return false; }
            if (tt == typeof(uint)) { if (uint.TryParse(text, out var u)) { value = u; return true; } return false; }
            if (tt == typeof(DateTime)) { if (DateTime.TryParse(text, out var dt)) { value = dt; return true; } return false; }
            if (tt == typeof(string)) { value = text; return true; }
            return false; // List<> 等复杂类型不支持
        }

        // ---------- log ----------

        int DoLog(CliRequest r)
        {
            var sub = r.Args.Count > 0 ? r.Args[0].ToLowerInvariant() : "compress";
            if (sub == "compress")
            {
                var recs = _compressLog.ReadAll();
                if (!string.IsNullOrEmpty(r.Grep))
                    recs = recs.Where(x => (x.FileName ?? "").IndexOf(r.Grep, StringComparison.OrdinalIgnoreCase) >= 0
                                        || (x.Path ?? "").IndexOf(r.Grep, StringComparison.OrdinalIgnoreCase) >= 0
                                        || (x.Password ?? "").IndexOf(r.Grep, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (r.Limit > 0) recs = recs.Take(r.Limit.Value).ToList();
                foreach (var x in recs)
                    _out(x.TimeText + "  " + x.FileName + "  密码:" + (r.ShowPasswords ? (x.Password ?? "-") : "***") + "  " + x.Path);
                return 0;
            }
            if (sub == "app")
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(dir)) { _err("无日志目录"); return 1; }
                var newest = Directory.GetFiles(dir, "app-*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (newest == null) { _err("无运行日志"); return 1; }
                var lines = File.ReadAllLines(newest);
                int n = r.Limit ?? 50;
                foreach (var l in lines.Skip(Math.Max(0, lines.Length - n))) _out(l);
                return 0;
            }
            _err("未知 log 子命令: " + sub); return 2;
        }

        // ---------- shell ----------

        int DoShell(CliRequest r)
        {
            var sub = r.Args.Count > 0 ? r.Args[0].ToLowerInvariant() : "status";
            using (var svc = new ShellMenuService())
            {
                switch (sub)
                {
                    case "register": svc.Register(ShellMenuService.CurrentExePath); _out("已注册右键菜单 -> " + ShellMenuService.CurrentExePath); return 0;
                    case "unregister": svc.Unregister(); _out("已移除右键菜单"); return 0;
                    case "status":
                        var st = svc.State(ShellMenuService.CurrentExePath);
                        _out("状态: " + st + "   当前指向: " + (svc.ProbeCommand() ?? "(无)"));
                        return st == ShellMenuState.Missing ? 1 : 0;
                    default: _err("未知 shell 子命令: " + sub); return 2;
                }
            }
        }

        // ---------- watch once ----------

        async Task<int> DoWatch(CliRequest r, CancellationToken ct)
        {
            var sub = r.Args.Count > 0 ? r.Args[0].ToLowerInvariant() : "once";
            if (sub != "once") { _err("CLI 仅支持 watch once <目录>（常驻监听由 GUI 负责）"); return 2; }
            var folder = r.Args.Count > 1 ? r.Args[1] : r.Output;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { _err("用法: uzip2 watch once <目录> [-o 解压到] [--auto]"); return 2; }
            int code = 0, done = 0;
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (!ArchiveInspector.CanExtractByExtension(f)) continue;
                string dest = ResolveDest(f, new CliRequest { Output = r.Output, Here = r.Here });
                var (res, _) = await TryExtractAsync(f, dest, r, ct).ConfigureAwait(false);
                if (res.Success) { done++; _out(f + " -> " + dest.TrimEnd('\\')); }
                else { _err(f + ": " + (res.Diagnosis ?? "失败")); code = 1; }
            }
            _out("完成 " + done + " 个");
            return code;
        }

        // ---------- update ----------

        async Task<int> DoUpdate(CancellationToken ct)
        {
            var info = await UpdateService.CheckAsync(null, ct).ConfigureAwait(false);
            if (info == null) { _err("没问到版本信息：离线、代理不通或已被限流"); return 1; }
            string cur = UpdateService.CurrentVersion();
            bool newer = UpdateService.IsNewer(cur, info.Version);
            _out(JsonSerializer.Serialize(new { current = cur, latest = info.Version, updateAvailable = newer, url = info.Url }, Json));
            return 0;
        }
    }
}
