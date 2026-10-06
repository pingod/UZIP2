using System;
using System.Linq;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // v2.3 回归：随机密码只能用 0-9A-Za-z。旧 UTool 的字符池带符号，
    // 符号进文件名/命令行会引发解压失败，所以这里锁死字符集。
    public class PasswordGeneratorTests
    {
        static readonly string Pool = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(64)]
        [InlineData(256)]
        public void New_returns_exactly_the_requested_length(int len)
        {
            Assert.Equal(len, PasswordGenerator.New(len).Length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-100)]
        public void New_with_non_positive_length_returns_empty(int len)
        {
            Assert.Equal("", PasswordGenerator.New(len));
        }

        [Fact]
        public void New_uses_only_alphanumeric_pool_chars()
        {
            var generated = new string(Enumerable.Range(0, 40).SelectMany(_ => PasswordGenerator.New(32)).ToArray());
            Assert.All(generated, c => Assert.True(Pool.IndexOf(c) >= 0, "不应出现字符池之外的字符: " + c));
        }

        [Fact]
        public void New_can_reach_every_pool_char()
        {
            var sample = PasswordGenerator.New(4000);
            var missing = Pool.Where(c => sample.IndexOf(c) < 0).ToArray();
            Assert.Empty(missing);
        }

        [Fact]
        public void New_is_not_deterministic()
        {
            Assert.NotEqual(PasswordGenerator.New(32), PasswordGenerator.New(32));
        }
    }
}
