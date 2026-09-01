"""SPIL DXF Validation web application."""

from __future__ import annotations

import csv
import io
import sys
import tempfile
from pathlib import Path

from flask import Flask, Response, jsonify, render_template, request

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

from analyse_db_shapes import (
    DEFAULT_UPLOADS_DIR,
    AnalysisQuery,
    get_shape_png,
    ping_database,
    resolve_uploads_dir,
    run_analysis,
)
from dxf_geometry import ComparisonResult, compare_dxf_files, polygon_points

app = Flask(__name__, template_folder="templates", static_folder="static")
app.config["MAX_CONTENT_LENGTH"] = 64 * 1024 * 1024
app.config["TEMPLATES_AUTO_RELOAD"] = True


def default_query() -> AnalysisQuery:
    return AnalysisQuery()


def query_from_request(data: dict | None = None) -> AnalysisQuery:
    data = data or {}
    order_index = data.get("order_index")
    shape_id = data.get("shape_id")
    limit = data.get("limit", 100)
    try:
        order_index = int(order_index) if order_index not in (None, "") else None
    except (TypeError, ValueError):
        order_index = None
    try:
        shape_id = int(shape_id) if shape_id not in (None, "") else None
    except (TypeError, ValueError):
        shape_id = None
    try:
        limit = int(limit) if limit not in (None, "") else 100
    except (TypeError, ValueError):
        limit = 100
    try:
        tolerance = float(data.get("tolerance") or 1e-4)
    except (TypeError, ValueError):
        tolerance = 1e-4
    return AnalysisQuery(
        from_date=data.get("from_date") or None,
        to_date=data.get("to_date") or None,
        order_index=order_index,
        order_num=(data.get("order_num") or "").strip() or None,
        shape_id=shape_id,
        only_custom=bool(data.get("only_custom")),
        only_flipped=bool(data.get("only_flipped")),
        limit=max(0, limit),
        tolerance=tolerance,
    )


def result_payload(result: ComparisonResult) -> dict:
    return {
        "match": result.is_match,
        "flipped": result.is_flipped,
        "flip_side": result.flip_side,
        "flip_description": result.flip_description,
        "rotation_ccw": round(result.rotation_degrees_ccw, 4),
        "rotation_cw": round(result.rotation_degrees_cw, 4),
        "transform": result.transform_summary,
        "fit_error": result.fit_error,
        "vertex_count": result.vertex_count,
        "message": result.message,
    }


def attach_outlines(row: dict) -> dict:
    actual_pts: list[dict] = []
    generated_pts: list[dict] = []
    if row.get("actual_dxf"):
        try:
            actual_pts = polygon_points(row["actual_dxf"], row.get("actual_layer") or "3")
        except Exception:
            try:
                actual_pts = polygon_points(row["actual_dxf"])
            except Exception:
                actual_pts = []
    if row.get("generated_dxf"):
        try:
            generated_pts = polygon_points(row["generated_dxf"])
        except Exception:
            generated_pts = []
    row["actual_points"] = actual_pts
    row["generated_points"] = generated_pts
    return row


def summarise(rows: list[dict]) -> dict:
    compared = [r for r in rows if r.get("status") == "ok"]
    return {
        "total": len(rows),
        "compared": len(compared),
        "matched": sum(1 for r in compared if r.get("match")),
        "flipped": sum(1 for r in compared if r.get("flipped")),
        "missing_actual": sum(1 for r in rows if r.get("status") == "missing_actual_dxf"),
        "missing_generated": sum(1 for r in rows if r.get("status") == "missing_generated_dxf"),
        "errors": sum(1 for r in rows if r.get("status") == "compare_error"),
    }


@app.get("/")
def index():
    return render_template("index.html")


@app.get("/api/status")
def api_status():
    try:
        info = ping_database(default_query())
        return jsonify(info)
    except Exception as ex:
        return jsonify({"ok": False, "error": str(ex), "server": "LT-25-010", "database": "KRISTAL"}), 503


@app.post("/api/analyse")
def api_analyse():
    query = query_from_request(request.get_json(silent=True) or {})
    try:
        rows = run_analysis(query)
    except Exception as ex:
        return jsonify({"ok": False, "error": str(ex)}), 500
    rows = [attach_outlines(row) for row in rows]
    return jsonify({"ok": True, "summary": summarise(rows), "rows": rows})


@app.post("/api/compare")
def api_compare():
    actual = request.files.get("actual")
    generated = request.files.get("generated")
    if actual is None or generated is None or not actual.filename or not generated.filename:
        return jsonify({"ok": False, "error": "Upload both an original DXF and a generated DXF."}), 400
    try:
        tolerance = float(request.form.get("tolerance") or 1e-4)
    except ValueError:
        tolerance = 1e-4

    with tempfile.TemporaryDirectory(prefix="spil-dxf-") as tmp:
        actual_path = Path(tmp) / ("actual_" + Path(actual.filename).name)
        generated_path = Path(tmp) / ("generated_" + Path(generated.filename).name)
        actual.save(actual_path)
        generated.save(generated_path)
        try:
            used_layer = "3"
            try:
                result = compare_dxf_files(
                    actual_path, generated_path, relative_tolerance=tolerance, actual_layer="3"
                )
                actual_pts = polygon_points(actual_path, "3")
            except ValueError:
                used_layer = None
                result = compare_dxf_files(actual_path, generated_path, relative_tolerance=tolerance)
                actual_pts = polygon_points(actual_path)
            generated_pts = polygon_points(generated_path)
        except Exception as ex:
            return jsonify({"ok": False, "error": str(ex)}), 400

    payload = result_payload(result)
    payload.update(
        {
            "ok": True,
            "actual_name": actual.filename,
            "generated_name": generated.filename,
            "actual_points": actual_pts,
            "generated_points": generated_pts,
            "actual_layer": used_layer,
        }
    )
    return jsonify(payload)


@app.get("/api/shape/<int:shape_id>/image")
def api_shape_image(shape_id: int):
    png = get_shape_png(default_query(), shape_id)
    if not png:
        return Response(status=404)
    return Response(png, mimetype="image/png")


@app.post("/api/export.csv")
def api_export_csv():
    query = query_from_request(request.get_json(silent=True) or {})
    try:
        rows = run_analysis(query)
    except Exception as ex:
        return jsonify({"ok": False, "error": str(ex)}), 500
    if not rows:
        return Response("No rows\n", mimetype="text/csv")
    skip = {"actual_points", "generated_points"}
    fieldnames = [k for k in rows[0].keys() if k not in skip]
    buf = io.StringIO()
    writer = csv.DictWriter(buf, fieldnames=fieldnames, extrasaction="ignore")
    writer.writeheader()
    writer.writerows(rows)
    return Response(
        buf.getvalue(),
        mimetype="text/csv",
        headers={"Content-Disposition": "attachment; filename=spil-dxf-analysis.csv"},
    )


def main() -> None:
    uploads = resolve_uploads_dir(Path(DEFAULT_UPLOADS_DIR))
    print("SPIL DXF Validation  ->  http://127.0.0.1:5080")
    print(f"DXF uploads         ->  {uploads}")
    app.run(host="127.0.0.1", port=5080, debug=False)


if __name__ == "__main__":
    main()
