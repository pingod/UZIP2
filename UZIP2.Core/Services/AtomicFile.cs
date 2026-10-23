using System;
using System.IO;
using System.Text;
using System.Threading;

namespace UZIP2.Services
{
    // 写临时文件再原子替换。Windows 上杀软/索引服务会短暂抓住目标文件，
    // File.Move 偶发 UnauthorizedAccessException（实测 200 连写时约 3% 概率撞上），
    // 退避重试几十毫秒就能过去；实在失败也不留 .tmp 垃圾。
    public static class AtomicFile
    {
        const int Retries = 8;

        public static void Write(string path, string content, Encoding encoding = null)
        {
            // 临时名必须每次唯一：GUI 与 CLI 是两个进程（同进程内 worker 也会并发落盘），
            // 固定用 path+".tmp" 时甲的 Move 会把乙刚写好的临时文件搬走，
            // 乙再 Move 一个已经不存在的文件就直接抛异常。
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, encoding ?? new UTF8Encoding(false));
                for (int i = 0; ; i++)
                {
                    try
                    {
                        File.Move(temp, path, true);
                        return;
                    }
                    catch (UnauthorizedAccessException) when (i < Retries) { Thread.Sleep(25 * (i + 1)); }
                    catch (IOException) when (i < Retries) { Thread.Sleep(25 * (i + 1)); }
                }
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
    }
}
