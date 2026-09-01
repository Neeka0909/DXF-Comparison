using System.Globalization;
using DxfCompare.Geometry;

namespace DxfCompare.Dxf;

/// <summary>
/// Reads 2D polygon geometry from ASCII DXF, including AutoCAD R12 (AC1009)
/// which netDxf cannot load.
/// </summary>
internal static class AsciiDxfParser
{
    public static List<DxfPolygonReader.LayerPolygonCandidate> ReadPolygonCandidates(string filePath)
    {
        List<(int Code, string Value)> pairs = ReadPairs(filePath);
        var document = ParseDocument(pairs);

        var candidates = new List<DxfPolygonReader.LayerPolygonCandidate>();
        var segmentsByLayer = new Dictionary<string, List<(Point2D A, Point2D B)>>(StringComparer.OrdinalIgnoreCase);
        var exploded = new List<BlockItem>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BlockItem item in document.Entities)
            Explode(item, Transform2D.Identity, document.Blocks, exploded, visiting, depth: 0);

        foreach (BlockItem item in exploded)
        {
            switch (item)
            {
                case PolyItem poly:
                    DxfPolygonReader.AddIfPolygon(candidates, poly.Points, poly.IsClosed, poly.Layer);
                    break;
                case LineItem line:
                    if (line.A.DistanceTo(line.B) > DxfPolygonReader.EndpointTolerance)
                    {
                        if (!segmentsByLayer.TryGetValue(line.Layer, out List<(Point2D, Point2D)>? bucket))
                        {
                            bucket = [];
                            segmentsByLayer[line.Layer] = bucket;
                        }

                        bucket.Add((line.A, line.B));
                    }

                    break;
            }
        }

        foreach ((string layer, List<(Point2D A, Point2D B)> segments) in segmentsByLayer)
        {
            List<Point2D>? fromLines = DxfPolygonReader.TryBuildPolygonFromSegments(segments);
            if (fromLines is not null)
                candidates.Add(new DxfPolygonReader.LayerPolygonCandidate(fromLines, layer));
        }

