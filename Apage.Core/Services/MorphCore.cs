#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apage.Core.Services;

/// <summary>二维点（形变数学的值类型；record 提供值相等，便于测试断言）。</summary>
public sealed record Pt(double X, double Y);

/// <summary>路径段类型（mini-language 命令归类）。</summary>
public enum MorphCmd { Move, Line, Curve }

/// <summary>一条路径段：Move/Line 用 A→B；Curve 用 A,B,C,D 四个控制点。</summary>
public sealed class MorphSeg
{
    public MorphCmd Kind;
    public Pt A, B, C, D;
    public MorphSeg(MorphCmd k, Pt a, Pt b, Pt c, Pt d) { Kind = k; A = a; B = b; C = c; D = d; }
}

/// <summary>子路径（move 开启、Z 闭合；未闭合时 Closed=false）。</summary>
public sealed class MorphSubPath
{
    public List<MorphSeg> Segs = new();
    public bool Closed;
}

/// <summary>
/// WPF StreamGeometry mini-language 解析 + 形变数学（R10 pilot，移植自已验证 spike）。
/// <para>
/// 支持命令集与 Icons.xaml 实际用语一致：M/L/C/A/Z（含相对指令与隐式 L）。
/// A（弧）归一化为三次贝塞尔后统一按曲线展平。
/// </para>
/// <para>
/// 边界：path 的 d 携带非单位 transform 时无法静态烘焙（不完整解析 d 坐标），
/// <see cref="Parse"/> 会抛出 <see cref="FormatException"/> 让调用方拒绝该图标对——
/// 绝不静默产出错位几何。PowerShell 脚本侧同款告警见 tools/IconTools/svg-to-path.ps1。
/// </para>
/// </summary>
public static class MorphPath
{
    /// <summary>解析 mini-language 为子路径集合。</summary>
    public static List<MorphSubPath> Parse(string d)
    {
        if (string.IsNullOrWhiteSpace(d))
            throw new ArgumentException("路径数据不能为空。", nameof(d));

        var t = Tokenize(d);
        var subs = new List<MorphSubPath>();
        var cur = new MorphSubPath();
        Pt pen = new(0, 0), start = new(0, 0);
        int i = 0;
        void End()
        {
            if (cur.Segs.Count > 0) { subs.Add(cur); cur = new MorphSubPath(); }
        }

        while (i < t.Count)
        {
            char cmd = t[i++][0];
            bool abs = char.IsUpper(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    End();
                    pen = NextPt(ref i, t, pen, abs);
                    start = pen;
                    cur.Segs.Add(new MorphSeg(MorphCmd.Move, pen, pen, pen, pen));
                    while (i + 1 < t.Count && IsNum(t[i]))
                    {
                        var q = NextPt(ref i, t, pen, abs);
                        cur.Segs.Add(new MorphSeg(MorphCmd.Line, pen, q, q, q));
                        pen = q;
                    }
                    break;
                case 'L':
                    while (i + 1 < t.Count && IsNum(t[i]))
                    {
                        var q = NextPt(ref i, t, pen, abs);
                        cur.Segs.Add(new MorphSeg(MorphCmd.Line, pen, q, q, q));
                        pen = q;
                    }
                    break;
                case 'C':
                    while (i + 5 < t.Count && IsNum(t[i]))
                    {
                        var c1 = NextPt(ref i, t, pen, abs);
                        var c2 = NextPt(ref i, t, pen, abs);
                        var q = NextPt(ref i, t, pen, abs);
                        cur.Segs.Add(new MorphSeg(MorphCmd.Curve, pen, c1, c2, q));
                        pen = q;
                    }
                    break;
                case 'A':
                    while (i + 6 < t.Count && IsNum(t[i]))
                    {
                        double rx = Num(t[i++]), ry = Num(t[i++]), rot = Num(t[i++]);
                        int large = (int)Num(t[i++]), sweep = (int)Num(t[i++]);
                        var q = NextPt(ref i, t, pen, abs);
                        cur.Segs.AddRange(ArcToBeziers(pen, rx, ry, rot, large != 0, sweep != 0, q));
                        pen = q;
                    }
                    break;
                case 'Z':
                    if (cur.Segs.Count > 0 && pen != start)
                        cur.Segs.Add(new MorphSeg(MorphCmd.Line, pen, start, start, start));
                    cur.Closed = true;
                    End();
                    pen = start;
                    break;
                default:
                    throw new FormatException("不支持的 mini-language 命令: " + cmd);
            }
        }
        End();
        if (subs.Count == 0)
            throw new FormatException("路径数据无可转换几何。");
        return subs;
    }

