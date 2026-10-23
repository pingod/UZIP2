using System;
using UZIP2.Cli;

namespace UZIP2.CliHost
{
    /// <summary>Linux 无界面宿主：解析与执行全在跨平台核心的 CliHost 里。</summary>
    internal static class Program
    {
        private static int Main(string[] args) => UZIP2.Cli.CliEntry.Run(args);
    }
}
