using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UZIP2.Services;

namespace UZIP2.Avalonia.Services
{
    /// <summary>
    /// 在 Windows 上跑 Avalonia 版（开发调试）时仍用 DPAPI，保证与 WPF 版共用同一份
    /// passwords.json 时密文互相可读。Linux 走 FileSecretCipher。
    /// </summary>
    public sealed class WindowsDpapiCipher : IPasswordCipher
    {
        const string Prefix = "DPAPI:";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UZIP2.v1");

        public string Encode(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(cipher);
            }
            catch { return plain; }
        }

        public string Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)),
                    Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        public bool IsEncrypted(string stored)
            => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