        return candidates;
    }

    private static DxfSketch ParseDocument(List<(int Code, string Value)> pairs)
    {
        var sketch = new DxfSketch();
        string? currentSection = null;
        string? currentBlock = null;
        List<BlockItem>? blockItems = null;
        List<BlockItem> target = sketch.Entities;

        string entity = "";
        var fields = new Dictionary<int, string>();
        var lwVertices = new List<Point2D>();
        var polyVertices = new List<Point2D>();
        bool polyClosed = false;
        bool skipPolyline = false;
        double vx = 0, vy = 0;
        int vertexFlags = 0;
        bool inVertex = false;

        void FlushEntity()
        {
            if (inVertex && !skipPolyline && IsUsableVertex(vertexFlags))
                polyVertices.Add(new Point2D(vx, vy));
            inVertex = false;

            switch (entity)
            {
                case "LWPOLYLINE":
                    CommitLwPolyline(fields, lwVertices, target);
                    break;
                case "LINE":
                    CommitLine(fields, target);
                    break;
                case "INSERT":
                    CommitInsert(fields, target);
                    break;
                case "POLYLINE":
                    break;
            }

            fields.Clear();
            lwVertices.Clear();
            entity = "";
        }

        void FlushPolyline()
        {
            if (!skipPolyline && polyVertices.Count >= 3)
            {
                string layer = fields.TryGetValue(8, out string? layerName) ? layerName : "0";
                target.Add(new PolyItem([.. polyVertices], polyClosed, layer));
            }

            polyVertices.Clear();
            polyClosed = false;
            skipPolyline = false;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            (int code, string value) = pairs[i];
            string upper = value.ToUpperInvariant();

            if (code == 0)
            {
                if (upper is "VERTEX")
                {
                    if (inVertex && !skipPolyline && IsUsableVertex(vertexFlags))
                        polyVertices.Add(new Point2D(vx, vy));
                    inVertex = true;
                    vx = 0;
                    vy = 0;
                    vertexFlags = 0;
                    entity = "VERTEX";
                    continue;
                }

                if (upper is "SEQEND")
                {
                    if (inVertex && !skipPolyline && IsUsableVertex(vertexFlags))
                        polyVertices.Add(new Point2D(vx, vy));
                    inVertex = false;
                    if (entity is "POLYLINE" or "VERTEX")
                        FlushPolyline();
                    entity = "";
                    fields.Clear();
                    continue;
                }

                FlushEntity();

                if (upper == "SECTION")
                {
                    entity = "SECTION";
                    continue;
                }

                if (upper == "ENDSEC")
                {
                    currentSection = null;
                    continue;
                }

                if (upper == "BLOCK")
                {
                    entity = "BLOCK";
                    continue;
                }

                if (upper == "ENDBLK")
                {
                    if (currentBlock is not null && blockItems is not null)
                        sketch.Blocks[currentBlock] = blockItems;
                    currentBlock = null;
                    blockItems = null;
                    target = sketch.Entities;
                    continue;
                }

                if (upper is "POLYLINE")
                {
                    entity = "POLYLINE";
                    polyVertices.Clear();
                    polyClosed = false;
                    skipPolyline = false;
                    fields.Clear();
                    continue;
                }

                entity = upper;
                continue;
            }

            if (entity == "SECTION" && code == 2)
            {
                currentSection = upper;
                target = currentSection == "BLOCKS" ? [] : sketch.Entities;
                continue;
            }

            if (entity == "BLOCK" && code == 2 && currentSection == "BLOCKS")
            {
                currentBlock = value.Trim();
                blockItems = [];
                target = blockItems;
                continue;
            }

            switch (entity)
            {
                case "POLYLINE" when code == 70:
                    int flags = ParseInt(value);
                    polyClosed = (flags & 1) != 0;
                    skipPolyline = (flags & 16) != 0 || (flags & 64) != 0;
                    break;
                case "POLYLINE":
                    fields[code] = value;
                    break;
                case "VERTEX" when code == 10:
                    vx = ParseDouble(value);
                    break;
                case "VERTEX" when code == 20:
                    vy = ParseDouble(value);
                    break;
                case "VERTEX" when code == 70:
                    vertexFlags = ParseInt(value);
                    break;
                case "LWPOLYLINE" when code is 10:
                    lwVertices.Add(new Point2D(ParseDouble(value), 0));
                    break;
                case "LWPOLYLINE" when code is 20 && lwVertices.Count > 0:
                    lwVertices[^1] = lwVertices[^1] with { Y = ParseDouble(value) };
                    break;
                case "LWPOLYLINE":
                case "LINE":
                case "INSERT":
                    fields[code] = value;
                    break;
            }
        }

        FlushEntity();
        if (polyVertices.Count >= 3)
            FlushPolyline();

        return sketch;
    }

    private static void CommitLwPolyline(Dictionary<int, string> fields, List<Point2D> vertices, List<BlockItem> target)
    {
        if (vertices.Count < 3)
            return;
        int flags = fields.TryGetValue(70, out string? flagText) ? ParseInt(flagText) : 0;
        string layer = fields.TryGetValue(8, out string? layerName) ? layerName : "0";
        target.Add(new PolyItem([.. vertices], (flags & 1) != 0, layer));
    }

    private static void CommitLine(Dictionary<int, string> fields, List<BlockItem> target)
    {
        if (!fields.TryGetValue(10, out string? x1) || !fields.TryGetValue(20, out string? y1))
            return;
        if (!fields.TryGetValue(11, out string? x2) || !fields.TryGetValue(21, out string? y2))
            return;
        string layer = fields.TryGetValue(8, out string? layerName) ? layerName : "0";
        target.Add(new LineItem(
            new Point2D(ParseDouble(x1), ParseDouble(y1)),
            new Point2D(ParseDouble(x2), ParseDouble(y2)),
            layer));
    }

    private static void CommitInsert(Dictionary<int, string> fields, List<BlockItem> target)
    {
        if (!fields.TryGetValue(2, out string? name) || string.IsNullOrWhiteSpace(name))
            return;
        double x = fields.TryGetValue(10, out string? xs) ? ParseDouble(xs) : 0;
        double y = fields.TryGetValue(20, out string? ys) ? ParseDouble(ys) : 0;
        double sx = fields.TryGetValue(41, out string? sxs) ? ParseDouble(sxs) : 1;
        double sy = fields.TryGetValue(42, out string? sys) ? ParseDouble(sys) : 1;
        double rot = fields.TryGetValue(50, out string? rs) ? ParseDouble(rs) : 0;
        string layer = fields.TryGetValue(8, out string? layerName) ? layerName : "0";
        target.Add(new InsertItem(name.Trim(), new Point2D(x, y), rot, sx, sy, layer));
    }

    private static void Explode(
        BlockItem item,
        Transform2D transform,
        Dictionary<string, List<BlockItem>> blocks,
        List<BlockItem> output,
        HashSet<string> visiting,
        int depth)
    {
        if (depth > 32)
            return;

        switch (item)
        {
            case PolyItem poly:
                output.Add(new PolyItem(poly.Points.Select(transform.Apply).ToList(), poly.IsClosed, poly.Layer));
                break;
            case LineItem line:
                output.Add(new LineItem(transform.Apply(line.A), transform.Apply(line.B), line.Layer));
                break;
            case InsertItem insert:
                if (!blocks.TryGetValue(insert.Name, out List<BlockItem>? children))
                    return;
                if (!visiting.Add(insert.Name))
                    return;
                Transform2D nested = transform.Compose(Transform2D.FromInsert(insert));
                foreach (BlockItem child in children)
                    Explode(child, nested, blocks, output, visiting, depth + 1);
                visiting.Remove(insert.Name);
                break;
        }
    }

    private static bool IsUsableVertex(int flags)
    {
        const int ExtraCurveFit = 1;
        const int SplineFrame = 16;
        const int PolyfaceFace = 128;
        return (flags & ExtraCurveFit) == 0 && (flags & SplineFrame) == 0 && (flags & PolyfaceFace) == 0;
    }

    private static List<(int Code, string Value)> ReadPairs(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[22];
        int read = stream.Read(header);
        if (read >= 18 && System.Text.Encoding.ASCII.GetString(header[..18]) == "AutoCAD Binary DXF")
        {
            throw new InvalidDataException(
                "Binary DXF is not supported. Save the drawing as ASCII DXF (R12 or newer) and try again.");
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, System.Text.Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        var pairs = new List<(int, string)>();
        while (true)
        {
            string? codeLine = reader.ReadLine();
            if (codeLine is null)
                break;
            if (codeLine.Trim().Length == 0)
                continue;
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                continue;
            string? valueLine = reader.ReadLine();
            if (valueLine is null)
                break;
            pairs.Add((code, valueLine.Trim()));
        }

        return pairs;
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private sealed class DxfSketch
    {
        public List<BlockItem> Entities { get; } = [];
        public Dictionary<string, List<BlockItem>> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private abstract record BlockItem;
    private sealed record PolyItem(List<Point2D> Points, bool IsClosed, string Layer) : BlockItem;
    private sealed record LineItem(Point2D A, Point2D B, string Layer) : BlockItem;
    private sealed record InsertItem(string Name, Point2D Position, double RotationDegrees, double ScaleX, double ScaleY, string Layer) : BlockItem;

    private readonly struct Transform2D
    {
        public double M11 { get; init; }
        public double M12 { get; init; }
        public double M21 { get; init; }
        public double M22 { get; init; }
        public double Tx { get; init; }
        public double Ty { get; init; }

        public static Transform2D Identity => new() { M11 = 1, M22 = 1 };

        public static Transform2D FromInsert(InsertItem insert)
        {
            double rad = insert.RotationDegrees * Math.PI / 180.0;
            double c = Math.Cos(rad);
            double s = Math.Sin(rad);
            return new Transform2D
            {
                M11 = insert.ScaleX * c,
                M12 = -insert.ScaleY * s,
                M21 = insert.ScaleX * s,
                M22 = insert.ScaleY * c,
                Tx = insert.Position.X,
                Ty = insert.Position.Y
            };
        }

        public Transform2D Compose(Transform2D inner) => new()
        {
            M11 = M11 * inner.M11 + M12 * inner.M21,
            M12 = M11 * inner.M12 + M12 * inner.M22,
            M21 = M21 * inner.M11 + M22 * inner.M21,
            M22 = M21 * inner.M12 + M22 * inner.M22,
            Tx = M11 * inner.Tx + M12 * inner.Ty + Tx,
            Ty = M21 * inner.Tx + M22 * inner.Ty + Ty
        };

        public Point2D Apply(Point2D p) => new(
            M11 * p.X + M12 * p.Y + Tx,
            M21 * p.X + M22 * p.Y + Ty);
    }
}
