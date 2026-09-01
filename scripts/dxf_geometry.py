"""DXF polygon reading and flip/rotation comparison.

Ports the C# DxfCompare logic in PolygonComparer, AsciiDxfParser, and
DxfPolygonReader so Python results match the desktop application.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

ENDPOINT_TOLERANCE = 1e-6
RELATIVE_TOLERANCE_DEFAULT = 1e-4


@dataclass(frozen=True, slots=True)
class Point2D:
    x: float
    y: float

    def __add__(self, other: Point2D) -> Point2D:
        return Point2D(self.x + other.x, self.y + other.y)

    def __sub__(self, other: Point2D) -> Point2D:
        return Point2D(self.x - other.x, self.y - other.y)

    def __mul__(self, scale: float) -> Point2D:
        return Point2D(self.x * scale, self.y * scale)

    def distance_to(self, other: Point2D) -> float:
        dx = self.x - other.x
        dy = self.y - other.y
        return math.sqrt(dx * dx + dy * dy)

    @property
    def length(self) -> float:
        return math.sqrt(self.x * self.x + self.y * self.y)


@dataclass(frozen=True, slots=True)
class ComparisonResult:
    is_match: bool
    message: str
    vertex_count: int = 0
    is_flipped: bool = False
    flip_side: str = "None"
    flip_description: str = "Not flipped"
    rotation_degrees_ccw: float = 0.0
    rotation_degrees_cw: float = 0.0
    mirror_axis_degrees: float = 0.0
    fit_error: float = 0.0
    transform_summary: str = "No match"

    @staticmethod
    def no_match(message: str, vertex_count: int = 0) -> ComparisonResult:
        return ComparisonResult(is_match=False, message=message, vertex_count=vertex_count)

    @staticmethod
    def error(message: str) -> ComparisonResult:
        return ComparisonResult(is_match=False, message=message, transform_summary="Error")


@dataclass(slots=True)
class CandidateFit:
    is_match: bool
    is_flipped: bool
    rotation_radians: float
    fit_error: float
    flip_side: str
    flip_description: str
    mirror_axis_degrees: float
    transform_summary: str


@dataclass(slots=True)
class LayerPolygonCandidate:
    points: list[Point2D]
    layer: str


# ---------------------------------------------------------------------------
# Polygon comparison (PolygonComparer.cs)
# ---------------------------------------------------------------------------

def compare_polygons(
    poly_a: Sequence[Point2D] | None,
    poly_b: Sequence[Point2D] | None,
    relative_tolerance: float = RELATIVE_TOLERANCE_DEFAULT,
) -> ComparisonResult:
    if poly_a is None or poly_b is None:
        return ComparisonResult.no_match("One or both polygons are missing.")

    a = normalize(list(poly_a))
    b = normalize(list(poly_b))

    if len(a) < 3 or len(b) < 3:
        return ComparisonResult.no_match("A valid polygon needs at least 3 vertices after cleanup.")

    resampled = False
    if len(a) != len(b):
        sample_n = max(48, min(96, max(len(a), len(b))))
        a = _resample_closed(a, sample_n)
        b = _resample_closed(b, sample_n)
        resampled = True

    scale = max(_rms_radius(a), 1e-9)
    abs_tol = max(1e-8, relative_tolerance * scale)
    length_tol = None if resampled else max(
        1e-8, relative_tolerance * max(_perimeter(a), _perimeter(b)) / len(a)
    )

    best: CandidateFit | None = None

    for shift in range(len(a)):
        mapped = _map_same_winding(b, shift)
        if length_tol is not None and not _edges_match(a, mapped, length_tol):
            continue
        best = _better(best, _fit_without_flip(a, mapped, abs_tol))

    for shift in range(len(a)):
        mapped = _map_reversed(b, shift)
        if length_tol is not None and not _edges_match(a, mapped, length_tol):
            continue
        best = _better(best, _fit_with_flip(a, mapped, abs_tol))

    if best is None or not best.is_match:
        return ComparisonResult.no_match(
            "The polygons are not the same shape (even allowing rotation or flipping).",
            len(a),
        )

    return _to_result(best, len(a))


def normalize(pts: list[Point2D]) -> list[Point2D]:
    cleaned = _remove_duplicates(pts)
    cleaned = remove_collinear_points(cleaned)
    if len(cleaned) >= 3 and _signed_area(cleaned) < 0:
        cleaned.reverse()
    return cleaned


def remove_collinear_points(pts: list[Point2D], tolerance: float = 1e-5) -> list[Point2D]:
    if len(pts) <= 3:
        return list(pts)

    simplified: list[Point2D] = []
    n = len(pts)
    for i in range(n):
        prev = pts[(i - 1 + n) % n]
        curr = pts[i]
        nxt = pts[(i + 1) % n]
        if not _is_collinear(prev, curr, nxt, tolerance):
            simplified.append(curr)

    return simplified if len(simplified) >= 3 else list(pts)


def _remove_duplicates(pts: Sequence[Point2D], tolerance: float = 1e-8) -> list[Point2D]:
    result: list[Point2D] = []
    for p in pts:
        if not result or result[-1].distance_to(p) > tolerance:
            result.append(p)
    if len(result) > 1 and result[0].distance_to(result[-1]) <= tolerance:
        result.pop()
    return result


def _is_collinear(p1: Point2D, p2: Point2D, p3: Point2D, tolerance: float) -> bool:
    v1x, v1y = p2.x - p1.x, p2.y - p1.y
    v2x, v2y = p3.x - p2.x, p3.y - p2.y
    len1 = math.sqrt(v1x * v1x + v1y * v1y)
    len2 = math.sqrt(v2x * v2x + v2y * v2y)
    if len1 < tolerance or len2 < tolerance:
        return True
    v1x /= len1
    v1y /= len1
    v2x /= len2
    v2y /= len2
    cross = v1x * v2y - v1y * v2x
    dot = v1x * v2x + v1y * v2y
    return abs(cross) < tolerance and dot > 0


def _resample_closed(pts: Sequence[Point2D], count: int) -> list[Point2D]:
    n = len(pts)
    if n == 0 or count <= 0:
        return list(pts)
    lengths = [pts[i].distance_to(pts[(i + 1) % n]) for i in range(n)]
    total = sum(lengths)
    if total <= 1e-12:
        return list(pts)
    out: list[Point2D] = []
    for k in range(count):
        target = total * k / count
        acc = 0.0
        for i, slen in enumerate(lengths):
            nxt = acc + slen
            if nxt >= target - 1e-12 or i == n - 1:
                t = 0.0 if slen < 1e-12 else (target - acc) / slen
                t = min(max(t, 0.0), 1.0)
                a, b = pts[i], pts[(i + 1) % n]
                out.append(Point2D(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t))
                break
            acc = nxt
    return out


def _map_same_winding(poly: Sequence[Point2D], shift: int) -> list[Point2D]:
    n = len(poly)
    return [poly[(i + shift) % n] for i in range(n)]


def _map_reversed(poly: Sequence[Point2D], shift: int) -> list[Point2D]:
    n = len(poly)
    return [poly[(shift - i + n) % n] for i in range(n)]


def _edges_match(a: Sequence[Point2D], b: Sequence[Point2D], length_tol: float) -> bool:
    n = len(a)
    for i in range(n):
        la = a[i].distance_to(a[(i + 1) % n])
        lb = b[i].distance_to(b[(i + 1) % n])
        if abs(la - lb) > length_tol:
            return False
    return True


def _fit_without_flip(a: Sequence[Point2D], b: Sequence[Point2D], abs_tol: float) -> CandidateFit:
    ca, cb = _center_pair(a, b)
    angle, rmsd = _fit_rotation(ca, cb)
    match = rmsd <= abs_tol
    deg = _normalize_degrees(_to_degrees(angle))
    if match:
        summary = (
            "Same orientation (no rotation, no flip)"
            if _is_near_zero_degrees(deg)
            else f"Rotated {_format_degrees(deg)} CCW"
        )
    else:
        summary = "No match"
    return CandidateFit(
        is_match=match,
        is_flipped=False,
        rotation_radians=angle,
        fit_error=rmsd,
        flip_side="None",
        flip_description="Not flipped",
        mirror_axis_degrees=0,
        transform_summary=summary,
    )


def _fit_with_flip(a: Sequence[Point2D], b: Sequence[Point2D], abs_tol: float) -> CandidateFit:
    ca, cb = _center_pair(a, b)
    flipped_h = [Point2D(-p.x, p.y) for p in ca]
    flipped_v = [Point2D(p.x, -p.y) for p in ca]
    angle_h, rmsd_h = _fit_rotation(flipped_h, cb)
    angle_v, rmsd_v = _fit_rotation(flipped_v, cb)
    deg_h = _smallest_signed_degrees(_to_degrees(angle_h))
    deg_v = _smallest_signed_degrees(_to_degrees(angle_v))
    prefer_h = rmsd_h < rmsd_v - abs_tol * 0.1 or (
        abs(rmsd_h - rmsd_v) <= abs_tol * 0.1 and abs(deg_h) <= abs(deg_v)
    )
    rmsd = rmsd_h if prefer_h else rmsd_v
    angle = angle_h if prefer_h else angle_v
    deg = _normalize_degrees(_to_degrees(angle))
    match = rmsd <= abs_tol
    side = "Horizontal" if prefer_h else "Vertical"
    side_long = (
        "Horizontal (left-right, mirror over the vertical axis)"
        if prefer_h
        else "Vertical (up-down, mirror over the horizontal axis)"
    )
    axis = 90 if prefer_h else 0
    if not match:
        summary = "No match"
    elif _is_near_zero_degrees(deg):
        summary = (
            "Flipped horizontally (left-right, no extra rotation)"
            if prefer_h
            else "Flipped vertically (up-down, no extra rotation)"
        )
    else:
        adverb = "horizontally" if prefer_h else "vertically"
        summary = f"Flipped {adverb}, then rotated {_format_degrees(deg)} CCW"

    return CandidateFit(
        is_match=match,
        is_flipped=True,
        rotation_radians=angle,
        fit_error=rmsd,
        flip_side=side,
        flip_description=side_long,
        mirror_axis_degrees=axis,
        transform_summary=summary,
    )


def _center_pair(a: Sequence[Point2D], b: Sequence[Point2D]) -> tuple[list[Point2D], list[Point2D]]:
    ca = _centroid(a)
    cb = _centroid(b)
    return [p - ca for p in a], [p - cb for p in b]


def _fit_rotation(a: Sequence[Point2D], b: Sequence[Point2D]) -> tuple[float, float]:
    dot = 0.0
    cross = 0.0
    for i in range(len(a)):
        dot += a[i].x * b[i].x + a[i].y * b[i].y
        cross += a[i].x * b[i].y - a[i].y * b[i].x
    angle = math.atan2(cross, dot)
    c = math.cos(angle)
    s = math.sin(angle)
    sse = 0.0
    for i in range(len(a)):
        rx = c * a[i].x - s * a[i].y
        ry = s * a[i].x + c * a[i].y
        dx = rx - b[i].x
        dy = ry - b[i].y
        sse += dx * dx + dy * dy
    return angle, math.sqrt(sse / len(a))


def _better(current: CandidateFit | None, candidate: CandidateFit) -> CandidateFit:
    if not candidate.is_match:
        return current if current is not None else candidate
    if current is None or not current.is_match:
        return candidate
    if candidate.fit_error < current.fit_error * 0.5:
        return candidate
    if current.fit_error < candidate.fit_error * 0.5:
        return current
    current_abs = abs(_smallest_signed_degrees(_to_degrees(current.rotation_radians)))
    candidate_abs = abs(_smallest_signed_degrees(_to_degrees(candidate.rotation_radians)))
    if candidate.is_flipped == current.is_flipped:
        return candidate if candidate_abs < current_abs else current
    return current if current.is_flipped else candidate


def _to_result(fit: CandidateFit, vertex_count: int) -> ComparisonResult:
    ccw = _normalize_degrees(_to_degrees(fit.rotation_radians))
    cw = _normalize_degrees(360 - ccw)
    return ComparisonResult(
        is_match=True,
        message="The DXF shapes match (same polygon, allowing rotation and/or flipping).",
        vertex_count=vertex_count,
        is_flipped=fit.is_flipped,
        flip_side=fit.flip_side,
        flip_description=fit.flip_description,
        rotation_degrees_ccw=ccw,
        rotation_degrees_cw=0 if _is_near_zero_degrees(ccw) else cw,
        mirror_axis_degrees=fit.mirror_axis_degrees,
        fit_error=fit.fit_error,
        transform_summary=fit.transform_summary,
    )


def _centroid(pts: Sequence[Point2D]) -> Point2D:
    return Point2D(sum(p.x for p in pts) / len(pts), sum(p.y for p in pts) / len(pts))


def _signed_area(pts: Sequence[Point2D]) -> float:
    area = 0.0
    n = len(pts)
    for i in range(n):
        a = pts[i]
        b = pts[(i + 1) % n]
        area += a.x * b.y - b.x * a.y
    return area / 2.0


def _perimeter(pts: Sequence[Point2D]) -> float:
    n = len(pts)
    return sum(pts[i].distance_to(pts[(i + 1) % n]) for i in range(n))


def _rms_radius(pts: Sequence[Point2D]) -> float:
    c = _centroid(pts)
    total = sum((p.x - c.x) ** 2 + (p.y - c.y) ** 2 for p in pts)
    return math.sqrt(total / len(pts))


def _to_degrees(radians: float) -> float:
    return radians * 180.0 / math.pi


def _normalize_degrees(degrees: float) -> float:
    degrees %= 360.0
    if degrees < 0:
        degrees += 360.0
    if degrees >= 360.0 - 1e-8:
        degrees = 0
    return degrees


def _smallest_signed_degrees(degrees: float) -> float:
    degrees = _normalize_degrees(degrees)
    if degrees > 180:
        degrees -= 360
    return degrees


def _is_near_zero_degrees(degrees: float) -> bool:
    wrapped = _normalize_degrees(degrees)
    return wrapped < 0.05 or wrapped > 359.95


def _format_degrees(degrees: float) -> str:
    text = f"{degrees:.2f}".rstrip("0").rstrip(".")
    return f"{text}°"


# ---------------------------------------------------------------------------
# DXF reading
# ---------------------------------------------------------------------------

def read_polygon(source: str | Path | bytes, layer_name: str | None = None) -> list[Point2D]:
    candidates = read_polygon_candidates(source)
    return select_primary_polygon(candidates, _source_label(source), layer_name)


def polygon_points(source: str | Path | bytes, layer_name: str | None = None, cap: int = 500) -> list[dict]:
    pts = normalize(read_polygon(source, layer_name))
    if cap and len(pts) > cap:
        step = max(1, len(pts) // cap)
        pts = pts[::step]
    return [{"x": round(p.x, 4), "y": round(p.y, 4)} for p in pts]


def read_polygon_candidates(source: str | Path | bytes) -> list[LayerPolygonCandidate]:
    text = _load_dxf_text(source)
    candidates: list[LayerPolygonCandidate] = []
    try:
        candidates = _read_with_ezdxf(text)
    except Exception:
        candidates = []
    if not candidates:
        candidates = _read_ascii_candidates(text)
    return candidates


def select_primary_polygon(
    candidates: Sequence[LayerPolygonCandidate],
    source_label: str,
    layer_name: str | None = None,
) -> list[Point2D]:
    filtered = list(candidates)
    if layer_name and layer_name.strip():
        filtered = [c for c in candidates if _layer_matches(c.layer, layer_name)]
    if not filtered:
        if layer_name and layer_name.strip():
            available = ", ".join(sorted({c.layer for c in candidates})) or "(none)"
            raise ValueError(
                f"No closed shape found on layer '{layer_name}' in '{source_label}'. "
                f"Available layers: {available}"
            )
        raise ValueError(
            f"No closed shape found in '{source_label}'. "
            "Use a closed polyline, circle, or a closed loop of lines/arcs."
        )
    best = max(filtered, key=lambda c: (abs(_signed_area(c.points)), len(c.points)))
    return best.points


def _layer_matches(entity_layer: str, requested: str) -> bool:
    a = (entity_layer or "0").strip()
    b = requested.strip()
    if a.lower() == b.lower():
        return True
    try:
        return int(a) == int(b)
    except ValueError:
        return False


def compare_dxf_files(
    actual_source: str | Path | bytes,
    generated_source: str | Path | bytes,
    relative_tolerance: float = RELATIVE_TOLERANCE_DEFAULT,
    actual_layer: str | None = None,
    generated_layer: str | None = None,
) -> ComparisonResult:
    actual = read_polygon(actual_source, actual_layer)
    generated = read_polygon(generated_source, generated_layer)
    return compare_polygons(actual, generated, relative_tolerance)


def _source_label(source: str | Path | bytes) -> str:
    if isinstance(source, (str, Path)):
        return str(source)
    return "<dxf-bytes>"


def _load_dxf_text(source: str | Path | bytes) -> str:
    if isinstance(source, bytes):
        if source.startswith(b"AutoCAD Binary DXF"):
            raise ValueError("Binary DXF is not supported. Save as ASCII DXF and try again.")
        return source.decode("latin-1")
    path = Path(source)
    if not path.is_file():
        raise FileNotFoundError(f"DXF file not found: {path}")
    data = path.read_bytes()
    if data.startswith(b"AutoCAD Binary DXF"):
        raise ValueError("Binary DXF is not supported. Save as ASCII DXF and try again.")
    return data.decode("latin-1")


def _add_if_polygon(candidates: list[LayerPolygonCandidate], pts: list[Point2D], is_closed: bool, layer: str) -> None:
    pts = _strip_closing_duplicate(pts)
    if len(pts) < 3:
        return
    closed = is_closed or pts[0].distance_to(pts[-1]) <= ENDPOINT_TOLERANCE
    if not closed:
        return
    candidates.append(LayerPolygonCandidate(_strip_closing_duplicate(pts), layer or "0"))


def _strip_closing_duplicate(pts: list[Point2D]) -> list[Point2D]:
    if len(pts) >= 2 and pts[0].distance_to(pts[-1]) <= ENDPOINT_TOLERANCE:
        return pts[:-1]
    return pts


def try_build_polygon_from_segments(segments: list[tuple[Point2D, Point2D]]) -> list[Point2D] | None:
    rings = try_build_all_polygons_from_segments(segments)
    if not rings:
        return None
    return max(rings, key=lambda r: (abs(_signed_area(r)), len(r)))


def try_build_all_polygons_from_segments(segments: list[tuple[Point2D, Point2D]]) -> list[list[Point2D]]:
    unused = [(a, b) for a, b in segments if a.distance_to(b) > ENDPOINT_TOLERANCE]
    rings: list[list[Point2D]] = []
    while len(unused) >= 3:
        ring, unused = _extract_one_ring(unused)
        if ring is not None:
            rings.append(ring)
    return rings


def _extract_one_ring(
    segs: list[tuple[Point2D, Point2D]],
) -> tuple[list[Point2D] | None, list[tuple[Point2D, Point2D]]]:
    if not segs:
        return None, segs
    unused = list(segs)
    first = unused.pop(0)
    ring = [first[0], first[1]]
    while unused:
        tip = ring[-1]
        found = -1
        for i, (a, b) in enumerate(unused):
            if a.distance_to(tip) <= ENDPOINT_TOLERANCE or b.distance_to(tip) <= ENDPOINT_TOLERANCE:
                found = i
                break
        if found < 0:
            return None, segs[1:]
        a, b = unused.pop(found)
        nxt = b if a.distance_to(tip) <= ENDPOINT_TOLERANCE else a
        if nxt.distance_to(ring[0]) <= ENDPOINT_TOLERANCE:
            if len(ring) >= 3:
                return _strip_closing_duplicate(ring), unused
            return None, segs[1:]
        ring.append(nxt)
    if len(ring) >= 3 and ring[0].distance_to(ring[-1]) <= ENDPOINT_TOLERANCE:
        return _strip_closing_duplicate(ring), unused
    return None, segs[1:]


def _flatten_entity(entity, distance: float = 0.5) -> list[Point2D] | None:
    try:
        if hasattr(entity, "flattening"):
            pts = [Point2D(float(p.x), float(p.y)) for p in entity.flattening(distance)]
            return pts if len(pts) >= 2 else None
    except Exception:
        return None
    return None


def _circle_points(cx: float, cy: float, radius: float, count: int = 64) -> list[Point2D]:
    if radius <= ENDPOINT_TOLERANCE:
        return []
    return [
        Point2D(cx + radius * math.cos(2 * math.pi * i / count), cy + radius * math.sin(2 * math.pi * i / count))
        for i in range(count)
    ]


def _arc_points(cx: float, cy: float, radius: float, start_deg: float, end_deg: float, count: int = 24) -> list[Point2D]:
    if radius <= ENDPOINT_TOLERANCE:
        return []
    start = math.radians(start_deg)
    end = math.radians(end_deg)
    if end <= start:
        end += 2 * math.pi
    span = end - start
    steps = max(8, int(count * span / (2 * math.pi)))
    return [
        Point2D(cx + radius * math.cos(start + span * i / steps), cy + radius * math.sin(start + span * i / steps))
        for i in range(steps + 1)
    ]


def _read_with_ezdxf(text: str) -> list[LayerPolygonCandidate]:
    import io

    import ezdxf

    doc = ezdxf.read(io.StringIO(text))
    msp = doc.modelspace()
    candidates: list[LayerPolygonCandidate] = []
    lines_by_layer: dict[str, list[tuple[Point2D, Point2D]]] = {}

    def add_line(layer: str, start: Point2D, end: Point2D) -> None:
        if start.distance_to(end) > ENDPOINT_TOLERANCE:
            lines_by_layer.setdefault(layer, []).append((start, end))

    def add_chain(layer: str, pts: list[Point2D], closed: bool) -> None:
        if closed:
            _add_if_polygon(candidates, pts, True, layer)
            return
        for i in range(len(pts) - 1):
            add_line(layer, pts[i], pts[i + 1])

    def walk(entity) -> None:
        kind = entity.dxftype()
        layer = getattr(entity.dxf, "layer", "0") or "0"
        if kind == "INSERT":
            try:
                for virtual in entity.virtual_entities():
                    walk(virtual)
            except Exception:
                return
            return
        if kind == "LINE":
            s, e = entity.dxf.start, entity.dxf.end
            add_line(layer, Point2D(float(s.x), float(s.y)), Point2D(float(e.x), float(e.y)))
            return
        if kind == "CIRCLE":
            c = entity.dxf.center
            pts = _flatten_entity(entity) or _circle_points(float(c.x), float(c.y), float(entity.dxf.radius))
            add_chain(layer, pts, True)
            return
        if kind == "ARC":
            c = entity.dxf.center
            pts = _flatten_entity(entity) or _arc_points(
                float(c.x),
                float(c.y),
                float(entity.dxf.radius),
                float(entity.dxf.start_angle),
                float(entity.dxf.end_angle),
            )
            add_chain(layer, pts, False)
            return
        if kind in {"ELLIPSE", "SPLINE"}:
            pts = _flatten_entity(entity, 0.35)
            if pts:
                closed = pts[0].distance_to(pts[-1]) <= ENDPOINT_TOLERANCE or bool(
                    getattr(entity, "closed", False)
                )
                add_chain(layer, pts, closed)
            return
        if kind in {"SOLID", "TRACE", "3DFACE"}:
            pts: list[Point2D] = []
            for attr in ("vtx0", "vtx1", "vtx2", "vtx3"):
                if hasattr(entity.dxf, attr):
                    v = getattr(entity.dxf, attr)
                    pts.append(Point2D(float(v.x), float(v.y)))
            add_chain(layer, pts, True)
            return
        if kind in {"LWPOLYLINE", "POLYLINE"}:
            pts = _flatten_entity(entity, 0.35)
            if not pts:
                try:
                    pts = [Point2D(float(p[0]), float(p[1])) for p in entity.get_points("xy")]
                except Exception:
                    pts = []
                    if hasattr(entity, "vertices"):
                        for v in entity.vertices:
                            loc = v.dxf.location
                            pts.append(Point2D(float(loc.x), float(loc.y)))
            closed = bool(getattr(entity, "closed", False) or getattr(entity.dxf, "flags", 0) & 1)
            if pts:
                add_chain(layer, pts, closed)

    for entity in msp:
        walk(entity)

    for layer, segs in lines_by_layer.items():
        for ring in try_build_all_polygons_from_segments(segs):
            candidates.append(LayerPolygonCandidate(ring, layer))
    return candidates


# ---------------------------------------------------------------------------
# ASCII DXF parser (AsciiDxfParser.cs) — R12 and fallback
# ---------------------------------------------------------------------------

@dataclass(slots=True)
class _Transform2D:
    m11: float = 1.0
    m12: float = 0.0
    m21: float = 0.0
    m22: float = 1.0
    tx: float = 0.0
    ty: float = 0.0

    @staticmethod
    def identity() -> _Transform2D:
        return _Transform2D()

    @staticmethod
    def from_insert(x: float, y: float, rot_deg: float, sx: float, sy: float) -> _Transform2D:
        rad = rot_deg * math.pi / 180.0
        c, s = math.cos(rad), math.sin(rad)
        return _Transform2D(
            m11=sx * c,
            m12=-sy * s,
            m21=sx * s,
            m22=sy * c,
            tx=x,
            ty=y,
        )

    def compose(self, inner: _Transform2D) -> _Transform2D:
        return _Transform2D(
            m11=self.m11 * inner.m11 + self.m12 * inner.m21,
            m12=self.m11 * inner.m12 + self.m12 * inner.m22,
            m21=self.m21 * inner.m11 + self.m22 * inner.m21,
            m22=self.m21 * inner.m12 + self.m22 * inner.m22,
            tx=self.m11 * inner.tx + self.m12 * inner.ty + self.tx,
            ty=self.m21 * inner.tx + self.m22 * inner.ty + self.ty,
        )

    def apply(self, p: Point2D) -> Point2D:
        return Point2D(self.m11 * p.x + self.m12 * p.y + self.tx, self.m21 * p.x + self.m22 * p.y + self.ty)


def _parse_double(value: str) -> float:
    try:
        return float(value.strip().replace(",", "."))
    except ValueError:
        return 0.0


def _parse_int(value: str) -> int:
    try:
        return int(float(value.strip()))
    except ValueError:
        return 0


def _read_pairs_from_text(text: str) -> list[tuple[int, str]]:
    lines = text.splitlines()
    pairs: list[tuple[int, str]] = []
    i = 0
    while i < len(lines):
        code_line = lines[i].strip()
        i += 1
        if not code_line:
            continue
        try:
            code = int(code_line)
        except ValueError:
            continue
        if i >= len(lines):
            break
        pairs.append((code, lines[i].strip()))
        i += 1
    return pairs


def _read_ascii_candidates(text: str) -> list[LayerPolygonCandidate]:
    pairs = _read_pairs_from_text(text)
    entities, blocks = _parse_document(pairs)
    exploded: list[tuple] = []
    visiting: set[str] = set()
    for item in entities:
        _explode(item, _Transform2D.identity(), blocks, exploded, visiting, 0)

    candidates: list[LayerPolygonCandidate] = []
    segments_by_layer: dict[str, list[tuple[Point2D, Point2D]]] = {}
    for item in exploded:
        kind = item[0]
        if kind == "poly":
            _, pts, closed, layer = item
            if closed:
                _add_if_polygon(candidates, pts, True, layer)
            else:
                for i in range(len(pts) - 1):
                    a, b = pts[i], pts[i + 1]
                    if a.distance_to(b) > ENDPOINT_TOLERANCE:
                        segments_by_layer.setdefault(layer, []).append((a, b))
        elif kind == "line":
            _, a, b, layer = item
            if a.distance_to(b) > ENDPOINT_TOLERANCE:
                segments_by_layer.setdefault(layer, []).append((a, b))

    for layer, segs in segments_by_layer.items():
        for ring in try_build_all_polygons_from_segments(segs):
            candidates.append(LayerPolygonCandidate(ring, layer))
    return candidates


def _parse_document(pairs: list[tuple[int, str]]):
    entities: list[tuple] = []
    blocks: dict[str, list[tuple]] = {}
    current_section: str | None = None
    current_block: str | None = None
    block_items: list[tuple] | None = None
    target = entities

    entity = ""
    fields: dict[int, str] = {}
    lw_vertices: list[Point2D] = []
    poly_vertices: list[Point2D] = []
    poly_closed = False
    skip_polyline = False
    vx = vy = 0.0
    vertex_flags = 0
    in_vertex = False

    def flush_entity() -> None:
        nonlocal in_vertex, entity
        if in_vertex and not skip_polyline and _is_usable_vertex(vertex_flags):
            poly_vertices.append(Point2D(vx, vy))
        in_vertex = False
        if entity == "LWPOLYLINE":
            _commit_lw(fields, lw_vertices, target)
        elif entity == "LINE":
            _commit_line(fields, target)
        elif entity == "INSERT":
            _commit_insert(fields, target)
        elif entity == "CIRCLE":
            _commit_circle(fields, target)
        elif entity == "ARC":
            _commit_arc(fields, target)
        fields.clear()
        lw_vertices.clear()
        entity = ""

    def flush_polyline() -> None:
        nonlocal poly_closed, skip_polyline
        if not skip_polyline and len(poly_vertices) >= 3:
            layer = fields.get(8, "0")
            target.append(("poly", list(poly_vertices), poly_closed, layer))
        poly_vertices.clear()
        poly_closed = False
        skip_polyline = False

    for code, value in pairs:
        upper = value.upper()
        if code == 0:
            if upper == "VERTEX":
                if in_vertex and not skip_polyline and _is_usable_vertex(vertex_flags):
                    poly_vertices.append(Point2D(vx, vy))
                in_vertex = True
                vx = vy = 0.0
                vertex_flags = 0
                entity = "VERTEX"
                continue
            if upper == "SEQEND":
                if in_vertex and not skip_polyline and _is_usable_vertex(vertex_flags):
                    poly_vertices.append(Point2D(vx, vy))
                in_vertex = False
                if entity in {"POLYLINE", "VERTEX"}:
                    flush_polyline()
                entity = ""
                fields.clear()
                continue
            flush_entity()
            if upper == "SECTION":
                entity = "SECTION"
                continue
            if upper == "ENDSEC":
                current_section = None
                continue
            if upper == "BLOCK":
                entity = "BLOCK"
                continue
            if upper == "ENDBLK":
                if current_block is not None and block_items is not None:
                    blocks[current_block] = block_items
                current_block = None
                block_items = None
                target = entities
                continue
            if upper == "POLYLINE":
                entity = "POLYLINE"
                poly_vertices.clear()
                poly_closed = False
                skip_polyline = False
                fields.clear()
                continue
            entity = upper
            continue

        if entity == "SECTION" and code == 2:
            current_section = upper
            target = [] if current_section == "BLOCKS" else entities
            continue
        if entity == "BLOCK" and code == 2 and current_section == "BLOCKS":
            current_block = value.strip()
            block_items = []
            target = block_items
            continue

        if entity == "POLYLINE" and code == 70:
            flags = _parse_int(value)
            poly_closed = (flags & 1) != 0
            skip_polyline = (flags & 16) != 0 or (flags & 64) != 0
        elif entity == "POLYLINE":
            fields[code] = value
        elif entity == "VERTEX" and code == 10:
            vx = _parse_double(value)
        elif entity == "VERTEX" and code == 20:
            vy = _parse_double(value)
        elif entity == "VERTEX" and code == 70:
            vertex_flags = _parse_int(value)
        elif entity == "LWPOLYLINE" and code == 10:
            lw_vertices.append(Point2D(_parse_double(value), 0))
        elif entity == "LWPOLYLINE" and code == 20 and lw_vertices:
            last = lw_vertices[-1]
            lw_vertices[-1] = Point2D(last.x, _parse_double(value))
        elif entity in {"LWPOLYLINE", "LINE", "INSERT", "CIRCLE", "ARC"}:
            fields[code] = value

    flush_entity()
    if len(poly_vertices) >= 3:
        flush_polyline()
    return entities, blocks


def _commit_lw(fields: dict[int, str], vertices: list[Point2D], target: list[tuple]) -> None:
    if len(vertices) < 3:
        return
    flags = _parse_int(fields.get(70, "0"))
    layer = fields.get(8, "0")
    target.append(("poly", list(vertices), (flags & 1) != 0, layer))


def _commit_line(fields: dict[int, str], target: list[tuple]) -> None:
    if 10 not in fields or 20 not in fields or 11 not in fields or 21 not in fields:
        return
    layer = fields.get(8, "0")
    target.append(
        (
            "line",
            Point2D(_parse_double(fields[10]), _parse_double(fields[20])),
            Point2D(_parse_double(fields[11]), _parse_double(fields[21])),
            layer,
        )
    )


def _commit_circle(fields: dict[int, str], target: list[tuple]) -> None:
    if 10 not in fields or 20 not in fields or 40 not in fields:
        return
    layer = fields.get(8, "0")
    pts = _circle_points(_parse_double(fields[10]), _parse_double(fields[20]), _parse_double(fields[40]))
    if len(pts) >= 3:
        target.append(("poly", pts, True, layer))


def _commit_arc(fields: dict[int, str], target: list[tuple]) -> None:
    if 10 not in fields or 20 not in fields or 40 not in fields:
        return
    layer = fields.get(8, "0")
    pts = _arc_points(
        _parse_double(fields[10]),
        _parse_double(fields[20]),
        _parse_double(fields[40]),
        _parse_double(fields.get(50, "0")),
        _parse_double(fields.get(51, "0")),
    )
    if len(pts) >= 2:
        target.append(("poly", pts, False, layer))


def _commit_insert(fields: dict[int, str], target: list[tuple]) -> None:
    name = fields.get(2, "").strip()
    if not name:
        return
    x = _parse_double(fields.get(10, "0"))
    y = _parse_double(fields.get(20, "0"))
    sx = _parse_double(fields.get(41, "1")) if 41 in fields else 1.0
    sy = _parse_double(fields.get(42, "1")) if 42 in fields else 1.0
    rot = _parse_double(fields.get(50, "0"))
    layer = fields.get(8, "0")
    target.append(("insert", name, Point2D(x, y), rot, sx, sy, layer))


def _is_usable_vertex(flags: int) -> bool:
    return (flags & 1) == 0 and (flags & 16) == 0 and (flags & 128) == 0


def _explode(
    item: tuple,
    transform: _Transform2D,
    blocks: dict[str, list[tuple]],
    output: list[tuple],
    visiting: set[str],
    depth: int,
) -> None:
    if depth > 32:
        return
    kind = item[0]
    if kind == "poly":
        _, pts, closed, layer = item
        output.append(("poly", [transform.apply(p) for p in pts], closed, layer))
    elif kind == "line":
        _, a, b, layer = item
        output.append(("line", transform.apply(a), transform.apply(b), layer))
    elif kind == "insert":
        _, name, pos, rot, sx, sy, _layer = item
        children = blocks.get(name) or blocks.get(name.upper())
        if not children or name in visiting:
            return
        visiting.add(name)
        nested = transform.compose(_Transform2D.from_insert(pos.x, pos.y, rot, sx, sy))
        for child in children:
            _explode(child, nested, blocks, output, visiting, depth + 1)
        visiting.discard(name)


def run_self_test(samples_dir: str | Path) -> int:
    """Mirror Program.RunSelfTest shape cases against the sample DXF files."""
    samples_dir = Path(samples_dir)
    cases = [
        ("reference.dxf", True, False, "None", 0),
        ("translated.dxf", True, False, "None", 0),
        ("rotated-90.dxf", True, False, "None", 90),
        ("rotated-45.dxf", True, False, "None", 45),
        ("collinear-vertices.dxf", True, False, "None", 0),
        ("flipped-horizontal.dxf", True, True, "Horizontal", 0),
        ("flipped-vertical.dxf", True, True, "Vertical", 0),
        ("flipped-and-rotated.dxf", True, True, "Horizontal", 35),
        ("different-shape.dxf", False, False, "None", 0),
        ("reference-r12.dxf", True, False, "None", 0),
        ("rotated-90-r12.dxf", True, False, "None", 90),
        ("flipped-horizontal-r12.dxf", True, True, "Horizontal", 0),
    ]
    reference = samples_dir / "reference.dxf"
    failed = 0
    for name, match, flipped, flip_side, rotation in cases:
        result = compare_dxf_files(reference, samples_dir / name)
        ok = result.is_match == match
        if match:
            ok = ok and result.is_flipped == flipped
            ok = ok and result.flip_side == flip_side
            ok = ok and _angles_close(result.rotation_degrees_ccw, rotation)
        if not ok:
            failed += 1
        status = "PASS" if ok else "FAIL"
        print(
            f"{status:<4} {name:<26} match={result.is_match!s:<5} "
            f"flip={result.flip_side:<11} rot={result.rotation_degrees_ccw:7.2f}°  "
            f"{result.transform_summary}"
        )
    return failed


def _angles_close(actual: float, expected: float) -> bool:
    delta = abs(actual - expected) % 360.0
    if delta > 180:
        delta = 360 - delta
    return delta < 0.2
