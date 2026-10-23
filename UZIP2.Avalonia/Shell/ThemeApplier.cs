using System;
using Avalonia;
using Avalonia.Styling;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// 把 <c>AppSettings.Theme</c> 里的主题名翻译成 Avalonia 的主题变体并立即生效。
    ///
    /// 设置文件跨平台共用，WPF 那边有四种值（System/Light/Dark/HighContrast），
    /// Avalonia 的 Fluent 主题只有 Light/Dark/Default（Default = 跟随系统），
    /// 没有高对比度变体 —— 旧配置里的高对比度按「跟随系统」处理，不报错。
    /// </summary>
    public static class ThemeApplier
    {
        public const string System = "System";
        public const string Light = "Light";
        public const string Dark = "Dark";

        /// <summary>设置页下拉框的候选值（顺序即展示顺序）。</summary>
        public static readonly (string Key, string Label)[] Choices =
        {
            (System, "跟随系统"),
            (Light, "浅色"),
            (Dark, "深色"),
        };

        /// <summary>主题名归一化：只认 Light/Dark，其余（含 HighContrast、空值、旧版乱码）都算跟随系统。</summary>
        public static string Normalize(string theme)
        {
            if (string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase)) return Light;
            if (string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase)) return Dark;
            return System;
        }

        /// <summary>归一化后的主题名 → Avalonia 主题变体。</summary>
        public static ThemeVariant ToVariant(string theme)
        {
            switch (Normalize(theme))
            {
                case Light: return ThemeVariant.Light;
                case Dark: return ThemeVariant.Dark;
                default: return ThemeVariant.Default;
            }
        }

        /// <summary>
        /// 立即把主题应用到当前 Application。还没起来的（无头测试、构造期、控制台）静默跳过，
        /// 换主题失败也只记日志 —— 主题不该成为程序起不来的原因。
        /// 返回是否真的应用了。
        /// </summary>
        public static bool Apply(string theme)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return false;
                app.RequestedThemeVariant = ToVariant(theme);
                return true;
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("应用主题失败: " + ex.Message);
                return false;
            }
        }
    }
}
