using System;

namespace UZIP2.Services
{
    /// <summary>
    /// 密码条目的静态加密。Windows 用 DPAPI（绑定当前用户），Linux 用 libsecret / 密钥文件，
    /// 核心只认这个接缝。宿主必须在构造 PasswordService 之前设置 Current。
    /// </summary>
    public interface IPasswordCipher
    {
        string Encode(string plain);
        string Decode(string stored);
        bool IsEncrypted(string stored);
    }

    public static class PasswordCipher
    {
        /// <summary>
        /// 默认实现按明文处理 —— 与旧版 "DPAPI 不可用时回退明文" 一致，保证功能不中断。
        /// 界面宿主启动时必须替换成真正的加密实现。
        /// </summary>
        public static IPasswordCipher Current { get; set; } = new PlaintextCipher();
    }

    /// <summary>没有任何加密能力时的实现：密文字段就是明文，带前缀的旧密文一律视为不可解。</summary>
    public sealed class PlaintextCipher : IPasswordCipher
    {
        public string Encode(string plain) => plain;

        public string Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            // 别的平台/账户留下的密文（DPAPI: / SECRET: …）在本实现下解不开，按缺失处理
            return stored.IndexOf("DPAPI:", StringComparison.Ordinal) == 0 ? null : stored;
        }

        public bool IsEncrypted(string stored) => false;
    }
}
