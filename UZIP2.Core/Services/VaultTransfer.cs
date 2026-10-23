using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UZIP2.Services
{
    public sealed class VaultException : Exception
    {
        public VaultException(string message) : base(message) { }
    }

    // 密码库跨机搬运。DPAPI 绑定"本机+本用户"，拷到另一台机器解不开，
    // 所以导出时用用户口令重新加密: PBKDF2-SHA256 派生密钥 + AES-GCM 认证加密。
    public static class VaultTransfer
    {
        public const string FormatName = "UZIP-Vault";
        public const int Version = 1;
        public const int Iterations = 210000;
        const int MaxCipherBytes = 8 * 1024 * 1024;

        sealed class Envelope
        {
            [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
            [JsonPropertyName("v")] public int V { get; set; } = Version;
            [JsonPropertyName("kdf")] public string Kdf { get; set; } = "PBKDF2-SHA256";
            [JsonPropertyName("iter")] public int Iter { get; set; } = Iterations;
            [JsonPropertyName("salt")] public string Salt { get; set; }
            [JsonPropertyName("iv")] public string Iv { get; set; }
            [JsonPropertyName("tag")] public string Tag { get; set; }
            [JsonPropertyName("data")] public string Data { get; set; }
        }

        sealed class Item
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("pw")] public string Password { get; set; }
            [JsonPropertyName("hits")] public int Hits { get; set; }
            [JsonPropertyName("paper")] public bool Paper { get; set; }
        }

        public static string Export(IEnumerable<PasswordEntry> entries, string passphrase)
        {
            var pass = RequirePassphrase(passphrase);
            var items = (entries ?? Enumerable.Empty<PasswordEntry>())
                .Where(e => !string.IsNullOrEmpty(e?.Text))
                .Select(e => new Item { Name = e.Name ?? "", Password = e.Text, Hits = e.SuccessCount, Paper = e.IsPaper })
                .ToList();

            var plain = JsonSerializer.SerializeToUtf8Bytes(items,
                new JsonSerializerOptions { WriteIndented = false });

            var salt = RandomNumberGenerator.GetBytes(16);
            var iv = RandomNumberGenerator.GetBytes(12);
            var key = Derive(pass, salt, Iterations);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            try
            {
                using (var aes = new AesGcm(key, 16))
                    aes.Encrypt(iv, plain, cipher, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(key);
            }

            return JsonSerializer.Serialize(new Envelope
            {
                Salt = Convert.ToBase64String(salt),
                Iv = Convert.ToBase64String(iv),
                Tag = Convert.ToBase64String(tag),
                Data = Convert.ToBase64String(cipher),
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        public static List<PasswordEntry> Import(string json, string passphrase)
        {
            var pass = RequirePassphrase(passphrase);
            Envelope env;
            try { env = JsonSerializer.Deserialize<Envelope>(json ?? ""); }
            catch (JsonException) { env = null; }
            if (env == null) throw new VaultException("文件不是有效的 UZIP 密码库导出件。");
            if (!string.Equals(env.Format, FormatName, StringComparison.OrdinalIgnoreCase) || env.V != Version)
                throw new VaultException($"导出件版本不受支持（{env.Format} v{env.V}）。");
            if (string.IsNullOrEmpty(env.Salt) || string.IsNullOrEmpty(env.Iv)
                || string.IsNullOrEmpty(env.Tag) || string.IsNullOrEmpty(env.Data))
                throw new VaultException("导出件缺少必要字段，可能已被截断。");
            if (env.Iter <= 0 || env.Iter > 5_000_000)
                throw new VaultException("导出件的 KDF 迭代次数异常。");

            byte[] salt, iv, tag, cipher;
            try
            {
                salt = Convert.FromBase64String(env.Salt);
                iv = Convert.FromBase64String(env.Iv);
                tag = Convert.FromBase64String(env.Tag);
                cipher = Convert.FromBase64String(env.Data);
            }
            catch (FormatException) { throw new VaultException("导出件不是合法的 Base64 内容。"); }
            if (cipher.Length > MaxCipherBytes) throw new VaultException("导出件过大。");

            var key = Derive(pass, salt, env.Iter);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(iv, cipher, tag, plain);
            }
            catch (CryptographicException) { throw new VaultException("口令错误，或文件已被改动。"); }
            catch (ArgumentException) { throw new VaultException("导出内容长度异常，文件可能已损坏。"); }
            finally { CryptographicOperations.ZeroMemory(key); }

            List<Item> items;
            try { items = JsonSerializer.Deserialize<List<Item>>(plain); }
            catch (JsonException) { items = null; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (items == null) throw new VaultException("导出内容无法解析，文件可能已损坏。");

            return items
                .Where(i => !string.IsNullOrEmpty(i?.Password))
                .Select(i => new PasswordEntry
                {
                    Name = i.Name ?? "",
                    Text = i.Password,
                    SuccessCount = i.Hits,
                    IsPaper = i.Paper,
                })
                .ToList();
        }

        static string RequirePassphrase(string passphrase)
        {
            if (string.IsNullOrWhiteSpace(passphrase))
                throw new VaultException("口令不能为空。");
            if (passphrase.Length < 8)
                throw new VaultException("口令至少 8 个字符。");
            return passphrase;
        }

        static byte[] Derive(string passphrase, byte[] salt, int iterations)
            => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt,
                iterations, HashAlgorithmName.SHA256, 32);
    }
}
