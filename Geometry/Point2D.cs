namespace DxfCompare.Geometry;

public readonly record struct Point2D(double X, double Y)
{
    public static Point2D operator +(Point2D a, Point2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator *(Point2D a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(Point2D other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public Point2D Rotated(double angleRadians)
    {
        double c = Math.Cos(angleRadians);
        double s = Math.Sin(angleRadians);
        return new(c * X - s * Y, s * X + c * Y);
    }

    public override string ToString() => $"({X:G6}, {Y:G6})";
}
