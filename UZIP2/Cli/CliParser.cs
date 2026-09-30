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
                { "checksum", CliCommand.Checksum },
                { "vault", CliCommand.Vault },
                { "config", CliCommand.Config },
                { "log", CliCommand.Log },
                { "shell", CliCommand.Shell },
                { "watch", CliCommand.Watch },
                { "update", CliCommand.Update },
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

        public static bool IsCli(string[] args)
        {
            if (args == null) return false;
            foreach (var raw in args)
            {
                var t = Clean(raw);
                if (t.Length == 0) continue;
                return IsCliToken(t);
            }
            return false;
        }

        static bool IsCliToken(string t)
        {
            if (HelpFlags.Contains(t) || VersionFlags.Contains(t)) return true;
            return Commands.ContainsKey(t);
        }

        static string Clean(string s) => (s ?? "").Trim().Trim('"');

        public static CliRequest Parse(string[] args)
        {
            var r = new CliRequest();
            if (args == null) { r.Command = CliCommand.Help; return r; }

            var tokens = new List<string>();
            foreach (var a in args) { var c = Clean(a); if (c.Length > 0) tokens.Add(c); }
            if (tokens.Count == 0) { r.Command = CliCommand.Help; return r; }

            int i = 0;
            // 允许 --help/--version 出现在最前面（UZIP2.exe --help 等价于 UZIP2.exe help）
            while (i < tokens.Count && (tokens[i].StartsWith("--") || tokens[i] == "-o" || tokens[i] == "-h" || HelpFlags.Contains(tokens[i])))
            {
                if (HelpFlags.Contains(tokens[i])) { r.Command = CliCommand.Help; return r; }
                if (VersionFlags.Contains(tokens[i])) { r.Command = CliCommand.Version; return r; }
                break;
            }

            if (i < tokens.Count && Commands.TryGetValue(tokens[i], out var cmd))
            {
                r.Command = cmd;
                i++;
            }
            else
            {
                r.Command = CliCommand.Help;
                return r;
            }

            bool onlyPositional = false;
            for (; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (onlyPositional) { r.Args.Add(t); continue; }

                if (t == "--") { onlyPositional = true; continue; }

                if (HelpFlags.Contains(t)) { r.Command = CliCommand.Help; return r; }
                if (VersionFlags.Contains(t)) { r.Command = CliCommand.Version; return r; }

                if (ValueOptions.Contains(t))
                {
                    if (i + 1 >= tokens.Count) { r.Error = "选项 " + t + " 缺少参数值"; return r; }
                    ApplyValue(r, t, tokens[++i]);
                    continue;
                }

                if (t.StartsWith("--") || (t.StartsWith("-") && t.Length > 1 && !char.IsLetter(t[1])))
                {
                    // 布尔开关；未知开关忽略（容忍旧脚本残留参数）
                    switch (t.ToLowerInvariant())
                    {
                        case "--auto": r.Auto = true; break;
                        case "--here": r.Here = true; break;
                        case "--headers": r.Headers = true; break;
                        case "--delete-source": r.DeleteSource = true; break;
                        case "--verify": r.Verify = true; break;
                        case "--json": r.Json = true; break;
                        case "--show-passwords": r.ShowPasswords = true; break;
                        case "--apply": r.Apply = true; break;
                        case "--check": r.Check = true; break;
                        default:
                            if (!ValueOptions.Contains(t)) { /* ignore unknown flag */ }
                            break;
                    }
                    continue;
                }

                r.Args.Add(t);
            }
            return r;
        }

        static void ApplyValue(CliRequest r, string opt, string val)
        {
            switch (opt.ToLowerInvariant())
            {
                case "-o":
                case "--out": r.Output = val; break;
                case "--name": r.Name = val; break;
                case "--password": r.Password = val; break;
                case "--cover": r.Cover = val; break;
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
