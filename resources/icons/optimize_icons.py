"""
Apage 图标资产体积优化（一次性）。

背景：resources/icons/build_icons.py 生成的 PNG 未做压缩优化（256px 扁平标志 61KB），
ICO 七档全嵌（93KB），导致 Apage.Portable/Assets 随 csproj Resource + ApplicationIcon
嵌入单文件后推高约 155KB，使 4.25MB 开发目标超支（4,365,824 > 4,250,000）。

本脚本从同一 src/logo-master.png 重新导出优化资产：
  1. PNG：palette 量化（扁平标志 256 色足够）+ optimize；
  2. ICO：只嵌 16/32/48/256 四档（Explorer/任务栏/Alt+Tab 实际使用尺寸），跳过 24/64/128；
  3. 写回 resources/icons/ 与 Apage.Portable/Assets/（apage.ico + apage-256.png）。

用法：python resources/icons/optimize_icons.py
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src", "logo-master.png")
PORTABLE_ASSETS = os.path.join(os.path.dirname(HERE), "..", "Apage.Portable", "Assets")
PORTABLE_ASSETS = os.path.abspath(PORTABLE_ASSETS)

# Explorer 大/小图标、任务栏、Alt+Tab 实际取用尺寸；24/64/128 由邻近档缩放，肉眼无差
ICO_SIZES = [(16, 16), (32, 32), (48, 48), (256, 256)]


def prepare_master():
    img = Image.open(SRC).convert("RGBA")
    datas = img.getdata()
    new_data = []
    for r, g, b, a in datas:
        if r > 240 and g > 240 and b > 240:
            new_data.append((255, 255, 255, 0))
        else:
            new_data.append((r, g, b, a))
    img.putdata(new_data)
    img = img.crop(img.getbbox())
    w, h = img.size
    side = max(w, h)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(img, ((side - w) // 2, (side - h) // 2), img)
    return canvas


def save_optimized_png(img, size, path):
    resized = img.resize((size, size), Image.LANCZOS)
    # 扁平标志（蓝底白字）量化到 256 色后肉眼无损，体积通常降一个量级
    quantized = resized.convert("P", palette=Image.ADAPTIVE, colors=256)
    quantized.save(path, "PNG", optimize=True)


def main():
    img = prepare_master()
    before = {}
    after = {}

    # 1) resources/icons/ 全尺寸 PNG 优化重写
    for s in [16, 24, 32, 48, 64, 128, 256]:
        p = os.path.join(HERE, f"apage-{s}.png")
        if os.path.exists(p):
            before[p] = os.path.getsize(p)
        save_optimized_png(img, s, p)
        after[p] = os.path.getsize(p)
        print(f"  apage-{s}.png: {before.get(p, '?')} -> {after[p]} bytes")

    # 2) ICO：四档嵌入，每档独立优化
    ico_path = os.path.join(HERE, "apage.ico")
    if os.path.exists(ico_path):
        before[ico_path] = os.path.getsize(ico_path)
    frames = []
    for (w, h) in ICO_SIZES:
        resized = img.resize((w, h), Image.LANCZOS)
        frames.append(resized.convert("P", palette=Image.ADAPTIVE, colors=256))
    frames[-1].save(
        ico_path,
        format="ICO",
        sizes=ICO_SIZES,
        append_images=frames[:-1],
    )
    after[ico_path] = os.path.getsize(ico_path)
    print(f"  apage.ico: {before.get(ico_path, '?')} -> {after[ico_path]} bytes")

    # 3) 同步便携资产（EXE 图标 + WPF 256 资源）
    os.makedirs(PORTABLE_ASSETS, exist_ok=True)
    for name in ("apage.ico", "apage-256.png"):
        src = os.path.join(HERE, name)
        dst = os.path.join(PORTABLE_ASSETS, name)
        if os.path.exists(dst):
            before[dst] = os.path.getsize(dst)
        with open(src, "rb") as f:
            data = f.read()
        with open(dst, "wb") as f:
            f.write(data)
        after[dst] = os.path.getsize(dst)
        print(f"  Portable/Assets/{name}: {before.get(dst, '?')} -> {after[dst]} bytes")

    total_before = sum(before.values())
    total_after = sum(after.values())
    print(f"total: {total_before} -> {total_after} bytes (saved {total_before - total_after})")


if __name__ == "__main__":
    main()
