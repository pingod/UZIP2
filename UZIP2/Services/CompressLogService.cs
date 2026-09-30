using System;
using System.IO;
using System.Text;

namespace UZIP2.Services
{
    // 压缩日志（旧 Compress.log 逐字格式: CompressResultToTxt）
    public sealed class CompressLogService
    {
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public string LogPath { get; }

        public CompressLogService(string configDirectory)
        {
            LogPath = Path.Combine(configDirectory, "Compress.log");
        }

        public void Log(string archivePath, string password = null)
        {
            string p = string.IsNullOrEmpty(password) ? "-" : password;
            string entry = DateTime.Now + "    文件名称：" + Path.GetFileName(archivePath)
                + "    解压密码：" + p + "\n输出目录：" + archivePath + "\n" + Environment.NewLine;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, entry, Utf8NoBom);
            }
            catch { }
        }
    }
}
