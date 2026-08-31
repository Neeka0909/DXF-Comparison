using DxfCompare.Geometry;

namespace DxfCompare.Comparison;

public static class PolygonComparer
{
    public static ComparisonResult Compare(List<Point2D> polyA, List<Point2D> polyB, double relativeTolerance = 1e-4)
    {
        if (polyA is null || polyB is null)
            return ComparisonResult.NoMatch("One or both polygons are missing.");

        List<Point2D> a = Normalize(polyA);
        List<Point2D> b = Normalize(polyB);

        if (a.Count < 3 || b.Count < 3)
            return ComparisonResult.NoMatch("A valid polygon needs at least 3 vertices after cleanup.");

        if (a.Count != b.Count)
        {
            return ComparisonResult.NoMatch(
                $"Vertex counts differ after removing collinear points ({a.Count} vs {b.Count}).",
                a.Count);
        }

        double scale = Math.Max(RmsRadius(a), 1e-9);
        double absTol = Math.Max(1e-8, relativeTolerance * scale);
        double lengthTol = Math.Max(1e-8, relativeTolerance * Math.Max(Perimeter(a), Perimeter(b)) / a.Count);

        CandidateFit? best = null;

        for (int shift = 0; shift < a.Count; shift++)
        {
            List<Point2D> mapped = MapSameWinding(b, shift);
            if (!EdgesMatch(a, mapped, lengthTol))
                continue;

            CandidateFit fit = FitWithoutFlip(a, mapped, absTol);
            best = Better(best, fit);
        }

        for (int shift = 0; shift < a.Count; shift++)
        {
            List<Point2D> mapped = MapReversed(b, shift);
            if (!EdgesMatch(a, mapped, lengthTol))
                continue;

            CandidateFit fit = FitWithFlip(a, mapped, absTol);
            best = Better(best, fit);
        }

        if (best is null || !best.IsMatch)
        {
            return ComparisonResult.NoMatch(
                "The polygons are not the same shape (even allowing rotation or flipping).",
                a.Count);
        }

        return ToResult(best, a.Count);
    }

    public static List<Point2D> Normalize(List<Point2D> pts)
    {
        List<Point2D> cleaned = RemoveDuplicates(pts);
        cleaned = RemoveCollinearPoints(cleaned);
        if (cleaned.Count >= 3 && SignedArea(cleaned) < 0)
            cleaned.Reverse();
        return cleaned;
    }

    public static List<Point2D> RemoveCollinearPoints(List<Point2D> pts, double tolerance = 1e-5)
    {
        if (pts.Count <= 3)
            return [.. pts];

        var simplified = new List<Point2D>();
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            Point2D prev = pts[(i - 1 + n) % n];
            Point2D curr = pts[i];
            Point2D next = pts[(i + 1) % n];
            if (!IsCollinear(prev, curr, next, tolerance))
                simplified.Add(curr);
        }

