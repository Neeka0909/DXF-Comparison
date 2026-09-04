# SPIL DXF Validation — Technical Documentation

**Version:** 2.0  
**Project:** SPIL DXF Validation  
**Last updated:** September 2026

The project has two tools that share the same flip/rotation compare logic:

| Tool | Stack | Purpose |
|---|---|---|
| **Web analyser** | Python 3, Flask | Load KRISTAL orders, pair original vs exported DXF, detect flip/rotation |
| **DxfCompare CLI** | .NET 9 | Compare two local DXF files, size check, edge service |

---

## 1. Overview

Production validation compares the **imported customer outline** with the **Opti/SPIL generated outline** and reports whether they are the same polygon under translation, rotation, and reflection.

Typical production pairing:

- Original: `{UniqueID}.dxf` **layer 3**
- Exported: `{UniqueID}optiDxf.dxf` (all layers; largest closed outline)

If the opti file is missing, the analyser falls back to **ShapeSAX** from the database.

### 1.1 Key capabilities

| Capability | Web / Python | C# CLI |
|---|---|---|
| Shape congruence (rotation + flip) | Yes | Yes |
| Prefer “not flipped” when a rectangle matches both ways | Yes | Yes |
| Closed polylines and LINE loops | Yes | Yes |
| Circles, arcs, ellipses, splines, bulged polylines | Yes (tessellated) | No (straight vertices only) |
| KRISTAL order analysis | Yes | No |
| UniqueID file matching in `dxf_uploads` | Yes | No |
| Size validation / edge service | No | Yes |
| Multi-layer read | Yes (original uses layer `3`) | Yes (`--layer`) |

### 1.2 Out of scope

- Scale differences (drawings must be the same scale)
- Multiple cut outlines on the same layer (largest area is used)
- Binary DXF files
- 3D geometry (only X/Y)

---

## 2. System architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Web UI  http://127.0.0.1:5080     web/app.py  Flask                    │
│  Order filters · results table · PNG + overlay · file-compare tab       │
└───────────────────────────────┬─────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  scripts/analyse_db_shapes.py                                           │
│  SQL Server KRISTAL · ShapeXML · UniqueID file match · ShapeSAX fallback│
└───────────────────────────────┬─────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  scripts/dxf_geometry.py          port of C# PolygonComparer            │
│  ezdxf + ASCII fallback · closed shapes · Procrustes flip/rotation      │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│  DxfCompare.exe (optional)        Program.cs                            │
│  Same compare core · size check · edge service · --self-test            │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.1 Project structure

```
SPIL DXF Validation/
├── TECHNICAL_DOCUMENTATION.md
├── Program.cs / DxfCompare.csproj     C# CLI
├── Geometry/  Dxf/  Comparison/       C# compare, DXF read, edge service
├── samples/                           Shared test DXF files
├── scripts/
│   ├── dxf_geometry.py                Compare + DXF reader (Python)
│   ├── analyse_db_shapes.py           KRISTAL pull, file match, CLI
│   └── requirements.txt
└── web/
    ├── app.py                         Flask app (port 5080)
    ├── templates/index.html
    └── static/app.js  app.css
```

### 2.2 Dependencies

| Package | Used by | Purpose |
|---|---|---|
| netDxf 2023.11.10 | C# | AutoCAD 2000+ DXF |
| pyodbc | Python | SQL Server |
| ezdxf | Python | DXF read, bulge flattening |
| flask | Python | Web UI |

ODBC Driver 17 (or 18) for SQL Server must be installed on the machine that runs the web app.

---

## 3. Web application

Start from the repo root:

```text
pip install -r scripts/requirements.txt
python web/app.py
```

Then open **http://127.0.0.1:5080**.

On startup the console prints the resolved uploads folder. Flask does not auto-reload Python modules; restart `python web/app.py` after changing `scripts/` or `web/app.py`. Templates/static pick up query-string cache busting after a browser refresh.

### 3.1 Order shapes tab

