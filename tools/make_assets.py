"""Genereert het app-icoon (.ico) en de Velopack-splash (.png).

Puur met Pillow (geen extra tools nodig). Thema: een kabelboom -- een
connector waar gekleurde draden uit vertakken.

Gebruik:
    python tools/make_assets.py
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ASSETS = Path(__file__).resolve().parents[1] / "assets"

# Kleurenpalet
BG_TOP = (31, 42, 68)       # donkerblauw
BG_BOTTOM = (17, 24, 40)    # bijna zwart
CONNECTOR = (203, 213, 225)  # lichtgrijs
CONNECTOR_EDGE = (148, 163, 184)
PIN = (71, 85, 105)
WIRE_COLORS = [
    (224, 49, 49),   # rood
    (47, 158, 68),   # groen
    (240, 140, 0),   # geel/oranje
    (34, 139, 230),  # blauw
]
TEXT = (241, 245, 249)
SUBTEXT = (148, 163, 184)


def _font(size: int, bold: bool = True) -> ImageFont.FreeTypeFont:
    candidates = (
        ["segoeuib.ttf", "arialbd.ttf"] if bold else ["segoeui.ttf", "arial.ttf"]
    )
    for name in candidates:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _cubic_bezier(p0, p1, p2, p3, steps=48):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        mt = 1 - t
        x = (mt**3 * p0[0] + 3 * mt**2 * t * p1[0]
             + 3 * mt * t**2 * p2[0] + t**3 * p3[0])
        y = (mt**3 * p0[1] + 3 * mt**2 * t * p1[1]
             + 3 * mt * t**2 * p2[1] + t**3 * p3[1])
        pts.append((x, y))
    return pts


def _vgradient(size, top, bottom):
    w, h = size
    base = Image.new("RGB", (1, h))
    for y in range(h):
        f = y / max(1, h - 1)
        base.putpixel((0, y), tuple(
            round(top[i] + (bottom[i] - top[i]) * f) for i in range(3)
        ))
    return base.resize((w, h))


def _rounded_mask(size, radius):
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255
    )
    return mask


def _draw_harness(draw, box, scale):
    """Tekent connector + vertakkende draden binnen box (x0,y0,x1,y1)."""
    x0, y0, x1, y1 = box
    h = y1 - y0
    # connector links
    cw = int((x1 - x0) * 0.22)
    ch = int(h * 0.5)
    cx0 = x0 + int((x1 - x0) * 0.06)
    cy0 = y0 + (h - ch) // 2
    cx1, cy1 = cx0 + cw, cy0 + ch
    r = max(4, int(8 * scale))
    draw.rounded_rectangle([cx0, cy0, cx1, cy1], radius=r,
                           fill=CONNECTOR, outline=CONNECTOR_EDGE,
                           width=max(2, int(3 * scale)))
    # pinnen
    n = 4
    pin_r = max(3, int(7 * scale))
    gap = ch / (n + 1)
    pin_x = cx0 + cw // 2
    pin_ys = [cy0 + gap * (i + 1) for i in range(n)]
    for py in pin_ys:
        draw.ellipse([pin_x - pin_r, py - pin_r, pin_x + pin_r, py + pin_r],
                     fill=PIN)
    # draden vertakken van rechterrand connector naar rechts
    start_x = cx1
    width = max(3, int(9 * scale))
    end_x = x1 - int((x1 - x0) * 0.04)
    ys_end = [y0 + h * f for f in (0.18, 0.40, 0.62, 0.84)]
    for i, (sy, ey) in enumerate(zip(pin_ys, ys_end)):
        color = WIRE_COLORS[i % len(WIRE_COLORS)]
        c1 = (start_x + (end_x - start_x) * 0.45, sy)
        c2 = (start_x + (end_x - start_x) * 0.55, ey)
        pts = _cubic_bezier((start_x, sy), c1, c2, (end_x, ey))
        draw.line(pts, fill=color, width=width, joint="curve")
        # eindpunt-dopje
        cap = width
        draw.ellipse([end_x - cap, ey - cap, end_x + cap, ey + cap], fill=color)


def make_icon():
    S = 1024
    pad = int(S * 0.06)
    img = _vgradient((S, S), BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = _rounded_mask((S, S), radius=int(S * 0.22))
    img.putalpha(mask)
    draw = ImageDraw.Draw(img)
    _draw_harness(draw, (pad, pad, S - pad, S - pad), scale=S / 256)
    out = ASSETS / "icon.ico"
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    img.save(out, format="ICO", sizes=sizes)
    # ook een png voor algemeen gebruik
    img.resize((256, 256)).save(ASSETS / "icon.png")
    print(f"  icoon  -> {out}")


def make_splash():
    W, H = 600, 360
    img = _vgradient((W, H), BG_TOP, BG_BOTTOM).convert("RGBA")
    draw = ImageDraw.Draw(img)
    # harness-motief rechtsboven, subtiel
    _draw_harness(draw, (W * 0.50, 30, W - 24, 170), scale=1.1)
    # titel
    draw.text((40, 110), "Kabelboom", font=_font(58), fill=TEXT)
    draw.text((40, 178), "Tekenstudio", font=_font(58), fill=(120, 170, 240))
    draw.text((42, 262), "Wordt geinstalleerd...", font=_font(22, bold=False),
              fill=SUBTEXT)
    out = ASSETS / "splash.png"
    img.convert("RGB").save(out)
    print(f"  splash -> {out}")


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    print("Assets genereren:")
    make_icon()
    make_splash()
    print("Klaar.")


if __name__ == "__main__":
    main()
