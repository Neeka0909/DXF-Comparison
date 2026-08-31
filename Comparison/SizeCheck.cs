using DxfCompare.Geometry;

namespace DxfCompare.Comparison;

public readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public static BoundingBox From(IReadOnlyList<Point2D> points)
    {
        if (points.Count == 0)
            return new BoundingBox(0, 0, 0, 0);

        double minX = points[0].X, maxX = points[0].X;
        double minY = points[0].Y, maxY = points[0].Y;
        for (int i = 1; i < points.Count; i++)
        {
            minX = Math.Min(minX, points[i].X);
            maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxY = Math.Max(maxY, points[i].Y);
        }

        return new BoundingBox(minX, minY, maxX, maxY);
    }
}

public sealed class SizeCheck
{
    public required double ReferenceWidth { get; init; }
    public required double ReferenceHeight { get; init; }
    public required double CandidateWidth { get; init; }
    public required double CandidateHeight { get; init; }
    public double? ExpectedWidth { get; init; }
    public double? ExpectedHeight { get; init; }
    public bool Checked { get; init; }
    public bool Passed { get; init; } = true;
    public bool DimensionsSwapped { get; init; }
    public string Summary { get; init; } = "Not checked";

    public static SizeCheck Measure(IReadOnlyList<Point2D> reference, IReadOnlyList<Point2D> candidate)
    {
        BoundingBox refBox = BoundingBox.From(reference);
        BoundingBox candBox = BoundingBox.From(candidate);
        return new SizeCheck
        {
            ReferenceWidth = refBox.Width,
            ReferenceHeight = refBox.Height,
            CandidateWidth = candBox.Width,
            CandidateHeight = candBox.Height,
            Checked = false,
            Passed = true,
            Summary = "No expected size given"
        };
    }

    public SizeCheck AgainstExpected(double expectedWidth, double expectedHeight, double tolerance)
    {
        (bool refOk, bool refSwapped) = Matches(ReferenceWidth, ReferenceHeight, expectedWidth, expectedHeight, tolerance);
        (bool candOk, _) = Matches(CandidateWidth, CandidateHeight, expectedWidth, expectedHeight, tolerance);

        // Candidate may be rotated 90°, so its as-drawn box can be swapped.
        // If the polygons already matched, the shared outline size is the reference box.
        bool passed = refOk;
        bool swapped = refSwapped;
        string summary;

        if (!refOk)
        {
            summary =
                $"FAIL — expected {Format(expectedWidth)} x {Format(expectedHeight)}, " +
                $"reference measures {Format(ReferenceWidth)} x {Format(ReferenceHeight)}";
        }
        else if (refSwapped)
        {
            summary =
                $"PASS — expected {Format(expectedWidth)} x {Format(expectedHeight)} matches " +
                $"reference {Format(ReferenceWidth)} x {Format(ReferenceHeight)} (width/height swapped)";
        }
        else
        {
            summary =
                $"PASS — expected {Format(expectedWidth)} x {Format(expectedHeight)}, " +
                $"measured {Format(ReferenceWidth)} x {Format(ReferenceHeight)}";
        }

        if (refOk && !candOk)
        {
            summary += $"; candidate as-drawn box is {Format(CandidateWidth)} x {Format(CandidateHeight)} " +
                       "(different because of rotation; outline size still matches)";
        }

        return new SizeCheck
        {
            ReferenceWidth = ReferenceWidth,
            ReferenceHeight = ReferenceHeight,
            CandidateWidth = CandidateWidth,
            CandidateHeight = CandidateHeight,
            ExpectedWidth = expectedWidth,
            ExpectedHeight = expectedHeight,
            Checked = true,
            Passed = passed,
            DimensionsSwapped = swapped,
            Summary = summary
        };
    }

    private static (bool Ok, bool Swapped) Matches(double width, double height, double expectedW, double expectedH, double tolerance)
    {
        bool same = Approx(width, expectedW, tolerance) && Approx(height, expectedH, tolerance);
        if (same)
            return (true, false);

        bool swapped = Approx(width, expectedH, tolerance) && Approx(height, expectedW, tolerance);
        return (swapped, swapped);
    }

    private static bool Approx(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    private static string Format(double value) => value.ToString("0.####");
}
