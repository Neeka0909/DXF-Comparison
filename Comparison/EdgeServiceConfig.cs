namespace DxfCompare.Comparison;

/// <summary>
/// Per-side edge service allowance added outward from the base shape outline.
/// Values are in drawing units (e.g. mm).
/// </summary>
public sealed class EdgeServiceConfig
{
    public double Top { get; init; }
    public double Bottom { get; init; }
    public double Left { get; init; }
    public double Right { get; init; }

    public bool IsActive => Top > 0 || Bottom > 0 || Left > 0 || Right > 0;

    public static EdgeServiceConfig None => new();

    public static EdgeServiceConfig Uniform(double value) => new()
    {
        Top = value,
        Bottom = value,
        Left = value,
        Right = value
    };

    public (double Width, double Height) ExpandSize(double baseWidth, double baseHeight) =>
        (baseWidth + Left + Right, baseHeight + Top + Bottom);

    public string Describe()
    {
        if (!IsActive)
            return "None";

        if (Top == Bottom && Bottom == Left && Left == Right)
            return $"{Top:0.####} on all sides";

        return $"top={Top:0.####}, bottom={Bottom:0.####}, left={Left:0.####}, right={Right:0.####}";
    }
}
