using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UZIP2.Services
{
    // 一条压缩记录: 时间 + 包名 + 密码 + 输出全路径
    public sealed class CompressLogRecord
    {
        public DateTime Time { get; set; }
        public string FileName { get; set; }
        public string Password { get; set; }
        public string Path { get; set; }

        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    // 压缩日志（旧 Compress.log 逐字格式: CompressResultToTxt）
    public sealed class CompressLogService
    {
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        const string PasswordMark = "解压密码：";
        const string OutDirMark = "输出目录：";
        const string NameMark = "文件名称：";

        private readonly ISettingsService _settings;

        public string LogPath { get; }

        public CompressLogService(string configDirectory, ISettingsService settings = null)
        {
            LogPath = Path.Combine(configDirectory, "Compress.log");
            _settings = settings;
        }

        public void Log(string archivePath, string password = null)
        {
            // 旧版把密码明文写进日志，是设计里最后一个未加密的秘密。
            // 默认保持明文(行为对等)，设置里可以关掉只留路径。
            bool keepPassword = _settings?.Current?.LogPasswords ?? true;
            string p = string.IsNullOrEmpty(password) ? "-" : (keepPassword ? password : "-");
            string entry = DateTime.Now + "    文件名称：" + Path.GetFileName(archivePath)
                + "    " + PasswordMark + p + "\n" + OutDirMark + archivePath + "\n" + Environment.NewLine;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, entry, Utf8NoBom);
            }
            catch { }
        }

        public List<CompressLogRecord> ReadAll()
        {
            try
            {
                if (!File.Exists(LogPath)) return new List<CompressLogRecord>();
                return Parse(File.ReadAllText(LogPath, Encoding.UTF8));
            }
            catch { return new List<CompressLogRecord>(); }
        }

        // 容错解析: 手工编辑过的日志、旧版格式、缺字段都不能让检索界面打不开。
        // 一条记录 = "时间 文件名称：x 解压密码：y" + "输出目录：path"，空行分隔，新记录排在前面。
        public static List<CompressLogRecord> Parse(string text)
        {
            var list = new List<CompressLogRecord>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            var block = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    AddBlock(list, block);
                    block.Clear();
                    continue;
                }
                block.Add(line);
            }
            AddBlock(list, block);
            list.Reverse();
            return list;
        }

        static void AddBlock(List<CompressLogRecord> list, List<string> block)
        {
            if (block.Count == 0) return;
            var rec = new CompressLogRecord();
            string bare = null;
            foreach (var line in block)
            {
                int pw = line.IndexOf(PasswordMark, StringComparison.Ordinal);
                int od = line.IndexOf(OutDirMark, StringComparison.Ordinal);
                if (pw >= 0) ReadHeader(line, pw, rec);
                else if (od >= 0) rec.Path = line.Substring(od + OutDirMark.Length).Trim();
                else bare = line;   // 没有字段名的续行，最后一条当路径
            }
            rec.Path ??= bare;
            // 没有输出目录就无从定位文件，整条丢弃
            if (!string.IsNullOrEmpty(rec.Path)) list.Add(rec);
        }

        static void ReadHeader(string line, int pw, CompressLogRecord rec)
        {
            rec.Password = NullIfDash(line.Substring(pw + PasswordMark.Length).Trim());
            int nm = line.IndexOf(NameMark, StringComparison.Ordinal);
            if (nm >= 0 && pw >= nm + NameMark.Length)
                rec.FileName = NullIfDash(line.Substring(nm + NameMark.Length, pw - nm - NameMark.Length).Trim());
            DateTime t;
            if (DateTime.TryParse((nm >= 0 ? line.Substring(0, nm) : line.Substring(0, pw)).Trim(),
                CultureInfo.CurrentCulture, DateTimeStyles.None, out t))
                rec.Time = t;
        }

        static string NullIfDash(string s)
            => string.IsNullOrEmpty(s) || s == "-" ? null : s;
    }
}
