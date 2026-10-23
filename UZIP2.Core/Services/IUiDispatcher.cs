using System;
using System.Collections.Specialized;

namespace UZIP2.Services
{
    /// <summary>
    /// 界面线程投递接缝。WPF 用 Dispatcher，Avalonia 用 Dispatcher.UIThread，
    /// 无界面（CLI）用直通实现。核心层不引用任何 UI 框架。
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>当前线程是否就是界面线程。</summary>
        bool CheckAccess();

        /// <summary>异步投递（WPF 的 BeginInvoke）：绝不能把后台线程挂进界面队列。</summary>
        void Post(Action action);

        /// <summary>同步投递（WPF 的 Invoke）：调用方需要返回值时使用。</summary>
        void Invoke(Action action);

        /// <summary>同步投递并取回结果。</summary>
        T Invoke<T>(Func<T> func);

        /// <summary>
        /// 让可观察集合允许跨线程绑定（WPF 的 EnableCollectionSynchronization）。
        /// 没有这个概念的平台留空即可。
        /// </summary>
        void EnableCollectionSync(INotifyCollectionChanged collection, object syncRoot);
    }

    /// <summary>CLI / 单测：没有界面线程，所有投递当场执行。</summary>
    public sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action?.Invoke();
        public void Invoke(Action action) => action?.Invoke();
        public T Invoke<T>(Func<T> func) => func == null ? default : func();
        public void EnableCollectionSync(INotifyCollectionChanged collection, object syncRoot) { }
    }
}
