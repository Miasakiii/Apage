#nullable enable
using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Apage.Portable.Tabs;

namespace Apage.Portable.Controls
{
    /// <summary>
    /// 标签条：数据驱动呈现（ItemsSource 为 TabManager.Tabs），只负责显示与交互事件上抛；
    /// 具体的新建/关闭/切换/重排由宿主（MainWindow）委托 TabManager 执行。
    /// 模板只绑定 BrowserTab 的 Title / IsCurrent / Favicon。
    /// 交互：左键点击=切换；中键=关闭；按住拖动=重排（拖动残影 0.7 + 2px accent 插入竖线）。
    /// </summary>
    public partial class TabStrip : UserControl
    {
        private const double ScrollStep = 240; // 滚动按钮单次步进（约一个标签多一点）

        // 拖拽状态
        private Point _pressPoint;
        private BrowserTab? _pressTab;
        private bool _isDragging;
        private InsertionAdorner? _insertionAdorner;

        public TabStrip()
        {
            InitializeComponent();
        }

        /// <summary>点击「+」新建标签。</summary>
        public event EventHandler? NewTabRequested;

        /// <summary>点击某标签请求切换。</summary>
        public event EventHandler<BrowserTab>? TabSelected;

        /// <summary>点击某标签 X（或中键）请求关闭。</summary>
        public event EventHandler<BrowserTab>? TabCloseRequested;

        /// <summary>拖拽重排请求：把第 from 个标签移动到第 to 个位置（均为收敛后的合法下标）。</summary>
        public event Action<int, int>? TabReorderRequested;

        /// <summary>绑定标签集合（通常为 TabManager.Tabs）。</summary>
        public void SetItemsSource(IEnumerable tabs) => TabItems.ItemsSource = tabs;

