using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Services
{
    // 就地自更新：只针对"框架依赖单文件 UZIP2.exe"。
    //
    // 运行中的 exe 在 Windows 上被锁住、不能直接覆盖，所以策略是：
    //   1. 把新 exe 下到同目录的 .update.new 文件；
    //   2. 校验它确实在我们期望的这个版本、且是合法 PE；
    //   3. 生成一个中继脚本并脱离启动：脚本等本进程退出 → 反复重试覆盖 →
    //      覆盖成功后拉起新版 → 删掉脚本自身；
    //   4. 调用方随后退出，把控制权交给脚本。
    //
    // 自包含单文件（~170MB）绝不就地覆盖：GitHub 上唯一的直链资产是 8MB 的
    // 框架依赖版，用它覆盖 170MB 会把"免装运行时"的用户变成"必须装 .NET 8"，
    // 所以 CanApplyInPlace 会拒绝大文件。
    public static class SelfUpdater
    {
        // 40MB 阈值：框架依赖单文件约 8.5MB，自包含约 170MB，中间留足余量。
        public const long SelfContainedThresholdBytes = 40L * 1024 * 1024;

        public static string CurrentExePath()
        {
            try { return Environment.ProcessPath; }
            catch { return null; }
        }

        // 能否就地替换：路径存在、不是单文件运行时的解压缓存目录、且体积是框架依赖量级。
        public static bool CanApplyInPlace(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            try
            {
                if (!File.Exists(exePath)) return false;
                var dir = (Path.GetDirectoryName(exePath) ?? "").Replace('/', '\\');
                // 单文件 native 库解压缓存（%LOCALAPPDATA%\Temp\.net\UZIP2\...）不能当替换目标。
                if (dir.IndexOf("\\Temp\\.net\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (dir.IndexOf("\\.net\\UZIP2", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                var length = new FileInfo(exePath).Length;
                if (length > SelfContainedThresholdBytes) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 流式下载到 tempPath，按已下载字节数回调进度。返回落盘总字节数。
        // 用 ResponseHeadersRead，让 HttpClient.Timeout 只约束"拿到响应头"，
        // 大文件下载靠 CancellationToken 取消，不受整体超时限制。
        public static async Task<long> DownloadToTempAsync(string url, string tempPath,
            IProgress<long> progress, CancellationToken ct)
        {
            var http = UpdateService.NewClient(TimeSpan.FromSeconds(30));
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var expected = resp.Content.Headers.ContentLength ?? -1L;
                if (expected > UpdateService.MaxDownloadBytes && UpdateService.MaxDownloadBytes > 0)
                    throw new InvalidDataException("下载体积超出预期（可能不是我们发布的 exe）");
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                return total;
            }
            finally
            {
                http.Dispose();
            }
        }

        // 校验下载物：必须存在、是 MZ/PE、且版本正好等于期望版本（防止拿错包覆盖好包）。
        // expectedVersion 传空则只校验"是合法 exe"。返回 (Ok, Error)，Error 为 null 表示通过。
        public static Task<(bool Ok, string Error)> VerifyAsync(string tempPath, string expectedVersion)
            => Task.Run(() => Verify(tempPath, expectedVersion));

        public static (bool Ok, string Error) Verify(string tempPath, string expectedVersion)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath))
                    return (false, "下载文件不存在");
                var fi = new FileInfo(tempPath);
                if (fi.Length < 64 * 1024)
                    return (false, "下载文件过小，不像一个完整的程序");
                // MZ 头
                using (var fs = File.OpenRead(tempPath))
                {
                    int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                    if (b0 != 'M' || b1 != 'Z')
                        return (false, "下载文件不是可执行程序");
                }
                // FileVersionInfo 能读到版本资源 → 基本确认是合法 PE
                var pv = FileVersionInfo.GetVersionInfo(tempPath);
                var fileVer = Normalize(pv.FileVersion);
                if (string.IsNullOrEmpty(pv.OriginalFilename) && string.IsNullOrEmpty(fileVer))
                    return (false, "无法读取该文件的版本信息");
                var want = Normalize(expectedVersion);
                if (!string.IsNullOrEmpty(want))
                {
                    if (string.IsNullOrEmpty(fileVer))
                        return (false, "下载文件缺少版本信息，无法确认是否为 " + expectedVersion);
                    if (!string.Equals(fileVer, want, StringComparison.OrdinalIgnoreCase))
                        return (false, $"下载文件版本 {fileVer} 与期望 {want} 不一致");
                }
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, "校验失败: " + ex.Message);
            }
        }

        // "3.3.0.0" / "v3.3.0-beta" → "3.3.0"（最多三段，去掉尾部 0 段之外的修饰）
        static string Normalize(string version)
        {
            version = (version ?? "").Trim().TrimStart('v', 'V');
            var parts = version.Split('.', '-');
            string joined = null;
            for (int i = 0; i < parts.Length && i < 3; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) break;
                joined = joined == null ? parts[i] : joined + "." + parts[i];
            }
            return joined;
        }

        // 纯函数：生成中继 cmd。pid/exe/new 直接内联（路径用双引号包住，规避空格）。
        // 脚本流程：等 pid 退出 → 最多 8 次重试覆盖 new→exe → 成功后删 new、拉起 exe → 删自身。
        public static string BuildRelayScript(int pid, string exePath, string newPath, string launchArgs)
        {
            launchArgs = launchArgs ?? "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine("rem UZIP2 self-update relay (generated; safe to delete)");
            // 等本进程退出：tasklist 里还能看到该 PID 就继续等。
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /nh /fi \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            // 覆盖：文件锁可能还没完全释放，重试若干次。
            sb.AppendLine("set \"OK=\"");
            sb.AppendLine("for /l %%i in (1,1,8) do (");
            sb.AppendLine("  if not defined OK (");
            sb.AppendLine($"    copy /y \"{newPath}\" \"{exePath}\" >nul 2>&1");
            sb.AppendLine("    if not errorlevel 1 set \"OK=1\"");
            sb.AppendLine("    if not defined OK ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("if defined OK (");
            sb.AppendLine($"  del /f /q \"{newPath}\" >nul 2>&1");
            sb.AppendLine($"  start \"\" \"{exePath}\" {launchArgs}");
            sb.AppendLine(")");
            sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");
            sb.AppendLine("endlocal");
            return sb.ToString();
        }

        public static string RelayScriptPath()
            => Path.Combine(Path.GetTempPath(), "uzip_update_" + Guid.NewGuid().ToString("N") + ".cmd");

        // 脱离当前进程启动脚本：UseShellExecute=false + CreateNoWindow，不抢桌面焦点。
        public static void StartDetached(string scriptPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c \"\"" + scriptPath + "\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }

        // 编排：下载→校验→写脚本→脱离启动。成功返回 (true,null)，调用方应立即退出。
        // 失败清理 .new 并返回中文错误。取消也走这条（返回"更新已取消"）。
        public static async Task<(bool Ok, string Error)> StageAndApplyAsync(
            UpdateInfo info, string exePath, IProgress<long> progress, CancellationToken ct)
        {
            if (info == null || string.IsNullOrEmpty(info.DownloadUrl))
                return (false, "该版本没有可用的直下链接，请从下载页手动更新");
            if (!CanApplyInPlace(exePath))
                return (false, "当前运行方式不支持就地更新，请从下载页手动更新");

            string temp = exePath + ".update.new";
            try
            {
                await DownloadToTempAsync(info.DownloadUrl, temp, progress, ct).ConfigureAwait(false);
                var v = Verify(temp, info.Version);
                if (!v.Ok) { TryDelete(temp); return v; }

                string script = BuildRelayScript(Environment.ProcessId, exePath, temp, "");
                string scriptPath = RelayScriptPath();
                File.WriteAllText(scriptPath, script, System.Text.Encoding.ASCII);
                StartDetached(scriptPath);
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                return (false, "更新已取消");
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                return (false, "更新失败: " + ex.Message);
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
