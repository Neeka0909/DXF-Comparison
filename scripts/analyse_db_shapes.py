"""Analyse SPIL DXF shapes from SQL Server for flip/rotation.

Pulls shape XML and generated DXF (ShapeSAX) from KRISTAL, locates the original
DXF on disk, then compares the two with the same polygon logic as DxfCompare.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import pyodbc

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from dxf_geometry import ComparisonResult, compare_dxf_files, run_self_test

ORIGINAL_DXF_LAYER = "3"

DEFAULT_SERVER = "LT-25-010"
DEFAULT_DATABASE = "KRISTAL"
DEFAULT_USER = "sa"
DEFAULT_PASSWORD = "sa"

DEFAULT_UPLOADS_DIR = r"D:\SPIL DATA\KRISTAL 2\dxf_uploads\dxf_uploads"

DEFAULT_ACTUAL_DIRS = [
    DEFAULT_UPLOADS_DIR,
    r"C:\SPIL\SPIL Glass\Shape\SAX Files",
    r"C:\SPIL\DXF",
    r"C:\SPIL\SPIL Glass RGT\Shape\SAX Files",
]

_TRAILING_PANE = re.compile(r"_\d+$")


@dataclass
class AnalysisQuery:
    from_date: str | None = None
    to_date: str | None = None
    order_index: int | None = None
    order_num: str | None = None
    shape_id: int | None = None
    only_custom: bool = False
    only_flipped: bool = False
    limit: int = 0
    server: str = DEFAULT_SERVER
    database: str = DEFAULT_DATABASE
    user: str = DEFAULT_USER
    password: str = DEFAULT_PASSWORD
    actual_dirs: list[str] | None = None
    uploads_dir: str = DEFAULT_UPLOADS_DIR
    extract_dir: str = ""
    tolerance: float = 1e-4


@dataclass
class XmlShapeInfo:
    unique_id: str = ""
    return_dxf: str = ""
    return_detail_dxf: str = ""
    flipping_side: str = ""
    pattern_flip: str = ""
    pattern_rotate: str = ""
    pattern_side: str = ""
    shape_name: str = ""
    shape_width: str = ""
    shape_height: str = ""
    border_top: float = 0.0
    border_bottom: float = 0.0
    border_left: float = 0.0
    border_right: float = 0.0
    order_width: str = ""
    order_height: str = ""


@dataclass
class ShapeRow:
    shape_id: int
    order_index: int
    order_num: str
    order_date: datetime | None
    entered_datetime: datetime | None
    inv_detail_id: int
    glass_line_no: int
    shape_name: str
    shape_file_name: str
    flipping_side: str
    mirror: bool
    width: float | None
    height: float | None
    xml_text: str
    generated_dxf: bytes
    xml: XmlShapeInfo


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compare original DXF files with generated SPIL DXF shapes and detect flip/rotation."
    )
    parser.add_argument("--server", default=os.getenv("SPIL_SQL_SERVER", DEFAULT_SERVER))
    parser.add_argument("--database", default=os.getenv("SPIL_SQL_DATABASE", DEFAULT_DATABASE))
    parser.add_argument("--user", default=os.getenv("SPIL_SQL_USER", DEFAULT_USER))
    parser.add_argument("--password", default=os.getenv("SPIL_SQL_PASSWORD", DEFAULT_PASSWORD))
    parser.add_argument("--from-date", dest="from_date", help="Inclusive start date (YYYY-MM-DD)")
    parser.add_argument("--to-date", dest="to_date", help="Inclusive end date (YYYY-MM-DD)")
    parser.add_argument("--order-index", type=int, help="Filter by spilInvNum.OrderIndex")
    parser.add_argument(
        "--order-num",
        help="Filter by order number (comma-separated for several, e.g. 18543,18473)",
    )
    parser.add_argument("--shape-id", type=int, help="Filter by spilInvNumLines_ShapeDetails.ID")
    parser.add_argument("--only-custom", action="store_true", help="Only shape 99 (uploaded DXF)")
    parser.add_argument("--only-flipped", action="store_true", help="Only rows with FlippingSide set in the database")
    parser.add_argument("--limit", type=int, default=0, help="Max rows to analyse (0 = all)")
    parser.add_argument(
        "--uploads-dir",
        default=os.getenv("SPIL_DXF_UPLOADS", DEFAULT_UPLOADS_DIR),
        help=r"Folder with {UniqueID}.dxf originals and {UniqueID}optiDxf.dxf exports",
    )
    parser.add_argument(
        "--actual-dir",
        action="append",
        dest="actual_dirs",
        help="Extra fallback folder for original DXF files (repeatable)",
    )
    parser.add_argument(
        "--extract-dir",
        default="",
        help="Write generated ShapeSAX DXF files here (default: scripts/_generated_dxf)",
    )
    parser.add_argument("--csv", dest="csv_path", help="Write results to this CSV file")
    parser.add_argument("--json", dest="json_path", help="Write results to this JSON file")
    parser.add_argument("--tolerance", type=float, default=1e-4, help="Relative geometry tolerance")
    parser.add_argument("--self-test", action="store_true", help="Run sample DXF tests and exit")
    parser.add_argument("--samples", default="", help="Folder of sample DXF files for --self-test")
    parser.add_argument(
        "--compare",
        nargs=2,
        metavar=("ACTUAL.dxf", "GENERATED.dxf"),
        help="Compare two local DXF files without using the database",
    )
    return parser.parse_args(argv)


def connect(args: argparse.Namespace) -> pyodbc.Connection:
    drivers = pyodbc.drivers()
    preferred = [
        "ODBC Driver 17 for SQL Server",
        "ODBC Driver 18 for SQL Server",
        "SQL Server Native Client 11.0",
        "SQL Server",
    ]
    driver = next((d for d in preferred if d in drivers), None)
    if driver is None:
        raise RuntimeError(f"No SQL Server ODBC driver found. Installed: {drivers}")
    extra = "TrustServerCertificate=yes;" if "18" in driver or "17" in driver else ""
    conn_str = (
        f"DRIVER={{{driver}}};SERVER={args.server};DATABASE={args.database};"
        f"UID={args.user};PWD={args.password};{extra}"
    )
    return pyodbc.connect(conn_str)


def parse_xml_float(raw: str | None) -> float:
    if not raw:
        return 0.0
    text = raw.strip().replace(" ", "").replace(",", ".")
    try:
        return float(text)
    except ValueError:
        return 0.0


def _elem_text(parent: ET.Element | None, path: str) -> str:
    if parent is None:
        return ""
    node = parent.find(path)
    if node is None:
        return ""
    if node.get("value"):
        return (node.get("value") or "").strip()
    child = node.find("value")
    if child is not None:
        attr = (child.get("value") or "").strip()
        if attr:
            return attr
        return (child.text or "").strip()
    return (node.text or "").strip()


def parse_shape_xml(xml_text: str) -> XmlShapeInfo:
    info = XmlShapeInfo()
    if not xml_text or not xml_text.strip():
        return info
    try:
        root = ET.fromstring(xml_text)
    except ET.ParseError:
        try:
            root = ET.fromstring(xml_text.encode("utf-16"))
        except Exception:
            return info

    info.unique_id = _elem_text(root, "UniqueID")
    ret = root.find("ReturnDXF")
    if ret is not None:
        info.return_dxf = (ret.get("target") or "").strip()
    detail = root.find("ReturnDetailDXF")
    if detail is not None:
        info.return_detail_dxf = (detail.get("target") or "").strip()

    info.flipping_side = _elem_text(root, "FlippingSide")
    pattern = root.find("Pattern")
    if pattern is not None:
        info.pattern_rotate = _elem_text(pattern, "Rotate")
        info.pattern_flip = _elem_text(pattern, "Flip")
        info.pattern_side = _elem_text(pattern, "Side")

    compact = root.find("pattern")
    if compact is not None:
        if not info.pattern_flip:
            info.pattern_flip = (compact.get("flip") or "").strip()
        if not info.pattern_rotate:
            info.pattern_rotate = (compact.get("rotate") or "").strip()

    dim = root.find("OrderEntryDimention")
    if dim is not None:
        info.order_width = dim.get("width") or ""
        info.order_height = dim.get("height") or ""

    shape = root.find(".//Shape")
    if shape is not None:
        name = shape.find("Name")
        info.shape_name = (name.get("value") if name is not None else "") or (shape.get("name") or "")
        info.shape_width = shape.get("width") or ""
        info.shape_height = shape.get("height") or ""
        methods = shape.find("Methods")
        border = None if methods is None else methods.find("Border")
        if border is None:
            border = root.find("Borders") or root.find("Border") or root.find(".//Border")
        if border is not None:
            info.border_top = parse_xml_float(border.get("TOP") or border.get("top"))
            info.border_bottom = parse_xml_float(border.get("BOTTOM") or border.get("bottom"))
            info.border_left = parse_xml_float(border.get("LEFT") or border.get("left"))
            info.border_right = parse_xml_float(border.get("RIGHT") or border.get("right"))
    return info


def fetch_shapes(conn: pyodbc.Connection, args: argparse.Namespace) -> list[ShapeRow]:
    clauses = ["s.ShapeXML IS NOT NULL"]
    params: list[object] = []
    if args.from_date:
        clauses.append("n.OrderDate >= ?")
        params.append(args.from_date)
    if args.to_date:
        clauses.append("n.OrderDate < DATEADD(day, 1, CAST(? AS date))")
        params.append(args.to_date)
    if args.order_index is not None:
        clauses.append("n.OrderIndex = ?")
        params.append(args.order_index)
    if args.order_num:
        nums = [n.strip() for n in args.order_num.split(",") if n.strip()]
        if len(nums) == 1:
            clauses.append("n.OrderNum = ?")
            params.append(nums[0])
        elif nums:
            placeholders = ", ".join("?" for _ in nums)
            clauses.append(f"n.OrderNum IN ({placeholders})")
            params.extend(nums)
    if args.shape_id is not None:
        clauses.append("s.ID = ?")
        params.append(args.shape_id)
    if args.only_custom:
        clauses.append("s.ShapeName = '99'")
    if args.only_flipped:
        clauses.append("ISNULL(s.FlippingSide, '') <> ''")

    limit_sql = f"TOP ({int(args.limit)}) " if args.limit and args.limit > 0 else ""
    sql = f"""
        SELECT {limit_sql}
            s.ID,
            s.OrderIndex,
            n.OrderNum,
            n.OrderDate,
            n.EnteredDateTime,
            s.iInvDetailID,
            ISNULL(l.GlassLineNo, 0) AS GlassLineNo,
            s.ShapeName,
            ISNULL(l.ShapeFileName, '') AS ShapeFileName,
            ISNULL(s.FlippingSide, '') AS FlippingSide,
            s.Mirror,
            l.iWidth,
            l.iHeight,
            s.ShapeXML,
            s.ShapeSAX
        FROM spilInvNumLines_ShapeDetails s
        INNER JOIN spilInvNum n ON n.OrderIndex = s.OrderIndex
        LEFT JOIN spilInvNumLines l ON l.iInvDetailID = s.iInvDetailID
        WHERE {' AND '.join(clauses)}
        ORDER BY n.OrderDate, n.EnteredDateTime, n.OrderIndex, l.GlassLineNo, s.ID
    """
    cur = conn.cursor()
    cur.execute(sql, params)
    rows: list[ShapeRow] = []
    for rec in cur.fetchall():
        xml_text = rec.ShapeXML or ""
        sax = rec.ShapeSAX
        generated = bytes(sax) if sax is not None else b""
        rows.append(
            ShapeRow(
                shape_id=int(rec.ID),
                order_index=int(rec.OrderIndex),
                order_num=str(rec.OrderNum or "").strip(),
                order_date=rec.OrderDate,
                entered_datetime=rec.EnteredDateTime,
                inv_detail_id=int(rec.iInvDetailID or 0),
                glass_line_no=int(rec.GlassLineNo or 0),
                shape_name=str(rec.ShapeName or "").strip(),
                shape_file_name=str(rec.ShapeFileName or "").strip(),
                flipping_side=str(rec.FlippingSide or "").strip(),
                mirror=bool(rec.Mirror),
                width=float(rec.iWidth) if rec.iWidth is not None else None,
                height=float(rec.iHeight) if rec.iHeight is not None else None,
                xml_text=xml_text,
                generated_dxf=generated,
                xml=parse_shape_xml(xml_text),
            )
        )
    return rows


def unique_id_candidates(row: ShapeRow) -> list[str]:
    raw = (row.xml.unique_id or "").strip()
    keys: list[str] = []

    def add(value: str) -> None:
        value = value.strip()
        if value and value not in keys:
            keys.append(value)

    add(raw)
    if raw:
        base = _TRAILING_PANE.sub("", raw)
        add(base)
        if row.glass_line_no:
            add(f"{base}_{row.glass_line_no}")
    return keys


def _is_opti_stem(stem: str) -> bool:
    compact = stem.lower().replace(" ", "").replace("_", "").replace("-", "")
    return "optidxf" in compact


def resolve_uploads_dir(configured: Path) -> Path:
    """Use nested dxf_uploads if the configured folder only contains that subfolder."""
    if not configured.is_dir():
        nested = configured / "dxf_uploads"
        return nested if nested.is_dir() else configured
    nested = configured / "dxf_uploads"
    if nested.is_dir():
        try:
            has_dxf_here = any(p.is_file() and p.suffix.lower() == ".dxf" for p in configured.iterdir())
        except OSError:
            has_dxf_here = False
        if not has_dxf_here:
            return nested
    return configured


def _dxf_named(folder: Path, stem: str) -> Path | None:
    if not stem:
        return None
    path = folder / f"{stem}.dxf"
    if path.is_file():
        return path
    return None


def _list_dxf(folder: Path) -> list[Path]:
    try:
        return [p for p in folder.iterdir() if p.is_file() and p.suffix.lower() == ".dxf"]
    except OSError:
        return []


def find_original_by_unique_id(row: ShapeRow, uploads_dir: Path) -> Path | None:
    if not uploads_dir.is_dir():
        return None
    for uid in unique_id_candidates(row):
        path = _dxf_named(uploads_dir, uid)
        if path is not None and not _is_opti_stem(path.stem):
            return path
    return None


def find_exported_opti_dxf(row: ShapeRow, uploads_dir: Path) -> Path | None:
    if not uploads_dir.is_dir():
        return None
    ids = unique_id_candidates(row)
    if not ids:
        return None
    for uid in ids:
        for glue in ("", "_", "-", " "):
            path = _dxf_named(uploads_dir, f"{uid}{glue}optiDxf")
            if path is not None:
                return path
            path = _dxf_named(uploads_dir, f"{uid}{glue}optidxf")
            if path is not None:
                return path
    return None


def candidate_actual_names(row: ShapeRow) -> list[str]:
    order = row.order_num.strip()
    line = row.glass_line_no if row.glass_line_no else 1
    return [
        f"{order}-{line}.DXF",
        f"{order}-{line}.dxf",
        f"#{order}-{line}.DXF",
        f"#{order}-{line}.dxf",
        f"{order}-{line} - Copy.DXF",
    ]


def find_actual_dxf(row: ShapeRow, search_dirs: list[Path]) -> Path | None:
    names = candidate_actual_names(row)
    wanted = {n.lower() for n in names}
    for folder in search_dirs:
        if not folder.is_dir():
            continue
        for name in names:
            path = folder / name
            if path.is_file():
                return path
        try:
            for entry in folder.iterdir():
                if entry.is_file() and entry.name.lower() in wanted:
                    return entry
        except OSError:
            continue
    return None


def write_generated_dxf(row: ShapeRow, extract_dir: Path) -> Path | None:
    if not row.generated_dxf:
        return None
    extract_dir.mkdir(parents=True, exist_ok=True)
    line = row.glass_line_no if row.glass_line_no else 1
    name = f"{row.order_num}-{line}_generated_id{row.shape_id}.dxf"
    path = extract_dir / name
    path.write_bytes(row.generated_dxf)
    return path


def resolve_dxf_files(
    row: ShapeRow,
    uploads_dir: Path,
    search_dirs: list[Path],
    extract_dir: Path,
) -> tuple[Path | None, Path | None, str]:
    actual = find_original_by_unique_id(row, uploads_dir)
    exported = find_exported_opti_dxf(row, uploads_dir)
    generated_source = "optiDxf" if exported else ""

    if actual is None:
        fallback_dirs = [d for d in search_dirs if d.resolve() != uploads_dir.resolve()] if uploads_dir.exists() else search_dirs
        actual = find_actual_dxf(row, fallback_dirs)

    generated = exported
    if generated is None:
        generated = write_generated_dxf(row, extract_dir)
        if generated is not None:
            generated_source = "shapesax"

    return actual, generated, generated_source


def analyse_row(
    row: ShapeRow,
    search_dirs: list[Path],
    extract_dir: Path,
    tolerance: float,
    uploads_dir: Path,
) -> dict:
    actual_path, generated_path, generated_source = resolve_dxf_files(
        row, uploads_dir, search_dirs, extract_dir
    )
    xml_flip = row.xml.flipping_side or row.flipping_side
    unique_id = row.xml.unique_id
    uid_hint = (unique_id_candidates(row) or [""])[0]
    result: ComparisonResult | None = None
    status = "ok"
    error = ""

    if actual_path is None:
        status = "missing_actual_dxf"
        error = (
            f"Original DXF not found (expected {uid_hint}.dxf layer {ORIGINAL_DXF_LAYER} in {uploads_dir})"
            if uid_hint
            else f"Original DXF not found in {uploads_dir}"
        )
    elif generated_path is None:
        status = "missing_generated_dxf"
        error = (
            f"Exported DXF not found (expected {uid_hint}optiDxf.dxf in {uploads_dir})"
            if uid_hint
            else "Exported optiDxf / ShapeSAX is missing"
        )
    else:
        try:
            result = compare_dxf_files(
                actual_path,
                generated_path,
                relative_tolerance=tolerance,
                actual_layer=ORIGINAL_DXF_LAYER,
            )
        except Exception as ex:
            status = "compare_error"
            error = str(ex)

    payload = {
        "shape_id": row.shape_id,
        "order_index": row.order_index,
        "order_num": row.order_num,
        "order_date": row.order_date.isoformat(sep=" ") if row.order_date else "",
        "entered_datetime": row.entered_datetime.isoformat(sep=" ") if row.entered_datetime else "",
        "glass_line_no": row.glass_line_no,
        "unique_id": unique_id,
        "shape_name": row.shape_name or row.xml.shape_name,
        "db_flipping_side": row.flipping_side,
        "xml_flipping_side": xml_flip,
        "xml_pattern_flip": row.xml.pattern_flip,
        "xml_pattern_rotate": row.xml.pattern_rotate,
        "xml_pattern_side": row.xml.pattern_side,
        "mirror": row.mirror,
        "width": row.width,
        "height": row.height,
        "actual_dxf": str(actual_path) if actual_path else "",
        "actual_layer": ORIGINAL_DXF_LAYER if actual_path else "",
        "generated_dxf": str(generated_path) if generated_path else "",
        "generated_source": generated_source,
        "return_dxf_xml": row.xml.return_dxf,
        "status": status,
        "error": error,
        "match": result.is_match if result else False,
        "flipped": result.is_flipped if result else False,
        "flip_side": result.flip_side if result else "",
        "flip_description": result.flip_description if result else "",
        "rotation_ccw": round(result.rotation_degrees_ccw, 4) if result else None,
        "rotation_cw": round(result.rotation_degrees_cw, 4) if result else None,
        "transform": result.transform_summary if result else "",
        "fit_error": result.fit_error if result else None,
        "vertex_count": result.vertex_count if result else 0,
        "message": result.message if result else error,
    }
    return payload


def run_analysis(query: AnalysisQuery) -> list[dict]:
    conn = connect(query)
    try:
        rows = fetch_shapes(conn, query)
    finally:
        conn.close()

    uploads_dir = resolve_uploads_dir(Path(query.uploads_dir or DEFAULT_UPLOADS_DIR))
    search_dirs = [Path(p) for p in (query.actual_dirs or DEFAULT_ACTUAL_DIRS)]
    extract_dir = Path(query.extract_dir) if query.extract_dir else SCRIPT_DIR / "_generated_dxf"
    return [
        analyse_row(row, search_dirs, extract_dir, query.tolerance, uploads_dir)
        for row in rows
    ]


def get_shape_png(query: AnalysisQuery, shape_id: int) -> bytes | None:
    conn = connect(query)
    try:
        cur = conn.cursor()
        cur.execute("SELECT ShapePNG FROM spilInvNumLines_ShapeDetails WHERE ID=?", (shape_id,))
        rec = cur.fetchone()
        if rec is None or rec[0] is None:
            return None
        return bytes(rec[0])
    finally:
        conn.close()


def ping_database(query: AnalysisQuery) -> dict:
    conn = connect(query)
    try:
        cur = conn.cursor()
        cur.execute(
            """
            SELECT
                COUNT(*) AS shapes,
                MIN(n.OrderDate) AS first_date,
                MAX(n.OrderDate) AS last_date
            FROM spilInvNumLines_ShapeDetails s
            INNER JOIN spilInvNum n ON n.OrderIndex = s.OrderIndex
            """
        )
        rec = cur.fetchone()
        uploads = resolve_uploads_dir(Path(query.uploads_dir or DEFAULT_UPLOADS_DIR))
        upload_count = 0
        if uploads.is_dir():
            upload_count = len(_list_dxf(uploads))
        return {
            "ok": True,
            "server": query.server,
            "database": query.database,
            "shapes": int(rec.shapes or 0),
            "first_date": rec.first_date.strftime("%Y-%m-%d") if rec.first_date else "",
            "last_date": rec.last_date.strftime("%Y-%m-%d") if rec.last_date else "",
            "uploads_dir": str(uploads),
            "uploads_files": upload_count,
        }
    finally:
        conn.close()


def print_table(results: list[dict]) -> None:
    if not results:
        print("No shape rows matched the filter.")
        return
    header = (
        f"{'Date':<17} {'Order':<8} {'Ln':<3} {'ID':<6} {'Shape':<5} "
        f"{'DB flip':<13} {'Detected':<11} {'Rot':>8} {'Match':<6} Status"
    )
    print(header)
    print("-" * len(header))
    for r in results:
        date = (r["order_date"] or "")[:16]
        detected = r["flip_side"] if r["status"] == "ok" else "-"
        rot = f"{r['rotation_ccw']:.1f}" if r["rotation_ccw"] is not None and r["status"] == "ok" else "-"
        match = "yes" if r["match"] else "no" if r["status"] == "ok" else "-"
        print(
            f"{date:<17} {r['order_num']:<8} {r['glass_line_no']:<3} {r['shape_id']:<6} "
            f"{str(r['shape_name']):<5} {str(r['db_flipping_side'] or '-'):<13} "
            f"{str(detected):<11} {rot:>8} {match:<6} {r['status']}"
        )
        if r["status"] == "ok":
            print(f"    {r['transform']}")
            print(f"    actual={r['actual_dxf']}")
            print(f"    generated={r['generated_dxf']}")
        elif r["error"]:
            print(f"    {r['error']}")
            if r["generated_dxf"]:
                print(f"    generated={r['generated_dxf']}")

    compared = [r for r in results if r["status"] == "ok"]
    flipped = [r for r in compared if r["flipped"]]
    missing = [r for r in results if r["status"] == "missing_actual_dxf"]
    print()
    print(
        f"Rows: {len(results)}  compared: {len(compared)}  "
        f"flipped: {len(flipped)}  original DXF missing: {len(missing)}"
    )


def write_csv(path: Path, results: list[dict]) -> None:
    if not results:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=list(results[0].keys()))
        writer.writeheader()
        writer.writerows(results)


def repo_samples_dir() -> Path:
    return Path(__file__).resolve().parents[1] / "samples"


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)

    if args.self_test:
        samples = Path(args.samples) if args.samples else repo_samples_dir()
        if not samples.is_dir():
            print(f"Sample folder not found: {samples}", file=sys.stderr)
            return 2
        failed = run_self_test(samples)
        print()
        print("All tests passed." if failed == 0 else f"{failed} tests failed.")
        return 0 if failed == 0 else 1

    if args.compare:
        actual, generated = args.compare
        try:
            result = compare_dxf_files(actual, generated, relative_tolerance=args.tolerance)
        except Exception as ex:
            print(f"Error: {ex}", file=sys.stderr)
            return 2
        print("DXF polygon comparison")
        print("-" * 40)
        print(f"Actual    : {Path(actual).resolve()}")
        print(f"Generated : {Path(generated).resolve()}")
        print(f"Result    : {'MATCH' if result.is_match else 'NO MATCH'}")
        print(f"Vertices  : {result.vertex_count}")
        print(f"Transform : {result.transform_summary}")
        print(f"Flipped   : {'Yes' if result.is_flipped else 'No'}")
        print(f"Flip side : {result.flip_description}")
        print(f"Rotation  : {result.rotation_degrees_ccw:.2f}° CCW  ({result.rotation_degrees_cw:.2f}° CW)")
        print(f"Fit error : {result.fit_error:G}")
        print(f"Details   : {result.message}")
        return 0 if result.is_match else 1

    query = AnalysisQuery(
        from_date=args.from_date,
        to_date=args.to_date,
        order_index=args.order_index,
        order_num=args.order_num,
        shape_id=args.shape_id,
        only_custom=args.only_custom,
        only_flipped=args.only_flipped,
        limit=args.limit,
        server=args.server,
        database=args.database,
        user=args.user,
        password=args.password,
        actual_dirs=args.actual_dirs,
        uploads_dir=args.uploads_dir or DEFAULT_UPLOADS_DIR,
        extract_dir=args.extract_dir or "",
        tolerance=args.tolerance,
    )
    try:
        results = run_analysis(query)
    except Exception as ex:
        print(f"Database connection failed: {ex}", file=sys.stderr)
        return 2
    print_table(results)

    if args.csv_path:
        write_csv(Path(args.csv_path), results)
        print(f"CSV written: {args.csv_path}")
    if args.json_path:
        Path(args.json_path).write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(f"JSON written: {args.json_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
