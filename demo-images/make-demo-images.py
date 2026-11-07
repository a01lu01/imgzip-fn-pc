"""生成 ImgZip 测试用的演示图片。

生成 10 张基础图，再复制成 5 组（a-e），共 50 张，用于批量压缩压力测试。
基础图覆盖压缩器的典型分支：缩放、格式转换、透明度、极端体积、中文与空格路径。

只编码一次，其余四组直接文件复制，保证 5 组字节完全一致，便于比对结果。
"""
import math
import os
import random
import shutil

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = os.path.dirname(os.path.abspath(__file__))
os.makedirs(OUT, exist_ok=True)
GROUPS = ["a", "b", "c", "d", "e"]
rng = np.random.default_rng(20261107)
random.seed(20261107)


def gradient(width, height, colors, angle=0.0):
    """沿一个方向插值出平滑渐变的 RGB 数组。"""
    ys, xs = np.mgrid[0:height, 0:width]
    t = (xs * math.cos(angle) + ys * math.sin(angle)).astype(np.float64)
    t = (t - t.min()) / (t.max() - t.min() or 1)
    stops = np.array(colors, dtype=np.float64)
    pos = np.linspace(0, 1, len(stops))
    return np.stack([np.interp(t, pos, stops[:, c]) for c in range(3)], axis=-1)


def soft_shapes(width, height, count, palette, blur=2.0):
    """柔和色块叠加，模拟照片里的大面积连续色调。"""
    layer = Image.new("RGB", (width, height), palette[0])
    draw = ImageDraw.Draw(layer)
    for _ in range(count):
        x, y = random.randrange(-width // 3, width), random.randrange(-height // 3, height)
        r = random.randrange(width // 12, width // 3)
        draw.ellipse([x, y, x + r, y + r], fill=random.choice(palette))
    return np.asarray(layer.filter(ImageFilter.GaussianBlur(blur)), dtype=np.float64)


def build(name, **kwargs):
    """返回 (PIL 图像, 文件名主体, 保存参数)。"""
    return (name, kwargs)


# 1. 大尺寸照片感图像：验证长边缩放与降采样质量，压缩收益应当明显。
base = gradient(6000, 4000, [(30, 60, 120), (240, 170, 90), (250, 240, 220)], angle=0.6)
detail = soft_shapes(6000, 4000, 220, [(40, 70, 130), (235, 180, 100), (255, 245, 230), (120, 150, 200)])
arr = np.clip(base * 0.55 + detail * 0.45 + rng.normal(0, 4, base.shape), 0, 255).astype(np.uint8)
img_large = Image.fromarray(arr)
del base, detail, arr

# 2. 高细节噪点：最考验有损压缩，质量档位差异会很明显。
noise = rng.integers(0, 256, (2400, 3600, 3), dtype=np.uint8)
tint = gradient(3600, 2400, [(200, 120, 60), (60, 140, 120)], angle=1.1)
noise = np.clip(noise * 0.35 + tint * 0.65, 0, 255).astype(np.uint8)
img_noise = Image.fromarray(noise)
del noise, tint

# 3. 平坦色块：压缩比最高，用来确认算法没有过度膨胀小图。
flat = gradient(2000, 1400, [(255, 255, 255), (245, 245, 245)], angle=0.0).astype(np.uint8)
img_flat = Image.fromarray(flat)
del flat

# 4. 带文字与锐利边缘的图表：观察 WebP/JPEG 对硬边和文字的差异。
chart = Image.new("RGB", (2600, 1700), (252, 252, 252))
d = ImageDraw.Draw(chart)
try:
    font = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 64)
    small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 36)
except OSError:
    font = small = ImageFont.load_default()
d.text((90, 70), "压缩测试图表 Compression Chart", font=font, fill=(20, 20, 20))
for i in range(12):
    h = 180 + int(520 * abs(math.sin(i * 0.7)))
    x = 110 + i * 195
    d.rectangle([x, 1200 - h, x + 140, 1200], fill=(108, 53, 124) if i % 2 else (16, 124, 16))
    d.text((x, 1220), f"#{i+1:02d}", font=small, fill=(70, 70, 70))
d.line([90, 1200, 2520, 1200], fill=(30, 30, 30), width=4)
img_chart = chart

# 5. 带透明通道的 PNG：验证保留 PNG 与转格式时的透明度处理。
alpha = Image.new("RGBA", (1800, 1800), (0, 0, 0, 0))
d = ImageDraw.Draw(alpha)
for i in range(9):
    r = 150 + i * 80
    d.ellipse([900 - r, 900 - r, 900 + r, 900 + r], fill=(120 + i * 14, 60 + i * 10, 160, 60 + i * 18))
d.ellipse([720, 720, 1080, 1080], fill=(255, 214, 0, 235))
img_alpha = alpha

