using System;
using System.Text;
using System.Security.Cryptography;

namespace UZIP2
{
    /// <summary>
    /// 用 Windows DPAPI (ProtectedData) 对密码做对称加密。
    /// 密文与当前 Windows 用户账户绑定，其他账户无法解密。
    /// 存储时统一加 "DPAPI:" 前缀，用于和旧版明文配置区分。
    /// </summary>
    public static class DpapiHelper
    {
        private const string Prefix = "DPAPI:";
        private static readonly byte[] entropy = Encoding.UTF8.GetBytes("UZIP2.v1");

        /// <summary>把明文密码加密成可落盘的字符串（带 DPAPI: 前缀）。失败时回退返回明文。</summary>
        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] cipher = ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(cipher);
            }
            catch
            {
                // DPAPI 不可用时（极少见）退回明文，保证功能不中断
                return plain;
            }
        }

        /// <summary>读取配置值。带 DPAPI: 前缀则解密，否则按旧明文原样返回。</summary>
        public static string Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
            try
            {
                byte[] cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] plain = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // 密文损坏或跨用户，返回空串让上层视为密码缺失
                return null;
            }
        }

        /// <summary>判断配置值是否已经是 DPAPI 加密格式。</summary>
        public static bool IsEncrypted(string stored)
        {
            return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
        }
    }
}
