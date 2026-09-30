using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Models
{
    public enum SevenZipError { None, WrongPassword, Corrupt, UnsupportedFormat, DiskFull, PathTooLong, Occupied, Cancelled, NotFound, Unknown }

    // 免掉整包 t 探测用的加密状态: 只有 Unknown 才回退到旧的全量测试链
    public enum EncryptionState { NotEncrypted, Encrypted, Unknown }

    public record SevenZipProgress(double? Percent, string CurrentFile, int DoneCount);

    public record SevenZipResult(bool Success, string Output, string ArchivePath, SevenZipError Error, string Diagnosis)
    {
        public static SevenZipResult Ok(string output, string archive = null) => new SevenZipResult(true, output, archive, SevenZipError.None, null);
        public static SevenZipResult Fail(string output, SevenZipError err, string diagnosis, string archive = null) => new SevenZipResult(false, output, archive, err, diagnosis);
    }
}

namespace UZIP2.Services
{
    using UZIP2.Models;

    // 直接进程调用 7z.exe（替代旧 cmd.exe 管道），ArgumentList 防注入，支持进度/取消/诊断。
    public sealed class SevenZipClient
    {
        private readonly ISettingsService _settings;
        private readonly IFileLogger _logger;

        public SevenZipClient(ISettingsService settings, IFileLogger logger = null)
        {
            _settings = settings;
            _logger = logger;
        }

        public string SevenZipPath => Locate(_settings);

