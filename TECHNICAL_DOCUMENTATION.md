# DxfCompare — Technical Documentation

**Version:** 1.0  
**Project:** SPIL DXF Validation  
**Platform:** .NET 9.0  
**Last updated:** August 2026

---

## 1. Overview

**DxfCompare** is a command-line application that validates whether two DXF files contain the same single 2D polygon outline, even when one drawing is rotated, flipped (reflected), or translated relative to the other.

In addition to shape comparison, the tool optionally validates **expected width and height** against the measured outline of the reference polygon.

### 1.1 Key capabilities

| Capability | Description |
|---|---|
| Shape congruence | Determines if two polygons are geometrically identical under rotation and reflection |
| Transform detection | Reports rotation angle (CCW/CW) and flip direction (horizontal or vertical) |
| Size validation | Compares measured bounding-box dimensions against user-supplied width and height |
| Legacy DXF support | Reads ASCII AutoCAD R12 (AC1009) files via a built-in parser |
| Modern DXF support | Reads AutoCAD 2000+ files via the **netDxf** library |
| Collinear cleanup | Removes duplicate and collinear vertices before comparison |

### 1.2 Out of scope

- Scale differences (shapes must be drawn at the same scale)
- Multiple shapes per file (only the largest closed polygon is used)
- Curved edges (arcs, splines, bulged polyline segments are not supported)
- Binary DXF files
- 3D geometry (only X/Y coordinates are used)

---

## 2. System architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         Program.cs (CLI)                        │
│  Argument parsing · Output formatting · Self-test orchestration │
└────────────┬───────────────────────────────┬────────────────────┘
             │                               │
             ▼                               ▼
┌────────────────────────┐      ┌───────────────────────────────┐
│   DxfPolygonReader     │      │      Comparison layer         │
│  ┌──────────────────┐  │      │  ┌─────────────────────────┐  │
│  │ netDxf (AC2000+)   │  │      │  │   PolygonComparer       │  │
│  └──────────────────┘  │      │  │   Shape match + transform │  │
│  ┌──────────────────┐  │      │  └─────────────────────────┘  │
│  │ AsciiDxfParser   │  │      │  ┌─────────────────────────┐  │
│  │ (R12 / fallback) │  │      │  │   SizeCheck             │  │
│  └──────────────────┘  │      │  │   Width/height validate │  │
└────────────┬───────────┘      │  └─────────────────────────┘  │
             │                  └───────────────────────────────┘
             ▼
┌────────────────────────┐
│   Geometry / Point2D   │
└────────────────────────┘
```

### 2.1 Project structure

```
SPIL DXF Validation/
├── Program.cs                    Entry point, CLI, output, self-tests
├── DxfCompare.csproj             .NET 9 project file
├── Geometry/
│   └── Point2D.cs                2D point struct with vector math
├── Dxf/
│   ├── DxfPolygonReader.cs       Unified DXF reader (netDxf + fallback)
│   ├── AsciiDxfParser.cs         ASCII DXF parser for R12 and legacy files
│   └── SampleDxfFactory.cs       Sample file generator for testing
├── Comparison/
│   ├── PolygonComparer.cs        Congruence detection and transform fit
│   ├── SizeCheck.cs              Bounding-box size validation
│   └── ComparisonResult.cs       Shape comparison result model
└── samples/                      Generated test DXF files
```

### 2.2 Dependencies

| Package | Version | Purpose |
|---|---|---|
| netDxf | 2023.11.10 | Read/write DXF files (AutoCAD 2000 and newer) |

No other third-party libraries are used.

---

## 3. DXF input requirements

Each DXF file must contain **one primary closed polygon**. The tool extracts geometry in this order:

1. **Polyline2D** (lightweight polyline) — preferred
2. **Polyline3D** — X/Y projected to 2D
3. **Closed loop of Line entities** — stitched into a polygon if endpoints connect

If multiple closed polygons exist, the one with the **largest absolute area** is selected.

### 3.1 Supported DXF versions

| Version | Format | Reader |
|---|---|---|
| AutoCAD R12 (AC1009) | ASCII | `AsciiDxfParser` |
| AutoCAD 2000+ | ASCII / binary | `netDxf` (with ASCII fallback) |
| Binary R12 | Binary | **Not supported** — re-save as ASCII DXF |

### 3.2 DXF reading pipeline

```
File path
    │
    ▼
