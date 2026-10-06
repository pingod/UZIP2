using System;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 方块提示位（图标下方那一行小字）的取舍：进度、完成、拖拽预览、空闲，谁占这一行
    public class PuckHintTests
    {
        [Fact]
        public void Idle_shows_the_drop_prompt()
            => Assert.Equal("拖到这里", PuckProgress.ResolveHint(
                previewing: false, runningCount: 0, percent: null, finishing: false));

        [Fact]
        public void Finished_flash_shows_done()
            => Assert.Equal("完成", PuckProgress.ResolveHint(
                previewing: false, runningCount: 0, percent: null, finishing: true));

        // 活体冒烟抓到的 bug：环收起后提示还卡在"完成"，永远回不到空闲文案
        [Fact]
        public void Restores_the_idle_prompt_when_the_flash_ends()
        {
            var during = PuckProgress.ResolveHint(previewing: false, runningCount: 0, percent: null, finishing: true);
            var after = PuckProgress.ResolveHint(previewing: false, runningCount: 0, percent: null, finishing: false);
            Assert.Equal("完成", during);
            Assert.NotEqual(during, after);
            Assert.Equal("拖到这里", after);
        }

        [Fact]
        public void Dragging_preview_keeps_the_preview_text()
            => Assert.Equal("预览中", PuckProgress.ResolveHint(
                previewing: true, runningCount: 1, percent: 40, finishing: true, previewText: "预览中"));

        [Theory]
        [InlineData(0.0, "准备中")]
        [InlineData(2.0, "2%")]
        [InlineData(42.0, "42%")]
        public void Single_job_shows_its_own_progress(double percent, string expected)
            => Assert.Equal(expected, PuckProgress.ResolveHint(
                previewing: false, runningCount: 1, percent: percent, finishing: false));

        [Theory]
        [InlineData(0.0, "3 个任务")]
        [InlineData(42.0, "3 个任务 42%")]
        public void Several_jobs_carry_their_count(double percent, string expected)
            => Assert.Equal(expected, PuckProgress.ResolveHint(
                previewing: false, runningCount: 3, percent: percent, finishing: false));

        [Fact]
        public void Queued_only_batch_does_not_claim_zero_progress()
            => Assert.Equal("2 个任务", PuckProgress.ResolveHint(
                previewing: false, runningCount: 2, percent: 0, finishing: false));
    }
}
