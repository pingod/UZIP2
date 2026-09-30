using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UZIP2.Models;

namespace UZIP2.Services
{
    public sealed record MigrationResult(bool Performed, bool Success, string Reason)
    {
        public static MigrationResult NothingToDo() => new MigrationResult(false, true, "已是新版配置");
        public static MigrationResult Ok(int book, int paper) => new MigrationResult(true, true, $"迁移完成: 密码本 {book} 条, 密码纸 {paper} 条");
        public static MigrationResult Fail(string reason) => new MigrationResult(true, false, reason);
    }

    // 一次性迁移: Config/UZip.config(appSettings) + PasswordNote.config + PasswordPage.config
    // -> settings.json + passwords.json。成功后旧文件改名 .bak。任何异常保留旧文件原样。
    public static class LegacyConfigMigrator
    {
        public static MigrationResult TryMigrate(string configDir, SettingsService settings, PasswordService passwords)
        {
            var legacyMain = Path.Combine(configDir, "UZip.config");
            var legacyNote = Path.Combine(configDir, "PasswordNote.config");
            var legacyPage = Path.Combine(configDir, "PasswordPage.config");

            bool anyLegacy = File.Exists(legacyMain) || File.Exists(legacyNote) || File.Exists(legacyPage);
            if (!anyLegacy || File.Exists(settings.SettingsPath))
                return MigrationResult.NothingToDo();

            try
            {
                var map = ReadAppSettings(legacyMain);
                var fresh = new AppSettings();

                fresh.AppMode = GetInt(map, "AppMode", 0);
                fresh.WindowOnTop = GetBool(map, "WindowOnTop", true);
                fresh.TrimSpace = GetBool(map, "TrimSpace", false);
                fresh.UseHotKey = GetBool(map, "UseHotKey", false);
                fresh.HotKeyKey = (uint)GetInt(map, "HotKeyKey", 0);
                fresh.HotKeyAlt = GetBool(map, "HotKeyAlt", false);
                fresh.HotKeyShift = GetBool(map, "HotKeyShift", false);
                fresh.HotKeyCtrl = GetBool(map, "HotKeyCtrl", false);
                fresh.ResultWindow = GetBool(map, "ResultWindow", false);
                fresh.ShowDebug = GetBool(map, "ShowDebug", false);
                fresh.DebugMode = GetBool(map, "DebugMode", false);
                fresh.Customize7z = GetBool(map, "Customize7z", false);
                fresh.Customize7zPath = GetString(map, "Customize7zPath", "");
                fresh.ExtractOutMode = GetInt(map, "ExtractOutMode", 1);
                fresh.ExtractOutModePop = GetInt(map, "ExtractOutModePop", 0);
                fresh.ExtractCoverMode = GetCoverMode(map);
                fresh.ExtractUnknow = GetBool(map, "ExtractUnknow", true);
                fresh.DeleteFinishFile = GetBool(map, "DeleteFinishFile", false);
                fresh.AutoOpenAfterExtract = GetBool(map, "AutoOpenAfterExtract", true);
                fresh.CleanTempOnStartup = GetBool(map, "CleanTempOnStartup", true);
                fresh.CreateNewFolder = GetBool(map, "CreateNewFolder", false);
                fresh.CreateNameFolder = GetBool(map, "CreateNameFolder", false);
                fresh.NameToPassword = GetBool(map, "NameToPassword", false);
                fresh.HideZipContent = GetBool(map, "HideZipContent", false);
                fresh.ExtractFilter = GetString(map, "ExtractFilter", "");
                fresh.CompressType = GetInt(map, "CompressType", 0);
                fresh.CompressLevel = GetInt(map, "CompressLevel", 5);
                fresh.CompressOutMode = GetInt(map, "CompressOutMode", 1);
                fresh.CompressOutModePop = GetInt(map, "CompressOutModePop", 0);
                fresh.CompressFilter = GetString(map, "CompressFilter", "");
                fresh.NameFilter = GetString(map, "NameFilter", "");
                fresh.NameFilter2 = GetString(map, "NameFilter2", "");
                fresh.PasswordToName = GetBool(map, "PasswordToName", false);
                fresh.CompressAlone = GetBool(map, "CompressAlone", true);
                fresh.DeleteCompressFinish = GetBool(map, "DeleteCompressFinish", false);
                fresh.LastExtractPath = GetString(map, "LastExtractPath", "");
                fresh.LastCompressPath = GetString(map, "LastCompressPath", "");
                fresh.PWUrl = GetString(map, "PWUrl", "");
                fresh.ReadPasswordMode = GetInt(map, "ReadPasswordMode", 0);
                fresh.PasswordMode = GetInt(map, "PasswordMode", 0);
                fresh.PasswordModePop = GetInt(map, "PasswordModePop", 0);
                fresh.WindowLeft = GetDouble(map, "WindowLeft", -1);
                fresh.WindowTop = GetDouble(map, "WindowTop", -1);

                for (int i = 1; i <= 3; i++)
                    fresh.CustomPasswords[i - 1] = DecryptOrRaw(map, "CustomizePassword" + i);

                for (int i = 1; i <= 8; i++)
                {
                    var path = GetString(map, "CustomizeFolderPath" + i, "");
                    if (!string.IsNullOrWhiteSpace(path))
                        fresh.CustomizeFolders.Add(new CustomFolder
                        {
                            Name = GetString(map, "CustomizeFolderName" + i, "自定义位置" + i),
                            Path = path
                        });
                }

                settings.ReplaceAll(fresh);

                var noteMap = ReadAppSettings(legacyNote);
                var pageMap = ReadAppSettings(legacyPage);
                var book = CollectEntries(noteMap, "PWNote");
                var paper = CollectEntries(pageMap, "PWPaper"); // v2.22 遗留: 纯数字键
                if (paper.Count == 0)
                    foreach (var kv in pageMap)
                        if (IsPureDigits(kv.Key)) paper.Add(new PasswordEntry { Cipher = kv.Value ?? "" });

                ApplyScores(book, noteMap, "PWNote");
                ApplyScores(paper, pageMap, "PWPaper");

                passwords.ImportBook(book);
                passwords.ImportPaper(paper);

                foreach (var f in new[] { legacyMain, legacyNote, legacyPage })
                    if (File.Exists(f))
                        File.Move(f, f + ".bak", true);

                return MigrationResult.Ok(book.Count, paper.Count);
            }
            catch (Exception ex)
            {
                return MigrationResult.Fail("迁移失败(旧配置保持原样): " + ex.Message);
            }
        }

