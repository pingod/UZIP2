using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UZIP2.Services
{
    /// <summary>
    /// 没有 DPAPI 的平台（Linux）用的密码存储：AES-256-GCM + 本机密钥文件。
    /// 密钥文件默认 0600（仅本人可读写）；同机同用户的 GUI 与 CLI 共用同一份。
    /// 密文格式 <c>AESGCM1:base64(nonce|ciphertext|tag)</c>，AAD 固定，防止跨版本挪用。
    /// </summary>
    public sealed class FileSecretCipher : IPasswordCipher
    {
        public const string Prefix = "AESGCM1:";
        const int KeySize = 32;
        const int NonceSize = 12;
        const int TagSize = 16;
        static readonly byte[] Aad = Encoding.UTF8.GetBytes("UZIP2.v1");

        private readonly string _keyPath;
        private readonly object _sync = new object();
        private byte[] _key;

        public FileSecretCipher(string keyFilePath)
        {
            _keyPath = keyFilePath ?? throw new ArgumentNullException(nameof(keyFilePath));
        }

        public string Encode(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                byte[] key = Key();
                byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] cipher = new byte[data.Length];
                byte[] tag = new byte[TagSize];
                using (var aes = new AesGcm(key, TagSize))
                    aes.Encrypt(nonce, data, cipher, tag, Aad);

                var blob = new byte[NonceSize + cipher.Length + TagSize];
                Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
                Buffer.BlockCopy(cipher, 0, blob, NonceSize, cipher.Length);
                Buffer.BlockCopy(tag, 0, blob, NonceSize + cipher.Length, TagSize);
                return Prefix + Convert.ToBase64String(blob);
            }
            catch
            {
                // 与 DPAPI 实现一致：加密不可用时退回明文，保证功能不中断
                return plain;
            }
        }

        public string Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (stored.StartsWith("DPAPI:", StringComparison.Ordinal)) return null;   // 别的平台留下的
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;  // 旧明文
            try
            {
                byte[] blob = Convert.FromBase64String(stored.Substring(Prefix.Length));
                if (blob.Length < NonceSize + TagSize) return null;

                int cipherLen = blob.Length - NonceSize - TagSize;
                byte[] plain = new byte[cipherLen];
                using (var aes = new AesGcm(Key(), TagSize))
                    aes.Decrypt(blob.AsSpan(0, NonceSize),
                                blob.AsSpan(NonceSize, cipherLen),
                                blob.AsSpan(NonceSize + cipherLen, TagSize),
                                plain, Aad);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // 密钥换了、密文损坏或来自别的机器：按"缺失"处理，绝不当成密码用
                return null;
            }
        }

        public bool IsEncrypted(string stored)
            => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

        byte[] Key()
        {
            lock (_sync)
            {
                if (_key != null) return _key;

                var dir = Path.GetDirectoryName(_keyPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                _key = TryReadKey();
                if (_key == null)
                {
                    _key = RandomNumberGenerator.GetBytes(KeySize);
                    WriteKey(_key);
                }
                return _key;
            }
        }

        byte[] TryReadKey()
        {
            try
            {
                if (!File.Exists(_keyPath)) return null;
                var raw = File.ReadAllBytes(_keyPath);
                // 长度不对视为损坏：重新生成（旧密文随之不可解，宁可丢密码也不要用错密钥）
                return raw.Length == KeySize ? raw : null;
            }
            catch
            {
                return null;
            }
        }

        void WriteKey(byte[] key)
        {
            // 密钥文件存在但长度不对 = 损坏：先挪开（而不是硬覆盖）再写新的
            if (File.Exists(_keyPath) && TryReadKey() == null)
            {
                try { File.Delete(_keyPath); } catch { }
            }

            var tmp = _keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, key);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            try
            {
                // 并发首次启动时两个进程都可能在写：后到的直接采用先到的那把钥匙
                File.Move(tmp, _keyPath, overwrite: false);
            }
            catch (IOException)
            {
                try { File.Delete(tmp); } catch { }
                var existing = TryReadKey();
                if (existing != null) _key = existing;
            }
        }
    }
}
