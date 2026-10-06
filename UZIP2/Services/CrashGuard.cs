using System;
using System.Windows.Threading;

namespace UZIP2.Services
{
    // 全局异常兜底。三类出口：
    //  - UI 线程：记日志 + 提示，然后继续跑（一次绑定的空引用不该带走整个程序）；
    //  - 后台线程：CLR 一定要终止进程，能做的只有崩之前把现场写进日志；
    //  - 未 await 的 Task：.NET 默认吞掉，静默吞掉等于 bug 隐身，至少留下记录。
    // 致命异常（OOM / 栈溢出）不吞：那之后继续跑只会在坏掉的状态上写用户的文件。
    public static class CrashGuard
    {
        public static bool OnUiThreadException(Exception ex, IFileLogger logger, Action<string> notify)
        {
            if (ex == null) return false;
            Report("UI 线程未处理异常", ex, logger);
            if (!CanKeepRunning(ex)) return false;
            try { notify?.Invoke(Summary(ex)); } catch { /* 提示失败不能再抛一次 */ }
            return true;
        }

        public static void OnBackgroundException(Exception ex, IFileLogger logger)
        {
            if (ex == null) return;
            Report("后台线程未处理异常（进程即将退出）", Flatten(ex), logger);
        }

        public static void OnUnobservedTaskException(AggregateException ex, IFileLogger logger)
        {
            if (ex == null) return;
            Report("未被观察的 Task 异常", Flatten(ex), logger);
        }

        public static bool CanKeepRunning(Exception ex) =>
            !(ex is OutOfMemoryException || ex is StackOverflowException);

        public static string Summary(Exception ex) => ex.GetType().Name + ": " + ex.Message;

        static void Report(string title, Exception ex, IFileLogger logger)
        {
            try { logger?.Error(title + ": " + ex, ex); }
            catch { /* 日志自己坏了也不能再抛 */ }
        }

        // AggregateException 的 Message 只有"发生一个或多个错误"，要看的是里面那条
        static Exception Flatten(Exception ex)
        {
            var inner = (ex as AggregateException)?.Flatten().InnerException;
            return inner ?? ex;
        }

        // 挂到 WPF/CLR 的三个出口上。notify 传 null 时只记日志不弹窗。
        public static void Attach(IFileLogger logger, Dispatcher dispatcher, Action<string> notify)
        {
            if (dispatcher != null)
                dispatcher.UnhandledException += (s, e) => e.Handled = OnUiThreadException(e.Exception, logger, notify);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                OnBackgroundException(e.ExceptionObject as Exception, logger);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                OnUnobservedTaskException(e.Exception, logger);
                e.SetObserved();       // 不让 Finalizer 线程再抛一次把退出流程搅乱
            };
        }
    }
}