Default date range is the last 14 days. From/To use a calendar popup (native date pickers are hidden on the dark theme).

| Filter | Effect |
|---|---|
| From / To | Inclusive `spilInvNum.OrderDate` range |
| Order number | One value or comma-separated (`18543, 18473`) |
| Order index | `spilInvNum.OrderIndex` |
| Limit | Max rows (default 100, max 2000 in the UI) |
| DB flipped only | `FlippingSide` is not empty |
| Custom DXF (99) | `ShapeName = '99'` |

Results are sorted by order date, entered date/time, order index, glass line, then shape id.

**Stats:** shapes loaded, compared, matched, flipped (detected outline flip), missing original, missing export.

Click a row for:

- ShapePNG from the database
- Overlay of original (layer 3) vs generated outline
- Unique ID, DB/XML flip flags, detected transform, file paths

**Export CSV** uses the same filters as Analyse.

### 3.2 Compare files tab

Upload an original DXF and a generated DXF. The original is read on **layer 3** first; if that layer has no closed shape, all layers are used.

### 3.3 HTTP API

| Method | Path | Description |
|---|---|---|
| `GET` | `/` | UI |
| `GET` | `/api/status` | Database ping, date span, uploads folder and DXF count |
| `POST` | `/api/analyse` | JSON filters → summary + rows (including outline points) |
| `POST` | `/api/compare` | Multipart `actual` + `generated` files |
| `GET` | `/api/shape/<id>/image` | ShapePNG |
| `POST` | `/api/export.csv` | Same filters as analyse |

Analyse JSON body fields: `from_date`, `to_date`, `order_num`, `order_index`, `shape_id`, `limit`, `only_flipped`, `only_custom`, `tolerance`.

---

## 4. KRISTAL database

| Setting | Default | Override |
|---|---|---|
| Server | `LT-25-010` | `SPIL_SQL_SERVER` / `--server` |
| Database | `KRISTAL` | `SPIL_SQL_DATABASE` / `--database` |
| User | `sa` | `SPIL_SQL_USER` / `--user` |
| Password | `sa` | `SPIL_SQL_PASSWORD` / `--password` |

### 4.1 Tables

```
spilInvNumLines_ShapeDetails s
    INNER JOIN spilInvNum n ON n.OrderIndex = s.OrderIndex
    LEFT JOIN spilInvNumLines l ON l.iInvDetailID = s.iInvDetailID
```

| Source | Fields used |
|---|---|
| `spilInvNumLines_ShapeDetails` | `ID`, `OrderIndex`, `ShapeName`, `FlippingSide`, `Mirror`, `ShapeXML`, `ShapeSAX`, `ShapePNG`, `iInvDetailID` |
| `spilInvNum` | `OrderNum`, `OrderDate`, `EnteredDateTime` |
| `spilInvNumLines` | `GlassLineNo`, `ShapeFileName`, `iWidth`, `iHeight` |

Only rows with `ShapeXML IS NOT NULL` are analysed.

### 4.2 ShapeXML

Parsed fields include:

- `UniqueID` — file stem for original/export DXF (IGU panes may look like `{guid}_1`)
- `FlippingSide`, `Pattern/Flip`, `Pattern/Rotate`, `Pattern/Side`
- `ReturnDXF` / `ReturnDetailDXF`
- Shape name, order dimensions, border offsets

DB `FlippingSide` and XML flip flags are **production metadata**. Detected flip is from **outline geometry** (original vs generated DXF). They often agree, but a rectangle can be stored as “horizontally” in XML while the outlines are identical.

---

## 5. DXF file matching

### 5.1 Uploads folder

Default:

```text
D:\SPIL DATA\KRISTAL 2\dxf_uploads\dxf_uploads
```

Override: `SPIL_DXF_UPLOADS` or `--uploads-dir`.

If the configured path has no `.dxf` files but contains a child folder named `dxf_uploads`, that nested folder is used automatically (the parent `...\dxf_uploads` directory is often empty).

### 5.2 Naming

