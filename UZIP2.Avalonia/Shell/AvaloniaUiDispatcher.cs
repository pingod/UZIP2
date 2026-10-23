using System;
using System.Collections.Specialized;
using Avalonia.Threading;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>Avalonia 侧的界面线程接缝。</summary>
    public sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

        public void Post(Action action) => Dispatcher.UIThread.Post(action);

        public void Invoke(Action action) => Dispatcher.UIThread.Invoke(action);

        public T Invoke<T>(Func<T> func) => Dispatcher.UIThread.Invoke(func);

        // Avalonia 的集合没有 WPF 那种跨线程绑定限制，这里不需要额外登记
        public void EnableCollectionSync(INotifyCollectionChanged collection, object syncRoot) { }
    }
}
