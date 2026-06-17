from __future__ import annotations

import ctypes
import sys
from dataclasses import dataclass
from typing import Tuple


@dataclass(frozen=True)
class MonitorMetrics:
    x: int
    y: int
    width: int
    height: int
    dpi: int = 96


class RECT(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long),
        ("top", ctypes.c_long),
        ("right", ctypes.c_long),
        ("bottom", ctypes.c_long),
    ]


class MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("rcMonitor", RECT),
        ("rcWork", RECT),
        ("dwFlags", ctypes.c_ulong),
    ]


MONITOR_DEFAULTTONEAREST = 2
UI_SCALE_PERCENT_OPTIONS = (60, 70, 80, 90, 100, 110, 125, 150)
UI_SCALE_LABELS = tuple(f"{value}%" for value in UI_SCALE_PERCENT_OPTIONS)


def normalize_ui_scale_percent(value, fallback: int = 100) -> int:
    try:
        percent = int(str(value).strip().rstrip("%"))
    except (TypeError, ValueError):
        percent = int(fallback)
    return max(60, min(200, percent))


def set_ui_scale(window, percent, *, apply_tk_scaling: bool = True) -> int:
    percent = normalize_ui_scale_percent(percent)
    base_scaling = getattr(window, "_base_tk_scaling", None)
    if base_scaling is None:
        try:
            base_scaling = float(window.tk.call("tk", "scaling"))
        except Exception:
            base_scaling = 96.0 / 72.0
        window._base_tk_scaling = base_scaling
    window._ui_scale_percent = percent
    try:
        scale = percent / 100.0 if apply_tk_scaling else 1.0
        window.tk.call("tk", "scaling", float(base_scaling) * scale)
        window.update_idletasks()
    except Exception:
        pass
    return percent


def enable_dpi_awareness() -> None:
    if sys.platform != "win32":
        return

    user32 = ctypes.windll.user32
    shcore = getattr(ctypes.windll, "shcore", None)

    try:
        awareness_context = ctypes.c_void_p(-4)
        if user32.SetProcessDpiAwarenessContext(awareness_context):
            return
    except Exception:
        pass

    if shcore is not None:
        try:
            shcore.SetProcessDpiAwareness(2)
            return
        except Exception:
            pass

    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass


def schedule_window_scaling(
    window,
    *,
    design_size: Tuple[int, int],
    min_size: Tuple[int, int],
    width_ratio: float = 0.92,
    height_ratio: float = 0.90,
    upscale_cap: float = 1.25,
    apply_tk_scaling: bool = True,
) -> None:
    def apply() -> None:
        if getattr(window, "_auto_window_scaling_done", False):
            return
        window._auto_window_scaling_done = True
        _apply_window_scaling(
            window,
            design_size=design_size,
            min_size=min_size,
            width_ratio=width_ratio,
            height_ratio=height_ratio,
            upscale_cap=upscale_cap,
            apply_tk_scaling=apply_tk_scaling,
        )

    window.after(40, apply)
    window.bind("<Map>", lambda _event: apply(), add="+")


def _apply_window_scaling(
    window,
    *,
    design_size: Tuple[int, int],
    min_size: Tuple[int, int],
    width_ratio: float,
    height_ratio: float,
    upscale_cap: float,
    apply_tk_scaling: bool,
) -> None:
    window.update_idletasks()
    metrics = _monitor_metrics_for_window(window)
    _apply_tk_scaling(window, metrics.dpi, apply_tk_scaling=apply_tk_scaling)

    design_w, design_h = design_size
    base_min_w, base_min_h = min_size
    fit_scale = min(
        (metrics.width * width_ratio) / max(1, design_w),
        (metrics.height * height_ratio) / max(1, design_h),
    )
    dpi_scale = max(1.0, metrics.dpi / 96.0)
    preferred_scale = max(dpi_scale, min(fit_scale, upscale_cap))
    window_scale = max(0.55, min(fit_scale, preferred_scale))

    width = max(640, int(round(design_w * window_scale)))
    height = max(480, int(round(design_h * window_scale)))

    min_w = min(width, max(640, min(base_min_w, int(round(width * 0.82)))))
    min_h = min(height, max(480, min(base_min_h, int(round(height * 0.82)))))
    window.minsize(min_w, min_h)

    pos_x = metrics.x + max(0, (metrics.width - width) // 2)
    pos_y = metrics.y + max(0, (metrics.height - height) // 2)
    window.geometry(f"{width}x{height}+{pos_x}+{pos_y}")


def _apply_tk_scaling(window, dpi: int, *, apply_tk_scaling: bool = True) -> None:
    base_scaling = dpi / 72.0
    window._base_tk_scaling = base_scaling
    percent = normalize_ui_scale_percent(getattr(window, "_ui_scale_percent", 100))
    try:
        scale = percent / 100.0 if apply_tk_scaling else 1.0
        window.tk.call("tk", "scaling", base_scaling * scale)
    except Exception:
        pass


def _monitor_metrics_for_window(window) -> MonitorMetrics:
    fallback = MonitorMetrics(
        x=0,
        y=0,
        width=max(1, int(window.winfo_screenwidth())),
        height=max(1, int(window.winfo_screenheight())),
        dpi=96,
    )
    if sys.platform != "win32":
        return fallback

    try:
        user32 = ctypes.windll.user32
        hwnd = int(window.winfo_id())
        monitor = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
        if not monitor:
            return fallback

        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        if not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
            return fallback

        dpi = 96
        if hasattr(user32, "GetDpiForWindow"):
            try:
                dpi = int(user32.GetDpiForWindow(hwnd)) or 96
            except Exception:
                dpi = 96

        work = info.rcWork
        return MonitorMetrics(
            x=int(work.left),
            y=int(work.top),
            width=max(1, int(work.right - work.left)),
            height=max(1, int(work.bottom - work.top)),
            dpi=max(96, dpi),
        )
    except Exception:
        return fallback
