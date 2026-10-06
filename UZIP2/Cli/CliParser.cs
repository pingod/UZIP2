using System;
using System.Collections.Generic;

namespace UZIP2.Cli
{
    public static class CliParser
    {
        // 命令词表。只有显式命令词/帮助/版本才认定为 CLI，
        // 右键菜单的 --extract / --compress / --register-shell 保持走 GUI 路径。
        static readonly Dictionary<string, CliCommand> Commands =
            new Dictionary<string, CliCommand>(StringComparer.OrdinalIgnoreCase)
            {
                { "list", CliCommand.List },
                { "test", CliCommand.Test },
                { "extract", CliCommand.Extract },
                { "compress", CliCommand.Compress },
                { "convert", CliCommand.Convert },
                { "checksum", CliCommand.Checksum },
                { "diff", CliCommand.Diff },
                { "vault", CliCommand.Vault },
                { "config", CliCommand.Config },
                { "log", CliCommand.Log },
                { "shell", CliCommand.Shell },
                { "watch", CliCommand.Watch },
                { "update", CliCommand.Update },
                { "history", CliCommand.History },
                { "help", CliCommand.Help },
                { "version", CliCommand.Version },
            };

        static readonly HashSet<string> HelpFlags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--help", "-h", "/?", "/h" };

        static readonly HashSet<string> VersionFlags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--version" };

        // 需要跟一个值的长选项
        static readonly HashSet<string> ValueOptions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-o", "--out", "--name", "--password", "--cover", "--type", "--level",
                "--solid", "--threads", "--volume", "--exclude", "--entries", "--write",
                "--passphrase", "--length", "--limit", "--tail", "--grep"
            };

