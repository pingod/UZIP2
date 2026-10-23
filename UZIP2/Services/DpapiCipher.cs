namespace UZIP2.Services
{
    /// <summary>Windows 侧实现：密码条目用 DPAPI 加密（绑定当前 Windows 用户）。</summary>
    public sealed class DpapiCipher : IPasswordCipher
    {
        public string Encode(string plain) => DpapiHelper.Encode(plain);
        public string Decode(string stored) => DpapiHelper.Decode(stored);
        public bool IsEncrypted(string stored) => DpapiHelper.IsEncrypted(stored);
    }
}
