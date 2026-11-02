using System;
using System.Collections.Generic;
using System.Linq;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// MorphPath / MorphMath：图标形变内核（R10 pilot，移植自已验证 spike）。
/// 验收基线即 spike 实测值：Menu→Close 旋转 45°/135°、缩放 1.061；
/// 纯弧路径半径误差 &lt; 0.01；64 点重采样；t=0.5 插值 sane。
/// </summary>
public sealed class MorphCoreTests
{
    // Icons.xaml 真实几何
    private const string Menu = "M4,7 L20,7 M4,12 L20,12 M4,17 L20,17";
    private const string Close = "M6,6 L18,18 M18,6 L6,18";
    private const string PureArc = "M12,8 A4,4 0 1,0 12,16 A4,4 0 1,0 12,8";

    private static List<List<Pt>> ResampleAll(string data, int n = 64) =>
        MorphPath.Parse(data).Select(s => MorphMath.Resample(MorphMath.Flatten(s), n)).ToList();

    [Fact]
    public void Parse_MenuAndClose_SubPathCounts()
    {
        Assert.Equal(3, MorphPath.Parse(Menu).Count);
        Assert.Equal(2, MorphPath.Parse(Close).Count);
    }

    [Fact]
    public void Parse_Z_MarksClosed()
    {
        var subs = MorphPath.Parse("M2,12 C5,6 9,3 12,3 Z");
        Assert.Single(subs);
        Assert.True(subs[0].Closed);
    }

    [Fact]
    public void Parse_UnsupportedCommand_Throws()
    {
        Assert.Throws<FormatException>(() => MorphPath.Parse("M1,1 Q2,2 3,3"));
    }

    [Fact]
    public void Parse_EmptyData_Throws()
    {
        Assert.Throws<ArgumentException>(() => MorphPath.Parse(""));
    }

    [Fact]
    public void Parse_ImplicitLineAfterMove()
    {
        // M 后跟多对坐标 = 隐式 L
        var subs = MorphPath.Parse("M4,7 L20,7 M4,12 20,12");
        Assert.Equal(2, subs.Count);
        Assert.Equal(2, subs[1].Segs.Count); // Move + 隐式 Line
        Assert.Equal(MorphCmd.Line, subs[1].Segs[1].Kind);
    }

    [Fact]
    public void Parse_RelativeCommands()
    {
        var subs = MorphPath.Parse("m4,7 l16,0");
        Assert.Single(subs);
        var seg = subs[0].Segs[1];
        Assert.Equal(new Pt(20, 7), seg.B);
    }

    [Fact]
    public void ArcToBeziers_PureCircle_RadiusErrorBelowThreshold()
    {
        // spike 基线：半径误差 < 0.01（实测 0.0011）
        var subs = MorphPath.Parse(PureArc);
        double maxErr = 0;
        foreach (var s in subs)
        {
            foreach (var seg in s.Segs)
            {
                if (seg.Kind != MorphCmd.Curve) continue;
                for (int k = 0; k <= 24; k++)
                {
                    var pt = MorphMath.Bezier(seg.A, seg.B, seg.C, seg.D, (double)k / 24);
                    maxErr = Math.Max(maxErr, Math.Abs(MorphMath.Dist(pt, new Pt(12, 12)) - 4));
                }
            }
        }
        Assert.True(maxErr < 0.01, $"radius err {maxErr} 超验收线");
    }

    [Fact]
    public void Resample_ProducesExactPointCountAndSpans()
    {
        var res = ResampleAll(Menu);

        Assert.All(res, r => Assert.Equal(64, r.Count));
        Assert.Equal(new Pt(4, 7), res[0][0]);
        Assert.Equal(new Pt(20, 7), res[0][63]);
        Assert.Equal(new Pt(4, 17), res[2][0]);
    }

    [Fact]
    public void Resample_ZeroLengthPath_RepeatsFirstPoint()
    {
        var res = MorphMath.Resample(new List<Pt> { new Pt(3, 4) }, 64);

        Assert.All(res, p => Assert.Equal(new Pt(3, 4), p));
    }

    [Fact]
    public void Align_MenuToClose_MatchesSpikeBaseline()
    {
        // spike 实测：45°/135° 旋转、1.061 缩放（= 对角线长/边长 16.97/16）
        var m = ResampleAll(Menu);
        var c = ResampleAll(Close);

        var (t0, s0, _) = MorphMath.Align(m[0], c[0]);
        var (t1, s1, _) = MorphMath.Align(m[1], c[1]);

        Assert.Equal(45.0, t0 * 180 / Math.PI, 3);
        Assert.Equal(135.0, t1 * 180 / Math.PI, 3);
        Assert.Equal(1.061, s0, 3);
        Assert.Equal(1.061, s1, 3);
    }