| Role | File | Layer |
|---|---|---|
| Original import | `{UniqueID}.dxf` | **3** |
| Opti export | `{UniqueID}optiDxf.dxf` | all (largest closed shape) |

Also accepted for export: `{UniqueID}_optiDxf.dxf`, `{UniqueID}-optiDxf.dxf`.

UniqueID candidates:

1. XML `UniqueID` as stored
2. Trailing `_n` stripped (IGU pane suffix)
3. `{base}_{GlassLineNo}`

### 5.3 Fallbacks

1. Original missing → `{OrderNum}-{GlassLineNo}.DXF` in SAX/DXF folders (`C:\SPIL\SPIL Glass\Shape\SAX Files`, `C:\SPIL\DXF`, RGT SAX folder). The uploads folder itself is not searched again for the old name.
2. Opti missing → write `ShapeSAX` to `scripts/_generated_dxf/{OrderNum}-{Line}_generated_id{ShapeID}.dxf` (`generated_source = shapesax`).

Status values: `ok`, `missing_actual_dxf`, `missing_generated_dxf`, `compare_error`.

---

## 6. Geometry comparison

Python `scripts/dxf_geometry.py` ports C# `PolygonComparer`. The C# CLI still uses the same core.

### 6.1 Normalization

1. Remove consecutive duplicates (`1e-8`)
2. Remove collinear vertices
3. Force counter-clockwise winding

### 6.2 Match

Default relative tolerance `1e-4`.

```
absTol    = max(1e-8, relativeTolerance × RMS radius)
lengthTol = max(1e-8, relativeTolerance × perimeter / vertexCount)
```

1. Cyclic shifts of B, same winding — edge lengths, then Procrustes rotation (no flip)
2. Cyclic shifts of B, reversed winding — edge lengths, then rotation after horizontal or vertical mirror
3. Match if RMSD ≤ `absTol`

Horizontal flip: `(x, y) → (-x, y)`. Vertical: `(x, y) → (x, -y)`.

### 6.3 Choosing flip vs no flip

A rectangle (and other symmetric outlines) can match **both** with ~0 error. The chooser:

1. Takes a candidate whose fit error is less than half of the other
2. Otherwise prefers the **smaller rotation**
3. If one result is flipped and one is not, **prefers not flipped**

That last rule is in both Python and C#. Without it, identical rectangles were reported as flipped.

A **true** mirror of a slightly non-rectangular quad still reports flipped: the unflipped fit fails the tight tolerance, the mirror fit does not.

### 6.4 Unequal vertex counts (Python only)

C# returns no match if vertex counts differ after cleanup.

Python resamples both closed outlines to 48–96 points along the perimeter (for circles vs polylines, or extra tessellation) and skips the edge-length gate. Flip vs no-flip still uses the rule in 6.3.

### 6.5 Python DXF reader

Try **ezdxf**, then the ASCII R12-style parser.

Closed geometry used:

- LWPOLYLINE / POLYLINE (including bulge flattening)
- Closed LINE loops (all rings; largest kept)
- CIRCLE, ARC (as segments of a loop), ELLIPSE, SPLINE
- SOLID / TRACE / 3DFACE
- INSERT exploded to virtual entities

Largest absolute area on the requested layer wins. Layer `3` matches `"3"` or numeric equivalents.

C# `DxfPolygonReader` still reads Polyline2D, Polyline3D, and LINE loops only (bulges treated as straight).

---

## 7. Python CLI

```text
python scripts/analyse_db_shapes.py --self-test
python scripts/analyse_db_shapes.py --from-date 2026-08-18 --to-date 2026-09-01 --only-custom --limit 100
python scripts/analyse_db_shapes.py --order-num 18158
python scripts/analyse_db_shapes.py --order-index 8709
python scripts/analyse_db_shapes.py --compare original.dxf generated.dxf
```

