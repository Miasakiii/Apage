#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Apage.Core.Services;

namespace Apage.Portable.Controls;

/// <summary>
/// 图标形变控件（R10 pilot）：在两条 StreamGeometry 数据之间做弹簧动画。
/// <para>
/// 组合方案（Path 是密封类不可继承）：本控件是 <see cref="Control"/>，
/// 默认模板内置 <see cref="System.Windows.Shapes.Path"/>；每帧按插值参数重算折线
/// 并写入<b>冻结</b>的 <see cref="StreamGeometry"/>——冻结后命中测试区域稳定、
/// 无每帧 GC 压力（spike 阶段识别的风险）。
/// </para>
/// <para>
/// 用法：FromData/ToData 为 mini-language 字符串（Icons.xaml 的 geometry 数据），
/// <see cref="ShowTo"/> 为方向开关；翻转即触发弹簧动画。驱动用
/// CompositionTarget.Rendering（WPF 每帧回调），静止即摘除回调，零常驻开销。
/// </para>
/// <para>
/// reduced-motion：尊重系统「减弱动画」设置（SPI_GETCLIENTAREAANIMATION），
/// 开启时直接跳终态，不走物理（与 morphicons 的 never/user/always 契约对齐）。
/// </para>
/// </summary>
public sealed class MorphIcon : Control
{
    private Path? _path;

    /// <summary>起始几何数据（mini-language）。</summary>
    public static readonly DependencyProperty FromDataProperty =
        DependencyProperty.Register(nameof(FromData), typeof(string), typeof(MorphIcon),
            new PropertyMetadata(null, OnGeometryDataChanged));

    /// <summary>目标几何数据（mini-language）。</summary>
    public static readonly DependencyProperty ToDataProperty =
        DependencyProperty.Register(nameof(ToData), typeof(string), typeof(MorphIcon),
            new PropertyMetadata(null, OnGeometryDataChanged));

    /// <summary>方向：false = From，true = To。翻转触发动画。</summary>
    public static readonly DependencyProperty ShowToProperty =
        DependencyProperty.Register(nameof(ShowTo), typeof(bool), typeof(MorphIcon),
            new PropertyMetadata(false, OnShowToChanged));

    private readonly Spring _spring = new(Spring.Preset.Smooth, 0);
    private bool _renderingHooked;
    private DateTime _lastFrame = DateTime.MinValue;

    // 形变计划：解析 + 重采样 + 对齐（数据变化时一次性构建）
    private List<List<Pt>>? _fromPoints;   // 已对齐到 to 的 from 点集（每子路径 64 点）
    private List<List<Pt>>? _toPoints;     // to 点集（每子路径 64 点）
    private int _planSubPaths;

