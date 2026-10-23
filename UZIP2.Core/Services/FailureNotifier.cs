namespace UZIP2.Services
{
    /// <summary>
    /// 失败气泡的节流：攒够"安静期"再报总数，但最长等待期一到就强行播报。
    /// 监听目录会不停喂新作业，只等整条队列空等于失败永远没人通知。
    /// </summary>
    public sealed class FailureNotifier
    {
        private readonly int _quietMs;
        private readonly int _maxWaitMs;
        private readonly object _sync = new object();
        private int _pending;
        private long _firstAtMs;
        private long _lastAtMs;

        public FailureNotifier(int quietMs = 900, int maxWaitMs = 5000)
        {
            _quietMs = quietMs;
            _maxWaitMs = maxWaitMs;
        }

        public int Pending { get { lock (_sync) return _pending; } }

        public void NoteFailure(long nowMs)
        {
            lock (_sync)
            {
                if (_pending == 0) _firstAtMs = nowMs;
                _pending++;
                _lastAtMs = nowMs;
            }
        }

        /// <summary>到报的时刻就取走并清零计数，否则返回 0 继续攒。</summary>
        public int FlushIfDue(bool queueBusy, long nowMs)
        {
            lock (_sync)
            {
                if (_pending == 0) return 0;
                bool quiet = nowMs - _lastAtMs >= _quietMs;
                bool overdue = nowMs - _firstAtMs >= _maxWaitMs;
                // 批内不报是为了把整批的失败攒成一条气泡；最长等待期给这个"攒"上了上限。
                if (!overdue && !(quiet && !queueBusy)) return 0;
                int n = _pending;
                _pending = 0;
                return n;
            }
        }
    }
}
