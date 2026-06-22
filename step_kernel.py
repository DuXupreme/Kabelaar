"""OpenCASCADE-gebaseerde STEP-tessellatie via de compacte ``cascadio`` wheel.

Deze module kent geen Tk- of app-state. Daardoor is de kernel los te testen en
kan :mod:`step_import` hem optioneel gebruiken met een regex-fallback.
"""

from __future__ import annotations

import math
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple

Point2D = Tuple[float, float]
Point3D = Tuple[float, float, float]
Triangle = Tuple[int, int, int]


class StepKernelUnavailable(RuntimeError):
    """De optionele OpenCASCADE-binding is niet geïnstalleerd."""


class StepKernelError(RuntimeError):
    """De kernel kon het STEP-bestand niet tesselleren."""


@dataclass
class StepMesh:
    vertices: List[Point3D]
    triangles: List[Triangle]


def kernel_available() -> bool:
    try:
        import cascadio  # noqa: F401

        return True
    except (ImportError, OSError):
        return False


def parse_obj_mesh(path: Path) -> StepMesh:
    """Lees de door cascadio geschreven OBJ en trianguleer polygonen als fan."""

    vertices: List[Point3D] = []
    triangles: List[Triangle] = []
    for raw_line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw_line.strip()
        if line.startswith("v "):
            fields = line.split()
            if len(fields) >= 4:
                vertices.append((float(fields[1]), float(fields[2]), float(fields[3])))
        elif line.startswith("f "):
            refs: List[int] = []
            for token in line.split()[1:]:
                head = token.split("/", 1)[0]
                if not head:
                    continue
                idx = int(head)
                refs.append(idx - 1 if idx > 0 else len(vertices) + idx)
            if len(refs) >= 3:
                for pos in range(1, len(refs) - 1):
                    tri = (refs[0], refs[pos], refs[pos + 1])
                    if len(set(tri)) == 3 and all(0 <= idx < len(vertices) for idx in tri):
                        triangles.append(tri)
    return StepMesh(vertices=vertices, triangles=triangles)


def load_step_mesh(step_path: Path, linear_tolerance_mm: float = 0.05) -> StepMesh:
    """Tesselleer STEP naar een driehoeksmesh met OpenCASCADE/cascadio."""

    try:
        import cascadio
    except (ImportError, OSError) as exc:
        raise StepKernelUnavailable("cascadio/OpenCASCADE is niet beschikbaar") from exc

    source = Path(step_path).resolve()
    if not source.is_file():
        raise StepKernelError(f"STEP-bestand bestaat niet: {source}")
    with tempfile.TemporaryDirectory(prefix="kabelaar_step_") as temp_dir:
        obj_path = Path(temp_dir) / "tessellation.obj"
        try:
            result = cascadio.step_to_obj(
                str(source),
                str(obj_path),
                tol_linear=max(0.005, float(linear_tolerance_mm)),
                tol_angular=0.25,
                tol_relative=False,
                use_parallel=True,
                use_colors=False,
            )
        except Exception as exc:
            raise StepKernelError(f"OpenCASCADE kon STEP niet lezen: {exc}") from exc
        if not obj_path.is_file() or obj_path.stat().st_size == 0:
            raise StepKernelError(f"OpenCASCADE leverde geen mesh op (status {result!r})")
        mesh = parse_obj_mesh(obj_path)
    if len(mesh.vertices) < 3 or not mesh.triangles:
        raise StepKernelError("OpenCASCADE leverde geen bruikbare driehoeksmesh op")
    return mesh


def _sub(a: Point3D, b: Point3D) -> Point3D:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a: Point3D, b: Point3D) -> Point3D:
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _normal(a: Point3D, b: Point3D, c: Point3D) -> Point3D:
    raw = _cross(_sub(b, a), _sub(c, a))
    length = math.sqrt(raw[0] ** 2 + raw[1] ** 2 + raw[2] ** 2)
    if length <= 1e-12:
        return (0.0, 0.0, 0.0)
    return (raw[0] / length, raw[1] / length, raw[2] / length)


