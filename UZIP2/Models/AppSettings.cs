using System.Collections.Generic;

namespace UZIP2.Models
{
    public sealed class CustomFolder
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    public sealed class AppSettings
    {
        // 0=自动 1=仅解压 2=仅压缩 (与旧 AppMode 编号一致)
        public int AppMode { get; set; }
        public string Theme { get; set; } = "System"; // System|Light|Dark

        public bool WindowOnTop { get; set; } = true;
        public bool UseHotKey { get; set; }
        public bool TrimSpace { get; set; }
        public bool ResultWindow { get; set; }
        public bool ShowDebug { get; set; }
        public bool DebugMode { get; set; }

        public uint HotKeyKey { get; set; }
        public bool HotKeyAlt { get; set; }
        public bool HotKeyShift { get; set; }
        public bool HotKeyCtrl { get; set; }

        public bool Customize7z { get; set; }
        public string Customize7zPath { get; set; } = "";

        // 解压
        public int ExtractOutMode { get; set; }
        public int ExtractOutModePop { get; set; }
        public string ExtractCoverMode { get; set; } = "-aos"; // 7z 覆盖开关原值: -aoa 覆盖 -aos 跳过 -aou 重命名新 -aot 重命名旧
        public bool ExtractUnknow { get; set; } = true;
        public bool DeleteFinishFile { get; set; }
        public bool DeleteToRecycle { get; set; } = true;
        public bool AutoOpenAfterExtract { get; set; } = true;
        public bool CleanTempOnStartup { get; set; } = true;
        // 并行作业数(重启生效)。实测 4×51 MB: 串行 3.263 s → 4 并发 0.880 s。
        public int ParallelExtract { get; set; } = 3;
        public int ParallelCompress { get; set; } = 2;
        public bool CreateNewFolder { get; set; }
        public bool CreateNameFolder { get; set; }
        public bool NameToPassword { get; set; }
        public bool HideZipContent { get; set; }
        public string ExtractFilter { get; set; } = "";
        public List<CustomFolder> CustomizeFolders { get; set; } = new List<CustomFolder>();

        // 压缩
        public int CompressOutMode { get; set; }
        public int CompressOutModePop { get; set; }
        public int CompressType { get; set; }        // 旧 CompressTypes 枚举: 0zip 1_7z 2bz 3gz 4tar 5wim 6xz
        public int CompressLevel { get; set; } = 5;  // 旧 CompressLevels: 0/1/3/5/7/9
        public string CompressFilter { get; set; } = "";
        public string NameFilter { get; set; } = "";
        public string NameFilter2 { get; set; } = "";
        public List<string> CustomPasswords { get; set; } = new List<string> { "", "", "" }; // 旧 CustomizePassword1-3
        public bool PasswordToName { get; set; }
        public bool CompressAlone { get; set; } = true;
        public bool DeleteCompressFinish { get; set; }

        // 密码
        public int ReadPasswordMode { get; set; }
        public string PWUrl { get; set; } = "";
        public int PasswordMode { get; set; }
        public int PasswordModePop { get; set; }
        public List<string> InternalPasswords { get; set; } = new List<string>
        {
            "password1", "password2", "password3", "password4", "password5", "password6"
        };

        public string LastExtractPath { get; set; } = "";
        public string LastCompressPath { get; set; } = "";

        // 窗口几何 (-1=未记录)
        public double WindowLeft { get; set; } = -1;
        public double WindowTop { get; set; } = -1;
        public double WindowWidth { get; set; } = -1;
        public double WindowHeight { get; set; } = -1;
    }
}
