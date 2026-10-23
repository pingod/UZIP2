using System;
using System.IO;
using System.Linq;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// 把一批路径按"自动"模式分流：压缩包进解压队列，其余（文件/目录）进压缩队列。
    /// 拖放、启动参数、第二实例转发都走这里，避免三套判定漂移。
    /// </summary>
    public static class FileRouter
    {
        public static int EnqueueAuto(string[] paths)
        {
            var list = (paths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToList();
            if (list.Count == 0) return 0;

            var archives = list.Where(p => File.Exists(p) && ArchiveInspector.CanExtractByExtension(p)).ToList();
            var others = list.Except(archives).ToList();
            if (archives.Count > 0) AppHost.Worker.EnqueueExtract(archives);
            if (others.Count > 0) AppHost.Worker.EnqueueCompress(others);
            return archives.Count + others.Count;
        }
    }
}