def _dot(a: Point3D, b: Point3D) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _edge_faces(mesh: StepMesh) -> Tuple[Dict[Tuple[int, int], List[int]], List[Point3D]]:
    edge_faces: Dict[Tuple[int, int], List[int]] = {}
    normals: List[Point3D] = []
    for face_idx, tri in enumerate(mesh.triangles):
        normals.append(_normal(mesh.vertices[tri[0]], mesh.vertices[tri[1]], mesh.vertices[tri[2]]))
        for a, b in ((tri[0], tri[1]), (tri[1], tri[2]), (tri[2], tri[0])):
            edge = (a, b) if a < b else (b, a)
            edge_faces.setdefault(edge, []).append(face_idx)
    return edge_faces, normals


def _segments_from_edges(mesh: StepMesh, edges: Iterable[Tuple[int, int]]) -> List[List[Point3D]]:
    return [[mesh.vertices[a], mesh.vertices[b]] for a, b in edges if mesh.vertices[a] != mesh.vertices[b]]


def feature_polylines(mesh: StepMesh, crease_angle_deg: float = 32.0) -> List[List[Point3D]]:
    """Geef grens- en scherpe mesh-edges voor de roteerbare 3D-preview."""

    edge_faces, normals = _edge_faces(mesh)
    crease_cos = math.cos(math.radians(crease_angle_deg))
    selected: List[Tuple[int, int]] = []
    for edge, faces in edge_faces.items():
        if len(faces) != 2:
            selected.append(edge)
            continue
        if _dot(normals[faces[0]], normals[faces[1]]) < crease_cos:
            selected.append(edge)
    if not selected:
        # Volledig gladde gesloten vorm: toon een begrensde subset zodat de preview niet leeg is.
        selected = list(edge_faces)[:: max(1, len(edge_faces) // 12_000)]
    return _segments_from_edges(mesh, selected)


def _projection_view_direction(projection: str) -> Point3D:
    return {
        "Top (XY)": (0.0, 0.0, 1.0),
        "Bottom (XY)": (0.0, 0.0, -1.0),
        "Front (XZ)": (0.0, 1.0, 0.0),
        "Back (XZ)": (0.0, -1.0, 0.0),
        "Right (YZ)": (1.0, 0.0, 0.0),
        "Left (YZ)": (-1.0, 0.0, 0.0),
    }.get(projection, (0.0, 0.0, 1.0))


def _project(point: Point3D, projection: str) -> Point2D:
    x, y, z = point
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


def project_mesh_outline(mesh: StepMesh, projection: str, crease_angle_deg: float = 32.0) -> List[List[Point2D]]:
    """Projecteer zichtbare silhouet-, grens- en scherpe edges naar 2D."""

    edge_faces, normals = _edge_faces(mesh)
    view = _projection_view_direction(projection)
    facing = [_dot(normal, view) for normal in normals]
    crease_cos = math.cos(math.radians(crease_angle_deg))
    selected: List[Tuple[int, int]] = []
    for edge, faces in edge_faces.items():
        include = len(faces) != 2
        if len(faces) >= 2:
            first, second = faces[0], faces[1]
            silhouette = (facing[first] < -1e-7 and facing[second] > 1e-7) or (
                facing[first] > 1e-7 and facing[second] < -1e-7
            )
            sharp_visible = _dot(normals[first], normals[second]) < crease_cos and max(facing[first], facing[second]) >= -1e-7
            include = include or silhouette or sharp_visible
        if include:
            selected.append(edge)

    polylines: List[List[Point2D]] = []
    seen: set[Tuple[Tuple[float, float], Tuple[float, float]]] = set()
    for a_idx, b_idx in selected:
        a = _project(mesh.vertices[a_idx], projection)
        b = _project(mesh.vertices[b_idx], projection)
        if math.dist(a, b) <= 1e-7:
            continue
        ak = (round(a[0], 6), round(a[1], 6))
        bk = (round(b[0], 6), round(b[1], 6))
        key = (ak, bk) if ak <= bk else (bk, ak)
        if key in seen:
            continue
        seen.add(key)
        polylines.append([a, b])
    return polylines
