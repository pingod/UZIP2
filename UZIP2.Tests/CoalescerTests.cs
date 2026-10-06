using System;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 批量收尾时每个作业的状态变化都要刷一次汇总计数；200 个作业连着结束就是 200 次全表扫描。
    // 汇总只需要"变脏之后至少刷一次"，所以排队请求要能合并。
    public class CoalescerTests
    {
        [Fact]
        public void First_request_needs_scheduling()
        {
            var c = new Coalescer();
            Assert.True(c.TryRequest());
        }

        [Fact]
        public void Repeat_requests_are_merged_until_completion()
        {
            var c = new Coalescer();
            Assert.True(c.TryRequest());
            Assert.False(c.TryRequest());
            Assert.False(c.TryRequest());
            c.Complete();
            Assert.True(c.TryRequest());     // 刷完了还能再排下一次
        }

        [Fact]
        public void Pending_flag_reflects_state()
        {
            var c = new Coalescer();
            Assert.False(c.IsPending);
            c.TryRequest();
            Assert.True(c.IsPending);
            c.Complete();
            Assert.False(c.IsPending);
        }

        [Fact]
        public async Task Concurrent_requests_schedule_exactly_once()
        {
            var c = new Coalescer();
            int granted = 0;
            var tasks = new Task[64];
            var go = new ManualResetEventSlim(false);
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(() =>
                {
                    go.Wait();
                    if (c.TryRequest()) Interlocked.Increment(ref granted);
                });
            go.Set();
            await Task.WhenAll(tasks);
            Assert.Equal(1, granted);       // 64 个线程同时来，也只该排一次队
        }
    }
}
