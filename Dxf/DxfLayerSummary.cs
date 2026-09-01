namespace DxfCompare.Dxf;

public sealed class DxfLayerSummary
{
    public required string Name { get; init; }
    public int PolylineCount { get; init; }
    public int LineCount { get; init; }
    public bool HasClosedPolygon { get; init; }

    public int EntityCount => PolylineCount + LineCount;
}
