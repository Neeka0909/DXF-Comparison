using DxfCompare.Geometry;
using netDxf;
using netDxf.Entities;
using netDxf.Header;

namespace DxfCompare.Dxf;

public static class DxfPolygonReader
{
    internal const double EndpointTolerance = 1e-6;

    public static List<Point2D> ReadPrimaryPolygon(string filePath) =>
        ReadPolygon(filePath, layerName: null);

    public static List<Point2D> ReadPolygon(string filePath, string? layerName = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("DXF file not found.", filePath);

        DxfVersion? version = TryReadVersion(filePath);
        bool useLegacyFirst = version is not null && version < DxfVersion.AutoCad2000;

        List<LayerPolygonCandidate> candidates;
        if (useLegacyFirst)
        {
            candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);
        }
        else
        {
            try
            {
                candidates = ReadCandidatesWithNetDxf(filePath);
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);
            }
        }

        if (candidates.Count == 0 && !useLegacyFirst)
            candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);

        return SelectPrimaryPolygon(candidates, filePath, layerName);
    }

    public static IReadOnlyList<DxfLayerSummary> ListLayers(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("DXF file not found.", filePath);

        DxfVersion? version = TryReadVersion(filePath);
        bool useLegacyFirst = version is not null && version < DxfVersion.AutoCad2000;

        List<LayerPolygonCandidate> candidates;
        if (useLegacyFirst)
        {
            candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);
        }
        else
        {
            try
            {
                candidates = ReadCandidatesWithNetDxf(filePath);
            }
            catch
            {
                candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);
            }
        }

        if (candidates.Count == 0)
            candidates = AsciiDxfParser.ReadPolygonCandidates(filePath);

        return SummarizeLayers(candidates);
    }

    internal static void AddIfPolygon(List<LayerPolygonCandidate> candidates, List<Point2D> pts, bool isClosed, string layer)
    {
        pts = StripClosingDuplicate(pts);
        if (pts.Count < 3)
            return;

        bool closed = isClosed || pts[0].DistanceTo(pts[^1]) <= EndpointTolerance;
        if (!closed)
            return;

        candidates.Add(new LayerPolygonCandidate(StripClosingDuplicate(pts), layer));
    }

    internal static List<Point2D>? TryBuildPolygonFromSegments(List<(Point2D A, Point2D B)> segments)
    {
        if (segments.Count < 3)
            return null;

        var unused = segments
            .Where(seg => seg.A.DistanceTo(seg.B) > EndpointTolerance)
            .ToList();

        if (unused.Count < 3)
            return null;

        var ring = new List<Point2D> { unused[0].A, unused[0].B };
        unused.RemoveAt(0);

        while (unused.Count > 0)
        {
            Point2D tip = ring[^1];
            int found = unused.FindIndex(seg =>
                seg.A.DistanceTo(tip) <= EndpointTolerance ||
                seg.B.DistanceTo(tip) <= EndpointTolerance);

            if (found < 0)
                break;

            (Point2D a, Point2D b) = unused[found];
            unused.RemoveAt(found);
            Point2D next = a.DistanceTo(tip) <= EndpointTolerance ? b : a;
            if (next.DistanceTo(ring[0]) <= EndpointTolerance)
                return ring.Count >= 3 ? ring : null;

            ring.Add(next);
        }

        return ring.Count >= 3 && ring[0].DistanceTo(ring[^1]) <= EndpointTolerance
            ? StripClosingDuplicate(ring)
            : null;
    }

    internal static bool LayerMatches(string entityLayer, string? requestedLayer)
    {
        if (string.IsNullOrWhiteSpace(requestedLayer))
            return true;
        return string.Equals(entityLayer.Trim(), requestedLayer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<LayerPolygonCandidate> ReadCandidatesWithNetDxf(string filePath)
    {
        DxfDocument? doc = DxfDocument.Load(filePath);
        if (doc is null)
            throw new InvalidDataException($"Failed to load DXF file '{filePath}'.");

        var candidates = new List<LayerPolygonCandidate>();
        var linesByLayer = new Dictionary<string, List<(Point2D A, Point2D B)>>(StringComparer.OrdinalIgnoreCase);

        foreach (Polyline2D polyline in doc.Entities.Polylines2D)
        {
            string layer = polyline.Layer?.Name ?? "0";
            List<Point2D> pts = polyline.Vertexes
                .Select(v => new Point2D(v.Position.X, v.Position.Y))
                .ToList();
            AddIfPolygon(candidates, pts, polyline.IsClosed, layer);
        }

        foreach (Polyline3D polyline in doc.Entities.Polylines3D)
        {
            string layer = polyline.Layer?.Name ?? "0";
            List<Point2D> pts = polyline.Vertexes
                .Select(v => new Point2D(v.X, v.Y))
                .ToList();
            AddIfPolygon(candidates, pts, polyline.IsClosed, layer);
        }

        foreach (Line line in doc.Entities.Lines)
        {
            string layer = line.Layer?.Name ?? "0";
            if (!linesByLayer.TryGetValue(layer, out List<(Point2D, Point2D)>? bucket))
            {
                bucket = [];
                linesByLayer[layer] = bucket;
            }

            bucket.Add((
                new Point2D(line.StartPoint.X, line.StartPoint.Y),
                new Point2D(line.EndPoint.X, line.EndPoint.Y)));
        }

        foreach ((string layer, List<(Point2D A, Point2D B)> segments) in linesByLayer)
        {
            List<Point2D>? fromLines = TryBuildPolygonFromSegments(segments);
            if (fromLines is not null)
                candidates.Add(new LayerPolygonCandidate(fromLines, layer));
        }

        return candidates;
    }

    private static List<Point2D> SelectPrimaryPolygon(
        List<LayerPolygonCandidate> candidates,
        string filePath,
        string? layerName)
    {
        IEnumerable<LayerPolygonCandidate> filtered = candidates;
        if (!string.IsNullOrWhiteSpace(layerName))
            filtered = candidates.Where(c => LayerMatches(c.Layer, layerName));

        var polygons = filtered.ToList();
        if (polygons.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                IReadOnlyList<DxfLayerSummary> layers = SummarizeLayers(candidates);
                string available = layers.Count == 0
                    ? "No layers with closed polygon geometry were found."
                    : "Available layers: " + string.Join(", ",
                        layers.Select(l => $"'{l.Name}' ({l.PolylineCount} polyline(s), closed={l.HasClosedPolygon})"));

                throw new InvalidDataException(
                    $"No closed polygon found on layer '{layerName}' in '{filePath}'. {available}");
            }

            throw new InvalidDataException(
                $"No closed polygon found in '{filePath}'. Draw the shape as a closed polyline (or a closed loop of lines).");
        }

        LayerPolygonCandidate best = polygons
            .OrderByDescending(p => Math.Abs(SignedArea(p.Points)))
            .ThenByDescending(p => p.Points.Count)
            .First();

        return best.Points;
    }

    private static IReadOnlyList<DxfLayerSummary> SummarizeLayers(List<LayerPolygonCandidate> candidates)
    {
        return candidates
            .GroupBy(c => c.Layer, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DxfLayerSummary
            {
                Name = g.First().Layer,
                PolylineCount = g.Count(),
                LineCount = 0,
                HasClosedPolygon = g.Any()
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DxfVersion? TryReadVersion(string filePath)
    {
        try
        {
            return DxfDocument.CheckDxfFileVersion(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static List<Point2D> StripClosingDuplicate(List<Point2D> pts)
    {
        if (pts.Count >= 2 && pts[0].DistanceTo(pts[^1]) <= EndpointTolerance)
            return pts.Take(pts.Count - 1).ToList();
        return pts;
    }

    private static double SignedArea(List<Point2D> pts)
    {
        double area = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            Point2D a = pts[i];
            Point2D b = pts[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    internal sealed record LayerPolygonCandidate(List<Point2D> Points, string Layer);
}
