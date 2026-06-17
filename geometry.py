"""Pure 2D/3D geometrie-helpers, zonder afhankelijkheid van de app of Tkinter.

Losgetrokken uit kabelboom_tekenstudio.py zodat deze functies apart te testen
en te hergebruiken zijn.
"""

from __future__ import annotations

import math
from typing import List, Optional, Tuple


def clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, value))


def distance_point_segment(px, py, x1, y1, x2, y2) -> float:
    dx = x2 - x1
    dy = y2 - y1
    if abs(dx) < 1e-9 and abs(dy) < 1e-9:
        return math.hypot(px - x1, py - y1)
    t = ((px - x1) * dx + (py - y1) * dy) / (dx * dx + dy * dy)
    t = clamp(t, 0.0, 1.0)
    cx = x1 + t * dx
    cy = y1 + t * dy
    return math.hypot(px - cx, py - cy)


def closest_point_on_segment(
    px: float, py: float, x1: float, y1: float, x2: float, y2: float
) -> Tuple[Tuple[float, float], float, float]:
    dx = x2 - x1
    dy = y2 - y1
    if abs(dx) < 1e-9 and abs(dy) < 1e-9:
        return ((x1, y1), 0.0, math.hypot(px - x1, py - y1))
    t = ((px - x1) * dx + (py - y1) * dy) / (dx * dx + dy * dy)
    t = clamp(t, 0.0, 1.0)
    cx = x1 + t * dx
    cy = y1 + t * dy
    return ((cx, cy), t, math.hypot(px - cx, py - cy))


def segment_intersection(
    a1: Tuple[float, float],
    a2: Tuple[float, float],
    b1: Tuple[float, float],
    b2: Tuple[float, float],
    tol: float = 1e-9,
) -> Optional[Tuple[Tuple[float, float], float, float]]:
    ax = a2[0] - a1[0]
    ay = a2[1] - a1[1]
    bx = b2[0] - b1[0]
    by = b2[1] - b1[1]
    denom = ax * by - ay * bx
    if abs(denom) <= tol:
        return None
    qx = b1[0] - a1[0]
    qy = b1[1] - a1[1]
    ta = (qx * by - qy * bx) / denom
    tb = (qx * ay - qy * ax) / denom
    if -tol <= ta <= 1.0 + tol and -tol <= tb <= 1.0 + tol:
        ta = clamp(ta, 0.0, 1.0)
        tb = clamp(tb, 0.0, 1.0)
        return ((a1[0] + ax * ta, a1[1] + ay * ta), ta, tb)
    return None


def polyline_bbox(polylines: List[List[Tuple[float, float]]]) -> Tuple[float, float, float, float]:
    xs: List[float] = []
    ys: List[float] = []
    for line in polylines:
        for x, y in line:
            xs.append(x)
            ys.append(y)
    if not xs:
        return (0.0, 0.0, 10.0, 10.0)
    return (min(xs), min(ys), max(xs), max(ys))


def polyline_point_count(polylines: List[List[Tuple[float, float]]]) -> int:
    return sum(len(line) for line in polylines)


def polyline_segment_count(polylines: List[List[Tuple[float, float]]]) -> int:
    return sum(max(0, len(line) - 1) for line in polylines)


def normalize_polylines(polylines: List[List[Tuple[float, float]]]) -> Tuple[List[List[Tuple[float, float]]], float, float]:
    x1, y1, x2, y2 = polyline_bbox(polylines)
    w = max(0.001, x2 - x1)
    h = max(0.001, y2 - y1)
    cx = (x1 + x2) / 2.0
    cy = (y1 + y2) / 2.0
    normalized: List[List[Tuple[float, float]]] = []
    for line in polylines:
        normalized.append([(x - cx, y - cy) for x, y in line])
    return normalized, w, h


def envelope_outline(points: List[Tuple[float, float]], bins: int = 140) -> List[Tuple[float, float]]:
    if len(points) < 8:
        return points
    x_values = [p[0] for p in points]
    xmin = min(x_values)
    xmax = max(x_values)
    if abs(xmax - xmin) < 1e-9:
        return points
    bins = max(24, bins)
    bucket: List[List[Tuple[float, float]]] = [[] for _ in range(bins)]
    for x, y in points:
        idx = int((x - xmin) / (xmax - xmin) * (bins - 1))
        idx = max(0, min(bins - 1, idx))
        bucket[idx].append((x, y))

    upper: List[Tuple[float, float]] = []
    lower: List[Tuple[float, float]] = []
    for i, pts in enumerate(bucket):
        if not pts:
            continue
        cx = sum(p[0] for p in pts) / len(pts)
        y_top = min(p[1] for p in pts)
        y_bottom = max(p[1] for p in pts)
        upper.append((cx, y_top))
        lower.append((cx, y_bottom))

    if len(upper) < 3 or len(lower) < 3:
        return points
    return upper + list(reversed(lower)) + [upper[0]]


