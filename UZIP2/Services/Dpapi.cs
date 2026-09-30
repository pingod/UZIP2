namespace UZIP2.Services
{
    // 对旧 DpapiHelper 的薄封装（同前缀/同 entropy/同用户范围，密文互解）
    public static class Dpapi
    {
        public static string Encode(string plain) => UZIP2.DpapiHelper.Encrypt(plain);
        public static string Decode(string stored) => UZIP2.DpapiHelper.Decode(stored);
        public static bool IsEncrypted(string stored) => UZIP2.DpapiHelper.IsEncrypted(stored);
    }
}
