using System;
using System.Text.RegularExpressions;
using System.Windows;

namespace UZIP2.Services
{
    // 剪贴板导入密码纸（移植 GetClipboardHelp/PatsePassword 的读取与清洗部分）
    public sealed class ClipboardService
    {
        private readonly PasswordService _passwords;
        private readonly ISettingsService _settings;

        public event Action<string> Info;      // 给 UI 的提示文本

        public ClipboardService(PasswordService passwords, ISettingsService settings)
        {
            _passwords = passwords;
            _settings = settings;
        }

        // 清洗剪切板文本: 可选整体 Trim，永远去掉制表符/回车残留
        public static string Clean(string raw, bool trimSpace)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = Regex.Replace(raw, "[\t\r]", "");
            if (trimSpace) s = s.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // 读剪切板并导入密码纸，返回新增数量
        public int PasteFromClipboard()
        {
            string text;
            try
            {
                if (!Clipboard.ContainsText())
                {
                    Info?.Invoke("剪切板内没有密码可以贴入");
                    return 0;
                }
                text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                Info?.Invoke("读取剪切板失败: " + ex.Message);
                return 0;
            }

            var cleaned = Clean(text, _settings?.Current.TrimSpace ?? true);
            if (cleaned == null)
            {
                Info?.Invoke("剪切板内没有密码可以贴入");
                return 0;
            }

            int added = _passwords.PasteToPaper(cleaned);
            Info?.Invoke(added > 0
                ? $"已写入密码纸 (+{added})，现有 {_passwords.Paper.Count} 个"
                : "密码已在密码纸/密码本中(或密码纸已满)");
            return added;
        }
    }
}
