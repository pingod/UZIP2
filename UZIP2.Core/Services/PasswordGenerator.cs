using System.Security.Cryptography;
using System.Text;

namespace UZIP2.Services
{
    // 随机压缩密码（移植 UTool.GetRandomString，改用 RandomNumberGenerator 直接逐字符取随机）
    public static class PasswordGenerator
    {
        const string Pool = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public static string New(int length)
        {
            if (length <= 0) return "";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(Pool[RandomNumberGenerator.GetInt32(Pool.Length)]);
            return sb.ToString();
        }
    }
}
