using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using VB = Microsoft.VisualBasic;

namespace UZIP2.Services
{
    // 7z 同名文件处理方式（原值直接透传给 7z）
    public static class CoverModes
    {
        public const string Cover = "-aoa";
        public const string Pass = "-aos";
        public const string RenameNew = "-aou";
        public const string RenameOld = "-aot";
    }

    // 文件过滤 + 目录操作（移植 Tools.cs:73 Split / 239-376 MoveFolder/FindFile/Delete）
    public static class FilterService
    {
        // 分号规则串解析: 去空白、合并连续分号、去尾分号; 无有效规则返回 null
        public static string[] ParseRules(string rules)
        {
            if (string.IsNullOrEmpty(rules)) return null;
            string s = Regex.Replace(rules, @"\s", "");
            string prev;
            do
            {
                prev = s;
                s = s.Replace(";;", ";");
            } while (s != prev);
            if (s.Length == 0) return null;
            if (s.EndsWith(";")) s = s.Substring(0, s.Length - 1);
            if (s.Length == 0) return null;
            return s.Split(';');
        }

        // 递归查找目录中匹配通配符的文件（移植 UTool.FindFile）
        public static List<string> FindFiles(string rootPath, string pattern)
        {
            var list = new List<string>();
            FindFilesHelp(rootPath, pattern, list);
            return list;
        }

        static void FindFilesHelp(string dir, string pattern, List<string> list)
        {
            if (!Directory.Exists(dir)) return;
            list.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            foreach (var sub in Directory.GetDirectories(dir))
                FindFilesHelp(sub, pattern, list);
        }

        // 对解压目录执行过滤删除，返回删除数量；单个文件删除失败不影响其余
        public static int Apply(string extractedDir, string filterRules, bool toRecycle = false)
        {
            var rules = ParseRules(filterRules);
            if (rules == null) return 0;
            int deleted = 0;
            foreach (var rule in rules)
            {
                foreach (var file in FindFiles(extractedDir, rule))
                {
                    try { Delete(file, toRecycle); deleted++; }
                    catch { }
                }
            }
            return deleted;
        }

        public static void Delete(string path, bool recycle = false)
        {
            if (File.Exists(path))
            {
                if (recycle)
                    VB.FileIO.FileSystem.DeleteFile(path, VB.FileIO.UIOption.OnlyErrorDialogs, VB.FileIO.RecycleOption.SendToRecycleBin);
                else
                    File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                if (recycle)
                    VB.FileIO.FileSystem.DeleteDirectory(path, VB.FileIO.UIOption.OnlyErrorDialogs, VB.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Directory.Delete(path, true);
            }
        }

        // 同名避让: name-New01.ext / name-Old01.ext ...（移植 MoveFolderHelp）
        public static string NextFreePath(string path, string suffix)
        {
            string candidate = path;
            int num = 1;
            while (File.Exists(candidate))
            {
                string name = Path.GetFileNameWithoutExtension(path) + suffix + (num / 10 < 1 ? "0" + num : num.ToString())
                    + Path.GetExtension(path);
                candidate = Path.Combine(Path.GetDirectoryName(path), name);
                num++;
            }
            return candidate;
        }

        // 递归移动目录内容并按覆盖模式处理冲突（移植 UTool.MoveFolder）
        public static void MoveFolder(string sourcePath, string destPath, string coverMode = CoverModes.Pass)
        {
            if (!Directory.Exists(sourcePath)) return;
            if (!Directory.Exists(destPath))
                Directory.CreateDirectory(destPath);

            foreach (var file in Directory.GetFiles(sourcePath))
            {
                string destFile = Path.Combine(destPath, Path.GetFileName(file));
                if (File.Exists(destFile))
                {
                    if (coverMode == CoverModes.Cover)
                    {
                        File.Delete(destFile);
                        File.Move(file, destFile);
                    }
                    else if (coverMode == CoverModes.RenameNew)
                    {
                        File.Move(file, NextFreePath(destFile, "-New"));
                    }
                    else if (coverMode == CoverModes.RenameOld)
                    {
                        File.Move(destFile, NextFreePath(destFile, "-Old"));
                        File.Move(file, destFile);
                    }
                    // Pass / null: 跳过
                }
                else
                {
                    File.Move(file, destFile);
                }
            }

            foreach (var sub in Directory.GetDirectories(sourcePath))
                MoveFolder(sub, Path.Combine(destPath, Path.GetFileName(sub)), coverMode);
        }
    }
}
