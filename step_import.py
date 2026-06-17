"""STEP (ISO 10303-21) import: parsen, eenheden naar mm, projectie naar 2D.

Een pragmatische, regex-gebaseerde parser. Geen volledige STEP-implementatie,
maar genoeg voor connector-footprints: punten, polylines, B-splines (via
controlepunten), rechte edges en cirkel-/boog-edges, met eenheidsconversie.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from geometry import (
    circle_arc_points_3d,
    normalize_polylines,
    project_xyz,
    step_polyline_segments,
)


@dataclass
class StepGeometry3D:
    polylines: List[List[Tuple[float, float, float]]]


# SI-prefix -> tiende-machtsexponent t.o.v. de basiseenheid (METRE).
SI_PREFIX_EXP = {
    "": 0, "DECA": 1, "HECTO": 2, "KILO": 3, "MEGA": 6, "GIGA": 9, "TERA": 12,
    "PETA": 15, "EXA": 18, "DECI": -1, "CENTI": -2, "MILLI": -3, "MICRO": -6,
    "NANO": -9, "PICO": -12, "FEMTO": -15, "ATTO": -18,
}


def parse_step_length_scale(text: str) -> float:
    """Factor om STEP-lengte-eenheden naar millimeter te schalen.

    Ondersteunt SI-eenheden (m, mm, cm, ...) en CONVERSION_BASED_UNIT (inch,
    foot). Valt terug op 1.0 (= aanname millimeter) als niets herkend wordt,
    zodat bestaande mm-bestanden ongewijzigd binnenkomen.
    """
    number = r"[+-]?\d+(?:\.\d+)?(?:[Ee][+-]?\d+)?"
    si_len: Dict[int, float] = {}
    for eid, prefix in re.findall(
        r"#(\d+)\s*=\s*\(?[^;]*?SI_UNIT\s*\(\s*(\.[A-Z]+\.|\$)\s*,\s*\.METRE\.\s*\)",
        text,
        flags=re.IGNORECASE,
    ):
        token = prefix.strip(".")
        exp = SI_PREFIX_EXP.get(token.upper(), 0) if token and token != "$" else 0
        si_len[int(eid)] = 1000.0 * (10.0 ** exp)

    measures: Dict[int, Tuple[float, int]] = {}
    for eid, value, ref in re.findall(
        rf"#(\d+)\s*=\s*LENGTH_MEASURE_WITH_UNIT\s*\(\s*LENGTH_MEASURE\s*\(\s*({number})\s*\)\s*,\s*#(\d+)\s*\)",
        text,
        flags=re.IGNORECASE,
    ):
        measures[int(eid)] = (float(value), int(ref))

    conv_values: List[float] = []
    for mid in re.findall(r"CONVERSION_BASED_UNIT\s*\(\s*'[^']*'\s*,\s*#(\d+)\s*\)", text, flags=re.IGNORECASE):
        ref = measures.get(int(mid))
        if ref is not None:
            value, base_ref = ref
            conv_values.append(value * si_len.get(base_ref, 1.0))

    if conv_values:
        return conv_values[0]
    if si_len:
        return next(iter(si_len.values()))
    return 1.0


def _arc_from_placement(placements, points, directions, placement_id, radius, p_start, p_end):
    placement = placements.get(placement_id)
    if not placement:
        return None
    loc_tok, axis_tok, ref_tok = placement

    def dir_of(tok, fallback):
        if tok and tok.startswith("#"):
            return directions.get(int(tok[1:]), fallback)
        return fallback

    center = points.get(int(loc_tok[1:])) if loc_tok.startswith("#") else None
    if center is None:
        return None
    z_axis = dir_of(axis_tok, (0.0, 0.0, 1.0))
    x_axis = dir_of(ref_tok, (1.0, 0.0, 0.0))
    return circle_arc_points_3d(center, z_axis, x_axis, radius, p_start, p_end)


def parse_step_geometry(step_path: Path) -> StepGeometry3D:
    text = step_path.read_text(encoding="utf-8", errors="ignore")
    number = r"[+-]?\d+(?:\.\d+)?(?:[Ee][+-]?\d+)?"

    scale = parse_step_length_scale(text)

    points_by_id: Dict[int, Tuple[float, float, float]] = {}
    for pid, xs, ys, zs in re.findall(
        rf"#(\d+)\s*=\s*CARTESIAN_POINT\s*\(\s*'[^']*'\s*,\s*\(\s*({number})\s*,\s*({number})\s*,\s*({number})\s*\)\s*\)\s*;",
        text,
        flags=re.IGNORECASE,
    ):
        points_by_id[int(pid)] = (float(xs) * scale, float(ys) * scale, float(zs) * scale)

    directions_by_id: Dict[int, Tuple[float, float, float]] = {}
    for did, xs, ys, zs in re.findall(
        rf"#(\d+)\s*=\s*DIRECTION\s*\(\s*'[^']*'\s*,\s*\(\s*({number})\s*,\s*({number})\s*,\s*({number})\s*\)\s*\)",
        text,
        flags=re.IGNORECASE,
    ):
        directions_by_id[int(did)] = (float(xs), float(ys), float(zs))

    placements_by_id: Dict[int, Tuple[str, str, str]] = {}
    for pid, loc, axis, ref in re.findall(
        r"#(\d+)\s*=\s*AXIS2_PLACEMENT_3D\s*\(\s*'[^']*'\s*,\s*(#\d+|\$)\s*,\s*(#\d+|\$)\s*,\s*(#\d+|\$)\s*\)",
        text,
        flags=re.IGNORECASE,
    ):
        placements_by_id[int(pid)] = (loc, axis, ref)

    circles_by_id: Dict[int, Tuple[int, float]] = {}
    for cid, placement, radius in re.findall(
        rf"#(\d+)\s*=\s*CIRCLE\s*\(\s*'[^']*'\s*,\s*#(\d+)\s*,\s*({number})\s*\)",
        text,
        flags=re.IGNORECASE,
    ):
        circles_by_id[int(cid)] = (int(placement), float(radius) * scale)

    vertex_to_point: Dict[int, int] = {}
    for vid, pid in re.findall(
        r"#(\d+)\s*=\s*VERTEX_POINT\s*\([^#]*#(\d+)\s*\)\s*;",
        text,
        flags=re.IGNORECASE,
    ):
        vertex_to_point[int(vid)] = int(pid)

    polylines_3d: List[List[Tuple[float, float, float]]] = []

    for refs_blob in re.findall(
        r"#\d+\s*=\s*POLYLINE\s*\([^()]*\(\s*([^)]*?)\s*\)\s*\)\s*;",
        text,
        flags=re.IGNORECASE | re.DOTALL,
    ):
        ids = [int(x) for x in re.findall(r"#(\d+)", refs_blob)]
        line = [points_by_id[i] for i in ids if i in points_by_id]
        if len(line) >= 2:
            polylines_3d.append(line)

    for refs_blob in re.findall(
        r"#\d+\s*=\s*B_SPLINE_CURVE_WITH_KNOTS\s*\(\s*'[^']*'\s*,\s*\d+\s*,\s*\(\s*([^)]*?)\s*\)",
        text,
        flags=re.IGNORECASE | re.DOTALL,
    ):
        ids = [int(x) for x in re.findall(r"#(\d+)", refs_blob)]
        line = [points_by_id[i] for i in ids if i in points_by_id]
        if len(line) >= 2:
            polylines_3d.append(line)

    for v1, v2, curve in re.findall(
        r"#\d+\s*=\s*EDGE_CURVE\s*\(\s*[^,]*,\s*#(\d+)\s*,\s*#(\d+)\s*,\s*#(\d+)\s*,",
        text,
        flags=re.IGNORECASE,
    ):
        p1_id = vertex_to_point.get(int(v1))
        p2_id = vertex_to_point.get(int(v2))
        if p1_id not in points_by_id or p2_id not in points_by_id:
            continue
        p1 = points_by_id[p1_id]
        p2 = points_by_id[p2_id]
        circle = circles_by_id.get(int(curve))
        if circle is not None:
            placement_id, radius = circle
            arc = _arc_from_placement(placements_by_id, points_by_id, directions_by_id, placement_id, radius, p1, p2)
            if arc and len(arc) >= 2:
                polylines_3d.append(arc)
                continue
        if math.dist(p1, p2) > 1e-9:
            polylines_3d.append([p1, p2])

    if not polylines_3d and points_by_id:
        pts = list(points_by_id.values())
        pts.sort(key=lambda p: (p[0], p[1], p[2]))
        if len(pts) >= 2:
            polylines_3d.append(pts)

    dedup: Dict[Tuple[Tuple[float, float, float], Tuple[float, float, float]], None] = {}
    for a, b in step_polyline_segments(polylines_3d):
        a_key = (round(a[0], 6), round(a[1], 6), round(a[2], 6))
        b_key = (round(b[0], 6), round(b[1], 6), round(b[2], 6))
        key = (a_key, b_key) if a_key <= b_key else (b_key, a_key)
        dedup[key] = None

    compact_lines = [[(a[0], a[1], a[2]), (b[0], b[1], b[2])] for a, b in dedup.keys()]
    if compact_lines:
        return StepGeometry3D(polylines=compact_lines)
    return StepGeometry3D(polylines=polylines_3d)


def project_step_geometry(geometry: StepGeometry3D, projection: str) -> Tuple[List[List[Tuple[float, float]]], float, float]:
    polylines_2d: List[List[Tuple[float, float]]] = []
    for line in geometry.polylines:
        projected = [project_xyz(x, y, z, projection) for x, y, z in line]
        if len(projected) >= 2:
            polylines_2d.append(projected)

    if not polylines_2d:
        polylines_2d = [[(0.0, 0.0), (20.0, 0.0), (20.0, 10.0), (0.0, 10.0), (0.0, 0.0)]]
    normalized, w, h = normalize_polylines(polylines_2d)
    return normalized, w, h


def parse_step_point_cloud(step_path: Path, projection: str = "Top (XY)") -> Tuple[List[List[Tuple[float, float]]], float, float]:
    geometry = parse_step_geometry(step_path)
    return project_step_geometry(geometry, projection)
