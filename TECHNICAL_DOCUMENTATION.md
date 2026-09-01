# DxfCompare — Technical Documentation

**Version:** 1.1  
**Project:** SPIL DXF Validation  
**Platform:** .NET 9.0  
**Last updated:** September 2026

---

## 1. Overview

**DxfCompare** is a command-line application that validates whether two DXF files contain the same single 2D polygon outline, even when one drawing is rotated, flipped (reflected), or translated relative to the other.

The tool also supports:

- **Size validation** — compare measured width/height against expected dimensions
- **Edge service** — expand the base shape outward per side before comparing to an edge-service DXF
- **Multi-layer DXF** — select which layer to read from a reference file that contains shapes on multiple layers

### 1.1 Key capabilities

| Capability | Description |
|---|---|
| Shape congruence | Determines if two polygons are geometrically identical under rotation and reflection |
| Transform detection | Reports rotation angle (CCW/CW) and flip direction (horizontal or vertical) |
| Size validation | Compares measured bounding-box dimensions against user-supplied width and height |
| Edge service | Expands base shape per side (all-around or top/bottom/left/right) before comparison |
| Layer selection | Reads a specific layer from multi-layer reference or candidate DXF files |
| Layer listing | Lists all layers that contain closed polygon geometry |
| Legacy DXF support | Reads ASCII AutoCAD R12 (AC1009) files via a built-in parser |
| Modern DXF support | Reads AutoCAD 2000+ files via the **netDxf** library |
| Collinear cleanup | Removes duplicate and collinear vertices before comparison |

### 1.2 Out of scope

- Scale differences (shapes must be drawn at the same scale)
- Multiple shapes on the same layer (only the largest closed polygon on the selected layer is used)
- Curved edges (arcs, splines, bulged polyline segments are not supported)
- Binary DXF files
- 3D geometry (only X/Y coordinates are used)

---

## 2. System architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                            Program.cs (CLI)                              │
│   Argument parsing · Layer/edge options · Output · Self-test runner    │
└───────┬──────────────────────────────┬───────────────────────────────────┘
        │                              │
        ▼                              ▼
┌───────────────────────┐    ┌───────────────────────────────────────────┐
│   DxfPolygonReader    │    │              Comparison layer               │
│  ┌─────────────────┐  │    │  ┌─────────────────┐  ┌─────────────────┐  │
│  │ netDxf (AC2000+)│  │    │  │ PolygonComparer │  │ EdgeService     │  │
│  └─────────────────┘  │    │  │ Shape + transform│  │ Applicator      │  │
│  ┌─────────────────┐  │    │  └─────────────────┘  └─────────────────┘  │
│  │ AsciiDxfParser  │  │    │  ┌─────────────────┐  ┌─────────────────┐  │
│  │ (R12 / fallback)│  │    │  │ SizeCheck       │  │ EdgeService     │  │
│  └─────────────────┘  │    │  │ Width/height    │  │ Config          │  │
│  ListLayers()         │    │  └─────────────────┘  └─────────────────┘  │
└───────────┬───────────┘    └───────────────────────────────────────────┘
            ▼
