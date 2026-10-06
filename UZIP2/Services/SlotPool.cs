using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Services
{
    // 并行名额池。名额用完后等待者挂在 TaskCompletionSource 上，由"作业收尾"或
    // "并行度调大"当场唤醒——原来的 20 ms 轮询有两个毛病：白等最多一个轮询周期，
    // 而且"读计数→比较→占位"不是原子的，两个派发线程能同时认为还剩一个名额，超发并发。
    public sealed class SlotPool
    {
        readonly object _sync = new object();
        readonly List<TaskCompletionSource<bool>> _waiters = new List<TaskCompletionSource<bool>>();
        int _running;
        int _limit;

        public SlotPool(int limit) => _limit = limit < 1 ? 1 : limit;

        public int Running { get { lock (_sync) return _running; } }
        public int Limit { get { lock (_sync) return _limit; } }

        // 拿到名额才返回；取消只影响还在排队的等待者，已到手的名额不会被收回
        public Task WaitAsync(CancellationToken ct)
        {
            TaskCompletionSource<bool> tcs = null;
            CancellationTokenRegistration reg = default;
            lock (_sync)
            {
                // 排队者优先：队首还挂着人时，新来的不许插队拿走空位
                if (_waiters.Count == 0 && _running < _limit)
                {
                    _running++;
                    return Task.CompletedTask;
                }
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(tcs);
                reg = ct.Register(() =>
                {
                    lock (_sync)
                    {
                        if (_waiters.Remove(tcs)) tcs.TrySetCanceled(ct);
                    }
                });
            }
            return AwaitAsync(tcs, reg);
        }

        static async Task AwaitAsync(TaskCompletionSource<bool> tcs, CancellationTokenRegistration reg)
        {
            try { await tcs.Task.ConfigureAwait(false); }
            finally { reg.Dispose(); }
        }

        public void Release()
        {
            TaskCompletionSource<bool> grant = null;
            lock (_sync)
            {
                if (_running > 0) _running--;
                if (_waiters.Count > 0 && _running < _limit)
                {
                    _running++;
                    grant = _waiters[0];
                    _waiters.RemoveAt(0);
                }
            }
            grant?.TrySetResult(true);
        }

        // 放大立刻放行等待者；缩小只限制新作业，正在跑的名额一个都不抢
        public void SetLimit(int value)
        {
            if (value < 1) value = 1;
            List<TaskCompletionSource<bool>> grants = null;
            lock (_sync)
            {
                _limit = value;
                while (_waiters.Count > 0 && _running < _limit)
                {
                    _running++;
                    var first = _waiters[0];
                    _waiters.RemoveAt(0);
                    (grants ?? (grants = new List<TaskCompletionSource<bool>>())).Add(first);
                }
            }
            if (grants == null) return;
            foreach (var g in grants) g.TrySetResult(true);
        }
    }
}
