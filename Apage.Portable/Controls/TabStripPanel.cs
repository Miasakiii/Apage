#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;

namespace Apage.Portable.Controls
{
    /// <summary>
    /// 标签条排版面板（frontend-design.md §5.2）：所有标签等宽横向排布，
    /// 宽度 = clamp(可用宽 / 标签数, <see cref="MinTabWidth"/>, <see cref="MaxTabWidth"/>)。
    /// 收缩到 96px 仍超出可用宽时，内容总宽 > 视口，交由外层 ScrollViewer 横向滚动
    /// （宿主据此显示左右滚动按钮，见 TabStrip.xaml.cs）。
    /// </summary>
    /// <remarks>
    /// 本面板通常置于 <c>HorizontalScrollBarVisibility="Hidden"</c> 的 ScrollViewer 内，
    /// 此时测量传入的可用宽为无穷（Hidden 允许滚动但隐藏滚动条），故收缩计算改用
    /// <see cref="AvailableWidth"/>（由 XAML 绑定到 ScrollViewer 的 ActualWidth），
    /// 而 MeasureOverride 返回的期望总宽仍据实上报，让 ScrollViewer 得知内容溢出。
    /// </remarks>
    public sealed class TabStripPanel : Panel
    {
        /// <summary>标签最大宽度（默认/未溢出时）。</summary>
        public const double MaxTabWidth = 200;

        /// <summary>标签最小宽度（收缩下限，再窄则改为滚动）。</summary>
        public const double MinTabWidth = 96;

        /// <summary>可用（视口）宽度：由 XAML 绑定到外层 ScrollViewer 的 ActualWidth。</summary>
        public static readonly DependencyProperty AvailableWidthProperty =
            DependencyProperty.Register(
                nameof(AvailableWidth), typeof(double), typeof(TabStripPanel),
                new FrameworkPropertyMetadata(
                    0.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double AvailableWidth
        {
            get => (double)GetValue(AvailableWidthProperty);
            set => SetValue(AvailableWidthProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = InternalChildren.Count;
            if (count == 0)
                return new Size(0, 0);

            var tabWidth = ComputeTabWidth(availableSize.Width, count);
            var childHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;

            double measuredHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(tabWidth, availableSize.Height));
                measuredHeight = Math.Max(measuredHeight, child.DesiredSize.Height);
            }

            var height = childHeight > 0 ? childHeight : measuredHeight;
            // 期望总宽 = 等宽 × 数量（可能 > 视口 → 触发 ScrollViewer 横向滚动）
            return new Size(tabWidth * count, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var count = InternalChildren.Count;
            if (count == 0)
                return finalSize;

            // 与测量同一口径：优先用视口宽（AvailableWidth）算等宽，保证收缩一致、不受 finalSize 影响
            var tabWidth = ComputeTabWidth(finalSize.Width, count);
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(x, 0, tabWidth, finalSize.Height));
                x += tabWidth;
            }

            return finalSize;
        }

        // 收缩计算：优先用绑定进来的视口宽；缺省（未布局或未绑定）时退回传入宽或最大宽
        private double ComputeTabWidth(double incomingWidth, int count)
        {
            var viewport = AvailableWidth;
            if (viewport <= 0)
                viewport = double.IsInfinity(incomingWidth) || incomingWidth <= 0 ? MaxTabWidth * count : incomingWidth;

            return Clamp(viewport / count, MinTabWidth, MaxTabWidth);
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : (value > max ? max : value);
    }
}