┌───────────────────────┐
│  Geometry / Point2D   │
└───────────────────────┘
```

### 2.1 Project structure

```
SPIL DXF Validation/
├── Program.cs                      Entry point, CLI, output, self-tests
├── DxfCompare.csproj               .NET 9 project file
├── TECHNICAL_DOCUMENTATION.md      This document
├── Geometry/
│   └── Point2D.cs                  2D point struct with vector math
├── Dxf/
│   ├── DxfPolygonReader.cs         Unified DXF reader (netDxf + fallback)
│   ├── AsciiDxfParser.cs           ASCII DXF parser for R12 and legacy files
│   ├── DxfLayerSummary.cs          Layer listing model
│   └── SampleDxfFactory.cs         Sample file generator for testing
├── Comparison/
│   ├── PolygonComparer.cs          Congruence detection and transform fit
│   ├── SizeCheck.cs                Bounding-box size validation
│   ├── EdgeServiceConfig.cs        Per-side edge service values
│   ├── EdgeServiceApplicator.cs    Polygon outward expansion
│   └── ComparisonResult.cs         Shape comparison result model
└── samples/                        Generated test DXF files
```

### 2.2 Dependencies

| Package | Version | Purpose |
|---|---|---|
| netDxf | 2023.11.10 | Read/write DXF files (AutoCAD 2000 and newer) |

No other third-party libraries are used.

---

## 3. DXF input requirements

Each DXF file must contain at least **one closed polygon** on the selected layer (or on any layer if none is specified). Geometry is extracted in this order:

1. **Polyline2D** (lightweight polyline) — preferred
2. **Polyline3D** — X/Y projected to 2D
3. **Closed loop of Line entities** — stitched into a polygon if endpoints connect

If multiple closed polygons exist on the same layer, the one with the **largest absolute area** is selected.

### 3.1 Multi-layer DXF files

When a reference DXF contains shapes on several layers (e.g. layout, dimensions, cut profile), use `--reference-layer` / `--layer` to select which layer holds the shape to compare.

```text
dotnet run -- --list-layers reference.dxf
dotnet run -- reference.dxf candidate.dxf --layer LAYER2
```

| Scenario | Command |
|---|---|
| Reference has 3 layers, shape on LAYER2 | `--layer LAYER2` on reference |
| Candidate is single-layer | No `--candidate-layer` needed |
| Both files multi-layer | `--layer` + optional `--candidate-layer` |

Layer names are matched case-insensitively. Each entity's DXF group code **8** (layer name) is read by both parsers.

### 3.2 Supported DXF versions

| Version | Format | Reader |
|---|---|---|
| AutoCAD R12 (AC1009) | ASCII | `AsciiDxfParser` |
| AutoCAD 2000+ | ASCII / binary | `netDxf` (with ASCII fallback) |
| Binary R12 | Binary | **Not supported** — re-save as ASCII DXF |

### 3.3 DXF reading pipeline

```
File path  [+ optional layer name]
    │
    ▼
Check DXF version (DxfDocument.CheckDxfFileVersion)
    │
    ├── Version < AutoCad2000 ──► AsciiDxfParser.ReadPolygonCandidates()
    │
    └── Version >= AutoCad2000
            │
            ├── netDxf load succeeds ──► Extract Polylines2D, Polylines3D, Lines (with layer)
            │
            └── netDxf fails ──► AsciiDxfParser (fallback)
    │
    ▼
Filter by layer (if --layer specified)
    │
    ▼
SelectPrimaryPolygon() — largest area on that layer
    │
    ▼
