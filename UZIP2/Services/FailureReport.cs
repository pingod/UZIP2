using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UZIP2.Models;

namespace UZIP2.Services
{
    // 失败报告: 把队列里失败的作业写成一段可直接贴给别人看的纯文本。
    // 刻意不写密码——报告是用来求助的，不是用来泄露的。
    public static class FailureReport
    {
        public static List<JobEntry> Failed(IEnumerable<JobEntry> jobs)
            => (jobs ?? Enumerable.Empty<JobEntry>())
                .Where(j => j != null && j.Status == JobStatus.Failed)
                .ToList();

        public static string Build(IEnumerable<JobEntry> jobs, string sevenZipPath = null)
        {
            var all = (jobs ?? Enumerable.Empty<JobEntry>()).Where(j => j != null).ToList();
            var failed = all.Where(j => j.Status == JobStatus.Failed).ToList();
            var sb = new StringBuilder();

            sb.AppendLine("UZIP 失败报告");
            sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            sb.AppendLine("程序版本: " + VersionText());
            if (!string.IsNullOrEmpty(sevenZipPath)) sb.AppendLine("7z: " + sevenZipPath);
            sb.AppendLine($"失败 {failed.Count} / 共 {all.Count}");
            sb.AppendLine();

            if (failed.Count == 0)
            {
                sb.AppendLine("当前没有失败任务。");
                return sb.ToString();
            }

            for (int i = 0; i < failed.Count; i++)
            {
                var j = failed[i];
                sb.AppendLine($"[{i + 1}] {(j.Kind == "Compress" ? "压缩" : "解压")}  {j.Archive}");
                sb.AppendLine("    原因: " + (string.IsNullOrEmpty(j.Diagnosis) ? "未记录" : j.Diagnosis.Trim()));
                if (j.Total > 0)
                    sb.AppendLine($"    进度: {j.Done}/{j.Total}"
                        + (j.Percent.HasValue ? $" ({j.Percent.Value.ToString("0.#", CultureInfo.CurrentCulture)}%)" : ""));
                if (!string.IsNullOrEmpty(j.CurrentFile))
                    sb.AppendLine("    中断于: " + j.CurrentFile);
                sb.AppendLine("    源文件: " + DescribeFile(j.Archive));
                if (j.Sources != null && j.Sources.Count > 1)
                    sb.AppendLine($"    合并来源 {j.Sources.Count} 项，首项 {j.Sources[0]}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string DescribeFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return "无路径";
            try
            {
                if (!File.Exists(path)) return "不存在（可能已被删除或移动）";
                var fi = new FileInfo(path);
                return $"{fi.Length.ToString("N0", CultureInfo.CurrentCulture)} 字节，修改于 "
                    + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            }
            catch (Exception ex)
            {
                return "无法读取信息: " + ex.GetType().Name;
            }
        }

        static string VersionText()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "未知" : v.ToString();
            }
            catch { return "未知"; }
        }

        public static void Write(string targetPath, string content)
        {
            // BOM 让记事本/Excel 直接认出中文
            File.WriteAllText(targetPath, content, new UTF8Encoding(true));
        }
    }
}
