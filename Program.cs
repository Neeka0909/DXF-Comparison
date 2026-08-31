using System.Globalization;
using System.Text.Json;
using DxfCompare.Comparison;
using DxfCompare.Dxf;
using DxfCompare.Geometry;

namespace DxfCompare;

public static class Program
{
    private static readonly string[] ValueOptions =
        ["--tolerance", "--width", "--height", "--size-tolerance", "--write-samples", "-w"];

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

        List<string> positional = GetPositionals(args);
        if (positional.Count is not (2 or 4))
        {
            PrintUsage();
            return 2;
        }

        bool json = args.Contains("--json");
        bool askSize = args.Contains("--ask-size");
        if (!TryParsePositive(GetOptionValue(args, "--tolerance"), 1e-4, "tolerance", out double tolerance))
            return 2;
        if (!TryParsePositive(GetOptionValue(args, "--size-tolerance"), 0, "size-tolerance", out double sizeToleranceAbs))
            return 2;

        double? expectedWidth = null;
        double? expectedHeight = null;
        if (!TryReadExpectedSize(args, positional, out expectedWidth, out expectedHeight, out string? sizeError))
        {
            Console.Error.WriteLine($"Error: {sizeError}");
            return 2;
        }

        string referencePath = Path.GetFullPath(positional[0]);
        string candidatePath = Path.GetFullPath(positional[1]);

        List<Point2D> shapeA = DxfPolygonReader.ReadPrimaryPolygon(referencePath);
        List<Point2D> shapeB = DxfPolygonReader.ReadPrimaryPolygon(candidatePath);
        ComparisonResult result = PolygonComparer.Compare(shapeA, shapeB, tolerance);

        SizeCheck size = SizeCheck.Measure(PolygonComparer.Normalize(shapeA), PolygonComparer.Normalize(shapeB));

        if (askSize && expectedWidth is null)
        {
            if (json)
            {
                Console.Error.WriteLine("Error: --ask-size cannot be used with --json.");
                return 2;
            }

            PrintMeasuredSizes(size);
            if (!TryPromptSize(out expectedWidth, out expectedHeight, out string? promptError))
            {
                Console.Error.WriteLine($"Error: {promptError}");
                return 2;
            }
        }

        if (expectedWidth is not null && expectedHeight is not null)
        {
            double sizeTol = Math.Max(
                sizeToleranceAbs,
                tolerance * Math.Max(expectedWidth.Value, expectedHeight.Value));
            size = size.AgainstExpected(expectedWidth.Value, expectedHeight.Value, Math.Max(sizeTol, 1e-8));
        }

        if (json)
            PrintJson(referencePath, candidatePath, result, size);
        else
            PrintText(referencePath, candidatePath, result, size);