List<Point2D> vertex ring
```

### 3.4 ASCII DXF parser (R12)

The built-in `AsciiDxfParser` handles:

- `POLYLINE` / `VERTEX` / `SEQEND` (classic R12 polylines, layer on POLYLINE)
- `LWPOLYLINE` (lightweight polylines, group code 8)
- `LINE` entities (group code 8)
- `INSERT` block references (exploded with transform)

Block inserts are resolved with scale, rotation, and translation. Polyface and spline-frame vertices (flag bits 1, 16, 128) are skipped.

---

## 4. Polygon comparison algorithm

### 4.1 Preprocessing (normalization)

Before comparison, both polygons pass through `PolygonComparer.Normalize()`:

1. **Remove duplicate vertices** — consecutive points closer than `1e-8` units
2. **Remove collinear vertices** — middle points on straight edges (cross product ≈ 0, dot product > 0)
3. **Normalize winding** — ensure counter-clockwise orientation (positive signed area)

### 4.2 Congruence detection (two-phase)

#### Phase 1 — Edge length filter

For each cyclic shift of polygon B (and its reversed winding), edge lengths are compared to polygon A.

```
lengthTol = max(1e-8, relativeTolerance × perimeter / vertexCount)
```

#### Phase 2 — Procrustes rotation fit

Both polygons are centered at their centroid. Rotation is computed via Kabsch/Procrustes:

```
angle = atan2(Σ cross, Σ dot)
```

Match when `RMSD <= absTol`, where `absTol = max(1e-8, relativeTolerance × RmsRadius)`.

#### Phase 3 — Reflection handling

| Mode | Transform applied to reference |
|---|---|
| Horizontal flip | Mirror over vertical axis: `(x, y) → (-x, y)` |
| Vertical flip | Mirror over horizontal axis: `(x, y) → (x, -y)` |

### 4.3 Transform reporting

| Field | Description |
|---|---|
| `RotationDegreesCcw` | Counter-clockwise rotation (0°–360°) |
| `RotationDegreesCw` | Clockwise equivalent |
| `IsFlipped` | Whether a reflection was required |
| `FlipSide` | `None`, `Horizontal`, or `Vertical` |
| `TransformSummary` | e.g. *"Flipped horizontally, then rotated 35° CCW"* |
| `FitError` | RMSD after best-fit alignment |

---

## 5. Edge service

Edge service simulates adding material allowance around the base shape before comparing to a candidate DXF that already includes edge service.

### 5.1 Per-side expansion

Each edge is classified by its outward normal direction and offset by the corresponding value:

| Side | Normal direction | CLI option |
|---|---|---|
| Top | +Y | `--edge-top` |
| Bottom | −Y | `--edge-bottom` |
| Left | −X | `--edge-left` |
| Right | +X | `--edge-right` |
| All sides | All of the above | `--edge-service` |

### 5.2 Size formulas

Given base bounding-box width `W` and height `H`:

```
finalWidth  = W + left + right
finalHeight = H + top + bottom
```

| Base size | Edge service | Result |
|---|---|---|
| 100 × 50 mm | `--edge-service 2` (all sides) | **104 × 54 mm** |
| 100 × 50 mm | `--edge-top 2` (top only) | **100 × 52 mm** |
| 100 × 50 mm | `--edge-top 2 --edge-left 1` | **101 × 52 mm** |

### 5.3 Comparison workflow with edge service

1. Read **reference DXF** as the base shape (optionally from a specific layer)
2. Apply edge service expansion mathematically to the reference polygon
3. Compare expanded reference vs **candidate DXF** (which should already include edge service)
4. Auto-validate candidate size against `base + edge service` dimensions

Override expected size manually with `--width` / `--height` if needed.

### 5.4 Algorithm

Each polygon edge is offset outward along its normal by the side-specific value. New vertices are computed by intersecting adjacent offset edge lines (miter join).

---

## 6. Size validation

When the user supplies expected width and height (or edge service is active), the tool validates the **axis-aligned bounding box**.

### 6.1 Measurement

```
width  = max(X) - min(X)
height = max(Y) - min(Y)
```

### 6.2 Matching rules

| Check | Condition |
|---|---|
| Direct | `\|measuredW - expectedW\| ≤ tol` AND `\|measuredH - expectedH\| ≤ tol` |
| Swapped | `\|measuredW - expectedH\| ≤ tol` AND `\|measuredH - expectedW\| ≤ tol` |

Swapped dimensions are accepted when the drawing is rotated 90°.

### 6.3 Tolerance

```
sizeTol = max(sizeToleranceAbs, relativeTolerance × max(expectedWidth, expectedHeight))
```

### 6.4 Overall pass/fail

| Shape match | Size check | Result | Exit code |
|---|---|---|---|
| Yes | Pass (or not requested) | `MATCH` or `MATCH (shape and size)` | 0 |
| Yes | Fail | `SHAPE MATCH, SIZE FAIL` | 1 |
| No | — | `NO MATCH` | 1 |

---

## 7. Command-line reference

### 7.1 Usage

```text
DxfCompare <reference.dxf> <candidate.dxf> [options]
DxfCompare <reference.dxf> <candidate.dxf> <width> <height>
DxfCompare <reference.dxf> <candidate.dxf> --layer LAYER2
DxfCompare --list-layers <file.dxf>
DxfCompare --write-samples [folder]
DxfCompare --self-test
DxfCompare --help
```

### 7.2 Options

| Option | Alias | Default | Description |
|---|---|---|---|
| `--reference-layer <name>` | `--layer` | all layers | Layer to read from reference DXF |
| `--candidate-layer <name>` | — | all layers | Layer to read from candidate DXF |
| `--list-layers <file>` | — | — | List layers with closed polygon geometry |
| `--edge-service <n>` | — | — | Edge service on all sides (mm/units) |
| `--edge-top <n>` | — | — | Edge service on top side |
| `--edge-bottom <n>` | — | — | Edge service on bottom side |
| `--edge-left <n>` | — | — | Edge service on left side |
| `--edge-right <n>` | — | — | Edge service on right side |
| `--width <n>` | `-w` | — | Expected final width (overrides edge service calc) |
| `--height <n>` | — | — | Expected final height |
| `--ask-size` | — | off | Prompt for width/height after measuring |
| `--size-tolerance <n>` | — | `0` | Absolute size tolerance |
| `--tolerance <n>` | — | `0.0001` | Relative geometry tolerance |
| `--json` | — | off | JSON output |
| `--write-samples [dir]` | — | `./samples` | Generate sample DXF files |
| `--self-test` | — | — | Run built-in test suite |
| `--help` | `-h` | — | Show usage |

### 7.3 Exit codes

| Code | Meaning |
|---|---|
| `0` | Shapes match (and size matches when validated) |
| `1` | Shapes or size do not match |
| `2` | Invalid arguments, missing file, or unsupported format |

### 7.4 Examples

**Basic comparison:**

```text
dotnet run -- reference.dxf candidate.dxf
```

**Multi-layer reference — compare LAYER2 only:**

```text
dotnet run -- --list-layers multi-layer-3.dxf
dotnet run -- multi-layer-3.dxf single-layer-shape.dxf --layer LAYER2
```

**Flipped shape:**

```text
dotnet run -- single-layer-shape.dxf single-layer-shape-flipped.dxf
```

**Edge service — all sides 2 mm:**

```text
dotnet run -- rect-100x50.dxf rect-edge-all-2mm.dxf --edge-service 2
```

**Edge service — top only 2 mm:**

```text
dotnet run -- rect-100x50.dxf rect-edge-top-2mm.dxf --edge-top 2
```

**Size validation:**

```text
dotnet run -- reference.dxf candidate.dxf --width 10 --height 12
dotnet run -- reference.dxf candidate.dxf 10 12
```

**Combined — layer + edge service:**

```text
dotnet run -- base-3-layers.dxf edge-result.dxf --layer LAYER2 --edge-service 2
```

**Generate all sample files:**

```text
dotnet run -- --write-samples
dotnet run -- --write-samples "D:\SPIL - Repo\SPIL DXF Validation\samples"
```

**JSON output:**

```text
dotnet run -- reference.dxf candidate.dxf --layer LAYER2 --json
```

---

## 8. Output formats

### 8.1 Text output (default)

```text
DXF polygon comparison
----------------------------------------
Reference : D:\drawings\multi-layer-3.dxf
Ref layer : LAYER2
Candidate : D:\drawings\single-layer-shape.dxf
Result    : MATCH (shape and size)
Vertices  : 6
Edge svc  : 2 on all sides
Transform : Rotated 90° CCW
Flipped   : No
Flip side : Not flipped
Rotation  : 90° CCW  (270° CW)
Base size : 100 x 50
Size (ref): 104 x 54
Size (cand): 104 x 54
Expected  : 104 x 54
Size check: PASS — base 100 x 50 + edge service (2 on all sides) => 104 x 54
Fit error : 1.422E-14
Details   : The DXF shapes match (same polygon, allowing rotation and/or flipping).
```

### 8.2 JSON output (`--json`)

```json
{
  "reference": "D:\\drawings\\multi-layer-3.dxf",
  "referenceLayer": "LAYER2",
  "candidate": "D:\\drawings\\result.dxf",
  "candidateLayer": null,
  "match": true,
  "shapeMatch": true,
  "vertexCount": 6,
  "edgeService": {
    "top": 2,
    "bottom": 2,
    "left": 2,
    "right": 2,
    "description": "2 on all sides"
  },
  "flipped": false,
  "flipSide": "None",
  "rotationDegreesCcw": 0,
  "transform": "Same orientation (no rotation, no flip)",
  "size": {
    "baseReferenceWidth": 100,
    "baseReferenceHeight": 50,
    "referenceWidth": 104,
    "referenceHeight": 54,
    "candidateWidth": 104,
    "candidateHeight": 54,
    "expectedWidth": 104,
    "expectedHeight": 54,
    "checkedSize": true,
    "passed": true,
    "summary": "PASS — base 100 x 50 + edge service (2 on all sides) => 104 x 54"
  },
  "fitError": 0,
  "message": "The DXF shapes match (same polygon, allowing rotation and/or flipping)."
}
```

The top-level `match` field is `true` only when both shape and size checks pass.

---

## 9. Build and deployment

### 9.1 Prerequisites

- .NET 9.0 SDK or later

### 9.2 Build

```text
dotnet build
```

### 9.3 Run (development)

```text
dotnet run -- <args>
```

### 9.4 Publish (standalone executable)

```text
dotnet publish -c Release -r win-x64 --self-contained
```

Output: `bin/Release/net9.0/win-x64/publish/DxfCompare.exe`

---

## 10. Testing

### 10.1 Self-test suite

```text
dotnet run -- --self-test
```

| Category | Tests |
|---|---|
| Shape match | Identical, translated, rotated (45°, 90°), collinear vertices |
| Flip detection | Horizontal, vertical, flip + rotation |
| Negative | Different shape (expected no match) |
| R12 format | Reference, rotated, flipped in AutoCAD 12 ASCII DXF |
| Size validation | 10×12 pass, 12×10 swapped pass, 9×12 fail |
| Edge service | All-around 104×54, top-only 100×52, full compare flow |
| Multi-layer | List layers, LAYER2 match, LAYER1 no match |

### 10.2 Sample files

Generated by `--write-samples`:

| File | Description |
|---|---|
| `reference.dxf` | L-shaped polygon (10 × 12 bounding box) |
| `rotated-90.dxf` | Same shape rotated 90° CCW |
| `rotated-45.dxf` | Same shape rotated 45° CCW |
| `flipped-horizontal.dxf` | Left-right mirror |
| `flipped-vertical.dxf` | Up-down mirror |
| `flipped-and-rotated.dxf` | Horizontal flip + 35° rotation |
| `translated.dxf` | Same shape, moved in X/Y |
| `collinear-vertices.dxf` | Extra collinear vertices inserted |
| `different-shape.dxf` | Rectangle (different shape) |
| `reference-r12.dxf` | R12 format reference |
| `rotated-90-r12.dxf` | R12 format, rotated 90° |
| `flipped-horizontal-r12.dxf` | R12 format, flipped |
| `rect-100x50.dxf` | Rectangle 100 × 50 (edge service base) |
| `rect-edge-all-2mm.dxf` | Rectangle after 2 mm all-side edge service (104 × 54) |
| `rect-edge-top-2mm.dxf` | Rectangle after 2 mm top edge service (100 × 52) |
| `multi-layer-3.dxf` | 3 layers: LAYER1 (other), **LAYER2 (L-shape)**, LAYER3 (other) |
| `single-layer-shape.dxf` | Single-layer L-shape on layer 0 |
| `single-layer-shape-flipped.dxf` | Same L-shape, horizontally flipped |

---

## 11. Limitations and known constraints

### 11.1 Geometry

- **Straight edges only.** Bulged polyline segments are treated as straight lines between vertices.
- **One polygon per layer.** If a layer has multiple closed shapes, only the largest by area is used.
- **Same scale required.** Shapes drawn at different scales will not match.
- **2D only.** Z coordinates from Polyline3D are ignored.

### 11.2 File format

- **Binary DXF is not supported.** Re-save as ASCII DXF from CAD software.
- **Block references** in the ASCII parser are exploded up to depth 32.
- **Hatches, splines, circles, arcs** as standalone entities are not read.

### 11.3 Size and edge service

- Size is the **axis-aligned bounding box**, not oriented minimum bounding rectangle.
- For L-shapes, width/height are overall X/Y extents, not individual leg dimensions.
- Edge service uses **per-edge normal offset** with miter joins at corners.

---

## 12. Troubleshooting

| Error / symptom | Likely cause | Resolution |
|---|---|---|
| `DXF file not found` | Invalid path | Verify file path and permissions |
| `No closed polygon found` | Shape not closed or wrong entity type | Draw as closed polyline or connected lines |
| `No closed polygon found on layer 'X'` | Wrong layer name or no shape on that layer | Run `--list-layers` to see available layers |
| `Binary DXF is not supported` | File saved in binary format | Re-save as ASCII DXF |
| `Vertex counts differ` | Different shapes | Verify both files represent the same outline |
| `SHAPE MATCH, SIZE FAIL` | Wrong dimensions or edge service values | Check `--width`/`--height` or edge service settings |
| Low fit error but no match | Floating-point edge case | Increase `--tolerance` slightly |

---

## 13. Algorithm reference

### 13.1 Collinear vertex removal

```
v1 = normalize(P[i] - P[i-1])
v2 = normalize(P[i+1] - P[i])
collinear = |v1 × v2| < tolerance  AND  v1 · v2 > 0
```

### 13.2 Edge service offset

For each edge, outward normal `n` is computed from CCW winding. Offset distance is selected by dominant normal component (top/bottom/left/right). Adjacent offset lines are intersected to form new vertices.

---

## 14. Glossary

| Term | Definition |
|---|---|
| **Congruent** | Same shape and size under rotation, translation, and reflection |
| **CCW** | Counter-clockwise rotation |
| **Edge service** | Material allowance added outward around a shape perimeter |
| **Layer** | DXF drawing layer (group code 8) separating entity groups |
| **RMSD** | Root mean square deviation after alignment |
| **Bounding box** | Axis-aligned rectangle enclosing all vertices |
| **R12 / AC1009** | AutoCAD Release 12 DXF format |
| **Procrustes fit** | Optimal rotation minimizing sum of squared distances |

---

*Document maintained for the SPIL DXF Validation project.*