        // 不带值的开关选项；与 ValueOptions 一起构成"已知选项全集"，
        // 不在里面的 --xxx 一律按拼写错误处理，不再静默忽略。
        static readonly HashSet<string> SwitchOptions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--auto", "--here", "--headers", "--delete-source", "--verify", "--json",
                "--show-passwords", "--apply", "--check", "--clear", "--test", "--no-test"
            };

        static bool IsKnownOption(string t)
            => t == "--" || ValueOptions.Contains(t) || SwitchOptions.Contains(t)
               || HelpFlags.Contains(t) || VersionFlags.Contains(t);

        public static bool IsCli(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                var t = Clean(args[i]);
                if (t.Length == 0) continue;
                if (HelpFlags.Contains(t) || VersionFlags.Contains(t)) return true;
                if (t.StartsWith("-"))
                {
                    // 已知选项可以写在命令词前面，未知的留给 GUI 路径
                    if (!IsKnownOption(t)) return false;
                    if (ValueOptions.Contains(t)) i++;   // 取值参数不参与命令词判定
                    continue;
                }
                return Commands.ContainsKey(t);
            }
            return false;
        }

        static string Clean(string s) => (s ?? "").Trim().Trim('"');

        public static CliRequest Parse(string[] args)
        {
            var r = new CliRequest();
            if (args == null) { r.Command = CliCommand.Help; return r; }

            var tokens = new List<string>();
            foreach (var a in args) tokens.Add(Clean(a));
            // 只丢前导空令牌：中间的空串是显式传值（--out ""），吞掉会把下一个参数顺走
            int start = 0;
            while (start < tokens.Count && tokens[start].Length == 0) start++;
            if (start == tokens.Count) { r.Command = CliCommand.Help; return r; }

            // 命令词可以出现在已知选项之后（uzip2 --json list a.zip）
            int cmdAt = -1;
            CliCommand cmd = CliCommand.None;
            for (int k = start; k < tokens.Count; k++)
            {
                var t = tokens[k];
                if (HelpFlags.Contains(t)) { r.Command = CliCommand.Help; return r; }
                if (VersionFlags.Contains(t)) { r.Command = CliCommand.Version; return r; }
                if (t.StartsWith("-"))
                {
                    if (!IsKnownOption(t)) { r.Error = "未知选项: " + t; return r; }
                    if (ValueOptions.Contains(t)) k++;
                    continue;
                }
                if (!Commands.TryGetValue(t, out cmd)) { r.Error = "未知命令: " + t; return r; }
                cmdAt = k;
                break;
            }
            if (cmdAt < 0)
            {
                r.Error = "缺少命令词（list/test/extract/compress/…，完整列表见 help）";
                return r;
            }
            r.Command = cmd;

            bool onlyPositional = false;
            for (int k = start; k < tokens.Count; k++)
            {
                if (k == cmdAt) continue;
                var t = tokens[k];
                if (onlyPositional) { r.Args.Add(t); continue; }

                if (t == "--") { onlyPositional = true; continue; }

                if (ValueOptions.Contains(t))
                {
                    if (k + 1 >= tokens.Count) { r.Error = "选项 " + t + " 缺少参数值"; return r; }
                    ApplyValue(r, t, tokens[++k]);
                    if (r.HasError) return r;
                    continue;
                }

                if (SwitchOptions.Contains(t))
                {
                    switch (t.ToLowerInvariant())
                    {
                        case "--auto": r.Auto = true; break;
                        case "--here": r.Here = true; break;
                        case "--headers": r.Headers = true; break;
                        case "--delete-source": r.DeleteSource = true; break;
                        case "--verify": r.Verify = true; break;
                        case "--test": r.TestAfter = true; break;
                        case "--no-test": r.TestAfter = false; break;
                        case "--json": r.Json = true; break;
                        case "--show-passwords": r.ShowPasswords = true; break;
                        case "--apply": r.Apply = true; break;
                        case "--check": r.Check = true; break;
                        case "--clear": r.Clear = true; break;
                    }
                    continue;
                }

                // 已知选项都处理完了，剩下的 --xxx / -x 只会是拼写错误。
                // 当成位置参数交给 7z，轻则"文件不存在"，重则被 -i@ 清单吃掉。
                if (t.StartsWith("-")) { r.Error = "未知选项: " + t; return r; }
                r.Args.Add(t);
            }
            return r;
        }

        // 7z 的覆盖模式开关：只此四个，大小写按 7z 原样
        static bool IsOverwriteMode(string value)
            => value == "-aoa" || value == "-aos" || value == "-aou" || value == "-aot";

        static void ApplyValue(CliRequest r, string opt, string val)
        {
            switch (opt.ToLowerInvariant())
            {
                case "-o":
                case "--out": r.Output = val; break;
                case "--name": r.Name = val; break;
                case "--password": r.Password = val; break;
                case "--cover":
                    // 这个值会原样进 7z 的 argv，只认四个覆盖模式，别把 "-i@清单" 之类塞进来
                    if (!IsOverwriteMode(val))
                    {
                        r.Error = "--cover 只接受 7z 覆盖模式: -aoa（覆盖）/ -aos（跳过）/ -aou（智能更新）/ -aot（重命名）";
                        return;
                    }
                    r.Cover = val;
                    break;
                case "--type": r.Type = val; break;
                case "--solid": r.Solid = val; break;
                case "--threads": r.Threads = val; break;
                case "--volume": r.Volume = val; break;
                case "--exclude": (r.Exclude ??= new List<string>()).Add(val); break;
                case "--write": r.Write = val; break;
                case "--passphrase": r.Passphrase = val; break;
                case "--grep": r.Grep = val; break;
                case "--entries":
                    (r.Entries ??= new List<string>()).AddRange(SplitList(val));
                    break;
                case "--level":
                    if (int.TryParse(val, out var lv)) r.Level = lv;
                    else r.Error = "--level 需要整数: " + val;
                    break;
                case "--length":
                    if (int.TryParse(val, out var len)) r.Length = len;
                    else r.Error = "--length 需要整数: " + val;
                    break;
                case "--limit":
                case "--tail":
                    if (int.TryParse(val, out var lim)) r.Limit = lim;
                    else r.Error = opt + " 需要整数: " + val;
                    break;
            }
        }

        static IEnumerable<string> SplitList(string val)
        {
            foreach (var part in val.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                yield return part.Trim();
        }
    }
}
