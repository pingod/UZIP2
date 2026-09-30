using System;
using System.Drawing;
using System.Windows.Forms;

namespace UZIP2.Services
{
    // 系统托盘：气泡通知 + 右键菜单（移植 Notifier.cs 到可注入服务）
    public sealed class TrayService : IDisposable
    {
        private readonly object _lock = new object();
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private Action _showMain;
        private Action _quit;
        private Action _showPuck;

        // 用窗口图标初始化托盘；重复调用只更新回调
        public void Setup(Action showMain, Action quit, Icon icon = null, Action showPuck = null)
        {
            _showMain = showMain;
            _quit = quit;
            _showPuck = showPuck;
            lock (_lock)
            {
                if (_icon != null) return;
                _icon = new NotifyIcon
                {
                    Icon = icon ?? SystemIcons.Application,
                    Visible = true,
                    Text = "UZIP2"
                };
                _menu = new ContextMenuStrip();
                _menu.Items.Add("显示主窗口", null, (s, e) => _showMain?.Invoke());
                _menu.Items.Add("桌面只留拖拽方块", null, (s, e) => _showPuck?.Invoke());
                _menu.Items.Add(new ToolStripSeparator());
                _menu.Items.Add("退出", null, (s, e) => _quit?.Invoke());
                _icon.ContextMenuStrip = _menu;
                _icon.DoubleClick += (s, e) => _showMain?.Invoke();
            }
        }

        public void ShowBalloon(string title, string text, bool error = false)
        {
            try
            {
                lock (_lock)
                {
                    if (_icon == null) return;
                    _icon.BalloonTipTitle = title;
                    _icon.BalloonTipText = text;
                    _icon.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
                    _icon.ShowBalloonTip(3000);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_icon == null) return;
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
                _menu?.Dispose();
                _menu = null;
            }
        }
    }
}
