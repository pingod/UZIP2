using System;
using System.Drawing;
using System.Windows.Forms;

namespace UZIP2
{
    /// <summary>
    /// 系统托盘：气泡通知 + 右键菜单（显示主窗口 / 退出）。
    /// </summary>
    public static class Notifier
    {
        private static NotifyIcon _ni;
        private static readonly object _lock = new object();
        private static Action _showMain;
        private static Action _quit;

        /// <summary>初始化托盘图标和右键菜单。在 UI 线程启动时调用一次。</summary>
        public static void Setup(Action showMain, Action quit)
        {
            _showMain = showMain;
            _quit = quit;
            lock (_lock)
            {
                if (_ni != null) return;
                _ni = new NotifyIcon();
                _ni.Icon = System.Drawing.SystemIcons.Application;
                _ni.Visible = true;
                _ni.Text = "UZIP2";
                var menu = new ContextMenuStrip();
                menu.Items.Add("显示主窗口", null, (s, e) => { if (_showMain != null) _showMain(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (s, e) => { if (_quit != null) _quit(); });
                _ni.ContextMenuStrip = menu;
                _ni.DoubleClick += (s, e) => { if (_showMain != null) _showMain(); };
            }
        }

        /// <summary>弹一个气泡通知。</summary>
        public static void Show(string title, string message, bool error = false)
        {
            try
            {
                lock (_lock)
                {
                    if (_ni == null) return;
                    _ni.BalloonTipTitle = title;
                    _ni.BalloonTipText = message;
                    _ni.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
                    _ni.ShowBalloonTip(3000);
                }
            }
            catch { }
        }

        /// <summary>释放托盘图标（程序真正退出时调用）。</summary>
        public static void Dispose()
        {
            lock (_lock)
            {
                if (_ni != null)
                {
                    _ni.Visible = false;
                    _ni.Dispose();
                    _ni = null;
                }
            }
        }
    }
}
