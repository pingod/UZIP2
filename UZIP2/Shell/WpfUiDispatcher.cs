using System;
using System.Collections.Specialized;
using System.Windows.Data;
using System.Windows.Threading;
using UZIP2.Services;

namespace UZIP2.Shell
{
    /// <summary>WPF 侧的界面线程接缝：ArchiveWorker 等核心组件只认 IUiDispatcher。</summary>
    public sealed class WpfUiDispatcher : IUiDispatcher
    {
        private readonly Dispatcher _dispatcher;

        public WpfUiDispatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public bool CheckAccess() => _dispatcher.CheckAccess();

        public void Post(Action action) => _dispatcher.BeginInvoke(action);

        public void Invoke(Action action) => _dispatcher.Invoke(action);

        public T Invoke<T>(Func<T> func) => _dispatcher.Invoke(func);

        public void EnableCollectionSync(INotifyCollectionChanged collection, object syncRoot)
        {
            // WPF 的签名要 IEnumerable；ObservableCollection 两者都实现，这里只需转一下。
            if (collection is System.Collections.IEnumerable list)
                BindingOperations.EnableCollectionSynchronization(list, syncRoot);
        }
    }
}