    static MorphIcon()
    {
        // 默认模板：一个 Path，Stroke/Fill 等由宿主样式经 Foreground 传入
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MorphIcon),
            new FrameworkPropertyMetadata(typeof(MorphIcon)));
    }

    public string? FromData
    {
        get => (string?)GetValue(FromDataProperty);
        set => SetValue(FromDataProperty, value);
    }

    public string? ToData
    {
        get => (string?)GetValue(ToDataProperty);
        set => SetValue(ToDataProperty, value);
    }

    public bool ShowTo
    {
        get => (bool)GetValue(ShowToProperty);
        set => SetValue(ShowToProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _path = GetTemplateChild("PART_Path") as Path;
        RebuildPlan();
    }

    private static void OnGeometryDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MorphIcon)d).RebuildPlan();
    }

    private static void OnShowToChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MorphIcon)d).OnDirectionChanged();
    }

    /// <summary>重构建形变计划并立即按当前方向渲染终态（无动画）。</summary>
    private void RebuildPlan()
    {
        var from = FromData;
        var to = ToData;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            _fromPoints = null;
            _toPoints = null;
            return;
        }

        try
        {
            var fromSubs = MorphPath.Parse(from!);
            var toSubs = MorphPath.Parse(to!);
            if (fromSubs.Count != toSubs.Count)
            {
                // 子路径数不一致（如 Menu 3 线 vs Close 2 叉线）：本期 pilot 不支持，
                // 回落为静态切换（显示目标或起始），不产出错位几何
                _fromPoints = null;
                _toPoints = null;
                return;
            }

            var fromRes = new List<List<Pt>>(fromSubs.Count);
            var toRes = new List<List<Pt>>(toSubs.Count);
            for (int i = 0; i < fromSubs.Count; i++)
            {
                fromRes.Add(MorphMath.Resample(MorphMath.Flatten(fromSubs[i]), 64));
                toRes.Add(MorphMath.Resample(MorphMath.Flatten(toSubs[i]), 64));
            }

            // from 对齐到 to（Procrustes 闭式解，逐子路径）
            var aligned = new List<List<Pt>>(fromSubs.Count);
            for (int i = 0; i < fromRes.Count; i++)
            {
                var (theta, scale, translate) = MorphMath.Align(fromRes[i], toRes[i]);
                aligned.Add(MorphMath.Transform(fromRes[i], theta, scale, translate));
            }

            _fromPoints = aligned;
            _toPoints = toRes;
            _planSubPaths = fromSubs.Count;
        }
        catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
        {
            // 无法静态烘焙的几何（非单位 transform 等）：回落静态切换，不抛到 UI 线程
            _fromPoints = null;
            _toPoints = null;
        }

        RenderCurrent();
    }

    private void OnDirectionChanged()
    {
        var target = ShowTo ? 1.0 : 0.0;
        if (_fromPoints == null || _toPoints == null || IsReducedMotion())
        {
            // 无计划或系统减弱动画：直跳终态（静态切换）
            _spring.Snap(target);
            RenderCurrent();
            return;
        }

        _spring.Retarget(target); // 飞行中可中断，保留速度
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
            _lastFrame = DateTime.MinValue;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = _lastFrame == DateTime.MinValue ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0) dt = 1.0 / 60;
        if (dt > 0.25) dt = 0.25; // 卡帧防御与 Spring.Advance 一致

        bool moving = _spring.Advance(dt);
        RenderCurrent();
        if (!moving)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    /// <summary>按当前弹簧位置渲染（t=0 → from 对齐态，t=1 → to）。</summary>
    private void RenderCurrent()
    {
        if (_path == null)
            return;

        if (_fromPoints == null || _toPoints == null || _planSubPaths == 0)
        {
            // 无计划：显示与方向对应的静态几何（Parse 失败/子路径数不等等降级路径）
            _path.Data = CreateStaticGeometry(ShowTo ? ToData : FromData);
            return;
        }

        double t = _spring.Position;
        var geometry = new StreamGeometry
        {
            FillRule = FillRule.Nonzero,
        };
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < _planSubPaths; i++)
            {
                var pts = MorphMath.Interpolate(_fromPoints[i], _toPoints[i], t);
                if (pts.Count == 0)
                    continue;
                ctx.BeginFigure(ToPoint(pts[0]), isFilled: false, isClosed: false);
                for (int k = 1; k < pts.Count; k++)
                    ctx.LineTo(ToPoint(pts[k]), isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze(); // 冻结：命中测试区域稳定、无 GC 压力
        _path.Data = geometry;
    }

    private static Geometry? CreateStaticGeometry(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;
        try
        {
            return Geometry.Parse(data);
        }
        catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
        {
            return null;
        }
    }

    // 系统「减弱动画」：关闭客户端区域动画时视为 reduced-motion
    private static bool IsReducedMotion()
    {
        try
        {
            return !SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return false;
        }
    }

    // Core 的 Pt（double 记录）→ WPF Point（StreamGeometryContext 只认后者）
    private static System.Windows.Point ToPoint(Pt p) => new System.Windows.Point(p.X, p.Y);
}
