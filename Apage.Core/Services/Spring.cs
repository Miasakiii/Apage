#nullable enable
using System;

namespace Apage.Core.Services;

/// <summary>
/// 弹簧驱动器（R10，移植 morphicons 的物理预设与中断语义）。
/// <para>
/// 半隐式 Euler、1/240s 固定子步：外部按帧间隔调用 <see cref="Advance"/>，
/// 内部把帧时间切为多个子步积分，保证不同帧率下收敛行为一致。
/// 飞行中可中断：<see cref="Retarget"/> 保留当前速度，从当前位置改道新目标——
/// 快速连点工具栏按钮时动画不跳变。
/// </para>
/// <para>
/// 预设（morphicons 同值）：smooth(170/26) / snappy(420/30，默认) / bouncy(300/14)。
/// reduced-motion 场景调用方直接 <see cref="Snap"/> 到目标，不走物理。
/// </para>
/// </summary>
public sealed class Spring
{
    /// <summary>弹簧预设（刚度 k / 阻尼 c）。</summary>
    public readonly record struct Preset(double Stiffness, double Damping)
    {
        /// <summary>柔和（默认用于 UI 微交互）。</summary>
        public static Preset Smooth => new(170, 26);

        /// <summary>利落（morphicons 默认）。</summary>
        public static Preset Snappy => new(420, 30);

        /// <summary>回弹（有 overshoot，慎用于图标）。</summary>
        public static Preset Bouncy => new(300, 14);
    }

    private const double SubStep = 1.0 / 240.0;
    private const double RestThreshold = 0.0005;
    private const double VelocityThreshold = 0.0025;

    private double _position;
    private double _velocity;
    private double _target;

    /// <summary>用指定预设创建弹簧；初始位置即目标（静止）。</summary>
    /// <param name="config">弹簧预设。</param>
    /// <param name="initial">初始位置（默认 0）。</param>
    public Spring(Preset config, double initial = 0)
    {
        Config = config;
        _position = initial;
        _target = initial;
    }

    /// <summary>当前预设。</summary>
    public Preset Config { get; }

    /// <summary>当前位置（插值参数 t）。</summary>
    public double Position => _position;

    /// <summary>目标位置。</summary>
    public double Target => _target;

    /// <summary>当前速度。</summary>
    public double Velocity => _velocity;

    /// <summary>是否已到达目标并静止（宿主可停止每帧渲染）。</summary>
    public bool IsAtRest => Math.Abs(_target - _position) < RestThreshold &&
                            Math.Abs(_velocity) < VelocityThreshold;

    /// <summary>
    /// 改道新目标并保留当前速度（飞行中可中断）。
    /// 已在目标静止时速度为 0，等同于重新起跑。
    /// </summary>
    public void Retarget(double target)
    {
        _target = ClampUnit(target);
        if (IsAtRest)
        {
            _position = _target;
            _velocity = 0;
        }
    }

    /// <summary>立即跳到目标并清零速度（reduced-motion / 取消动画）。</summary>
    public void Snap(double target)
    {
        _target = ClampUnit(target);
        _position = _target;
        _velocity = 0;
    }

    // netstandard2.0 无 Math.Clamp（2.1 起才有）
    private static double ClampUnit(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    /// <summary>
    /// 推进 dt 秒（内部按 1/240s 切子步积分）。返回是否仍在运动
    /// （false = 已静止，宿主可摘除帧回调）。
    /// </summary>
    public bool Advance(double dt)
    {
        if (dt <= 0)
            return !IsAtRest;
        if (IsAtRest)
            return false;

        double remaining = dt;
        // 防御性上限：卡帧（调试断点/机器休眠）后不要把几十分钟补算出来
        if (remaining > 0.25) remaining = 0.25;

        while (remaining > 0)
        {
            double step = remaining >= SubStep ? SubStep : remaining;
            Integrate(step);
            remaining -= step;
            if (IsAtRest)
            {
                _position = _target;
                _velocity = 0;
                return false;
            }
        }
        return true;
    }

    // ẍ = k(target − x) − c·ẋ 的半隐式 Euler（morphicons 同式，其 target 恒为 1；
    // 本类泛化 target，中断改道时物理必须拉向新目标而不是硬编码的 1）
    private void Integrate(double step)
    {
        double acceleration = Config.Stiffness * (_target - _position) - Config.Damping * _velocity;
        _velocity += acceleration * step;
        _position += _velocity * step;
    }
}
