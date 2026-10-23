using System;

namespace UZIP2.Services
{
    /// <summary>自更新策略：只有 Windows 的框架依赖单文件版支持就地换 exe。</summary>
    public static class UpdatePolicy
    {
        public const string PackageManagerHint =
            "Linux 版不支持就地自更新：请用包管理器更新（pacman -Syu），或重新下载 tar.gz 覆盖安装";

        public static bool CanApplyInPlaceOn(bool isWindows) => isWindows;
    }
}
