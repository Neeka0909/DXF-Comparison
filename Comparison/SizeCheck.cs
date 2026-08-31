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
    public double? BaseReferenceWidth { get; init; }
    public double? BaseReferenceHeight { get; init; }
    public EdgeServiceConfig? EdgeService { get; init; }
    public double? ExpectedWidth { get; init; }
    public double? ExpectedHeight { get; init; }
    public bool Checked { get; init; }
    public bool Passed { get; init; } = true;
    public bool DimensionsSwapped { get; init; }
    public string Summary { get; init; } = "Not checked";

    public static SizeCheck Measure(
        IReadOnlyList<Point2D> reference,
        IReadOnlyList<Point2D> candidate,
        IReadOnlyList<Point2D>? baseReference = null,
        EdgeServiceConfig? edgeService = null)
    {
        BoundingBox refBox = BoundingBox.From(reference);
        BoundingBox candBox = BoundingBox.From(candidate);
        BoundingBox? baseBox = baseReference is not null ? BoundingBox.From(baseReference) : null;

        return new SizeCheck
        {
            ReferenceWidth = refBox.Width,
            ReferenceHeight = refBox.Height,
            CandidateWidth = candBox.Width,
            CandidateHeight = candBox.Height,
            BaseReferenceWidth = baseBox?.Width,
            BaseReferenceHeight = baseBox?.Height,
            EdgeService = edgeService?.IsActive == true ? edgeService : null,
            Checked = false,
            Passed = true,
            Summary = edgeService?.IsActive == true
                ? $"Base size {baseBox?.Width:0.####} x {baseBox?.Height:0.####}; after edge service {refBox.Width:0.####} x {refBox.Height:0.####}"
                : "No expected size given"
        };
    }

    public SizeCheck AgainstExpected(double expectedWidth, double expectedHeight, double tolerance)
    {
        (bool refOk, bool refSwapped) = Matches(ReferenceWidth, ReferenceHeight, expectedWidth, expectedHeight, tolerance);
        (bool candOk, _) = Matches(CandidateWidth, CandidateHeight, expectedWidth, expectedHeight, tolerance);

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

        return CopyWithExpected(expectedWidth, expectedHeight, passed, swapped, summary);
    }

    public SizeCheck AgainstEdgeService(EdgeServiceConfig edgeService, double tolerance)
    {
        if (BaseReferenceWidth is null || BaseReferenceHeight is null)
            throw new InvalidOperationException("Base reference size is required for edge service validation.");

        (double expectedW, double expectedH) = edgeService.ExpandSize(BaseReferenceWidth.Value, BaseReferenceHeight.Value);

        // After edge service, validate the candidate (expanded shape) against computed expected size.
        (bool candOk, bool candSwapped) = Matches(CandidateWidth, CandidateHeight, expectedW, expectedH, tolerance);
        (bool refOk, _) = Matches(ReferenceWidth, ReferenceHeight, expectedW, expectedH, tolerance);

        bool passed = candOk && refOk;
        string summary;

        if (!passed)
        {
            summary =
                $"FAIL — base {Format(BaseReferenceWidth.Value)} x {Format(BaseReferenceHeight.Value)} + " +
                $"edge service ({edgeService.Describe()}) => expected {Format(expectedW)} x {Format(expectedH)}, " +
                $"candidate measures {Format(CandidateWidth)} x {Format(CandidateHeight)}";
        }
        else if (candSwapped)
        {
            summary =
                $"PASS — base {Format(BaseReferenceWidth.Value)} x {Format(BaseReferenceHeight.Value)} + " +
                $"edge service ({edgeService.Describe()}) => {Format(expectedW)} x {Format(expectedH)} " +
                $"(width/height swapped in drawing)";
        }
        else
        {
            summary =
                $"PASS — base {Format(BaseReferenceWidth.Value)} x {Format(BaseReferenceHeight.Value)} + " +
                $"edge service ({edgeService.Describe()}) => {Format(expectedW)} x {Format(expectedH)}";
        }

        return CopyWithExpected(expectedW, expectedH, passed, candSwapped, summary);
    }

    private SizeCheck CopyWithExpected(double expectedW, double expectedH, bool passed, bool swapped, string summary) =>
        new()
        {
            ReferenceWidth = ReferenceWidth,
            ReferenceHeight = ReferenceHeight,
            CandidateWidth = CandidateWidth,
            CandidateHeight = CandidateHeight,
            BaseReferenceWidth = BaseReferenceWidth,
            BaseReferenceHeight = BaseReferenceHeight,
            EdgeService = EdgeService,
            ExpectedWidth = expectedW,
            ExpectedHeight = expectedH,
            Checked = true,
            Passed = passed,
            DimensionsSwapped = swapped,
            Summary = summary
        };

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
