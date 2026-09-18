using System;
using System.IO;
using System.Windows;

namespace UZIP2
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        private void OnAppStartup(object sender, StartupEventArgs e)
        {
            // 启动时清理上次崩溃残留的临时目录（UZipTemp_*）
            CleanupTempFolders();

            if (e.Args.Length != 0)
            {
                USetting.FileList = e.Args;
                USetting.IsCmdMode = true;
            }
        }

        // 扫描程序目录下残留的 UZipTemp_* 隐藏目录并删除
        private void CleanupTempFolders()
        {
            try
            {
                if (!USetting.CleanTempOnStartup) return;
                string basePath = USetting.BasePath;
                if (!Directory.Exists(basePath)) return;
                foreach (string dir in Directory.GetDirectories(basePath, "UZipTemp_*", SearchOption.TopDirectoryOnly))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { /* 清理失败不影响启动 */ }
        }
    }
}
