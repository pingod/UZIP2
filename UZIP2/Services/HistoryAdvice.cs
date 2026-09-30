using System;

namespace UZIP2.Services
{
    // 纯函数：把一条失败作业的（类型 + 诊断文本）翻译成"下一步怎么做"的中文建议。
    // 不做任何 IO，便于单测，也保证失败报告 / 历史页 / CLI 三处给出完全一致的指引。
    public static class HistoryAdvice
    {
        /// <param name="kind">Extract | Compress</param>
        /// <param name="error">作业的 Diagnosis 文本（失败原因）</param>
        public static string For(string kind, string error)
        {
            bool extract = !string.Equals(kind, "Compress", StringComparison.OrdinalIgnoreCase);
            string e = (error ?? "").ToLowerInvariant();

            // —— 口令类（最高价值：这正是本项目存在的理由）——
            if (Contains(e, "密码", "口令", "password", "wrongpass", "encrypted", "加密"))
                return extract
                    ? "解压需要正确密码：确认密码本 / 密码纸已收录该包口令，或右键『解压』时手动指定；命令行可用 `extract <包> --password <口令>` 或 `--auto` 自动试库。"
                    : "压缩口令异常：检查设置的默认口令是否含非法字符，或换一条简单口令重试。";

            // —— 分卷 / 多卷 ——
            if (Contains(e, "分卷", "volume", ".00", "multi-part", "缺少", "missing"))
                return "分卷可能不完整：把同名前缀的 .001/.002…（或 .z01/.zip 各卷）放在同一目录后，对第一卷再解压一次。";

            // —— 磁盘空间 ——
            if (Contains(e, "磁盘", "空间", "no space", "not enough space", "insufficient"))
                return (extract ? "解压" : "压缩") + "目标磁盘空间不足：清理或换一个输出盘后重试。";

            // —— 文件被占用 / 拒绝访问 ——
            if (Contains(e, "being used", "in use", "locked", "拒绝访问", "access is denied", "unauthorizedacces", "正由另一进程"))
                return "文件被占用或权限不足：关闭正在使用该文件的程序（播放器 / 杀软 / 资源管理器预览），或以可访问该目录的身份重试。";

            // —— 路径过长 ——
            if (Contains(e, "path too long", "too long", "文件名过大", "路径过长", "max path"))
                return "路径过长：改用『解压到当前文件夹』、换一个更短的输出目录，或先重命名压缩包后再解压。";

            // —— 文件找不到 ——
            if (Contains(e, "could not find file", "not find", "filenotfound", "找不到文件", "系统找不"))
                return "源文件不存在或已被移动：确认路径后再重试；若来自下载目录，检查下载是否完成。";

            // —— 归档损坏 ——
            if (Contains(e, "corrupt", "damaged", "unexpected end", "data error", "损坏", "不可能", "crc"))
                return "归档可能已损坏或下载不完整：先用『测试 / test』校验；若为分卷，确认各卷齐全且校验通过。";

            // —— 格式不支持 ——
            if (Contains(e, "不支持", "unsupported", "unknown format", "cannot open", "不是压缩"))
                return "该格式当前 7-Zip 内核不支持或扩展名与真实格式不符：核对真实类型（用 `list` 试开），必要时换 7-Zip 完整版内核。";

            // —— 兜底 ——
            return (extract ? "解压" : "压缩") + "失败：查看下方诊断信息后，用相同参数重试；仍失败可在主页导出失败报告，或到设置里核对 7-Zip 内核路径。";
        }

        static bool Contains(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.Contains(n)) return true;
            return false;
        }
    }
}
