namespace UZIP2.Cli
{
    public static class Usage
    {
        public const string OneLine = "用法: UZIP2.exe <命令> [选项] [参数...]   （运行 UZIP2.exe help 查看全部）";

        public const string Text =
@"UZIP 命令行 (与 GUI 同一套内核，headless 运行，不弹窗)

说明: 主程序是 GUI 子系统(WinExe)。从 cmd/PowerShell 调用时若需等待并拿到退出码，用
      start """" /wait UZIP2.exe ...   或   PowerShell: Start-Process UZIP2.exe -ArgumentList ... -Wait

退出码: 0 成功 / 1 操作失败 / 2 用法错误

命令:
  list <包> [--password pw|--auto] [--json [--show-passwords]]   列出包内条目(密码默认脱敏)
  test <包...> [--password pw|--auto]              校验完整性
  extract <包...> [-o DIR|--here] [--password pw|--auto] [--entries a;b] [--cover -aoa]
                                                   解压(默认解到当前目录; --here 解到包所在目录)
  compress <文件/目录...> [-o DIR|--name x.7z] [--type 7z|zip|bz2|gz|tar|wim|xz]
             [--level 0|1|3|5|7|9] [--password pw] [--volume 700m|1g|字节]
             [--solid on|off] [--threads N|off] [--headers] [--exclude 规则] [--delete-source]
             [--test|--no-test]  压缩后自检产物(缺省跟随设置里的压缩后校验; 自检不过不删源)
  checksum <文件...> [--verify|--write sha256|md5|sha1] [--json]   SHA-256 展示/旁挂核对/写校验文件
  diff <包A> <包B> [--password pw|--auto] [--json]   对比两个包的清单(0=一致 1=有差异, 不读文件内容)
  convert <包...> [--type 7z|zip|tar|bz2|gz|wim|xz] [-o DIR] [--password pw|--auto] [--json]
                                                   格式互转: 解包后重打包, 原包保留(bz2/gz/xz 只装单个文件)
  vault list|add <名称> <密码>|remove <名称>|clear-paper|gen [--length N]
        |export <文件> --passphrase <口令>|import <文件> --passphrase <口令>
                                                   密码库(list 默认不显示明文, --show-passwords 才显示)
  config show|get <键>|set <键> <值> [--json]       读写 settings.json
  log compress [--grep 关键字] [--limit N] | log app [--tail N] [--show-passwords]
  shell register|unregister|status                 HKCU 右键菜单
  watch once <目录> [-o 解压到] [--auto]            扫描目录内所有压缩包并解压
  update [--check|--apply]                         检查 GitHub Releases 新版本; --apply 就地自动更新(仅框架依赖版)
  history [--grep 关键字] [--limit N] [--json] [--show-passwords] [--clear]
                                                   查看持久化的解压/压缩历史(失败项附下一步建议); --clear 清空
  help | version

通用选项: --json 结构化输出   -o/--out 输出目录   --auto 自动试密码本/密码纸
         --show-passwords 在输出里显示明文密码(默认一律脱敏)
         命令词或选项拼错都会以退出码 2 报错，不再静默忽略";
    }
}