    [Fact]
    public void Align_MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MorphMath.Align(new List<Pt> { new Pt(0, 0), new Pt(1, 1) }, new List<Pt> { new Pt(0, 0) }));
    }

    [Fact]
    public void TransformAndInterpolate_MidpointIsSane()
    {
        var m = ResampleAll(Menu);
        var c = ResampleAll(Close);
        var (theta, scale, translate) = MorphMath.Align(m[0], c[0]);
        var aligned = MorphMath.Transform(m[0], theta, scale, translate);

        // t=0：对齐后的 from；t=1：to
        var atStart = MorphMath.Interpolate(aligned, c[0], 0);
        var atEnd = MorphMath.Interpolate(aligned, c[0], 1);
        Assert.Equal(aligned[0], atStart[0]);
        Assert.Equal(c[0][0], atEnd[0]);

        // t=0.5：严格居中且连续（spike 输出的中点性质）
        var mid = MorphMath.Interpolate(aligned, c[0], 0.5);
        Assert.Equal(
            (aligned[0].X + c[0][0].X) / 2, mid[0].X, 6);
        Assert.Equal(
            (aligned[0].Y + c[0][0].Y) / 2, mid[0].Y, 6);
    }

    [Fact]
    public void Interpolate_ClampsT()
    {
        var a = new List<Pt> { new Pt(0, 0), new Pt(10, 0) };
        var b = new List<Pt> { new Pt(0, 10), new Pt(10, 10) };

        var below = MorphMath.Interpolate(a, b, -1);
        var above = MorphMath.Interpolate(a, b, 2);

        Assert.Equal(new Pt(0, 0), below[0]);
        Assert.Equal(new Pt(0, 10), above[0]);
    }

    [Fact]
    public void Tokenize_HandlesImplicitSigns()
    {
        var tokens = MorphPath.Tokenize("M5-3 2");

        Assert.Contains("-3", tokens);
        Assert.Contains("5", tokens);
    }
}

/// <summary>
/// Spring：可中断弹簧驱动器（R10）。
/// 把守：收敛性、预设差异、中断保速、reduced-motion 直跳、卡帧防御。
/// </summary>
public sealed class SpringTests
{
    [Fact]
    public void Initial_IsAtRestAtTarget()
    {
        var spring = new Spring(Spring.Preset.Smooth, 0);

        Assert.True(spring.IsAtRest);
        Assert.Equal(0, spring.Position);
        Assert.False(spring.Advance(0.016));
    }

    [Fact]
    public void Retarget_ConvergesToTarget()
    {
        var spring = new Spring(Spring.Preset.Snappy, 0);
        spring.Retarget(1);

        bool moving = true;
        for (int i = 0; i < 600 && moving; i++) // 10 秒 @60fps 必然收敛
            moving = spring.Advance(1.0 / 60);

        Assert.False(moving);
        Assert.Equal(1.0, spring.Position, 6);
    }

    [Fact]
    public void Retarget_MidFlight_KeepsVelocityAndConverges()
    {
        var spring = new Spring(Spring.Preset.Smooth, 0);
        spring.Retarget(1);
        spring.Advance(0.05); // 飞行中改道
        var midVelocity = spring.Velocity;

        spring.Retarget(0); // 中断：目标反转，速度保留

        Assert.NotEqual(0, midVelocity);
        Assert.Equal(0, spring.Target);

        bool moving = true;
        for (int i = 0; i < 600 && moving; i++)
            moving = spring.Advance(1.0 / 60);
        Assert.False(moving);
        Assert.Equal(0.0, spring.Position, 6);
    }

    [Fact]
    public void Retarget_ClampsOutsideUnitRange()
    {
        var spring = new Spring(Spring.Preset.Smooth, 0);

        spring.Retarget(5);
        Assert.Equal(1, spring.Target);
        spring.Retarget(-3);
        Assert.Equal(0, spring.Target);
    }

    [Fact]
    public void Snap_JumpsImmediately()
    {
        var spring = new Spring(Spring.Preset.Bouncy, 0);
        spring.Retarget(1);
        spring.Advance(0.02);

        spring.Snap(1); // reduced-motion：不走物理

        Assert.Equal(1.0, spring.Position);
        Assert.Equal(0, spring.Velocity);
        Assert.True(spring.IsAtRest);
    }

    [Fact]
    public void Advance_HugeDt_IsCapped()
    {
        // 卡帧（断点/休眠）不得把几十分钟补算出来导致穿越目标；
        // 欠阻尼弹簧物理上允许 overshoot，但不得爆出合理范围
        var spring = new Spring(Spring.Preset.Snappy, 0);
        spring.Retarget(1);

        spring.Advance(120); // 模拟休眠后回来的巨大 dt

        Assert.InRange(spring.Position, -0.5, 1.5);
        Assert.False(double.IsNaN(spring.Position));
    }

    [Fact]
    public void Advance_ZeroOrNegativeDt_NoMovement()
    {
        var spring = new Spring(Spring.Preset.Smooth, 0);
        spring.Retarget(1);
        var before = spring.Position;

        spring.Advance(0);
        spring.Advance(-1);

        Assert.Equal(before, spring.Position);
    }

    [Fact]
    public void Bouncy_Overshoots_SmoothDoesNot()
    {
        double MaxOvershoot(Spring.Preset preset)
        {
            var spring = new Spring(preset, 0);
            spring.Retarget(1);
            double max = 0;
            for (int i = 0; i < 600; i++)
            {
                spring.Advance(1.0 / 60);
                max = Math.Max(max, spring.Position);
                if (spring.IsAtRest) break;
            }
            return max;
        }

        Assert.True(MaxOvershoot(Spring.Preset.Bouncy) > 1.0);
        Assert.True(MaxOvershoot(Spring.Preset.Smooth) <= 1.0);
    }
}
