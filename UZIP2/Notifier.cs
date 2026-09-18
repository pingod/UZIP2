using System;
using System.Drawing;
using System.Windows.Forms;

namespace UZIP2
{
    /// <summary>
    /// 完成通知：用 NotifyIcon 气泡在系统托盘弹一次提示。
    /// 进程内共享一个 NotifyIcon，避免反复创建销毁。
    /// </summary>
    public static class Notifier
    {
        private static NotifyIcon _ni;
        private static readonly object _lock = new object();

        private static NotifyIcon Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_ni == null)
                    {
                        _ni = new NotifyIcon();
                        _ni.Icon = System.Drawing.SystemIcons.Application;
                        _ni.Visible = true;
                        _ni.Text = "UZIP2";
                    }
                    return _ni;
                }
            }
        }

        /// <summary>弹一个气泡通知。</summary>
        public static void Show(string title, string message, bool error = false)
        {
            try
            {
                var ni = Instance;
                ni.BalloonTipTitle = title;
                ni.BalloonTipText = message;
                ni.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
                ni.ShowBalloonTip(3000);
            }
            catch
            {
                // 通知失败不影响主流程
            }
        }

        /// <summary>释放托盘图标（程序退出时调用）。</summary>
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
