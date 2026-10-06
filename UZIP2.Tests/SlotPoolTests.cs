using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 并行名额池：名额释放/改设置都要立刻唤醒等待者，而不是靠 20 ms 轮询碰运气。
    public class SlotPoolTests
    {
        [Fact]
        public async Task Admits_up_to_the_limit_and_wakes_the_oldest_waiter_first()
        {
            var pool = new SlotPool(2);

            await pool.WaitAsync(CancellationToken.None);
            await pool.WaitAsync(CancellationToken.None);
            var third = pool.WaitAsync(CancellationToken.None);
            Assert.False(third.IsCompleted);

            pool.Release();
            await third;                     // FIFO：第一个排队的那个拿到名额
            Assert.Equal(2, pool.Running);

            pool.Release();
            pool.Release();
            Assert.Equal(0, pool.Running);
        }

        [Fact]
        public async Task Raising_the_limit_admits_waiters_without_a_release()
        {
            var pool = new SlotPool(1);
            await pool.WaitAsync(CancellationToken.None);
            var waiting = pool.WaitAsync(CancellationToken.None);
            Assert.False(waiting.IsCompleted);

            pool.SetLimit(3);               // 调大并行度：等待者当场放行
            await waiting;
            Assert.Equal(2, pool.Running);
        }

        // 调小平行度只限制新作业，不能把正在跑的名额抢走
        [Fact]
        public async Task Lowering_the_limit_never_interrupts_running_slots()
        {
            var pool = new SlotPool(3);
            for (int i = 0; i < 3; i++) await pool.WaitAsync(CancellationToken.None);

            var waiting = pool.WaitAsync(CancellationToken.None);
            pool.SetLimit(1);

            pool.Release();                  // 3 -> 2，仍高于新上限
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);

            pool.Release();                  // 2 -> 1，到达上限，还是不放
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);

            pool.Release();                  // 1 -> 0，此时才放行
            await waiting;
            Assert.Equal(1, pool.Running);
        }

        [Fact]
        public async Task Cancelled_waiter_leaks_no_slot()
        {
            var pool = new SlotPool(1);
            await pool.WaitAsync(CancellationToken.None);

            var cts = new CancellationTokenSource();
            var waiting = pool.WaitAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            // 排队者退出后，名额池必须还能正常给下一个作业用
            pool.Release();
            Assert.Equal(0, pool.Running);
            await pool.WaitAsync(CancellationToken.None);
            Assert.Equal(1, pool.Running);
        }

        [Fact]
        public async Task Check_then_act_race_cannot_exceed_the_limit()
        {
            var pool = new SlotPool(2);
            int peak = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < 12; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await pool.WaitAsync(CancellationToken.None);
                    int now = Interlocked.Increment(ref peak);
                    Assert.True(pool.Running <= 2, "并发名额超发，Running=" + pool.Running + " 峰值 " + now);
                    await Task.Delay(2);
                    pool.Release();
                }));
            }
            await Task.WhenAll(tasks);
            Assert.True(peak >= 2, "名额池没有真正并发过");
        }
    }
}