def project_xyz(x: float, y: float, z: float, projection: str) -> Tuple[float, float]:
    if projection == "Top (XY)":
        return (x, y)
    if projection == "Bottom (XY)":
        return (x, -y)
    if projection == "Front (XZ)":
        return (x, z)
    if projection == "Back (XZ)":
        return (-x, z)
    if projection == "Right (YZ)":
        return (y, z)
    if projection == "Left (YZ)":
        return (-y, z)
    return (x, y)


def rotate_xyz(point: Tuple[float, float, float], yaw: float, pitch: float) -> Tuple[float, float, float]:
    x, y, z = point
    cy = math.cos(yaw)
    sy = math.sin(yaw)
    x2 = x * cy + z * sy
    z2 = -x * sy + z * cy

    cp = math.cos(pitch)
    sp = math.sin(pitch)
    y3 = y * cp - z2 * sp
    z3 = y * sp + z2 * cp
    return (x2, y3, z3)


def step_polyline_segments(polylines_3d: List[List[Tuple[float, float, float]]]) -> List[Tuple[Tuple[float, float, float], Tuple[float, float, float]]]:
    segments: List[Tuple[Tuple[float, float, float], Tuple[float, float, float]]] = []
    for line in polylines_3d:
        if len(line) < 2:
            continue
        for i in range(len(line) - 1):
            a = line[i]
            b = line[i + 1]
            if math.dist(a, b) > 1e-9:
                segments.append((a, b))
    return segments


def _vec_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _vec_dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _vec_cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _vec_norm(a):
    length = math.sqrt(_vec_dot(a, a))
    if length <= 1e-12:
        return None
    return (a[0] / length, a[1] / length, a[2] / length)


def circle_arc_points_3d(
    center, z_axis, x_axis, radius, p_start, p_end, max_seg_angle: float = math.pi / 16
) -> List[Tuple[float, float, float]]:
    """Bemonster een cirkelboog (of volledige cirkel) naar 3D-punten.

    Heuristiek: vallen begin- en eindpunt samen dan een volledige cirkel,
    anders de korte boog tussen beide. Dat dekt afrondingen/gaten in
    connectorbehuizingen; bij een enkele 180 graden-boog kan de boogkant
    arbitrair zijn, maar dat is altijd beter dan een rechte koorde.
    """
    z = _vec_norm(z_axis) or (0.0, 0.0, 1.0)
    x = _vec_norm(x_axis) or (1.0, 0.0, 0.0)
    # Maak x loodrecht op z (Gram-Schmidt) zodat het frame orthonormaal is.
    proj = _vec_dot(x, z)
    x = _vec_norm(_vec_sub(x, (z[0] * proj, z[1] * proj, z[2] * proj))) or (1.0, 0.0, 0.0)
    y = _vec_cross(z, x)

    def angle_of(p):
        d = _vec_sub(p, center)
        return math.atan2(_vec_dot(d, y), _vec_dot(d, x))

    a1 = angle_of(p_start)
    if math.dist(p_start, p_end) <= max(1e-6, abs(radius) * 1e-6):
        sweep = 2.0 * math.pi
    else:
        sweep = (angle_of(p_end) - a1) % (2.0 * math.pi)
        if sweep > math.pi:
            sweep -= 2.0 * math.pi
    steps = max(2, int(math.ceil(abs(sweep) / max_seg_angle)))
    points: List[Tuple[float, float, float]] = []
    for i in range(steps + 1):
        ang = a1 + sweep * (i / steps)
        cr = math.cos(ang) * radius
        sr = math.sin(ang) * radius
        points.append(
            (
                center[0] + cr * x[0] + sr * y[0],
                center[1] + cr * x[1] + sr * y[1],
                center[2] + cr * x[2] + sr * y[2],
            )
        )
    return points


def preview_project(point: Tuple[float, float, float], yaw: float, pitch: float) -> Tuple[float, float]:
    x, y, _z = rotate_xyz(point, yaw, pitch)
    return (x, -y)
