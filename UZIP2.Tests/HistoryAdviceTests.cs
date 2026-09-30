using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class HistoryAdviceTests
    {
        [Theory]
        // 口令类
        [InlineData("Extract", "密码错误", "密码")]
        [InlineData("Extract", "Wrong password", "密码")]
        [InlineData("Extract", "该归档已加密 Data error", "密码")]
        [InlineData("Compress", "invalid password characters", "压缩口令")]
        // 分卷
        [InlineData("Extract", "missing volume .002", "分卷")]
        [InlineData("Extract", "分卷不完整", "分卷")]
        // 磁盘
        [InlineData("Extract", "There is not enough space on the disk", "空间")]
        [InlineData("Compress", "磁盘空间不足", "空间")]
        // 占用 / 权限
        [InlineData("Extract", "The process cannot access the file because it is being used by another process", "占用")]
        [InlineData("Compress", "拒绝访问", "权限")]
        // 路径过长
        [InlineData("Extract", "The system cannot find the path ... too long", "路径")]
        // 找不到
        [InlineData("Extract", "Could not find file 'x.zip'", "不存在")]
        // 损坏
        [InlineData("Extract", "Data Error: CRC failed", "损坏")]
        // 格式不支持
        [InlineData("Extract", "不支持的格式", "格式")]
        [InlineData("Compress", "Cannot open the file as archive", "格式")]
        public void Maps_diagnosis_to_targeted_advice(string kind, string error, string expectedSubstring)
        {
            string advice = HistoryAdvice.For(kind, error);
            Assert.False(string.IsNullOrWhiteSpace(advice));
            Assert.Contains(expectedSubstring, advice);
        }

        [Fact]
        public void Unknown_error_still_returns_actionable_fallback()
        {
            string advice = HistoryAdvice.For("Extract", "某种没见过 0x8000FFFF 的错误");
            Assert.False(string.IsNullOrWhiteSpace(advice));
            Assert.Contains("诊断", advice);
            Assert.Contains("重试", advice);
        }

        [Fact]
        public void Null_or_empty_error_does_not_throw_and_gives_fallback()
        {
            Assert.False(string.IsNullOrWhiteSpace(HistoryAdvice.For("Compress", null)));
            Assert.False(string.IsNullOrWhiteSpace(HistoryAdvice.For("Extract", "")));
        }

        [Fact]
        public void Compress_and_extract_wording_differ_for_the_same_class()
        {
            // 同为空间不足，主语随类型变化
            Assert.Contains("压缩", HistoryAdvice.For("Compress", "no space left"));
            Assert.Contains("解压", HistoryAdvice.For("Extract", "no space left"));
        }
    }
}