        // ---------- 内部 ----------

        private static readonly HashSet<string> CoverValues = new HashSet<string> { "-aoa", "-aos", "-aou", "-aot" };

        private static string GetCoverMode(Dictionary<string, string> map)
        {
            var c = map.TryGetValue("ExtractCoverMode", out var v) ? v : null;
            return CoverValues.Contains(c) ? c : "-aos";
        }

        private static List<PasswordEntry> CollectEntries(Dictionary<string, string> map, string prefix)
        {
            var list = new List<PasswordEntry>();
            for (int i = 0; i < 200; i++)
            {
                if (!map.TryGetValue(prefix + i, out var raw) || string.IsNullOrEmpty(raw)) break;
                list.Add(new PasswordEntry { Cipher = raw }); // 密文/明文原样搬运
            }
            return list;
        }

        private static void ApplyScores(List<PasswordEntry> entries, Dictionary<string, string> map, string prefix)
        {
            for (int i = 0; i < 200; i++)
            {
                // 旧 SaveScores 索引按字典序写，可能不从 0 起，缺键不能提前 break
                if (!map.TryGetValue("Score_" + prefix + i, out var raw) || string.IsNullOrEmpty(raw)) continue;
                // 密文自带 "DPAPI:" 前缀冒号，必须用最后一个冒号分隔计数
                var sep = raw.LastIndexOf(':');
                if (sep <= 0) continue;
                var pw = Dpapi.Decode(raw.Substring(0, sep));
                if (pw == null || !int.TryParse(raw.Substring(sep + 1), out var count)) continue;
                // 分数按密码原文对齐到条目
                foreach (var e in entries)
                    if (e.Text == pw) { e.SuccessCount = count; break; }
            }
        }

        private static string DecryptOrRaw(Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw)) return "";
            // 旧 CustomizePassword 从未加密，直接取原文
            return raw;
        }

        private static Dictionary<string, string> ReadAppSettings(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (path == null || !File.Exists(path)) return map;
            var doc = new XmlDocument();
            doc.Load(path);
            foreach (XmlNode node in doc.SelectNodes("/configuration/appSettings/add"))
            {
                var key = node.Attributes?["key"]?.Value;
                if (key == null) continue;
                map[key] = node.Attributes?["value"]?.Value ?? "";
            }
            return map;
        }

        private static string GetString(Dictionary<string, string> map, string key, string def)
            => map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;

        private static int GetInt(Dictionary<string, string> map, string key, int def)
            => map.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

        private static double GetDouble(Dictionary<string, string> map, string key, double def)
            => map.TryGetValue(key, out var v) && double.TryParse(v, out var n) ? n : def;

        private static bool GetBool(Dictionary<string, string> map, string key, bool def)
        {
            if (!map.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return def;
            if (bool.TryParse(v, out var b)) return b;
            if (v == "1") return true;
            if (v == "0") return false;
            return def;
        }

        private static bool IsPureDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (var c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