        return simplified.Count >= 3 ? simplified : [.. pts];
    }

    private static List<Point2D> RemoveDuplicates(List<Point2D> pts, double tolerance = 1e-8)
    {
        var result = new List<Point2D>();
        foreach (Point2D p in pts)
        {
            if (result.Count == 0 || result[^1].DistanceTo(p) > tolerance)
                result.Add(p);
        }

        if (result.Count > 1 && result[0].DistanceTo(result[^1]) <= tolerance)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static bool IsCollinear(Point2D p1, Point2D p2, Point2D p3, double tolerance)
    {
        double v1x = p2.X - p1.X;
        double v1y = p2.Y - p1.Y;
        double v2x = p3.X - p2.X;
        double v2y = p3.Y - p2.Y;

        double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
        double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
        if (len1 < tolerance || len2 < tolerance)
            return true;

        v1x /= len1;
        v1y /= len1;
        v2x /= len2;
        v2y /= len2;

        double cross = v1x * v2y - v1y * v2x;
        double dot = v1x * v2x + v1y * v2y;
        return Math.Abs(cross) < tolerance && dot > 0;
    }

    private static List<Point2D> MapSameWinding(List<Point2D> poly, int shift)
    {
        int n = poly.Count;
        var mapped = new List<Point2D>(n);
        for (int i = 0; i < n; i++)
            mapped.Add(poly[(i + shift) % n]);
        return mapped;
    }

    private static List<Point2D> MapReversed(List<Point2D> poly, int shift)
    {
        int n = poly.Count;
        var mapped = new List<Point2D>(n);
        for (int i = 0; i < n; i++)
            mapped.Add(poly[(shift - i + n) % n]);
        return mapped;
    }

    private static bool EdgesMatch(List<Point2D> a, List<Point2D> b, double lengthTol)
    {
        int n = a.Count;
        for (int i = 0; i < n; i++)
        {
            double la = a[i].DistanceTo(a[(i + 1) % n]);
            double lb = b[i].DistanceTo(b[(i + 1) % n]);
            if (Math.Abs(la - lb) > lengthTol)
                return false;
        }

        return true;
    }

    private static CandidateFit FitWithoutFlip(List<Point2D> a, List<Point2D> b, double absTol)
    {
        (Point2D[] ca, Point2D[] cb) = CenterPair(a, b);
        (double angle, double rmsd) = FitRotation(ca, cb);
        bool match = rmsd <= absTol;
        double deg = NormalizeDegrees(ToDegrees(angle));
        return new CandidateFit
        {
            IsMatch = match,
            IsFlipped = false,
            RotationRadians = angle,
            FitError = rmsd,
            FlipSide = "None",
            FlipDescription = "Not flipped",
            MirrorAxisDegrees = 0,
            TransformSummary = match
                ? (IsNearZeroDegrees(deg)
                    ? "Same orientation (no rotation, no flip)"
                    : $"Rotated {FormatDegrees(deg)} CCW")
                : "No match"
        };
    }

    private static CandidateFit FitWithFlip(List<Point2D> a, List<Point2D> b, double absTol)
    {
        (Point2D[] ca, Point2D[] cb) = CenterPair(a, b);

        // Candidate ≈ Rotate(Flip(reference)). Flip the reference, then find remaining rotation onto B.
        Point2D[] flippedHorizontal = ca.Select(p => p with { X = -p.X }).ToArray();
        Point2D[] flippedVertical = ca.Select(p => p with { Y = -p.Y }).ToArray();

        (double angleH, double rmsdH) = FitRotation(flippedHorizontal, cb);
        (double angleV, double rmsdV) = FitRotation(flippedVertical, cb);

        double degH = SmallestSignedDegrees(ToDegrees(angleH));
        double degV = SmallestSignedDegrees(ToDegrees(angleV));

        bool preferHorizontal = rmsdH < rmsdV - absTol * 0.1
            || (Math.Abs(rmsdH - rmsdV) <= absTol * 0.1 && Math.Abs(degH) <= Math.Abs(degV));

        double rmsd = preferHorizontal ? rmsdH : rmsdV;
        double angle = preferHorizontal ? angleH : angleV;
        double deg = NormalizeDegrees(ToDegrees(angle));
        bool match = rmsd <= absTol;

        string side = preferHorizontal ? "Horizontal" : "Vertical";
        string sideLong = preferHorizontal
            ? "Horizontal (left-right, mirror over the vertical axis)"
            : "Vertical (up-down, mirror over the horizontal axis)";

        double axis = preferHorizontal ? 90 : 0;
        string summary;
        if (!match)
        {
            summary = "No match";
        }
        else if (IsNearZeroDegrees(deg))
        {
            summary = preferHorizontal
                ? "Flipped horizontally (left-right, no extra rotation)"
                : "Flipped vertically (up-down, no extra rotation)";
        }
        else
        {
            string adverb = preferHorizontal ? "horizontally" : "vertically";
            summary = $"Flipped {adverb}, then rotated {FormatDegrees(deg)} CCW";
        }

        return new CandidateFit
        {
            IsMatch = match,
            IsFlipped = true,
            RotationRadians = angle,
            FitError = rmsd,
            FlipSide = side,
            FlipDescription = sideLong,
            MirrorAxisDegrees = axis,
            TransformSummary = summary
        };
    }

    private static (Point2D[] CenteredA, Point2D[] CenteredB) CenterPair(List<Point2D> a, List<Point2D> b)
    {
        Point2D ca = Centroid(a);
        Point2D cb = Centroid(b);
        var centeredA = a.Select(p => p - ca).ToArray();
        var centeredB = b.Select(p => p - cb).ToArray();
        return (centeredA, centeredB);
    }

    private static (double AngleRadians, double Rmsd) FitRotation(Point2D[] a, Point2D[] b)
    {
        double dot = 0;
        double cross = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i].X * b[i].X + a[i].Y * b[i].Y;
            cross += a[i].X * b[i].Y - a[i].Y * b[i].X;
        }

        double angle = Math.Atan2(cross, dot);
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        double sse = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double rx = c * a[i].X - s * a[i].Y;
            double ry = s * a[i].X + c * a[i].Y;
            double dx = rx - b[i].X;
            double dy = ry - b[i].Y;
            sse += dx * dx + dy * dy;
        }

        return (angle, Math.Sqrt(sse / a.Length));
    }

    private static CandidateFit Better(CandidateFit? current, CandidateFit candidate)
    {
        if (!candidate.IsMatch)
            return current ?? candidate;
        if (current is null || !current.IsMatch)
            return candidate;

        if (candidate.FitError < current.FitError * 0.5)
            return candidate;
        if (current.FitError < candidate.FitError * 0.5)
            return current;

        double currentAbs = Math.Abs(SmallestSignedDegrees(ToDegrees(current.RotationRadians)));
        double candidateAbs = Math.Abs(SmallestSignedDegrees(ToDegrees(candidate.RotationRadians)));
        if (candidate.IsFlipped == current.IsFlipped)
            return candidateAbs < currentAbs ? candidate : current;

        return current.IsFlipped ? current : candidate;
    }

    private static ComparisonResult ToResult(CandidateFit fit, int vertexCount)
    {
        double ccw = NormalizeDegrees(ToDegrees(fit.RotationRadians));
        double cw = NormalizeDegrees(360 - ccw);
        return new ComparisonResult
        {
            IsMatch = true,
            Message = "The DXF shapes match (same polygon, allowing rotation and/or flipping).",
            VertexCount = vertexCount,
            IsFlipped = fit.IsFlipped,
            FlipSide = fit.FlipSide,
            FlipDescription = fit.FlipDescription,
            RotationDegreesCcw = ccw,
            RotationDegreesCw = IsNearZeroDegrees(ccw) ? 0 : cw,
            MirrorAxisDegrees = fit.MirrorAxisDegrees,
            FitError = fit.FitError,
            TransformSummary = fit.TransformSummary
        };
    }

    private static Point2D Centroid(List<Point2D> pts)
    {
        double x = 0, y = 0;
        foreach (Point2D p in pts)
        {
            x += p.X;
            y += p.Y;
        }

        return new Point2D(x / pts.Count, y / pts.Count);
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

    private static double Perimeter(List<Point2D> pts)
    {
        double p = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
            p += pts[i].DistanceTo(pts[(i + 1) % n]);
        return p;
    }

    private static double RmsRadius(List<Point2D> pts)
    {
        Point2D c = Centroid(pts);
        double sum = 0;
        foreach (Point2D p in pts)
        {
            Point2D d = p - c;
            sum += d.X * d.X + d.Y * d.Y;
        }

        return Math.Sqrt(sum / pts.Count);
    }

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0)
            degrees += 360.0;
        if (degrees >= 360.0 - 1e-8)
            degrees = 0;
        return degrees;
    }

    private static double SmallestSignedDegrees(double degrees)
    {
        degrees = NormalizeDegrees(degrees);
        if (degrees > 180)
            degrees -= 360;
        return degrees;
    }

    private static bool IsNearZeroDegrees(double degrees)
    {
        double wrapped = NormalizeDegrees(degrees);
        return wrapped < 0.05 || wrapped > 359.95;
    }

    private static string FormatDegrees(double degrees) => $"{degrees:0.##}°";

    private sealed class CandidateFit
    {
        public bool IsMatch { get; init; }
        public bool IsFlipped { get; init; }
        public double RotationRadians { get; init; }
        public double FitError { get; init; }
        public string FlipSide { get; init; } = "None";
        public string FlipDescription { get; init; } = "Not flipped";
        public double MirrorAxisDegrees { get; init; }
        public string TransformSummary { get; init; } = "";
    }
}