    /// <summary>分词：命令字母 + 数字（含隐式符号 5-3 → "5","-3"）。</summary>
    public static List<string> Tokenize(string d)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c)) { tokens.Add(c.ToString()); i++; continue; }
            int j = i + 1;
            while (j < d.Length && (char.IsDigit(d[j]) || d[j] == '.')) j++;
            if (j < d.Length && (d[j] == '-' || d[j] == '+') && j > i &&
                j + 1 < d.Length && (char.IsDigit(d[j + 1]) || d[j + 1] == '.'))
            {
                tokens.Add(d.Substring(i, j - i));
                i = j;
                continue;
            }
            tokens.Add(d.Substring(i, j - i));
            i = j;
        }
        return tokens;
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static bool IsNum(string s) =>
        s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-' || s[0] == '.' || s[0] == '+');

    private static Pt NextPt(ref int i, List<string> t, Pt pen, bool abs)
    {
        double x = Num(t[i++]);
        double y = Num(t[i++]);
        return abs ? new Pt(x, y) : new Pt(pen.X + x, pen.Y + y);
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double c = (ux * vx + uy * vy) / len;
        if (c > 1) c = 1;
        if (c < -1) c = -1;
        double a = Math.Acos(c);
        return ux * vy - uy * vx < 0 ? -a : a;
    }

    /// <summary>SVG 实现笔记 F.6.5：端点参数化 → 中心参数化 → 90° 分段三次贝塞尔。</summary>
    public static List<MorphSeg> ArcToBeziers(Pt from, double rx, double ry, double phiDeg, bool largeArc, bool sweep, Pt to)
    {
        var result = new List<MorphSeg>();
        if (from == to || (rx == 0 && ry == 0))
        {
            result.Add(new MorphSeg(MorphCmd.Line, from, to, to, to));
            return result;
        }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = phiDeg * Math.PI / 180;
        double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
        double dx = (from.X - to.X) / 2, dy = (from.Y - to.Y) / 2;
        double x1p = cosP * dx + sinP * dy;
        double y1p = -sinP * dx + cosP * dy;
        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; }
        double sign = largeArc == sweep ? -1 : 1;
        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double coef = sign * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * rx * y1p / ry;
        double cyp = -coef * ry * x1p / rx;
        double cx = cosP * cxp - sinP * cyp + (from.X + to.X) / 2;
        double cy = sinP * cxp + cosP * cyp + (from.Y + to.Y) / 2;
        double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dtheta = Angle(1, 0, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
        if (sweep && dtheta < 0) dtheta += 2 * Math.PI;
        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(dtheta) / (Math.PI / 2)));
        double delta = dtheta / segments;
        double k = 4.0 / 3 * Math.Tan(delta / 4);
        double th = theta1;
        Pt p = from;
        for (int s = 0; s < segments; s++)
        {
            double th2 = th + delta;
            double c1 = Math.Cos(th), s1 = Math.Sin(th);
            double c2 = Math.Cos(th2), s2 = Math.Sin(th2);
            Pt e = new(cx + rx * cosP * c2 - ry * sinP * s2, cy + rx * sinP * c2 + ry * cosP * s2);
            Pt d1 = new(p.X + k * (-rx * cosP * s1 - ry * sinP * c1), p.Y + k * (-rx * sinP * s1 + ry * cosP * c1));
            Pt d2 = new(e.X - k * (-rx * cosP * s2 - ry * sinP * c2), e.Y - k * (-rx * sinP * s2 + ry * cosP * c2));
            result.Add(new MorphSeg(MorphCmd.Curve, p, d1, d2, e));
            p = e; th = th2;
        }
        return result;
    }
}