| Option | Description |
|---|---|
| `--from-date` / `--to-date` | Inclusive order dates |
| `--order-num` | One or comma-separated order numbers |
| `--order-index` | `OrderIndex` |
| `--shape-id` | `spilInvNumLines_ShapeDetails.ID` |
| `--only-custom` | Shape 99 |
| `--only-flipped` | DB `FlippingSide` set |
| `--limit` | Max rows (`0` = all) |
| `--uploads-dir` | UniqueID DXF folder |
| `--actual-dir` | Extra original-file search folder (repeatable) |
| `--extract-dir` | Where to write ShapeSAX DXF |
| `--csv` / `--json` | Write results |
| `--tolerance` | Relative geometry tolerance |
| `--self-test` | Sample DXF suite (includes rectangle vs itself) |
| `--compare A B` | Two local files, no database |

---

## 8. C# DxfCompare CLI

Unchanged entry point for local file compare, size check, and edge service.

```text
dotnet run -- <reference.dxf> <candidate.dxf> [options]
dotnet run -- --list-layers <file.dxf>
dotnet run -- --write-samples [folder]
dotnet run -- --self-test
```

### 8.1 Options

| Option | Alias | Default | Description |
|---|---|---|---|
| `--reference-layer <name>` | `--layer` | all layers | Layer on reference DXF |
| `--candidate-layer <name>` | — | all layers | Layer on candidate DXF |
| `--list-layers <file>` | — | — | List layers with closed polygons |
| `--edge-service <n>` | — | — | Edge service on all sides |
| `--edge-top/bottom/left/right <n>` | — | — | Per-side edge service |
| `--width <n>` | `-w` | — | Expected width |
| `--height <n>` | — | — | Expected height |
| `--ask-size` | — | off | Prompt for size after measuring |
| `--size-tolerance <n>` | — | `0` | Absolute size tolerance |
| `--tolerance <n>` | — | `0.0001` | Relative geometry tolerance |
| `--json` | — | off | JSON output |
| `--write-samples [dir]` | — | `./samples` | Generate sample DXF files |
| `--self-test` | — | — | Built-in tests |
| `--help` | `-h` | — | Usage |

Exit codes: `0` match, `1` no match / size fail, `2` bad args or file.

### 8.2 Edge service

Expand the **reference** polygon outward, then compare to a candidate that already includes edge service.

```
finalWidth  = W + left + right
finalHeight = H + top + bottom
```

Example: 100 × 50 with `--edge-service 2` → expected **104 × 54**.

Each edge is offset along its outward normal; new vertices are miter intersections.

### 8.3 Size validation

Axis-aligned bounding box. Direct match or swapped (90° rotation). Tolerance:

```
sizeTol = max(sizeToleranceAbs, relativeTolerance × max(expectedWidth, expectedHeight))
```

---

## 9. Build and run

### 9.1 Web analyser (current production UI)

```text
pip install -r scripts/requirements.txt
python web/app.py
```

Requires SQL Server ODBC driver and network access to `LT-25-010`.

### 9.2 C# CLI

```text
dotnet build
dotnet run -- --self-test
dotnet publish -c Release -r win-x64 --self-contained
```

Output: `bin/Release/net9.0/win-x64/publish/DxfCompare.exe`

---

## 10. Testing

### 10.1 Python

```text
python scripts/analyse_db_shapes.py --self-test
```

| Category | Tests |
|---|---|
| Shape match | Identical, translated, 45°, 90°, collinear vertices |
| Flip | Horizontal, vertical, flip + 35° |
| Negative | Different shape |
| R12 | Reference, rotated, flipped |
| Symmetric | `rect-100x50.dxf` compared to itself (must **not** be flipped) |

### 10.2 C#

```text
dotnet run -- --self-test
```

Adds size checks, edge service, multi-layer tests, and rectangle vs itself (same non-flip rule).

### 10.3 Sample files

Written by `dotnet run -- --write-samples` into `samples/`:

