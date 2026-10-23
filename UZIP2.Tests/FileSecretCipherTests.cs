using System;
using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // Linux 侧没有 DPAPI：密码条目用 AES-GCM 加密，密钥落在 0600 的密钥文件里。
    public class FileSecretCipherTests : IDisposable
    {
        private readonly string _dir;

        public FileSecretCipherTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uzip2-cipher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        private string KeyPath => Path.Combine(_dir, "secret.key");

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Roundtrip_returns_the_original_password()
        {
            var cipher = new FileSecretCipher(KeyPath);
            var encoded = cipher.Encode("hunter2 口令");
            Assert.Equal("hunter2 口令", cipher.Decode(encoded));
        }

        [Fact]
        public void Ciphertext_is_prefixed_and_does_not_leak_the_plaintext()
        {
            var cipher = new FileSecretCipher(KeyPath);
            var encoded = cipher.Encode("TopSecret42");
            Assert.StartsWith("AESGCM1:", encoded);
            Assert.DoesNotContain("TopSecret42", encoded);
            Assert.True(cipher.IsEncrypted(encoded));
        }

        [Fact]
        public void Two_stores_with_different_keys_cannot_read_each_other()
        {
            var a = new FileSecretCipher(KeyPath);
            var encoded = a.Encode("cross-instance");

            var otherKey = Path.Combine(_dir, "second.key");
            var b = new FileSecretCipher(otherKey);
            Assert.Null(b.Decode(encoded));
        }

        [Fact]
        public void A_second_instance_with_the_same_key_file_can_decode()
        {
            var encoded = new FileSecretCipher(KeyPath).Encode("reused key");
            // 模拟 GUI 与 CLI 两个进程共用一份密钥文件
            Assert.Equal("reused key", new FileSecretCipher(KeyPath).Decode(encoded));
        }

        [Fact]
        public void Tampered_ciphertext_is_rejected_instead_of_returning_garbage()
        {
            var cipher = new FileSecretCipher(KeyPath);
            var encoded = cipher.Encode("tamper-me");
            var body = Convert.FromBase64String(encoded.Substring("AESGCM1:".Length));
            body[body.Length - 1] ^= 0xFF;   // 破坏认证标签
            var tampered = "AESGCM1:" + Convert.ToBase64String(body);
            Assert.Null(cipher.Decode(tampered));
        }

        [Fact]
        public void Legacy_plaintext_values_pass_through()
        {
            var cipher = new FileSecretCipher(KeyPath);
            Assert.Equal("old-plain", cipher.Decode("old-plain"));
            Assert.False(cipher.IsEncrypted("old-plain"));
        }

        [Fact]
        public void Foreign_dpapi_ciphertext_is_unreadable_here()
        {
            var cipher = new FileSecretCipher(KeyPath);
            Assert.Null(cipher.Decode("DPAPI:AAAA"));
        }

        [Fact]
        public void Empty_values_are_passed_through()
        {
            var cipher = new FileSecretCipher(KeyPath);
            Assert.Equal("", cipher.Encode(""));
            Assert.Equal("", cipher.Decode(""));
            Assert.Null(cipher.Decode(null));
        }

        [Fact]
        public void Key_file_is_created_lazily_once_and_is_32_bytes()
        {
            Assert.False(File.Exists(KeyPath));
            var cipher = new FileSecretCipher(KeyPath);
            cipher.Encode("first write creates the key");
            Assert.True(File.Exists(KeyPath));
            Assert.Equal(32, new FileInfo(KeyPath).Length);
        }

        [Fact]
        public void A_corrupt_key_file_is_replaced_and_old_ciphertext_becomes_unreadable()
        {
            File.WriteAllBytes(KeyPath, new byte[] { 1, 2, 3 });
            var cipher = new FileSecretCipher(KeyPath);
            var encoded = cipher.Encode("after-repair");
            Assert.Equal("after-repair", new FileSecretCipher(KeyPath).Decode(encoded));
            Assert.Equal(32, new FileInfo(KeyPath).Length);
        }
    }
}
