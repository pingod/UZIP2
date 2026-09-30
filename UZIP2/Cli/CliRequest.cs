using System.Collections.Generic;

namespace UZIP2.Cli
{
    public enum CliCommand
    {
        None, Help, Version, List, Test, Extract, Compress,
        Checksum, Vault, Config, Log, Shell, Watch, Update
    }

    // 解析后的命令行意图。位置参数原样保留在 Args，由 Runner 按命令再解释
    // （vault/config/log/shell/watch 用 Args[0] 当子命令）。
    public sealed class CliRequest
    {
        public CliCommand Command = CliCommand.None;
        public List<string> Args = new List<string>();

        // 通用
        public string Output;          // -o / --out  输出目录
        public string Password;        // --password  显式密码
        public bool Auto;              // --auto      走密码本/密码纸试密码链
        public bool Json;              // --json      结构化输出
        public string Error;           // 非空 = 用法错误

        // 解压
        public bool Here;              // --here      解压到档案所在目录（平铺，不建同名目录）
        public List<string> Entries;   // --entries   仅解这些包内路径
        public string Cover;           // --cover     7z 覆盖开关 -aos/-aoa/-aou/-aot

        // 压缩
        public string Name;            // --name      指定输出档案名
        public string Type;            // --type      zip|7z|...
        public int? Level;             // --level     0/1/3/5/7/9
        public string Solid;           // --solid     on|off
        public string Threads;         // --threads   off|2|4|8...
        public bool Headers;           // --headers   -mhe=on（仅 7z）
        public string Volume;          // --volume    700m|1g|字节
        public List<string> Exclude;   // --exclude   排除规则（可重复）
        public bool DeleteSource;      // --delete-source

        // 校验
        public bool Verify;            // --verify    核对旁挂校验文件
        public string Write;           // --write     生成旁挂文件: sha256|md5|sha1

        // 密码库
        public string Passphrase;      // --passphrase 导出/导入口令
        public int Length = 16;        // --length     gen 长度

        // 日志
        public int? Limit;             // --limit / --tail
        public string Grep;            // --grep
        public bool ShowPasswords;     // --show-passwords

        public bool HasError => Error != null;
    }
}