        bool ok = result.IsMatch && (!size.Checked || size.Passed);
        return ok ? 0 : 1;
    }

    private static bool TryReadExpectedSize(
        string[] args,
        List<string> positional,
        out double? width,
        out double? height,
        out string? error)
    {
        width = null;
        height = null;
        error = null;

        string? widthRaw = GetOptionValue(args, "--width", "-w");
        string? heightRaw = GetOptionValue(args, "--height");

        if (positional.Count == 4)
        {
            widthRaw ??= positional[2];
            heightRaw ??= positional[3];
        }

        if (widthRaw is null && heightRaw is null)
            return true;

        if (widthRaw is null || heightRaw is null)
        {
            error = "Provide both width and height (--width and --height, or two extra numbers after the DXF files).";
            return false;
        }

        if (!TryParsePositive(widthRaw, 0, "width", out double w) || w <= 0)
        {
            error = "Width must be a positive number.";
            return false;
        }

        if (!TryParsePositive(heightRaw, 0, "height", out double h) || h <= 0)
        {
            error = "Height must be a positive number.";
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    private static bool TryPromptSize(out double? width, out double? height, out string? error)
    {
        width = null;
        height = null;
        error = null;

        Console.WriteLine();
        Console.Write("Enter expected width  : ");
        string? widthRaw = Console.ReadLine();
        Console.Write("Enter expected height : ");
        string? heightRaw = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(widthRaw) && string.IsNullOrWhiteSpace(heightRaw))
        {
            error = null;
            return true;
        }

        if (!double.TryParse(widthRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w <= 0)
        {
            error = "Width must be a positive number.";
            return false;
        }

        if (!double.TryParse(heightRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) || h <= 0)
        {
            error = "Height must be a positive number.";
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    private static void PrintMeasuredSizes(SizeCheck size)
    {
        Console.WriteLine($"Measured reference size : {size.ReferenceWidth:0.####} x {size.ReferenceHeight:0.####}");
        Console.WriteLine($"Measured candidate size : {size.CandidateWidth:0.####} x {size.CandidateHeight:0.####}");
    }

    private static void PrintText(string referencePath, string candidatePath, ComparisonResult result, SizeCheck size)
    {
        Console.WriteLine("DXF polygon comparison");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"Reference : {referencePath}");
        Console.WriteLine($"Candidate : {candidatePath}");
        Console.WriteLine($"Result    : {OverallLabel(result, size)}");
        Console.WriteLine($"Vertices  : {result.VertexCount}");
        Console.WriteLine($"Transform : {result.TransformSummary}");
        Console.WriteLine($"Flipped   : {(result.IsFlipped ? "Yes" : "No")}");
        Console.WriteLine($"Flip side : {result.FlipDescription}");
        Console.WriteLine($"Rotation  : {result.RotationDegreesCcw:0.##}° CCW  ({result.RotationDegreesCw:0.##}° CW)");
        if (result.IsFlipped)
            Console.WriteLine($"Mirror    : {DescribeAxis(result.MirrorAxisDegrees)}");
        Console.WriteLine($"Size (ref): {size.ReferenceWidth:0.####} x {size.ReferenceHeight:0.####}");
        Console.WriteLine($"Size (cand): {size.CandidateWidth:0.####} x {size.CandidateHeight:0.####}");
        if (size.ExpectedWidth is not null && size.ExpectedHeight is not null)
            Console.WriteLine($"Expected  : {size.ExpectedWidth:0.####} x {size.ExpectedHeight:0.####}");
        Console.WriteLine($"Size check: {size.Summary}");
        Console.WriteLine($"Fit error : {result.FitError:G4}");
        Console.WriteLine($"Details   : {result.Message}");
    }

    private static void PrintJson(string referencePath, string candidatePath, ComparisonResult result, SizeCheck size)
    {
        var payload = new
        {
            reference = referencePath,
            candidate = candidatePath,
            match = result.IsMatch && (!size.Checked || size.Passed),
            shapeMatch = result.IsMatch,
            vertexCount = result.VertexCount,
            flipped = result.IsFlipped,
            flipSide = result.FlipSide,
            flipDescription = result.FlipDescription,
            rotationDegreesCcw = Math.Round(result.RotationDegreesCcw, 4),
            rotationDegreesCw = Math.Round(result.RotationDegreesCw, 4),
            mirrorAxisDegrees = result.MirrorAxisDegrees,
            transform = result.TransformSummary,
            size = new
            {
                referenceWidth = Math.Round(size.ReferenceWidth, 6),
                referenceHeight = Math.Round(size.ReferenceHeight, 6),
                candidateWidth = Math.Round(size.CandidateWidth, 6),
                candidateHeight = Math.Round(size.CandidateHeight, 6),
                expectedWidth = size.ExpectedWidth,
                expectedHeight = size.ExpectedHeight,
                checkedSize = size.Checked,
                passed = size.Passed,
                dimensionsSwapped = size.DimensionsSwapped,
                summary = size.Summary
            },
            fitError = result.FitError,
            message = result.Message
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string OverallLabel(ComparisonResult result, SizeCheck size)
    {
        if (!result.IsMatch)
            return "NO MATCH";
        if (size.Checked && !size.Passed)
            return "SHAPE MATCH, SIZE FAIL";
        if (size.Checked)
            return "MATCH (shape and size)";
        return "MATCH";
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

        failed += RunSizeSelfTests(reference, Path.Combine(dir, "rotated-90.dxf"));

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "All tests passed."
            : $"{failed} tests failed.");
        Console.WriteLine($"Sample files: {dir}");
        return failed == 0 ? 0 : 1;
    }

    private static int RunSizeSelfTests(string referencePath, string rotated90Path)
    {
        int failed = 0;
        List<Point2D> a = PolygonComparer.Normalize(DxfPolygonReader.ReadPrimaryPolygon(referencePath));
        List<Point2D> b = PolygonComparer.Normalize(DxfPolygonReader.ReadPrimaryPolygon(rotated90Path));
        SizeCheck measured = SizeCheck.Measure(a, b);

        var cases = new (string Name, double Width, double Height, bool Pass)[]
        {
            ("size-10x12", 10, 12, true),
            ("size-12x10-swapped", 12, 10, true),
            ("size-wrong", 9, 12, false)
        };

        foreach ((string name, double width, double height, bool pass) in cases)
        {
            SizeCheck check = measured.AgainstExpected(width, height, 0.01);
            bool ok = check.Passed == pass;
            if (!ok)
                failed++;
            Console.WriteLine(
                $"{(ok ? "PASS" : "FAIL"),-4} {name,-26} sizePass={check.Passed,-5}  {check.Summary}");
        }

        bool measuredOk = Math.Abs(measured.ReferenceWidth - 10) < 0.01 && Math.Abs(measured.ReferenceHeight - 12) < 0.01;
        if (!measuredOk)
            failed++;
        Console.WriteLine(
            $"{(measuredOk ? "PASS" : "FAIL"),-4} {"measured-10x12",-26} {measured.ReferenceWidth:0.##} x {measured.ReferenceHeight:0.##}");

        return failed;
    }

    private static bool AnglesClose(double actual, double expected)
    {
        double delta = Math.Abs(actual - expected) % 360.0;
        if (delta > 180)
            delta = 360 - delta;
        return delta < 0.2;
    }

    private static List<string> GetPositionals(string[] args)
    {
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--json" or "--self-test" or "--help" or "-h" or "--ask-size")
                continue;

            string option = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            if (ValueOptions.Contains(option))
            {
                if (!arg.Contains('=') && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    i++;
                continue;
            }

            if (arg.StartsWith('-'))
                continue;

            positional.Add(arg);
        }

        return positional;
    }

    private static string? GetOptionValue(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length; i++)
        {
            foreach (string name in names)
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
        }

        return null;
    }

    private static bool TryParsePositive(string? raw, double fallback, string name, out double value)
    {
        value = fallback;
        if (raw is null)
            return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0)
            return true;
        Console.Error.WriteLine($"Error: --{name} must be a non-negative number.");
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DxfCompare — compare two DXF polygon shapes (rotation and flip allowed)

            Usage:
              DxfCompare <reference.dxf> <candidate.dxf> [width height]
              DxfCompare <reference.dxf> <candidate.dxf> --width 10 --height 12
              DxfCompare <reference.dxf> <candidate.dxf> --ask-size
              DxfCompare --write-samples [folder]
              DxfCompare --self-test
              DxfCompare --help

            Options:
              --width, -w <n>       Expected shape width
              --height <n>          Expected shape height
              --ask-size            Prompt for width and height after measuring the DXFs
              --size-tolerance <n>  Absolute size tolerance (drawing units)
              --tolerance <n>       Relative geometry tolerance (default 0.0001)
              --json                Machine-readable output

            Exit codes:
              0  shapes match (and size matches when width/height were given)
              1  shapes or size do not match
              2  usage or file error

            Width/height are the axis-aligned outline of the reference shape. A 90° rotation
            that swaps width and height is accepted. The candidate may also be flipped.

            ASCII DXF R12 (AutoCAD 12 / AC1009) through current versions are supported.
            """);
    }
}
