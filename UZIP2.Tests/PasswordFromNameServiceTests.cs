using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class PasswordFromNameServiceTests
    {
        [Theory]
        [InlineData("a-b-c", "-", "b")]
        [InlineData("片源@密码@说明", "@", "密码")]
        [InlineData("无前缀", "-", null)]
        [InlineData("x-abc", "-", "abc")]           // 只有一个分隔符时取其后全部
        [InlineData("a-b-c-d", "-", "b-c")]         // 旧语义: 首个之后到最后一个之前
        public void SplitString_matches_legacy(string text, string sep, string expected)
        {
            Assert.Equal(expected, PasswordFromNameService.SplitString(text, sep));
        }

        [Fact]
        public void Extract_single_delimiter_equals_splitString()
        {
            Assert.Equal("b", PasswordFromNameService.Extract("a-b-c", "-"));
        }

        [Fact]
        public void Extract_two_delimiters_cuts_at_first_tail()
        {
            Assert.Equal("mid|y", PasswordFromNameService.Extract("x|mid|y|z", "|", null));
            Assert.Equal("mid", PasswordFromNameService.Extract("[mid]rest", "[", "]"));
        }

        [Fact]
        public void PurifyName_removes_double_wrapped_first()
        {
            Assert.Equal("资源合集", PasswordFromNameService.PurifyName("资源_密码123_合集", "_", "密码123"));
        }

        [Fact]
        public void PurifyName_falls_back_to_single_wrap()
        {
            // "文件@pw" 只有一份分隔符: "-pw-" 不命中, 退化去掉 "@pw"
            Assert.Equal("文件", PasswordFromNameService.PurifyName("文件@pw", "@", "pw"));
        }

        [Fact]
        public void PurifyName_no_match_returns_original()
        {
            Assert.Equal("干净名字", PasswordFromNameService.PurifyName("干净名字", "@", "pw"));
        }
    }
}
