<#
.SYNOPSIS
    SVG → WPF StreamGeometry 一次性转换脚本（R11，构建期工具，非产品代码）。

.DESCRIPTION
    Apage 的图标体系是 Apage.Portable/Themes/Icons.xaml 里的 StreamGeometry 资源
    （24×24 viewBox、stroke 风格、StrokeThickness=1.9、Round 线帽线 join，
    与 Lucide/Tabler 的 monoline 惯例同构）。扩充图标时从上游库拷贝 SVG 最省事，
    本脚本把 SVG path 数据转成 WPF mini-language 后输出待合并条目。

    解析清单（对照 morphicons svgToIcon 的边界）：
      * path（d 属性，含 M/L/H/V/C/S/Q/T/A/Z 与相对指令——WPF mini-language 同集）
      * line / circle / ellipse / rect / polyline / polygon（rect 的 rx/ry 圆角近似为直角并告警）
      * transform 烘焙（translate/scale/rotate/matrix；rotate 绕任意中心）
      * viewBox 归一（非 24 网格提示，不自动缩放——风格统一由人工保证）
      * paint 继承（fill:none 期望；stroke-width 非 1.9 时告警）
      * 拒收 <use>（外部引用无法静态展开，与 morphicons held-out 测试同结论）

    弧参数注意：WPF A 语法为 "A rx,ry rotation largeArc clockwise x,y"，
    旋转角为度、largeArc/clockwise 为 0/1 标志——与 SVG 同名同序，可逐词搬运。

    输出：在 -OutDir 写出 <name>.geometry.txt（每文件一条 StreamGeometry 数据），
    并打印合并进 Icons.xaml 的提示。脚本本身零网络、零依赖（仅 .NET XML API）。

    许可：上游 Lucide 为 ISC、Feather/Tabler 为 MIT。新增图标必须在
    licenses/THIRD-PARTY-NOTICES.txt 与 build/check-licenses.ps1 期望中登记归属。

.EXAMPLE
    tools\IconTools\svg-to-path.ps1 -Svg .\downloads\star.svg -Name Star
    tools\IconTools\svg-to-path.ps1 -Svg .\downloads\lock.svg -Name Lock -OutDir .\tmp-icons
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Svg,

    [Parameter(Mandatory = $true)]
    [string]$Name,

    [string]$OutDir = '.',

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Svg)) {
    Write-Error "SVG not found: $Svg"
    exit 1
}

# WPF StreamGeometry mini-language 与 SVG path d 同集（M/L/H/V/C/S/Q/T/A/Z + 相对指令），
# 唯一差异：WPF 用空格或逗号分隔均可、F0/F1 奇偶规则相同。因此 path 的 d 可原样搬运，
# 本脚本负责：XML 解析、其它图元→path、transform 烘焙、校验与告警。
Add-Type -AssemblyName System.Xml.Linq

$doc = [System.Xml.Linq.XDocument]::Load($Svg)
$root = $doc.Root
if ($null -eq $root) { Write-Error "empty svg"; exit 1 }

$warnings = New-Object System.Collections.Generic.List[string]

# ---- viewBox / 尺寸 ----
$viewBox = $root.Attribute('viewBox')
if ($null -ne $viewBox) {
    $parts = $viewBox.Value -split '[\s,]+' | Where-Object { $_ -ne '' }
    if ($parts.Count -eq 4) {
        $minX = [double]$parts[0]; $minY = [double]$parts[1]
        $w = [double]$parts[2]; $h = [double]$parts[3]
        if ([math]::Abs($w - 24) -gt 0.01 -or [math]::Abs($h - 24) -gt 0.01 -or [math]::Abs($minX) -gt 0.01 -or [math]::Abs($minY) -gt 0.01) {
            $warnings.Add("viewBox 非 24×24@0,0（实际 $($parts -join ' ')）：风格统一需人工核对，脚本不自动缩放")
        }
    }
}

# ---- paint 继承（只支持最常见的 SVG 属性，style 属性做简单展开）----
function Get-Paint([System.Xml.Linq.XElement]$el, [string]$attr) {
    $v = $el.Attribute($attr)
    if ($null -ne $v) { return $v.Value.Trim() }
    $style = $el.Attribute('style')
    if ($null -ne $style) {
        foreach ($decl in ($style.Value -split ';')) {
            $kv = $decl -split ':', 2
            if ($kv.Count -eq 2 -and $kv[0].Trim() -eq $attr) { return $kv[1].Trim() }
        }
    }
    return $null
}

