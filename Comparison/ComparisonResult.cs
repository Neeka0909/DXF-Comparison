namespace DxfCompare.Comparison;

public sealed class ComparisonResult
{
    public required bool IsMatch { get; init; }
    public required string Message { get; init; }
    public int VertexCount { get; init; }
    public bool IsFlipped { get; init; }
    public string FlipSide { get; init; } = "None";
    public string FlipDescription { get; init; } = "Not flipped";
    public double RotationDegreesCcw { get; init; }
    public double RotationDegreesCw { get; init; }
    public double MirrorAxisDegrees { get; init; }
    public double FitError { get; init; }
    public string TransformSummary { get; init; } = "No match";

    public static ComparisonResult NoMatch(string message, int vertexCount = 0) => new()
    {
        IsMatch = false,
        Message = message,
        VertexCount = vertexCount,
        TransformSummary = "No match"
    };

    public static ComparisonResult Error(string message) => new()
    {
        IsMatch = false,
        Message = message,
        TransformSummary = "Error"
    };
}
