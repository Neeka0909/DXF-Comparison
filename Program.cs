using System.Globalization;
using System.Text.Json;
using DxfCompare.Comparison;
using DxfCompare.Dxf;
using DxfCompare.Geometry;

namespace DxfCompare;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (args.Contains("--self-test"))
            return RunSelfTest();

        if (args.Contains("--write-samples"))
        {
            string dir = GetOptionValue(args, "--write-samples") ?? Path.Combine(Environment.CurrentDirectory, "samples");
            SampleDxfFactory.WriteSamples(dir);
            Console.WriteLine($"Wrote sample DXF files to: {dir}");
            return 0;
        }

        string[] positional = args.Where(a => !a.StartsWith('-')).ToArray();
        if (positional.Length != 2)
        {
            PrintUsage();
            return 2;
        }

        bool json = args.Contains("--json");
        double tolerance = 1e-4;
        string? tolRaw = GetOptionValue(args, "--tolerance");
        if (tolRaw is not null)
        {
            if (!double.TryParse(tolRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance) || tolerance <= 0)
            {
                Console.Error.WriteLine("Error: --tolerance must be a positive number.");
                return 2;
            }
        }

        string referencePath = Path.GetFullPath(positional[0]);
        string candidatePath = Path.GetFullPath(positional[1]);

        List<Point2D> shapeA = DxfPolygonReader.ReadPrimaryPolygon(referencePath);
        List<Point2D> shapeB = DxfPolygonReader.ReadPrimaryPolygon(candidatePath);
        ComparisonResult result = PolygonComparer.Compare(shapeA, shapeB, tolerance);

        if (json)
            PrintJson(referencePath, candidatePath, result);
        else
            PrintText(referencePath, candidatePath, result);

        return result.IsMatch ? 0 : 1;
    }

    private static void PrintText(string referencePath, string candidatePath, ComparisonResult result)
    {
        Console.WriteLine("DXF polygon comparison");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"Reference : {referencePath}");
        Console.WriteLine($"Candidate : {candidatePath}");
        Console.WriteLine($"Result    : {(result.IsMatch ? "MATCH" : "NO MATCH")}");
        Console.WriteLine($"Vertices  : {result.VertexCount}");
        Console.WriteLine($"Transform : {result.TransformSummary}");
        Console.WriteLine($"Flipped   : {(result.IsFlipped ? "Yes" : "No")}");
        Console.WriteLine($"Flip side : {result.FlipDescription}");
        Console.WriteLine($"Rotation  : {result.RotationDegreesCcw:0.##}° CCW  ({result.RotationDegreesCw:0.##}° CW)");
        if (result.IsFlipped)
            Console.WriteLine($"Mirror    : {DescribeAxis(result.MirrorAxisDegrees)}");
        Console.WriteLine($"Fit error : {result.FitError:G4}");
        Console.WriteLine($"Details   : {result.Message}");
    }

    private static void PrintJson(string referencePath, string candidatePath, ComparisonResult result)
    {
        var payload = new
        {
            reference = referencePath,
            candidate = candidatePath,
            match = result.IsMatch,
            vertexCount = result.VertexCount,
            flipped = result.IsFlipped,
            flipSide = result.FlipSide,
            flipDescription = result.FlipDescription,
            rotationDegreesCcw = Math.Round(result.RotationDegreesCcw, 4),
            rotationDegreesCw = Math.Round(result.RotationDegreesCw, 4),
            mirrorAxisDegrees = result.MirrorAxisDegrees,
            transform = result.TransformSummary,
            fitError = result.FitError,
            message = result.Message
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string DescribeAxis(double degrees)
    {
        return Math.Abs(degrees - 90) < 1
            ? "Vertical axis (left-right flip)"
            : "Horizontal axis (up-down flip)";
    }

    private static int RunSelfTest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dxf-compare-selftest");
        SampleDxfFactory.WriteSamples(dir);

        var cases = new (string File, bool Match, bool Flipped, string FlipSide, double Rotation)[]
        {
            ("reference.dxf", true, false, "None", 0),
            ("translated.dxf", true, false, "None", 0),
            ("rotated-90.dxf", true, false, "None", 90),
            ("rotated-45.dxf", true, false, "None", 45),
            ("collinear-vertices.dxf", true, false, "None", 0),
            ("flipped-horizontal.dxf", true, true, "Horizontal", 0),
            ("flipped-vertical.dxf", true, true, "Vertical", 0),
            ("flipped-and-rotated.dxf", true, true, "Horizontal", 35),
            ("different-shape.dxf", false, false, "None", 0),
            ("reference-r12.dxf", true, false, "None", 0),
            ("rotated-90-r12.dxf", true, false, "None", 90),
            ("flipped-horizontal-r12.dxf", true, true, "Horizontal", 0)
        };

        string reference = Path.Combine(dir, "reference.dxf");
        int failed = 0;

        foreach ((string file, bool match, bool flipped, string flipSide, double rotation) in cases)
        {
            List<Point2D> a = DxfPolygonReader.ReadPrimaryPolygon(reference);
            List<Point2D> b = DxfPolygonReader.ReadPrimaryPolygon(Path.Combine(dir, file));
            ComparisonResult result = PolygonComparer.Compare(a, b);

            bool ok = result.IsMatch == match;
            if (match)
            {
                ok &= result.IsFlipped == flipped;
                ok &= result.FlipSide == flipSide;
                ok &= AnglesClose(result.RotationDegreesCcw, rotation);
            }

            string status = ok ? "PASS" : "FAIL";
            if (!ok)
                failed++;

            Console.WriteLine(
                $"{status,-4} {file,-26} match={result.IsMatch,-5} flip={result.FlipSide,-11} rot={result.RotationDegreesCcw,7:0.##}°  {result.TransformSummary}");
            if (!ok)
                Console.WriteLine($"     expected match={match} flip={flipSide} rot={rotation}° | {result.Message}");
        }

        // Both files AutoCAD 12
        {
            List<Point2D> a = DxfPolygonReader.ReadPrimaryPolygon(Path.Combine(dir, "reference-r12.dxf"));
            List<Point2D> b = DxfPolygonReader.ReadPrimaryPolygon(Path.Combine(dir, "rotated-90-r12.dxf"));
            ComparisonResult result = PolygonComparer.Compare(a, b);
            bool ok = result.IsMatch && !result.IsFlipped && AnglesClose(result.RotationDegreesCcw, 90);
            if (!ok)
                failed++;
            Console.WriteLine(
                $"{(ok ? "PASS" : "FAIL"),-4} {"r12-vs-r12-rot90",-26} match={result.IsMatch,-5} flip={result.FlipSide,-11} rot={result.RotationDegreesCcw,7:0.##}°  {result.TransformSummary}");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "All tests passed."
            : $"{failed} tests failed.");
        Console.WriteLine($"Sample files: {dir}");
        return failed == 0 ? 0 : 1;
    }

    private static bool AnglesClose(double actual, double expected)
    {
        double delta = Math.Abs(actual - expected) % 360.0;
        if (delta > 180)
            delta = 360 - delta;
        return delta < 0.2;
    }

    private static string? GetOptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name)
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    return args[i + 1];
                return null;
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DxfCompare — compare two DXF polygon shapes (rotation and flip allowed)

            Usage:
              DxfCompare <reference.dxf> <candidate.dxf> [--json] [--tolerance 0.0001]
              DxfCompare --write-samples [folder]
              DxfCompare --self-test
              DxfCompare --help

            Exit codes:
              0  shapes match (same polygon, possibly rotated and/or flipped)
              1  shapes do not match
              2  usage or file error

            The candidate may be translated, rotated, or flipped relative to the reference.
            The report includes the CCW rotation angle and whether the flip is horizontal
            (left-right) or vertical (up-down).

            ASCII DXF R12 (AutoCAD 12 / AC1009) through current versions are supported.
            """);
    }
}
