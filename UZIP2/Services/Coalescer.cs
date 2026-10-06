using System;
using System.Threading;

namespace UZIP2.Services
{
    /// <summary>
    /// 把"脏了要刷一次"的重复请求合并成一次排队：TryRequest 只在没有待处理请求时返回 true，
    /// 处理方跑完后调 Complete 放行下一轮。跨线程安全（UI 线程之外也会来请求）。
    /// </summary>
    public sealed class Coalescer
    {
        private int _pending;

        public bool IsPending => Volatile.Read(ref _pending) == 1;

        /// <summary>请求一次刷新：返回 true 表示由你负责去排队。</summary>
        public bool TryRequest() => Interlocked.Exchange(ref _pending, 1) == 0;

        /// <summary>本轮处理完毕，放行下一次请求。</summary>
        public void Complete() => Interlocked.Exchange(ref _pending, 0);
    }
}