        /// <summary>把指定标签滚入可视区（新建/激活后调用，杜绝被裁到屏外，回应「走回头路」反馈）。</summary>
        public void ScrollToTab(BrowserTab? tab)
        {
            if (tab == null)
                return;

            // 容器可能尚未生成（新增项布局未完成），延后到 Loaded 优先级再取容器 BringIntoView
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TabItems.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement fe)
                    fe.BringIntoView();
            }), DispatcherPriority.Loaded);
        }

        // ===== 点击 / 关闭 / 新建 =====

        // 左键抬起：请求切换为当前标签（拖拽路径已在 TabItems 预览事件里吞掉，不会走到这里）
        private void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
                return;
            if (((FrameworkElement)sender).DataContext is BrowserTab tab)
                TabSelected?.Invoke(this, tab);
        }

        // 中键关闭标签（frontend-design §5.2）
        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;
            if (((FrameworkElement)sender).DataContext is BrowserTab tab)
            {
                e.Handled = true;
                TabCloseRequested?.Invoke(this, tab);
            }
        }

        // 关闭 X：上抛关闭请求，不冒泡为“切换标签”
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is BrowserTab tab)
            {
                e.Handled = true;
                TabCloseRequested?.Invoke(this, tab);
            }
        }

        // + 按钮：上抛新建请求
        private void NewTabButton_Click(object sender, RoutedEventArgs e) =>
            NewTabRequested?.Invoke(this, EventArgs.Empty);

        // ===== 溢出滚动 =====

        // 内容溢出时露出左右滚动按钮，并按滚动位置设可用态
        private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var overflow = e.ExtentWidth > e.ViewportWidth + 0.5;
            var vis = overflow ? Visibility.Visible : Visibility.Collapsed;
            ScrollLeftButton.Visibility = vis;
            ScrollRightButton.Visibility = vis;

            if (overflow)
            {
                ScrollLeftButton.IsEnabled = e.HorizontalOffset > 0.5;
                ScrollRightButton.IsEnabled = e.HorizontalOffset < e.ExtentWidth - e.ViewportWidth - 0.5;
            }
        }

        private void ScrollLeftButton_Click(object sender, RoutedEventArgs e) =>
            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - ScrollStep);

        private void ScrollRightButton_Click(object sender, RoutedEventArgs e) =>
            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + ScrollStep);

        // ===== 拖拽重排 =====

        private void TabItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 按在关闭按钮（或任何按钮）上不触发拖拽，交由其自身处理
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            _pressTab = (e.OriginalSource as FrameworkElement)?.DataContext as BrowserTab;
            _pressPoint = e.GetPosition(TabItems);
        }

        private void TabItems_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressTab == null)
                return;

            var pos = e.GetPosition(TabItems);

            if (!_isDragging)
            {
                if (Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                BeginDrag();
            }

            if (_isDragging)
                UpdateInsertionIndicator(pos.X);
        }

        private void TabItems_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                _pressTab = null;
                return;
            }

            var insertion = GetInsertionIndex(e.GetPosition(TabItems).X);
            CommitDrag(insertion);
            e.Handled = true; // 拖拽落定：吞掉这次抬起，避免误触发点击切换
        }

        private void TabItems_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

        private void BeginDrag()
        {
            if (_pressTab == null)
                return;

            _isDragging = true;
            TabItems.CaptureMouse();

            // 拖动残影：被拖标签半透明 0.7
            if (TabItems.ItemContainerGenerator.ContainerFromItem(_pressTab) is UIElement container)
                container.Opacity = 0.7;

            var brush = TryFindResource("Apage.Brush.Accent") as Brush ?? Brushes.DodgerBlue;
            var layer = AdornerLayer.GetAdornerLayer(TabItems);
            if (layer != null)
            {
                _insertionAdorner = new InsertionAdorner(TabItems, brush);
                layer.Add(_insertionAdorner);
            }
        }

        private void UpdateInsertionIndicator(double x)
        {
            var insertion = GetInsertionIndex(x);
            _insertionAdorner?.SetX(GetInsertionX(insertion));
        }

        private void CommitDrag(int insertion)
        {
            var from = _pressTab != null ? TabItems.Items.IndexOf(_pressTab) : -1;
            EndDrag();

            if (from < 0)
                return;

            // 插入下标 -> Move 目标下标（移除源项后，落在插入位右侧的目标需减 1）
            var to = insertion > from ? insertion - 1 : insertion;
            var max = TabItems.Items.Count - 1;
            if (to < 0) to = 0;
            if (to > max) to = max;

            if (to != from)
                TabReorderRequested?.Invoke(from, to);
        }

        private void EndDrag()
        {
            if (_pressTab != null &&
                TabItems.ItemContainerGenerator.ContainerFromItem(_pressTab) is UIElement container)
                container.Opacity = 1.0;

            if (_insertionAdorner != null)
            {
                AdornerLayer.GetAdornerLayer(TabItems)?.Remove(_insertionAdorner);
                _insertionAdorner = null;
            }

            if (TabItems.IsMouseCaptured)
                TabItems.ReleaseMouseCapture();

            _isDragging = false;
            _pressTab = null;
        }

        // 指针 X（TabItems 坐标）落在哪个插入位（0..count）：以各容器中点为界
        private int GetInsertionIndex(double x)
        {
            var count = TabItems.Items.Count;
            for (var i = 0; i < count; i++)
            {
                if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe)
                    continue;
                var left = fe.TranslatePoint(new Point(0, 0), TabItems).X;
                if (x < left + fe.ActualWidth / 2)
                    return i;
            }
            return count;
        }

        // 插入位 -> 竖线绘制 X（TabItems 坐标）：该位左边缘；末位取最后一个容器右边缘
        private double GetInsertionX(int insertion)
        {
            var count = TabItems.Items.Count;
            if (count == 0)
                return 0;

            if (insertion < count &&
                TabItems.ItemContainerGenerator.ContainerFromIndex(insertion) is FrameworkElement fe)
                return fe.TranslatePoint(new Point(0, 0), TabItems).X;

            if (TabItems.ItemContainerGenerator.ContainerFromIndex(count - 1) is FrameworkElement last)
                return last.TranslatePoint(new Point(0, 0), TabItems).X + last.ActualWidth;

            return 0;
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T match)
                    return match;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>拖拽时的 2px accent 插入竖线（覆盖在标签条上层，随指针移动）。</summary>
        private sealed class InsertionAdorner : Adorner
        {
            private readonly Pen _pen;
            private double _x;

            public InsertionAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
            {
                IsHitTestVisible = false;
                _pen = new Pen(brush, 2);
                _pen.Freeze();
            }

            public void SetX(double x)
            {
                _x = x;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                var height = ((FrameworkElement)AdornedElement).ActualHeight;
                drawingContext.DrawLine(_pen, new Point(_x, 4), new Point(_x, height - 4));
            }
        }
    }
}