# 6. 半透明渐变 PNG：有损转换后最容易出现色带，便于肉眼比对。
ys, xs = np.mgrid[0:1400, 0:1400]
rad = np.sqrt((xs - 700) ** 2 + (ys - 700) ** 2) / 700
fade = np.clip(1.0 - rad, 0, 1)
rgba = np.zeros((1400, 1400, 4), dtype=np.uint8)
rgba[..., 0] = (60 + 150 * fade).astype(np.uint8)
rgba[..., 1] = (140 - 60 * fade).astype(np.uint8)
rgba[..., 2] = (220 - 40 * fade).astype(np.uint8)
rgba[..., 3] = (255 * fade).astype(np.uint8)
img_fade = Image.fromarray(rgba, "RGBA")
del ys, xs, rad, fade, rgba

# 7. 小尺寸图：验证"不放大"开关不会把小图撑大。
small_img = gradient(640, 480, [(240, 120, 90), (90, 140, 230)], angle=0.9)
small_img = np.clip(small_img + soft_shapes(640, 480, 30, [(255, 200, 160), (120, 170, 240)], 3.0) * 0.4, 0, 255).astype(np.uint8)
img_small = Image.fromarray(small_img)
del small_img

# 8. 中文与空格文件名：验证路径编码、批处理参数转义。
cn = gradient(3000, 2000, [(220, 40, 90), (250, 200, 60), (40, 160, 200)], angle=2.2)
cn = np.clip(cn + soft_shapes(3000, 2000, 90, [(230, 60, 110), (255, 220, 90)], 6.0) * 0.5, 0, 255).astype(np.uint8)
img_cn = Image.fromarray(cn)
del cn

# 9. WebP 输入：确认同格式再压缩与跨格式转换都不出错。
webp_src = gradient(1600, 1200, [(20, 140, 120), (250, 230, 120), (200, 90, 60)], angle=1.8)
webp_src = np.clip(webp_src + soft_shapes(1600, 1200, 60, [(30, 160, 140), (255, 240, 140)], 4.0) * 0.5, 0, 255).astype(np.uint8)
img_webp = Image.fromarray(webp_src)
del webp_src

# 10. 竖幅大图：验证"短边缩放"分支与竖图方向处理。
vertical = gradient(3200, 5200, [(10, 30, 60), (90, 60, 160), (250, 180, 120), (255, 250, 235)], angle=2.6)
vertical = np.clip(vertical + soft_shapes(3200, 5200, 180, [(20, 40, 80), (120, 80, 180), (255, 200, 150)], 5.0) * 0.45, 0, 255).astype(np.uint8)
img_vertical = Image.fromarray(vertical)
del vertical

# (文件名主体, 图像, 保存参数)
BASES = [
    ("01-large-photo", img_large, dict(quality=95, subsampling=0)),
    ("02-noise-detail", img_noise, dict(quality=92, subsampling=0)),
    ("03-flat-color", img_flat, dict(quality=95, subsampling=2)),
    ("04-chart-text", img_chart, dict(quality=95)),
    ("05-transparent", img_alpha, dict(optimize=True)),
    ("06-alpha-gradient", img_fade, dict(optimize=True)),
    ("07-small-640", img_small, dict(quality=95)),
    ("08 中文 与 空格", img_cn, dict(quality=94)),
    ("09-webp-source", img_webp, dict(quality=95)),
    ("10-portrait-large", img_vertical, dict(quality=93, subsampling=0)),
]

# 先写入第一组，其余组直接复制文件，保证字节一致。
written = []
for i, (stem, image, kwargs) in enumerate(BASES):
    ext = ".webp" if stem.startswith("09") else (".png" if i in (4, 5) else ".jpg")
    name = f"{GROUPS[0]}{stem}{ext}"
    path = os.path.join(OUT, name)
    image.save(path, **kwargs)
    written.append((name, os.path.getsize(path)))
    print(f"{name:36} {image.size[0]:5}x{image.size[1]:<5} {os.path.getsize(path)/1024:9.1f} KB")

for group in GROUPS[1:]:
    for name, _ in written:
        target = f"{group}{name[1:]}"
        shutil.copy2(os.path.join(OUT, name), os.path.join(OUT, target))
print(f"\n已复制出 {len(GROUPS) - 1} 组，共 {len(written) * len(GROUPS)} 张图片")

# 附：非图片文件，用于验证"复制其他文件"与跳过逻辑。
with open(os.path.join(OUT, "说明-README.txt"), "w", encoding="utf-8") as handle:
    handle.write("ImgZip 演示素材。图片用于压缩测试，本文件用于验证「复制其他文件」选项。\n")
with open(os.path.join(OUT, "Thumbs.db"), "wb") as handle:
    handle.write(b"\x00" * 2048)

images = [f for f in os.listdir(OUT) if f.lower().endswith((".jpg", ".jpeg", ".png", ".webp"))]
total = sum(os.path.getsize(os.path.join(OUT, f)) for f in images)
print(f"目录现有 {len(images)} 张图片，合计 {total/1024/1024:.1f} MB")
