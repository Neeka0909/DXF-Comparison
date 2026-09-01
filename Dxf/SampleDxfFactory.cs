using DxfCompare.Comparison;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using Point2D = DxfCompare.Geometry.Point2D;

namespace DxfCompare.Dxf;

public static class SampleDxfFactory
{
    public static readonly IReadOnlyList<Point2D> LShape =
    [
        new(0, 0),
        new(10, 0),
        new(10, 4),
        new(4, 4),
        new(4, 12),
        new(0, 12)
    ];

    public static readonly IReadOnlyList<Point2D> Rectangle100x50 =
    [
        new(0, 0),
        new(100, 0),
        new(100, 50),
        new(0, 50)
    ];

    public static readonly IReadOnlyList<Point2D> OtherShape =
    [
        new(0, 0),
        new(8, 0),
        new(8, 5),
        new(0, 5)
    ];

    public static string WriteSamples(string directory)
    {
        Directory.CreateDirectory(directory);

        Save(Path.Combine(directory, "reference.dxf"), LShape);
        Save(Path.Combine(directory, "rotated-90.dxf"), Transform(LShape, 90, 0, 0, flipX: false, flipY: false));
        Save(Path.Combine(directory, "rotated-45.dxf"), Transform(LShape, 45, 20, -7, flipX: false, flipY: false));
        Save(Path.Combine(directory, "flipped-horizontal.dxf"), Transform(LShape, 0, 0, 0, flipX: true, flipY: false));
        Save(Path.Combine(directory, "flipped-vertical.dxf"), Transform(LShape, 0, 0, 0, flipX: false, flipY: true));
        Save(Path.Combine(directory, "flipped-and-rotated.dxf"), Transform(LShape, 35, 15, 8, flipX: true, flipY: false));
        Save(Path.Combine(directory, "translated.dxf"), Transform(LShape, 0, 50, 25, flipX: false, flipY: false));
        Save(Path.Combine(directory, "collinear-vertices.dxf"), WithCollinearPoints(LShape));
        Save(Path.Combine(directory, "different-shape.dxf"), OtherShape);
        SaveR12(Path.Combine(directory, "reference-r12.dxf"), LShape);
        SaveR12(Path.Combine(directory, "rotated-90-r12.dxf"), Transform(LShape, 90, 0, 0, flipX: false, flipY: false));
        SaveR12(Path.Combine(directory, "flipped-horizontal-r12.dxf"), Transform(LShape, 0, 0, 0, flipX: true, flipY: false));

        Save(Path.Combine(directory, "rect-100x50.dxf"), Rectangle100x50);
        Save(Path.Combine(directory, "rect-edge-all-2mm.dxf"),
            EdgeServiceApplicator.Apply(Rectangle100x50, EdgeServiceConfig.Uniform(2)));
        Save(Path.Combine(directory, "rect-edge-top-2mm.dxf"),
            EdgeServiceApplicator.Apply(Rectangle100x50, new EdgeServiceConfig { Top = 2 }));

        SaveMultiLayer(Path.Combine(directory, "multi-layer-3.dxf"),
            ("LAYER1", OtherShape),
            ("LAYER2", LShape),
            ("LAYER3", Transform(OtherShape, 0, 30, 0, false, false)));
        Save(Path.Combine(directory, "single-layer-shape.dxf"), LShape, "0");
        Save(Path.Combine(directory, "single-layer-shape-flipped.dxf"),
            Transform(LShape, 0, 0, 0, flipX: true, flipY: false), "0");

        return directory;
    }

    public static void Save(string path, IReadOnlyList<Point2D> points, string layerName = "0")
    {
        var doc = new DxfDocument();
        Layer layer = EnsureLayer(doc, layerName);
        var vertexes = points.Select(p => new Polyline2DVertex(p.X, p.Y)).ToList();
        var polyline = new Polyline2D(vertexes, isClosed: true) { Layer = layer };
        doc.Entities.Add(polyline);
        doc.Save(path);
    }

    public static void SaveMultiLayer(string path, params (string Layer, IReadOnlyList<Point2D> Points)[] layers)
    {
        var doc = new DxfDocument();
        foreach ((string layerName, IReadOnlyList<Point2D> points) in layers)
        {
            Layer layer = EnsureLayer(doc, layerName);
            var vertexes = points.Select(p => new Polyline2DVertex(p.X, p.Y)).ToList();
            doc.Entities.Add(new Polyline2D(vertexes, isClosed: true) { Layer = layer });
        }

        doc.Save(path);
    }

    private static Layer EnsureLayer(DxfDocument doc, string layerName)
    {
        return doc.Layers.Contains(layerName)
            ? doc.Layers[layerName]
            : doc.Layers.Add(new Layer(layerName));
    }

    public static void SaveR12(string path, IReadOnlyList<Point2D> points)
    {
        using var writer = new StreamWriter(path, false, System.Text.Encoding.ASCII);
        void Pair(int code, string value)
        {
            writer.WriteLine($"{code,3}");
            writer.WriteLine(value);
        }

        Pair(0, "SECTION");
        Pair(2, "HEADER");
        Pair(9, "$ACADVER");
        Pair(1, "AC1009");
        Pair(0, "ENDSEC");
        Pair(0, "SECTION");
        Pair(2, "ENTITIES");
        Pair(0, "POLYLINE");
        Pair(8, "0");
        Pair(66, "1");
        Pair(70, "1");
        foreach (Point2D p in points)
        {
            Pair(0, "VERTEX");
            Pair(8, "0");
            Pair(10, p.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
            Pair(20, p.Y.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
            Pair(30, "0.0");
        }

        Pair(0, "SEQEND");
        Pair(0, "ENDSEC");
        Pair(0, "EOF");
    }

    public static List<Point2D> Transform(
        IReadOnlyList<Point2D> source,
        double degreesCcw,
        double translateX,
        double translateY,
        bool flipX,
        bool flipY)
    {
        double radians = degreesCcw * Math.PI / 180.0;
        Point2D centroid = Centroid(source);

        var result = new List<Point2D>(source.Count);
        foreach (Point2D p in source)
        {
            Point2D centered = p - centroid;
            if (flipX)
                centered = centered with { X = -centered.X };
            if (flipY)
                centered = centered with { Y = -centered.Y };
            Point2D rotated = centered.Rotated(radians);
            result.Add(rotated + centroid + new Point2D(translateX, translateY));
        }

        return result;
    }

    private static List<Point2D> WithCollinearPoints(IReadOnlyList<Point2D> source)
    {
        var pts = source.ToList();
        pts.Insert(1, new Point2D((source[0].X + source[1].X) / 2.0, (source[0].Y + source[1].Y) / 2.0));
        pts.Insert(3, new Point2D(source[1].X, (source[1].Y + source[2].Y) / 2.0));
        return pts;
    }

    private static Point2D Centroid(IReadOnlyList<Point2D> pts)
    {
        double x = 0, y = 0;
        foreach (Point2D p in pts)
        {
            x += p.X;
            y += p.Y;
        }

        return new Point2D(x / pts.Count, y / pts.Count);
    }
}