Check DXF version (DxfDocument.CheckDxfFileVersion)
    │
    ├── Version < AutoCad2000 ──► AsciiDxfParser.ReadPolygons()
    │
    └── Version >= AutoCad2000
            │
            ├── netDxf load succeeds ──► Extract Polylines2D, Polylines3D, Lines
            │
            └── netDxf fails ──► AsciiDxfParser.ReadPolygons() (fallback)
    │
    ▼
SelectPrimaryPolygon() — largest area wins
    │
    ▼
List<Point2D> vertex ring
```

### 3.3 ASCII DXF parser (R12)

The built-in `AsciiDxfParser` handles:

- `POLYLINE` / `VERTEX` / `SEQEND` (classic R12 polylines)
- `LWPOLYLINE` (lightweight polylines in newer ASCII files)
- `LINE` entities
- `INSERT` block references (one level of explosion with transform)

Block inserts are resolved with scale, rotation, and translation applied to child entities. Polyface and spline-frame vertices (flag bits 1, 16, 128) are skipped.

---

## 4. Polygon comparison algorithm

### 4.1 Preprocessing (normalization)

Before comparison, both polygons pass through `PolygonComparer.Normalize()`:

1. **Remove duplicate vertices** — consecutive points closer than `1e-8` units
2. **Remove collinear vertices** — middle points on straight edges (cross product ≈ 0, dot product > 0)
3. **Normalize winding** — ensure counter-clockwise orientation (positive signed area)

### 4.2 Congruence detection (two-phase)

#### Phase 1 — Edge length filter

For each cyclic shift of polygon B (and its reversed winding), edge lengths are compared to polygon A. Shifts where any edge length differs by more than `lengthTol` are rejected early.

```
lengthTol = max(1e-8, relativeTolerance × perimeter / vertexCount)
```

#### Phase 2 — Procrustes rotation fit

For each surviving shift, both polygons are centered at their centroid. A least-squares rotation is computed using the Kabsch/Procrustes method:

```
angle = atan2(Σ cross, Σ dot)
```

where `cross` and `dot` are summed over corresponding centered vertex pairs.

The root-mean-square deviation (RMSD) after rotation is the **fit error**. A match is declared when:

```
RMSD <= absTol
absTol = max(1e-8, relativeTolerance × RmsRadius)
```

#### Phase 3 — Reflection handling

Two flip modes are tested:

| Mode | Transform applied to reference |
|---|---|
| Horizontal flip | Mirror over vertical axis: `(x, y) → (-x, y)` |
| Vertical flip | Mirror over horizontal axis: `(x, y) → (-y, y)` |

After flipping, the same rotation fit is applied. The flip mode with the lowest RMSD is selected.

### 4.3 Transform reporting

When a match is found, the tool reports:

| Field | Description |
|---|---|
| `RotationDegreesCcw` | Counter-clockwise rotation from reference to candidate (0°–360°) |
| `RotationDegreesCw` | Clockwise equivalent |
| `IsFlipped` | Whether a reflection was required |
| `FlipSide` | `None`, `Horizontal`, or `Vertical` |
| `FlipDescription` | Human-readable flip explanation |
| `MirrorAxisDegrees` | `0` (horizontal axis) or `90` (vertical axis) |
| `TransformSummary` | Combined description, e.g. *"Flipped horizontally, then rotated 35° CCW"* |
| `FitError` | RMSD after best-fit alignment |

### 4.4 Invariances handled automatically

| Transformation | Handled? | Method |
|---|---|---|
| Translation | Yes | Centroid removal before fit |
| Rotation | Yes | Cyclic shift + Procrustes angle |
| Reflection (flip) | Yes | Horizontal/vertical mirror test |
| Different start vertex | Yes | Cyclic shift over all vertices |
| Collinear extra vertices | Yes | Preprocessing step |
| Clockwise vs counter-clockwise winding | Yes | Winding normalization + reversed mapping |

---

## 5. Size validation

When the user supplies expected width and height, the tool validates the **axis-aligned bounding box** of the normalized reference polygon.

### 5.1 Measurement

```
width  = max(X) - min(X)
height = max(Y) - min(Y)
```

Both reference and candidate bounding boxes are measured and reported. Size validation uses the **reference** dimensions.

### 5.2 Matching rules

Expected dimensions match if either orientation fits within tolerance:

| Check | Condition |
|---|---|
| Direct | `\|measuredW - expectedW\| ≤ tol` AND `\|measuredH - expectedH\| ≤ tol` |
| Swapped | `\|measuredW - expectedH\| ≤ tol` AND `\|measuredH - expectedW\| ≤ tol` |

Swapped dimensions are accepted to support cases where the user enters width/height in a different orientation than the drawing axes (e.g. after a 90° rotation the as-drawn box may read 12 × 10 while the outline size is still 10 × 12).

### 5.3 Tolerance

```
sizeTol = max(sizeToleranceAbs, relativeTolerance × max(expectedWidth, expectedHeight))
```

- `--size-tolerance` sets the absolute floor (default `0`)
- `--tolerance` also scales the size tolerance proportionally

### 5.4 Overall pass/fail

| Shape match | Size check | Result | Exit code |
|---|---|---|---|
| Yes | Pass (or not requested) | `MATCH` or `MATCH (shape and size)` | 0 |
| Yes | Fail | `SHAPE MATCH, SIZE FAIL` | 1 |
| No | — | `NO MATCH` | 1 |

---

## 6. Command-line reference

### 6.1 Usage

```text
DxfCompare <reference.dxf> <candidate.dxf> [options]
DxfCompare <reference.dxf> <candidate.dxf> <width> <height>
DxfCompare --write-samples [folder]
DxfCompare --self-test
DxfCompare --help
```

### 6.2 Options

| Option | Alias | Default | Description |
|---|---|---|---|
| `--width <n>` | `-w` | — | Expected shape width (drawing units) |
| `--height <n>` | — | — | Expected shape height (drawing units) |
| `--ask-size` | — | off | Print measured sizes and prompt for width/height |
| `--size-tolerance <n>` | — | `0` | Absolute size tolerance (drawing units) |
| `--tolerance <n>` | — | `0.0001` | Relative geometry tolerance |
| `--json` | — | off | Output results as JSON |
| `--write-samples [dir]` | — | `./samples` | Generate sample DXF files |
| `--self-test` | — | — | Run built-in test suite |
| `--help` | `-h` | — | Show usage |

### 6.3 Exit codes

| Code | Meaning |
|---|---|
| `0` | Success — shapes match (and size matches if provided) |
| `1` | Failure — shapes do not match, or size validation failed |
| `2` | Error — invalid arguments, missing file, or unsupported format |

### 6.4 Examples

**Basic shape comparison:**

```text
dotnet run -- reference.dxf candidate.dxf
```

**Shape + size validation:**

```text
dotnet run -- reference.dxf candidate.dxf --width 10 --height 12
dotnet run -- reference.dxf candidate.dxf 10 12
```

**Interactive size entry:**

```text
dotnet run -- reference.dxf candidate.dxf --ask-size
```

**JSON output for automation:**

```text
dotnet run -- reference.dxf candidate.dxf --width 10 --height 12 --json
```

**Generate sample files:**

```text
dotnet run -- --write-samples
```

**Run self-tests:**

```text
dotnet run -- --self-test
```

---

## 7. Output formats

### 7.1 Text output (default)

```text
DXF polygon comparison
----------------------------------------
Reference : C:\drawings\shape-a.dxf
Candidate : C:\drawings\shape-b.dxf
Result    : MATCH (shape and size)
Vertices  : 6
Transform : Rotated 90° CCW
Flipped   : No
Flip side : Not flipped
Rotation  : 90° CCW  (270° CW)
Size (ref): 10 x 12
Size (cand): 12 x 10
Expected  : 10 x 12
Size check: PASS — expected 10 x 12, measured 10 x 12
Fit error : 1.422E-14
Details   : The DXF shapes match (same polygon, allowing rotation and/or flipping).
```

### 7.2 JSON output (`--json`)

```json
{
  "reference": "C:\\drawings\\shape-a.dxf",
  "candidate": "C:\\drawings\\shape-b.dxf",
  "match": true,
  "shapeMatch": true,
  "vertexCount": 6,
  "flipped": false,
  "flipSide": "None",
  "flipDescription": "Not flipped",
  "rotationDegreesCcw": 90,
  "rotationDegreesCw": 270,
  "mirrorAxisDegrees": 0,
  "transform": "Rotated 90° CCW",
  "size": {
    "referenceWidth": 10,
    "referenceHeight": 12,
    "candidateWidth": 12,
    "candidateHeight": 10,
    "expectedWidth": 10,
    "expectedHeight": 12,
    "checkedSize": true,
    "passed": true,
    "dimensionsSwapped": false,
    "summary": "PASS — expected 10 x 12, measured 10 x 12"
  },
  "fitError": 1.421e-14,
  "message": "The DXF shapes match (same polygon, allowing rotation and/or flipping)."
}
```

The top-level `match` field is `true` only when both shape and size checks pass.

---

## 8. Build and deployment

### 8.1 Prerequisites

- .NET 9.0 SDK or later

### 8.2 Build

```text
dotnet build
```

### 8.3 Run (development)

```text
dotnet run -- <args>
```

### 8.4 Publish (standalone executable)

```text
dotnet publish -c Release -r win-x64 --self-contained
```

Output: `bin/Release/net9.0/win-x64/publish/DxfCompare.exe`

For Linux:

```text
dotnet publish -c Release -r linux-x64 --self-contained
```

---

## 9. Testing

### 9.1 Self-test suite

```text
dotnet run -- --self-test
```

The suite covers:

| Category | Tests |
|---|---|
| Shape match | Identical, translated, rotated (45°, 90°), collinear vertices |
| Flip detection | Horizontal, vertical, flip + rotation |
| Negative | Different shape (expected no match) |
| R12 format | Reference, rotated, flipped in AutoCAD 12 ASCII DXF |
| Size validation | 10×12 pass, 12×10 swapped pass, 9×12 fail |

### 9.2 Sample files

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

---

## 10. Limitations and known constraints

### 10.1 Geometry

- **Straight edges only.** Bulged polyline segments (arcs) are treated as straight lines between vertices.
- **Single polygon per file.** If a DXF contains multiple closed shapes, only the largest by area is compared.
- **Same scale required.** The tool does not normalize by perimeter; shapes drawn at different scales will not match.
- **2D only.** Z coordinates from Polyline3D are ignored; only X and Y are used.

### 10.2 File format

- **Binary DXF is not supported.** Files must be saved as ASCII DXF.
- **Block references** in the ASCII parser are exploded one level deep (nested blocks up to depth 32).
- **Hatches, splines, circles, arcs** as standalone entities are not read.

### 10.3 Size validation

- Size is measured as the **axis-aligned bounding box**, not the true oriented minimum bounding rectangle.
- For non-rectangular shapes (e.g. L-shapes), width and height refer to the overall extent in X and Y, not individual leg dimensions.

---

## 11. Troubleshooting

| Error / symptom | Likely cause | Resolution |
|---|---|---|
| `DXF file not found` | Invalid path | Verify file path and permissions |
| `No closed polygon found` | Shape not closed or wrong entity type | Draw as closed polyline or connected lines |
| `Binary DXF is not supported` | File saved in binary format | Re-save as ASCII DXF from CAD software |
| `Vertex counts differ` | Different shapes or missing vertices | Verify both files represent the same outline |
| `SHAPE MATCH, SIZE FAIL` | Outline correct but dimensions wrong | Check expected width/height and drawing units |
| Low fit error but no match | Floating-point edge case | Increase `--tolerance` slightly |

---

## 12. Algorithm reference (collinear removal)

A vertex is removed when the normalized cross product of its adjacent edges is near zero **and** the dot product is positive (continuing straight line, not a 180° fold):

```
v1 = normalize(P[i] - P[i-1])
v2 = normalize(P[i+1] - P[i])
collinear = |v1 × v2| < tolerance  AND  v1 · v2 > 0
```

Duplicate vertices (distance < `1e-8`) and zero-length edges are also removed.

---

## 13. Future enhancement considerations

Potential improvements not yet implemented:

- Oriented minimum bounding rectangle for size validation
- Perimeter-normalized comparison for scale-invariant matching
- Support for bulged polyline arcs (discretized into line segments)
- Batch mode (compare one reference against many candidates)
- Configuration file for default tolerances and expected dimensions

---

## 14. Glossary

| Term | Definition |
|---|---|
| **Congruent** | Same shape and size; identical under rotation, translation, and reflection |
| **CCW** | Counter-clockwise rotation (positive angle in standard math convention) |
| **RMSD** | Root mean square deviation — average point distance after alignment |
| **Bounding box** | Smallest axis-aligned rectangle enclosing all vertices |
| **R12 / AC1009** | AutoCAD Release 12 DXF format |
| **Procrustes fit** | Optimal rotation alignment minimizing sum of squared distances |

---

*Document generated for the SPIL DXF Validation project.*