/// <summary>
/// 形变几何运算：展平、64 点弧长重采样、Procrustes 闭式解对齐、插值。
/// 中间变量分步求值（PowerShell 侧同款缺陷的规避在此不适用，但保留可读性）。
/// </summary>
public static class MorphMath
{
    /// <summary>三次贝塞尔求点。</summary>
    public static Pt Bezier(Pt p0, Pt p1, Pt p2, Pt p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Pt(a * p0.X + b * p1.X + c * p2.X + d * p3.X,
                      a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    /// <summary>子路径展平为折线（曲线按 perCurve 分段采样）。</summary>
    public static List<Pt> Flatten(MorphSubPath sp, int perCurve = 24)
    {
        var pts = new List<Pt>();
        foreach (var s in sp.Segs)
        {
            if (s.Kind == MorphCmd.Move) { pts.Add(s.A); continue; }
            if (s.Kind == MorphCmd.Line) { pts.Add(s.B); continue; }
            for (int k = 1; k <= perCurve; k++)
                pts.Add(Bezier(s.A, s.B, s.C, s.D, (double)k / perCurve));
        }
        if (pts.Count == 0)
            pts.Add(new Pt(0, 0));
        return pts;
    }

    /// <summary>两点距离。</summary>
    public static double Dist(Pt a, Pt b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>按弧长等距重采样为 n 点（含端点；零长度路径退化为重复首点）。</summary>
    public static List<Pt> Resample(List<Pt> poly, int n)
    {
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n));
        var cum = new List<double> { 0 };
        for (int i = 1; i < poly.Count; i++) cum.Add(cum[cum.Count - 1] + Dist(poly[i - 1], poly[i]));
        double total = cum[cum.Count - 1];
        var result = new List<Pt>(n);
        if (total <= 1e-9)
        {
            for (int k = 0; k < n; k++) result.Add(poly[0]);
            return result;
        }
        int j = 0;
        for (int k = 0; k < n; k++)
        {
            double target = total * k / (n - 1);
            while (j < cum.Count - 2 && cum[j + 1] < target) j++;
            double segLen = cum[j + 1] - cum[j];
            double f = segLen <= 1e-12 ? 0 : (target - cum[j]) / segLen;
            result.Add(new Pt(
                poly[j].X + (poly[j + 1].X - poly[j].X) * f,
                poly[j].Y + (poly[j + 1].Y - poly[j].Y) * f));
        }
        return result;
    }

    /// <summary>质心。</summary>
    public static Pt Centroid(List<Pt> pts)
    {
        double x = 0, y = 0;
        foreach (var p in pts) { x += p.X; y += p.Y; }
        return new Pt(x / pts.Count, y / pts.Count);
    }

    /// <summary>
    /// 2D Procrustes 闭式解：min Σ|σRθp + t − q|²。
    /// 返回旋转角、均匀缩放与平移（spike 验证：Menu→Close 得 45°/1.061，与理论一致）。
    /// </summary>
    public static (double Theta, double Scale, Pt Translate) Align(List<Pt> from, List<Pt> to)
    {
        if (from.Count != to.Count)
            throw new ArgumentException("对齐点集长度必须一致（先 Resample 到同一 n）。");
        var mc = Centroid(from);
        var qc = Centroid(to);
        double dot = 0, cross = 0, p2 = 0;
        for (int i = 0; i < from.Count; i++)
        {
            double px = from[i].X - mc.X, py = from[i].Y - mc.Y;
            double qx = to[i].X - qc.X, qy = to[i].Y - qc.Y;
            dot += px * qx + py * qy;
            cross += px * qy - py * qx;
            p2 += px * px + py * py;
        }
        double theta = Math.Atan2(cross, dot);
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        double num = 0;
        for (int i = 0; i < from.Count; i++)
        {
            double px = from[i].X - mc.X, py = from[i].Y - mc.Y;
            double qx = to[i].X - qc.X, qy = to[i].Y - qc.Y;
            num += (cos * px - sin * py) * qx + (sin * px + cos * py) * qy;
        }
        double scale = p2 <= 1e-12 ? 1 : num / p2;
        var translate = new Pt(
            qc.X - scale * (cos * mc.X - sin * mc.Y),
            qc.Y - scale * (sin * mc.X + cos * mc.Y));
        return (theta, scale, translate);
    }

    /// <summary>把 from 点集按对齐结果变换（供插值前对齐）。</summary>
    public static List<Pt> Transform(List<Pt> from, double theta, double scale, Pt translate)
    {
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        var result = new List<Pt>(from.Count);
        foreach (var p in from)
        {
            result.Add(new Pt(
                scale * (cos * p.X - sin * p.Y) + translate.X,
                scale * (sin * p.X + cos * p.Y) + translate.Y));
        }
        return result;
    }

    /// <summary>两组已对齐点集在 t∈[0,1] 处的线性插值。</summary>
    public static List<Pt> Interpolate(List<Pt> fromAligned, List<Pt> to, double t)
    {
        if (fromAligned.Count != to.Count)
            throw new ArgumentException("插值点集长度必须一致。");
        double tc = t;
        if (tc < 0) tc = 0;
        if (tc > 1) tc = 1;
        var result = new List<Pt>(fromAligned.Count);
        for (int i = 0; i < fromAligned.Count; i++)
        {
            result.Add(new Pt(
                fromAligned[i].X + (to[i].X - fromAligned[i].X) * tc,
                fromAligned[i].Y + (to[i].Y - fromAligned[i].Y) * tc));
        }
        return result;
    }
}