$svgFill = Get-Paint $root 'fill'
if ($svgFill -and $svgFill -ne 'none') {
    $warnings.Add("根 fill='$svgFill' 非 none：Apage 图标是 stroke 风格，请人工确认")
}
$svgStrokeWidth = Get-Paint $root 'stroke-width'
if ($svgStrokeWidth -and [math]::Abs([double]$svgStrokeWidth - 1.9) -gt 0.01) {
    $warnings.Add("stroke-width=$svgStrokeWidth 非 1.9：Apage 基样式统一 1.9，请人工确认")
}

# ---- transform 烘焙 ----
function Parse-Transform([string]$t) {
    # 返回 2x3 矩阵 (a,b,c,d,e,f)
    $m = @(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    foreach ($op in ($t -split '(?<=\))\s*')) {
        if ($op -match '^\s*translate\(([^)]*)\)') {
            $p = $Matches[1] -split '[\s,]+' | Where-Object { $_ -ne '' }
            $tx = [double]$p[0]; $ty = if ($p.Count -gt 1) { [double]$p[1] } else { 0.0 }
            $m = @($m[0], $m[1], $m[2], $m[3], $m[0] * $tx + $m[2] * $ty + $m[4], $m[1] * $tx + $m[3] * $ty + $m[5])
        } elseif ($op -match '^\s*scale\(([^)]*)\)') {
            $p = $Matches[1] -split '[\s,]+' | Where-Object { $_ -ne '' }
            $sx = [double]$p[0]; $sy = if ($p.Count -gt 1) { [double]$p[1] } else { $sx }
            $m = @($m[0] * $sx, $m[1] * $sx, $m[2] * $sy, $m[3] * $sy, $m[4], $m[5])
        } elseif ($op -match '^\s*rotate\(([^)]*)\)') {
            $p = $Matches[1] -split '[\s,]+' | Where-Object { $_ -ne '' }
            $deg = [double]$p[0] * [math]::PI / 180.0
            $cos = [math]::Cos($deg); $sin = [math]::Sin($deg)
            if ($p.Count -eq 3) {
                $cx = [double]$p[1]; $cy = [double]$p[2]
                $m = @($m[0], $m[1], $m[2], $m[3], $m[0] * $cx + $m[2] * $cy + $m[4], $m[1] * $cx + $m[3] * $cy + $m[5])
            }
            $r = @($m[0] * $cos + $m[2] * $sin, $m[1] * $cos + $m[3] * $sin, $m[2] * $cos - $m[0] * $sin, $m[3] * $cos - $m[1] * $sin, $m[4], $m[5])
            $m = $r
            if ($p.Count -eq 3) {
                $cx = [double]$p[1]; $cy = [double]$p[2]
                $m = @($m[0], $m[1], $m[2], $m[3], $m[4] - ($m[0] * $cx + $m[2] * $cy), $m[5] - ($m[1] * $cx + $m[3] * $cy))
            }
        } elseif ($op -match '^\s*matrix\(([^)]*)\)') {
            $p = $Matches[1] -split '[\s,]+' | Where-Object { $_ -ne '' }
            if ($p.Count -ne 6) { $warnings.Add("matrix 参数个数不为 6，跳过"); continue }
            $n = @([double]$p[0], [double]$p[1], [double]$p[2], [double]$p[3], [double]$p[4], [double]$p[5])
            $m = @(
                $m[0] * $n[0] + $m[2] * $n[1], $m[1] * $n[0] + $m[3] * $n[1],
                $m[0] * $n[2] + $m[2] * $n[3], $m[1] * $n[2] + $m[3] * $n[3],
                $m[0] * $n[4] + $m[2] * $n[5] + $m[4], $m[1] * $n[4] + $m[3] * $n[5] + $m[5])
        } elseif ($op.Trim() -ne '') {
            $warnings.Add("未识别的 transform 操作：$($op.Trim())")
        }
    }
    return $m
}

function Format-Num([double]$v) {
    # 紧凑数字：去尾随零，保留最多 3 位小数（图标坐标 3 位足够）
    $s = $v.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
    if ($s -eq '-0') { $s = '0' }
    return $s
}

# 注意：参数不可加 [double[]] 类型约束，且矩阵运算必须用中间变量分步求值——
# PowerShell 对 `$m[0] * $x + $m[2] * $y + $m[4], ...` 这种写在 @() 里的链式表达式
# 会错误地把数组元素当数组做 op_Addition（V1/V4 实测失败）；分步赋值后完全正常（V2/V3 实测通过）。
function Apply-Matrix($m, $x, $y) {
    $e0 = [double]$m[0] * [double]$x + [double]$m[2] * [double]$y + [double]$m[4]
    $e1 = [double]$m[1] * [double]$x + [double]$m[3] * [double]$y + [double]$m[5]
    return @($e0, $e1)
}

# 自外向内组合祖先链上的全部 transform（<g transform=...> 包装在 Tabler 等库中很常见），
# 得到该元素相对于 viewBox 根的总矩阵。算术一律用中间变量分步求值——
# PowerShell 对 @() 内链式表达式有求值缺陷（见 Apply-Matrix 注释）。
function Get-ElementMatrix($el) {
    $chain = New-Object System.Collections.Generic.List[string]
    $cur = $el
    while ($null -ne $cur) {
        $tAttr = $cur.Attribute('transform')
        if ($null -ne $tAttr) { $chain.Insert(0, $tAttr.Value) }
        $cur = $cur.Parent
    }
    $m = @(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    foreach ($tv in $chain) {
        $n = Parse-Transform $tv
        $ma = $m[0] * $n[0] + $m[2] * $n[1]
        $mb = $m[1] * $n[0] + $m[3] * $n[1]
        $mc = $m[0] * $n[2] + $m[2] * $n[3]
        $md = $m[1] * $n[2] + $m[3] * $n[3]
        $me = $m[0] * $n[4] + $m[2] * $n[5] + $m[4]
        $mf = $m[1] * $n[4] + $m[3] * $n[5] + $m[5]
        $m = @($ma, $mb, $mc, $md, $me, $mf)
    }
    return $m
}

# ---- 图元 → path d ----
function Convert-Circle($cx, $cy, $r) {
    # 四段圆弧近似（与 SVG circle 同形的标准写法）
    return "M $(Format-Num ($cx - $r)),$(Format-Num $cy) A $r,$r 0 1 1 $(Format-Num ($cx + $r)),$(Format-Num $cy) A $r,$r 0 1 1 $(Format-Num ($cx - $r)),$(Format-Num $cy) Z"
}

function Convert-Rect($x, $y, $w, $h, $rx, $ry) {
    if ($rx -gt 0 -or $ry -gt 0) { $warnings.Add("rect 圆角（rx/ry）近似为直角，请人工处理") }
    return "M $(Format-Num $x),$(Format-Num $y) L $(Format-Num ($x + $w)),$(Format-Num $y) L $(Format-Num ($x + $w)),$(Format-Num ($y + $h)) L $(Format-Num $x),$(Format-Num ($y + $h)) Z"
}

function Convert-Poly($pointsAttr, $close) {
    $pts = @()
    foreach ($pair in ($pointsAttr -split '[\s,]+' | Where-Object { $_ -ne '' })) { }
    $nums = $pointsAttr -split '[\s,]+' | Where-Object { $_ -ne '' }
    for ($i = 0; $i -lt $nums.Count - 1; $i += 2) {
        $pts += , @([double]$nums[$i], [double]$nums[$i + 1])
    }
    if ($pts.Count -eq 0) { return '' }
    $d = "M $(Format-Num $pts[0][0]),$(Format-Num $pts[0][1])"
    for ($i = 1; $i -lt $pts.Count; $i++) {
        $d += " L $(Format-Num $pts[$i][0]),$(Format-Num $pts[$i][1])"
    }
    if ($close) { $d += ' Z' }
    return $d
}

$segments = New-Object System.Collections.Generic.List[string]
foreach ($el in $root.DescendantsAndSelf()) {
    if ($el.Name.LocalName -eq 'defs' -or $el.Name.LocalName -eq 'symbol') {
        $warnings.Add("含 <$($el.Name.LocalName)>：脚本不展开定义，请人工提取")
        continue
    }
    if ($el.Name.LocalName -eq 'use') {
        Write-Error "含 <use>：外部引用无法静态展开（morphicons held-out 测试中亦拒收），请手工提取目标图标"
        exit 1
    }

    $m = Get-ElementMatrix $el
    $t = $el.Attribute('transform')

    $fill = Get-Paint $el 'fill'
    if ($fill -eq 'none') { } # stroke 风格，符合预期
    elseif ($null -ne $fill -and $fill -ne $svgFill) {
        $warnings.Add("<$($el.Name.LocalName)> fill='$fill'：Apage 图标为 stroke 风格，请人工确认")
    }

    switch ($el.Name.LocalName) {
        'path' {
            $d = $el.Attribute('d')
            if ($null -eq $d) { $warnings.Add('path 缺少 d，跳过'); continue }
            # path 的 d 原样搬运（WPF mini-language 与 SVG path d 同集），但 transform 无法
            # 在不完整解析 d 数据的情况下烘焙——非单位矩阵（含祖先 <g> 链）时必须告警，
            # 绝不静默产出错位几何
            if ($m[0] -ne 1 -or $m[1] -ne 0 -or $m[2] -ne 0 -or $m[3] -ne 1 -or $m[4] -ne 0 -or $m[5] -ne 0) {
                $origin = if ($null -ne $t) { '自身 ' + $t.Value } else { '祖先链' }
                $warnings.Add("path 带非单位 transform（$origin）：d 数据未做坐标变换，请人工烘焙或改用无 transform 的上游图标")
            }
            $segments.Add($d.Value.Trim())
        }
        'line' {
            $x1 = [double]$el.Attribute('x1').Value; $y1 = [double]$el.Attribute('y1').Value
            $x2 = [double]$el.Attribute('x2').Value; $y2 = [double]$el.Attribute('y2').Value
            $p1 = Apply-Matrix $m $x1 $y1; $p2 = Apply-Matrix $m $x2 $y2
            $segments.Add("M $(Format-Num $p1[0]),$(Format-Num $p1[1]) L $(Format-Num $p2[0]),$(Format-Num $p2[1])")
        }
        'circle' {
            $cx = [double]$el.Attribute('cx').Value; $cy = [double]$el.Attribute('cy').Value; $r = [double]$el.Attribute('r').Value
            if ($m[0] -ne $m[3] -or $m[1] -ne 0 -or $m[2] -ne 0) { $warnings.Add('circle 遇非等比/旋转 transform，未烘焙（请人工处理）'); continue }
            $c = Apply-Matrix $m $cx $cy
            $segments.Add((Convert-Circle $c[0] $c[1] ($r * $m[0])))
        }
        'ellipse' {
            $warnings.Add('ellipse 未自动转换，请用两段圆弧手工表示')
            continue
        }
        'rect' {
            $x = [double]$el.Attribute('x').Value; $y = [double]$el.Attribute('y').Value
            $w = [double]$el.Attribute('width').Value; $h = [double]$el.Attribute('height').Value
            $rx = 0.0; $ry = 0.0
            if ($null -ne $el.Attribute('rx')) { $rx = [double]$el.Attribute('rx').Value }
            if ($null -ne $el.Attribute('ry')) { $ry = [double]$el.Attribute('ry').Value }
            if ($m[0] -ne $m[3] -or $m[1] -ne 0 -or $m[2] -ne 0) { $warnings.Add('rect 遇非等比/旋转 transform，未烘焙（请人工处理）'); continue }
            $p0 = Apply-Matrix $m $x $y
            $segments.Add((Convert-Rect $p0[0] $p0[1] ($w * $m[0]) ($h * $m[3]) $rx $ry))
        }
        'polyline' {
            $segments.Add((Convert-Poly $el.Attribute('points').Value $false))
        }
        'polygon' {
            $segments.Add((Convert-Poly $el.Attribute('points').Value $true))
        }
    }
}

if ($segments.Count -eq 0) {
    Write-Error "no convertible geometry found in $Svg"
    exit 1
}

$geometry = ($segments -join ' ')

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$outFile = Join-Path $OutDir "$Name.geometry.txt"
if ((Test-Path $outFile) -and -not $Force) {
    Write-Error "output exists: $outFile (use -Force to overwrite)"
    exit 1
}
$geometry | Out-File -FilePath $outFile -Encoding utf8 -NoNewline

Write-Host "==> $Name => $outFile"
Write-Host $geometry
foreach ($w in $warnings) { Write-Warning $w }
Write-Host "合并：在 Themes/Icons.xaml 增加 <StreamGeometry x:Key=`"Apage.Geo.$Name`">$geometry</StreamGeometry>，保持 24 viewBox / stroke 风格基样式不变。"
Write-Host "归属：若拷贝自 Lucide(ISC)/Feather/Tabler(MIT)，同步更新 licenses/THIRD-PARTY-NOTICES.txt。"