| File | Description |
|---|---|
| `reference.dxf` | L-shape 10 × 12 |
| `rotated-90.dxf` / `rotated-45.dxf` | Rotations |
| `flipped-horizontal.dxf` / `flipped-vertical.dxf` | Mirrors |
| `flipped-and-rotated.dxf` | Horizontal flip + 35° |
| `translated.dxf` | Same shape, moved |
| `collinear-vertices.dxf` | Extra collinear points |
| `different-shape.dxf` | Rectangle (should not match L-shape) |
| `*-r12.dxf` | AutoCAD 12 ASCII variants |
| `rect-100x50.dxf` | Rectangle for symmetry and edge-service tests |
| `rect-edge-all-2mm.dxf` | 104 × 54 |
| `rect-edge-top-2mm.dxf` | 100 × 52 |
| `multi-layer-3.dxf` | LAYER2 holds the L-shape |
| `single-layer-shape.dxf` / `-flipped.dxf` | Layer 0 L-shape |

---

## 11. Limitations

### 11.1 Geometry

- **C#:** straight edges only; bulges ignored.
- **Python:** curves are tessellated; vertex count may differ from C# on the same file.
- One polygon per layer (largest area).
- Same scale required.
- 2D only.

### 11.2 Files

- Binary DXF is not supported.
- ASCII block inserts exploded to depth 32.
- Original files must live under the nested uploads directory (or fallback SAX folders).

### 11.3 Flip reporting

- Detected flip is **outline congruence**, not coating/pattern side.
- XML/DB `FlippingSide` is shown separately in the drawer.
- Near-rectangles that are truly mirrored (corner Y values swapped) still report Horizontal/Vertical.

---

## 12. Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| Original DXF not found; path is parent `...\dxf_uploads` | Old process still running | Restart `python web/app.py`; confirm console shows nested `...\dxf_uploads\dxf_uploads` |
| Status shows `0 DXF in uploads` | Wrong folder | Nested folder should have thousands of `{guid}.dxf` files |
| Exported path is `scripts\_generated_dxf\...` | No `{UniqueID}optiDxf.dxf` | Copy opti files into uploads, or accept ShapeSAX fallback |
| No closed shape on layer 3 | Original has no layer 3 outline | File-compare tab falls back to all layers; order analyse does not |
| Rectangles all marked flipped | Old comparer | Restart app after the prefer-non-flip change; re-run Analyse |
| Date From/To has no calendar | Cached CSS/JS | Hard refresh (Ctrl+F5) |
| Database offline | ODBC / server | Install ODBC Driver 17; check `LT-25-010` |
| Analyse results look stale | Flask loaded old `dxf_geometry` | Restart the Python process |
| `Vertex counts differ` (C# only) | Different tessellation | Use the web/Python comparer, or match entity types |

---

## 13. Algorithm notes

### 13.1 Collinear removal

```
v1 = normalize(P[i] - P[i-1])
v2 = normalize(P[i+1] - P[i])
collinear = |v1 × v2| < tolerance  AND  v1 · v2 > 0
```

### 13.2 Procrustes rotation

```
angle = atan2(Σ (Ax By − Ay Bx), Σ (Ax Bx + Ay By))
```

Center both polygons at centroid first.

### 13.3 Edge service (C#)

Outward normal from CCW winding; offset by top/bottom/left/right; miter join at corners.

---

## 14. Glossary

| Term | Definition |
|---|---|
| **UniqueID** | GUID in ShapeXML; stem of the uploaded DXF file |
| **optiDxf** | Exported optimized DXF next to the original in `dxf_uploads` |
| **ShapeSAX** | ASCII DXF blob stored on the shape row; fallback when opti is missing |
| **Layer 3** | Production layer used for the original cut outline |
| **Congruent** | Same outline under rotation, translation, and reflection |
| **Detected flip** | Mirror required to overlay generated onto original |
| **DB / XML flip** | `FlippingSide` / Pattern flags from KRISTAL |
| **RMSD** | Root mean square deviation after alignment |
| **Procrustes fit** | Best rotation minimizing squared point distances |
| **R12 / AC1009** | AutoCAD Release 12 ASCII DXF |

---

*Document maintained for the SPIL DXF Validation project.*
