using System;

namespace UZIP2.Services
{
    public interface IFileLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception exception = null);
        string LatestLogPath { get; }
    }

    // logs/app-yyyyMMdd.log，按天分文件，保留 7 天
    public sealed class FileLogger : IFileLogger
    {
        private readonly string _logDir;
        private readonly object _sync = new object();
        private readonly int _keepDays;

        public FileLogger(string basePath, int keepDays = 7)
        {
            _logDir = System.IO.Path.Combine(basePath, "logs");
            _keepDays = keepDays;
        }

        public string LatestLogPath
        {
            get
            {
                lock (_sync)
                {
                    return System.IO.Path.Combine(_logDir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                }
            }
        }

        public void Info(string message) => Write("INFO", message, null);
        public void Warn(string message) => Write("WARN", message, null);
        public void Error(string message, Exception exception = null) => Write("ERROR", message, exception);

        private void Write(string level, string message, Exception exception)
        {
            try
            {
                lock (_sync)
                {
                    System.IO.Directory.CreateDirectory(_logDir);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (exception != null)
                        line += Environment.NewLine + "    " + exception;
                    System.IO.File.AppendAllText(LatestLogPath, line + Environment.NewLine);
                    CleanupOld();
                }
            }
            catch
            {
                // 日志失败绝不能拖垮主流程
            }
        }

        private void CleanupOld()
        {
            var cutoff = DateTime.Now.AddDays(-_keepDays);
            foreach (var file in System.IO.Directory.GetFiles(_logDir, "app-*.log"))
            {
                try
                {
                    if (System.IO.File.GetLastWriteTime(file) < cutoff)
                        System.IO.File.Delete(file);
                }
                catch { }
            }
        }
    }
}
