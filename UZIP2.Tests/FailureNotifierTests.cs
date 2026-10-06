using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 失败气泡的节流：批次跑完、安静下来再报总数。但监听目录会不停喂新作业，
    // 只等整条队列空等于失败永远没人通知——所以最长等待期一到必须强行播报。
    public class FailureNotifierTests
    {
        [Fact]
        public void Nothing_pending_flushes_nothing()
        {
            var n = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);
            Assert.Equal(0, n.FlushIfDue(queueBusy: false, nowMs: 100_000));
            Assert.Equal(0, n.Pending);
        }

        [Fact]
        public void Quiet_and_idle_flushes_the_total()
        {
            var n = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);
            n.NoteFailure(0);
            n.NoteFailure(100);

            Assert.Equal(0, n.FlushIfDue(queueBusy: false, nowMs: 950));   // 距最后一次只 850ms
            Assert.Equal(2, n.FlushIfDue(queueBusy: false, nowMs: 1050));
            Assert.Equal(0, n.Pending);
        }

        [Fact]
        public void Quiet_while_busy_holds_the_report_until_the_deadline()
        {
            var n = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);
            n.NoteFailure(0);

            Assert.Equal(0, n.FlushIfDue(queueBusy: true, nowMs: 950));    // 批次未完，先攒总数
            Assert.Equal(1, n.FlushIfDue(queueBusy: true, nowMs: 5001));   // 但绝不拖到下个批次
        }

        [Fact]
        public void Endless_failure_stream_is_flushed_at_the_hard_deadline()
        {
            var n = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);
            n.NoteFailure(0);
            n.NoteFailure(4000);
            n.NoteFailure(4800);   // 始终不安静：只有最长等待期能解锁

            Assert.Equal(0, n.FlushIfDue(queueBusy: true, nowMs: 4900));
            Assert.Equal(3, n.FlushIfDue(queueBusy: true, nowMs: 5100));
        }

        [Fact]
        public void Flush_starts_a_fresh_round()
        {
            var n = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);
            n.NoteFailure(0);
            Assert.Equal(1, n.FlushIfDue(queueBusy: false, nowMs: 1000));

            n.NoteFailure(2000);
            Assert.Equal(0, n.FlushIfDue(queueBusy: false, nowMs: 2500));
            Assert.Equal(1, n.FlushIfDue(queueBusy: false, nowMs: 3000));
        }
    }
}
