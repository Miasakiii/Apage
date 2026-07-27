using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Apage.Portable.Controls
{
    /// <summary>
    /// 窗口控制按钮：最小化 / 最大化（还原）/ 关闭。
    /// 通过 Window.GetWindow 作用于宿主编窗口，自身不持有窗口状态。
    /// </summary>
    public partial class WindowButtons : UserControl
    {
        public WindowButtons()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (win != null)
                win.StateChanged += OnWindowStateChanged;
            SyncMaxGlyph();
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            SyncMaxGlyph();
        }

        // 同步最大化按钮图标与提示（最大化→还原图标）
        private void SyncMaxGlyph()
        {
            var win = Window.GetWindow(this);
            bool maximized = win != null && win.WindowState == WindowState.Maximized;
            MaxIcon.Data = (Geometry)FindResource(maximized ? "Apage.Geo.WinRestore" : "Apage.Geo.WinMax");
            MaxButton.ToolTip = maximized ? "还原" : "最大化";
        }

        private void MinButton_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (win != null) win.WindowState = WindowState.Minimized;
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (win == null) return;
            win.WindowState = win.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (win != null) win.Close();
        }
    }
}