        // 旧 UCmdPathHelp.Find7zPath 优先级: 自定义 > 程序目录 7-Zip > ProgramFiles(x86)
        public static string Locate(ISettingsService settings, string basePath = null)
        {
            var s = settings?.Current;
            if (s != null && s.Customize7z && !string.IsNullOrEmpty(s.Customize7zPath) && File.Exists(s.Customize7zPath))
                return s.Customize7zPath;
            basePath = basePath ?? AppContext.BaseDirectory;
            var embedded = Path.Combine(basePath, "7-Zip", "7z.exe");
            if (File.Exists(embedded)) return embedded;
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        public Task<SevenZipResult> TestAsync(string archive, string password, CancellationToken ct)
        {
            var args = new List<string> { "t", archive };
            if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
            args.Add("-y");
            return RunAsync(args, archive, ct, isExtractOp: true);
        }

        // 只读归档头就能判定是否加密（实测 100 MB 包: l = 0.041 s，t = 1.548 s）。
        // 头加密的包连清单都读不出来，保守判 Encrypted。
        public async Task<EncryptionState> ProbeEncryptionAsync(string archive, CancellationToken ct)
        {
            var args = new List<string> { "l", archive, "-slt", "-y" };
            var r = await RunAsync(args, archive, ct, isExtractOp: true).ConfigureAwait(false);
            var o = r.Output ?? "";
            if (o.IndexOf("Encrypted = +", StringComparison.OrdinalIgnoreCase) >= 0)
                return EncryptionState.Encrypted;
            if (o.IndexOf("Encrypted = -", StringComparison.OrdinalIgnoreCase) >= 0)
                return EncryptionState.NotEncrypted;
            if (ct.IsCancellationRequested) return EncryptionState.Unknown;
            return Classify(o, 1, false) == SevenZipError.WrongPassword
                ? EncryptionState.Encrypted
                : EncryptionState.Unknown;
        }

        public Task<SevenZipResult> ExtractAsync(string archive, string dest, string password,
            IProgress<SevenZipProgress> progress, CancellationToken ct, string coverMode = "-aos")
        {
            Directory.CreateDirectory(dest);
            var args = new List<string> { "x", archive, "-o" + dest, coverMode };
            if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
            args.Add("-y");
            args.Add("-bsp1");
            return RunAsync(args, archive, ct, progress, isExtractOp: true);
        }

        // compressType: 旧 CompressTypes 枚举 (0=zip 1=7z 其余按 -t 名直传)
        public Task<SevenZipResult> CompressAsync(IReadOnlyList<string> files, string outArchive, string password,
            int compressType, int level, bool hideContent, IProgress<SevenZipProgress> progress, CancellationToken ct,
            IReadOnlyList<string> excludeFilters = null)
        {
            var args = new List<string> { "a", outArchive };
            foreach (var f in files) args.Add(f);
            args.Add("-t" + TypeName(compressType));
            args.Add("-mx" + level);
            if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
            if (hideContent && compressType == 1) args.Add("-mhe=on");
            if (excludeFilters != null)
                foreach (var x in excludeFilters) args.Add("-xr!" + x);
            args.Add("-y");
            args.Add("-bsp1");
            return RunAsync(args, outArchive, ct, progress, isExtractOp: false);
        }

        public string ListContent(string archive, string password)
        {
            var args = new List<string> { "l", archive };
            if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
            var r = RunAsync(args, archive, CancellationToken.None, isExtractOp: true).GetAwaiter().GetResult();
            return r.Output;
        }

        private static string TypeName(int compressType)
        {
            switch (compressType)
            {
                case 0: return "zip";
                case 1: return "7z";
                case 2: return "bzip2";
                case 3: return "gzip";
                case 4: return "tar";
                case 5: return "wim";
                case 6: return "xz";
                default: return "zip";
            }
        }

        private async Task<SevenZipResult> RunAsync(List<string> args, string archive, CancellationToken ct,
            IProgress<SevenZipProgress> progress = null, bool isExtractOp = true)
        {
            var exe = SevenZipPath;
            if (exe == null)
                return SevenZipResult.Fail("未找到 7z.exe", SevenZipError.NotFound, "未找到 7z，请在设置中定位 7z.exe");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            DebugLog(exe + " " + Redact(args));

            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    return SevenZipResult.Fail(ex.Message, SevenZipError.NotFound, "无法启动 7z: " + ex.Message);
                }

                p.StandardInput.Close();

                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sb) sb.AppendLine(e.Data);
                    if (progress != null)
                    {
                        var pr = ParseProgressLine(e.Data);
                        if (pr != null) progress.Report(pr);
                    }
                };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                {
                    try
                    {
                        await p.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(true); } catch { }
                        return SevenZipResult.Fail("已取消", SevenZipError.Cancelled, "任务已取消", archive);
                    }
                }
                // WaitForExitAsync(ct) 完成后流读取可能仍在收尾
                p.WaitForExit();

                string output;
                lock (sb) output = sb.ToString();
                var ok = p.ExitCode == 0 && output.Contains("Everything is Ok");
                if (ok) return SevenZipResult.Ok(output, archive);

                if (_settings?.Current?.DebugMode == true)
                    DebugLog("exit=" + p.ExitCode + "\n" + output);
                var err = Classify(output, p.ExitCode, ct.IsCancellationRequested);
                return SevenZipResult.Fail(output, err, Diagnose(output, err), archive);
            }
        }

        private void DebugLog(string message)
        {
            if (_settings?.Current?.DebugMode == true)
                _logger?.Info("[7z] " + message);
        }

        // 命令行含 -p<密码>，写日志前必须脱敏
        internal static string Redact(List<string> args)
        {
            var copy = new List<string>(args.Count);
            foreach (var a in args)
                copy.Add(a.StartsWith("-p", StringComparison.Ordinal) && a.Length > 2 ? "-p***" : a);
            return string.Join(" ", copy);
        }

        public static SevenZipProgress ParseProgressLine(string line)
        {
            if (line == null) return null;
            var m = Regex.Match(line.TrimStart(), @"^(\d+)%\s+(?:(\d+)\s+)?-\s*(.*)$");
            if (!m.Success) return null;
            double? pct = int.TryParse(m.Groups[1].Value, out var pc) ? pc : (double?)null;
            int done = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var d) ? d : 0;
            var file = m.Groups[3].Value.Trim();
            return new SevenZipProgress(pct, file.Length == 0 ? null : file, done);
        }

        public static SevenZipError Classify(string output, int exitCode, bool cancelled)
        {
            if (cancelled) return SevenZipError.Cancelled;
            var o = (output ?? "").ToLowerInvariant();
            if (o.Contains("wrong password") || o.Contains("cannot open encrypted")
                || o.Contains("data error in encrypted") || o.Contains("headers error")
                || o.Contains("enter password"))
                return SevenZipError.WrongPassword;
            if (o.Contains("crc failed") || o.Contains("data error")) return SevenZipError.Corrupt;
            if (o.Contains("not supported") || o.Contains("cannot open the file as archive")) return SevenZipError.UnsupportedFormat;
            if (o.Contains("there are some data after the end")) return SevenZipError.Corrupt;
            if (o.Contains("disk full") || o.Contains("not enough disk space")) return SevenZipError.DiskFull;
            if (o.Contains("filename or extension is too long") || o.Contains("path too long")) return SevenZipError.PathTooLong;
            if (o.Contains("being used by another process") || o.Contains("cannot open") || o.Contains("access is denied")
                || o.Contains("Sharing violation")) return SevenZipError.Occupied;
            if (o.Contains("no files to process")) return SevenZipError.UnsupportedFormat;
            if (exitCode == 2) return SevenZipError.Corrupt;
            return SevenZipError.Unknown;
        }

        // 旧 UTool.Diagnose7zError 的枚举化版本
        public static string Diagnose(string output, SevenZipError err)
        {
            switch (err)
            {
                case SevenZipError.WrongPassword: return "密码错误或加密头损坏";
                case SevenZipError.Corrupt: return "压缩包数据损坏 (CRC)";
                case SevenZipError.UnsupportedFormat: return "格式不支持或包内无文件";
                case SevenZipError.DiskFull: return "磁盘空间不足";
                case SevenZipError.PathTooLong: return "路径过长";
                case SevenZipError.Occupied: return "文件被占用或无法打开";
                case SevenZipError.Cancelled: return "任务已取消";
                case SevenZipError.NotFound: return "未找到 7z";
                case SevenZipError.None: return null;
                default: return "未知错误";
            }
        }
    }
}
