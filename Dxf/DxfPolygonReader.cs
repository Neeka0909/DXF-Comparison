using DxfCompare.Geometry;
using netDxf;
using netDxf.Entities;
using netDxf.Header;

namespace DxfCompare.Dxf;

public static class DxfPolygonReader
{
    internal const double EndpointTolerance = 1e-6;

    public static List<Point2D> ReadPrimaryPolygon(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("DXF file not found.", filePath);

        DxfVersion? version = TryReadVersion(filePath);
        bool useLegacyFirst = version is not null && version < DxfVersion.AutoCad2000;

        List<List<Point2D>> candidates;
        if (useLegacyFirst)
        {
            candidates = AsciiDxfParser.ReadPolygons(filePath);
        }
        else
        {
            try
            {
                candidates = ReadWithNetDxf(filePath);
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                candidates = AsciiDxfParser.ReadPolygons(filePath);
            }
        }

        if (candidates.Count == 0 && !useLegacyFirst)
            candidates = AsciiDxfParser.ReadPolygons(filePath);

        return SelectPrimaryPolygon(candidates, filePath);
    }

    internal static void AddIfPolygon(List<List<Point2D>> candidates, List<Point2D> pts, bool isClosed)
    {
        pts = StripClosingDuplicate(pts);
        if (pts.Count < 3)
            return;

        bool closed = isClosed || pts[0].DistanceTo(pts[^1]) <= EndpointTolerance;
        if (!closed)
            return;

        candidates.Add(StripClosingDuplicate(pts));
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

    private static List<List<Point2D>> ReadWithNetDxf(string filePath)
    {
        DxfDocument? doc = DxfDocument.Load(filePath);
        if (doc is null)
            throw new InvalidDataException($"Failed to load DXF file '{filePath}'.");

        var candidates = new List<List<Point2D>>();

        foreach (Polyline2D polyline in doc.Entities.Polylines2D)
        {
            List<Point2D> pts = polyline.Vertexes
                .Select(v => new Point2D(v.Position.X, v.Position.Y))
                .ToList();
            AddIfPolygon(candidates, pts, polyline.IsClosed);
        }

        foreach (Polyline3D polyline in doc.Entities.Polylines3D)
        {
            List<Point2D> pts = polyline.Vertexes
                .Select(v => new Point2D(v.X, v.Y))
                .ToList();
            AddIfPolygon(candidates, pts, polyline.IsClosed);
        }

        var segments = doc.Entities.Lines
            .Select(line => (
                new Point2D(line.StartPoint.X, line.StartPoint.Y),
                new Point2D(line.EndPoint.X, line.EndPoint.Y)))
            .ToList();
        List<Point2D>? fromLines = TryBuildPolygonFromSegments(segments);
        if (fromLines is not null)
            candidates.Add(fromLines);

        return candidates;
    }

    private static List<Point2D> SelectPrimaryPolygon(List<List<Point2D>> candidates, string filePath)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                $"No closed polygon found in '{filePath}'. Draw the shape as a closed polyline (or a closed loop of lines).");
        }

        return candidates
            .OrderByDescending(p => Math.Abs(SignedArea(p)))
            .ThenByDescending(p => p.Count)
            .First();
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
}
