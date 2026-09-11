# -*- coding: utf-8 -*-
"""Flat, minimal "image compress" app icon (1024x1024, transparent background).

Design space is always 1024x1024 and every output size is drawn from that
geometry and supersampled independently, so 16px stays crisp instead of being
a blurred reduction of the 1024px raster.

Geometry (1024 design space):
  badge  : rounded square 880px at (72,72), corner radius 194 = 22% of 880
  margin : 72px = 7.03% of the canvas on every side
  arrows : two solid white arrows pointing at each other about x=512
           shaft thickness 92 (~9% of icon), head 280 wide x 180 long,
           tip gap 112, left/right mirrored -> never overlap
"""
import os
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))

PURPLE = (108, 53, 124, 255)   # #6C357C
WHITE = (255, 255, 255, 255)   # #FFFFFF
DESIGN = 1024

BX, BY, BW, BH, BR = 72, 72, 880, 880, 194
CY = 512
HALF_W = 44                     # shaft half thickness -> 88 total
HH = 132                        # head half height      -> 264 total
HL = 152                        # head length
LEFT_TAIL, LEFT_TIP = 136, 464

LEFT_ARROW = [
    (LEFT_TAIL, CY - HALF_W),
    (LEFT_TIP - HL, CY - HALF_W),
    (LEFT_TIP - HL, CY - HH),
    (LEFT_TIP, CY),
    (LEFT_TIP - HL, CY + HH),
    (LEFT_TIP - HL, CY + HALF_W),
    (LEFT_TAIL, CY + HALF_W),
]
RIGHT_ARROW = [(DESIGN - x, y) for (x, y) in LEFT_ARROW]


def render(size, ss=8):
    """Draw the icon at `size` px directly, supersampled ss times."""
    k = size / DESIGN * ss
    img = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(
        [BX * k, BY * k, (BX + BW) * k - 1, (BY + BH) * k - 1],
        radius=BR * k,
        fill=PURPLE,
    )
    for pts in (LEFT_ARROW, RIGHT_ARROW):
        d.polygon([(x * k, y * k) for (x, y) in pts], fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)


def verify(im):
    px = im.load()
    checks = [
        ("corner transparent", (5, 5), (0, 0, 0, 0)),
        ("badge body #6C357C", (512, 130), PURPLE),
        ("left shaft white", (250, 512), WHITE),
        ("right shaft white", (774, 512), WHITE),
        ("left head white", (410, 512), WHITE),
        ("right head white", (614, 512), WHITE),
        ("centre gap = badge colour", (512, 512), PURPLE),
        ("above arrows = badge colour", (512, 250), PURPLE),
        ("left margin transparent", (40, 512), (0, 0, 0, 0)),
        ("right margin transparent", (984, 512), (0, 0, 0, 0)),
    ]
    ok = True
    for name, (x, y), want in checks:
        got = px[x, y]
        if got != want:
            ok = False
        print(f"  [{'OK  ' if got == want else 'FAIL'}] {name:<28} {got}")
    return ok


def preview(master):
    """Contact sheet: sizes on light background (top) and dark (bottom)."""
    sizes = [256, 128, 64, 48, 32, 24, 16]
    pad, gap = 28, 26
    row_h = max(sizes) + pad * 2
    width = pad * 2 + sum(sizes) + gap * (len(sizes) - 1)
    sheet = Image.new("RGBA", (width, row_h * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(sheet)
    d.rectangle([0, 0, width, row_h], fill=(240, 240, 242, 255))
    d.rectangle([0, row_h, width, row_h * 2], fill=(28, 28, 32, 255))
    x = pad
    for s in sizes:
        ic = render(s)
        top = pad + (max(sizes) - s) // 2
        sheet.alpha_composite(ic, (x, top))
        sheet.alpha_composite(ic, (x, row_h + top))
        x += s + gap
    return sheet


def build_ico(path, sizes=(16, 24, 32, 48, 64, 128, 256)):
    """Write a multi-size .ico whose layers are each rendered from geometry."""
    import io, struct

    blobs = []
    for s in sizes:
        buf = io.BytesIO()
        render(s).save(buf, format="PNG")
        blobs.append((s, buf.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = len(header) + 16 * len(blobs)
    entries, data = b"", b""
    for s, blob in blobs:
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        data += blob
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(header + entries + data)
    return path


if __name__ == "__main__":
    master = render(1024)
    master.save(os.path.join(OUT, "compress-icon-1024.png"))
    preview(master).save(os.path.join(OUT, "compress-icon-preview.png"))
    build_ico(os.path.join(OUT, "compress-icon.ico"))

    print("verification:")
    print("PASS" if verify(master) else "SOME CHECKS FAILED")
