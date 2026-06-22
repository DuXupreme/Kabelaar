"""Headless benchmark voor de canvas-redraw.

Bouwt de echte app, vult het blad met testdraden via ``stress_fill_wires`` en meet
hoe lang één volledige ``redraw()`` duurt bij oplopende aantallen. Zo zie je zwart-op-wit
of de redraw onder de 16 ms-drempel (60 fps) blijft op een vol blad, of dat de
``delete("all")`` + volledige heropbouw een knelpunt wordt.

Gebruik:
    python tools/bench_redraw.py
    python tools/bench_redraw.py 100 300 600 1000 1500
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

import kabelboom_tekenstudio as kb  # noqa: E402


def measure(app, runs: int = 15) -> dict:
    app.update_idletasks()
    app.redraw()  # warm-up (caches vullen)
    app.update_idletasks()
    times = []
    for _ in range(runs):
        t0 = time.perf_counter()
        app.redraw()
        app.update_idletasks()
        times.append((time.perf_counter() - t0) * 1000.0)
    times.sort()
    return {
        "min": times[0],
        "median": times[len(times) // 2],
        "max": times[-1],
        "items": len(app.canvas.find_all()),
    }


def main(counts):
    app = kb.HarnessDrawingStudio()
    app.geometry("1700x1050")
    app.deiconify()
    app.update()
    app.fit_page_to_view()
    app.update_idletasks()

    print(f"{'draden':>8} {'items':>8} {'min ms':>9} {'mediaan':>9} {'max ms':>9}  {'<16ms':>6}")
    print("-" * 60)
    added = 0
    for target in counts:
        if target > added:
            app.stress_fill_wires(count=target - added)
            added = target
        app.fit_page_to_view()
        stats = measure(app)
        ok = "ja" if stats["median"] <= 16.0 else "NEE"
        print(f"{added:>8} {stats['items']:>8} {stats['min']:>9.1f} {stats['median']:>9.1f} {stats['max']:>9.1f}  {ok:>6}")

    app.destroy()


if __name__ == "__main__":
    try:
        args = [int(a) for a in sys.argv[1:]] or [0, 100, 300, 600, 1000]
    except ValueError:
        print("Gebruik: python tools/bench_redraw.py [aantal ...]")
        raise SystemExit(2)
    main(args)
