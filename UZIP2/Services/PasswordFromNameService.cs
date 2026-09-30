using System;

namespace UZIP2.Services
{
    // 从文件名提取密码 + 提纯文件名（移植 UTool.SplitString / MainWindow 提纯逻辑）
    public static class PasswordFromNameService
    {
        // 取第一个分隔符之后、最后一个尾分隔符之前的内容；无首分隔符返回 null
        public static string SplitString(string text, string separator)
        {
            if (text == null || string.IsNullOrEmpty(separator)) return null;
            string p = text;
            int n1 = p.IndexOf(separator, StringComparison.Ordinal);
            if (n1 < 0) return null;
            p = p.Remove(0, n1 + separator.Length);
            int n2 = p.LastIndexOf(separator, StringComparison.Ordinal);
            if (n2 >= 0) p = p.Remove(n2);
            return p;
        }

        // delimiter2 为 null 时与 delimiter1 相同（旧版单分隔符语义）
        public static string Extract(string fileNameNoExtension, string delimiter1, string delimiter2 = null)
        {
            if (string.IsNullOrEmpty(delimiter1)) return null;
            if (delimiter2 == null) delimiter2 = delimiter1;
            if (delimiter1 == delimiter2)
                return SplitString(fileNameNoExtension, delimiter1);

            // 双分隔符: 首分隔符之后到其后第一个尾分隔符之前
            if (fileNameNoExtension == null) return null;
            int n1 = fileNameNoExtension.IndexOf(delimiter1, StringComparison.Ordinal);
            if (n1 < 0) return null;
            string rest = fileNameNoExtension.Remove(0, n1 + delimiter1.Length);
            int n2 = rest.IndexOf(delimiter2, StringComparison.Ordinal);
            return n2 >= 0 ? rest.Substring(0, n2) : rest;
        }

        // 提纯: 去掉 "分隔符+密码+分隔符"，退化则去掉 "分隔符+密码"（移植 MainWindow 逻辑）
        public static string PurifyName(string fileNameNoExtension, string filter, string password)
        {
            if (fileNameNoExtension == null || filter == null || password == null)
                return fileNameNoExtension;

            string rep = filter + password + filter;
            string stripped = fileNameNoExtension.Replace(rep, "");
            if (stripped == fileNameNoExtension)
            {
                rep = filter + password;
                stripped = fileNameNoExtension.Replace(rep, "");
            }
            return stripped;
        }
    }
}
