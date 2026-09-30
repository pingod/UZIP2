using System;
using System.Collections.Generic;

namespace UZIP2.Services
{
    public enum ShellVerb { Extract, ExtractHere, Compress }

    // 命令行意图。右键菜单写的是 "--verb 路径"，所以解析必须容忍乱序和重复。
    public sealed class ShellRequest
    {
        public ShellVerb Verb { get; set; } = ShellVerb.Extract;
        public List<string> Files { get; } = new List<string>();
        public bool RegisterShell { get; set; }
        public bool UnregisterShell { get; set; }
    }

    public static class ShellArgs
    {
        public const string RegisterFlag = "--register-shell";
        public const string UnregisterFlag = "--unregister-shell";
        public const string ExtractFlag = "--extract";
        public const string ExtractHereFlag = "--extract-here";
        public const string CompressFlag = "--compress";

        public static ShellRequest Parse(string[] args)
        {
            var r = new ShellRequest();
            if (args == null) return r;
            foreach (var raw in args)
            {
                var a = (raw ?? "").Trim().Trim('"');
                if (a.Length == 0) continue;
                switch (a.ToLowerInvariant())
                {
                    case RegisterFlag: r.RegisterShell = true; break;
                    case UnregisterFlag: r.UnregisterShell = true; break;
                    case ExtractFlag: r.Verb = ShellVerb.Extract; break;
                    case ExtractHereFlag: r.Verb = ShellVerb.ExtractHere; break;
                    case CompressFlag: r.Verb = ShellVerb.Compress; break;
                    default:
                        // 未知开关直接忽略，别让升级后旧菜单留下的参数把文件路径吃掉
                        if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                        r.Files.Add(a);
                        break;
                }
            }
            return r;
        }
    }
}
