"""Proof-of-concept: anti-aliased lijn-rendering via Pillow-supersampling.

Bewijst (a) de kwaliteitssprong t.o.v. de harde Tk-canvas-aliasing en (b) of de
rendertijd haalbaar is voor de live-view. Schrijft een vergelijkings-PNG en print timings.

    python tools/aa_render_poc.py
"""
from __future__ import annotations

import math
import time
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "aa_compare.png"


def _draw_scene(draw: ImageDraw.ImageDraw, w: int, h: int, scale: float, color=(31, 78, 121)):
    """Een fan van diagonalen + een gladde boog + een 'bridge hop' — precies de vormen
    waar Tk trapjes laat zien."""
    lw = max(1, round(1.4 * scale))
    cx, cy = w * 0.12, h * 0.5
    for k in range(12):
        ang = (k / 12.0) * (math.pi * 0.9) - math.pi * 0.45
        x2 = cx + math.cos(ang) * w * 0.55
        y2 = cy + math.sin(ang) * h * 0.42
        draw.line([(cx, cy), (x2, y2)], fill=color, width=lw)

    # Gladde boog (zoals een wire-curve).
    arc_pts = []
    for i in range(61):
        t = i / 60.0
        ax = w * 0.55 + t * w * 0.4
        ay = h * 0.25 + math.sin(t * math.pi) * h * 0.3
        arc_pts.append((ax, ay))
    draw.line(arc_pts, fill=color, width=lw, joint="curve")

    # Bridge 'hop' over een horizontale lijn.
    by = h * 0.82
    draw.line([(w * 0.1, by), (w * 0.42, by)], fill=color, width=lw)
    hop = []
    for i in range(21):
        t = i / 20.0
        hx = w * 0.42 + t * w * 0.06
        hy = by - math.sin(t * math.pi) * h * 0.05
        hop.append((hx, hy))
    draw.line(hop, fill=color, width=lw)
    draw.line([(w * 0.48, by), (w * 0.9, by)], fill=color, width=lw)


def render_aliased(w: int, h: int) -> Image.Image:
    img = Image.new("RGB", (w, h), "white")
    _draw_scene(ImageDraw.Draw(img), w, h, scale=1.0)
    return img


def render_aa(w: int, h: int, ss: int = 3) -> Image.Image:
    big = Image.new("RGB", (w * ss, h * ss), "white")
    _draw_scene(ImageDraw.Draw(big), w * ss, h * ss, scale=ss)
    return big.resize((w, h), Image.LANCZOS)


def bench_full_sheet(ss: int = 2, lines: int = 300):
    """Render N lijnen op A3-canvasresolutie en meet de tijd (incl. downscale)."""
    w, h = 1123, 794  # ~A3 liggend @ 96 dpi
    rng = __import__("random").Random(1)
    t0 = time.perf_counter()
    big = Image.new("RGB", (w * ss, h * ss), "white")
    d = ImageDraw.Draw(big)
    lw = max(1, round(1.2 * ss))
    for _ in range(lines):
        x1, y1 = rng.uniform(0, w * ss), rng.uniform(0, h * ss)
        x2, y2 = x1 + rng.uniform(-300, 300), y1 + rng.uniform(-200, 200)
        d.line([(x1, y1), (x2, y2)], fill=(31, 78, 121), width=lw)
    out = big.resize((w, h), Image.LANCZOS)
    return (time.perf_counter() - t0) * 1000.0, out


def main():
    w, h = 600, 520
    aliased = render_aliased(w, h)
    aa = render_aa(w, h, ss=3)
    combo = Image.new("RGB", (w * 2 + 20, h), "white")
    combo.paste(aliased, (0, 0))
    combo.paste(aa, (w + 20, 0))
    combo.save(OUT)
    print(f"Vergelijking opgeslagen: {OUT}")
    print("Links = hard gerasterd (zoals Tk nu).  Rechts = supersampled AA (3x + LANCZOS).")

    ms, _ = bench_full_sheet(ss=2, lines=300)
    print(f"\nVol A3-blad, 300 lijnen @ 2x supersample + downscale: {ms:.1f} ms")
    ms3, _ = bench_full_sheet(ss=3, lines=300)
    print(f"Idem @ 3x supersample:                                {ms3:.1f} ms")


if __name__ == "__main__":
    main()
