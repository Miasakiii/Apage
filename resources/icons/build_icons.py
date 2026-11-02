"""
Apage icon builder.
Reads src/logo-master.png, makes the white background transparent, crops to content,
then exports multi-size PNGs and a multi-resolution .ico for WPF.
"""
from PIL import Image
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src", "logo-master.png")
OUT = HERE

# Windows app icon sizes required
SIZES = [16, 24, 32, 48, 64, 128, 256]

def main():
    img = Image.open(SRC).convert("RGBA")
    print(f"Master size: {img.size}")

    # 1) Make near-white background transparent.
    # The icon is a blue rounded square on a white/very-light background.
    datas = img.getdata()
    new_data = []
    for r, g, b, a in datas:
        # White-ish pixels (background) -> fully transparent
        if r > 240 and g > 240 and b > 240:
            new_data.append((255, 255, 255, 0))
        else:
            new_data.append((r, g, b, a))
    img.putdata(new_data)

    # 2) Crop to bounding box of non-transparent content
    bbox = img.getbbox()
    print(f"Content bbox: {bbox}")
    img = img.crop(bbox)
    print(f"Cropped size: {img.size}")

    # 3) Pad to square (should already be, but be safe)
    w, h = img.size
    side = max(w, h)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(img, ((side - w) // 2, (side - h) // 2), img)
    img = canvas

    # 4) Export each size as PNG
    png_paths = []
    for s in SIZES:
        resized = img.resize((s, s), Image.LANCZOS)
        p = os.path.join(OUT, f"apage-{s}.png")
        resized.save(p, "PNG")
        png_paths.append(p)
        print(f"  wrote {p} ({s}x{s})")

    # 5) Build .ico with all sizes embedded
    ico_path = os.path.join(OUT, "apage.ico")
    # Pillow saves ICO from a list of sizes; use the largest as base and let it embed.
    base = img.resize((256, 256), Image.LANCZOS)
    base.save(
        ico_path,
        format="ICO",
        sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)],
    )
    print(f"  wrote {ico_path}")

    # 6) Also export a 512 master for stores / README preview
    master = img.resize((512, 512), Image.LANCZOS)
    master.save(os.path.join(OUT, "apage-512.png"), "PNG")
    print(f"  wrote {os.path.join(OUT, 'apage-512.png')}")

    print("Done.")

if __name__ == "__main__":
    main()
