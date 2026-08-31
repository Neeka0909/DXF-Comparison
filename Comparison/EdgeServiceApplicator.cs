using DxfCompare.Geometry;

namespace DxfCompare.Comparison;

public static class EdgeServiceApplicator
{
    private const double ParallelTolerance = 1e-10;

    /// <summary>
    /// Expands a polygon outward by the per-side edge service values.
    /// Each edge is offset along its outward normal; new vertices come from intersecting adjacent offset lines.
    /// </summary>
    public static List<Point2D> Apply(IReadOnlyList<Point2D> polygon, EdgeServiceConfig config)
    {
        if (!config.IsActive)
            return polygon.ToList();

        List<Point2D> pts = PolygonComparer.Normalize([.. polygon]);
        if (pts.Count < 3)
            return pts;

        int n = pts.Count;
        var offsetLines = new (Point2D Origin, Point2D Dir)[n];

        for (int i = 0; i < n; i++)
        {
            Point2D p1 = pts[i];
            Point2D p2 = pts[(i + 1) % n];
            Point2D d = p2 - p1;
            double len = d.Length;
            if (len < 1e-12)
            {
                offsetLines[i] = (p1, new Point2D(1, 0));
                continue;
            }

            double nx = d.Y / len;
            double ny = -d.X / len;
            double offset = OffsetForNormal(nx, ny, config);
            Point2D origin = p1 + new Point2D(nx * offset, ny * offset);
            offsetLines[i] = (origin, d);
        }

        var result = new List<Point2D>(n);
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            Point2D vertex = IntersectLines(
                offsetLines[prev].Origin,
                offsetLines[prev].Dir,
                offsetLines[i].Origin,
                offsetLines[i].Dir,
                pts[i]);
            result.Add(vertex);
        }

        return PolygonComparer.Normalize(result);
    }

    private static double OffsetForNormal(double nx, double ny, EdgeServiceConfig config)
    {
        if (Math.Abs(ny) >= Math.Abs(nx))
            return ny >= 0 ? config.Top : config.Bottom;
        return nx >= 0 ? config.Right : config.Left;
    }

    private static Point2D IntersectLines(Point2D o1, Point2D d1, Point2D o2, Point2D d2, Point2D fallback)
    {
        double cross = Cross(d1, d2);
        if (Math.Abs(cross) < ParallelTolerance)
        {
            Point2D mid = (o1 + o2) * 0.5;
            return mid;
        }

        Point2D diff = o2 - o1;
        double t = Cross(diff, d2) / cross;
        return o1 + d1 * t;
    }

    private static double Cross(Point2D a, Point2D b) => a.X * b.Y - a.Y * b.X;
}
