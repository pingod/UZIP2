namespace UZIP2.Models
{
    // 一条持久化的作业历史记录（落 Config\history.json）。
    // 刻意不含任何明文口令字段以外的敏感结构：Password 只有在 settings.LogPasswords 打开时才会被写入，
    // 关闭时留 null；Advice/Error 来自不含口令的诊断文本。
    public sealed class HistoryEntry
    {
        public string Kind { get; set; }            // Extract | Compress
        public string Source { get; set; }          // 源档案 / 被压缩的文件
        public string Destination { get; set; }     // 实际输出目录（可能为 null）
        public string Status { get; set; }          // Success | Failed
        public string Error { get; set; }           // 失败诊断（成功为 null）
        public string Advice { get; set; }          // 失败时的下一步建议（成功为 null）
        public long Timestamp { get; set; }         // Unix 毫秒（UTC），收尾时刻
        public int Count { get; set; }              // 处理的条目数
        public long DurationMs { get; set; }        // 从开始运行到收尾的耗时
        public string Password { get; set; }        // 命中的口令，仅当允许记录口令时写；否则 null
    }
}
