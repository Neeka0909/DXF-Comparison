using System.Globalization;
using System.Text.Json;
using DxfCompare.Comparison;
using DxfCompare.Dxf;
using DxfCompare.Geometry;

namespace DxfCompare;

public static class Program
{
    private static readonly string[] ValueOptions =
    [
        "--tolerance", "--width", "--height", "--size-tolerance", "--write-samples", "-w",
        "--edge-service", "--edge-top", "--edge-bottom", "--edge-left", "--edge-right"
    ];

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

        if (!TryParseEdgeService(args, out EdgeServiceConfig edgeService, out string? edgeError))
        {
            Console.Error.WriteLine($"Error: {edgeError}");
            return 2;
        }

        double? expectedWidth = null;
        double? expectedHeight = null;
        if (!TryReadExpectedSize(args, positional, out expectedWidth, out expectedHeight, out string? sizeError))
        {
            Console.Error.WriteLine($"Error: {sizeError}");
            return 2;
        }

        string referencePath = Path.GetFullPath(positional[0]);
        string candidatePath = Path.GetFullPath(positional[1]);

        List<Point2D> rawReference = DxfPolygonReader.ReadPrimaryPolygon(referencePath);
        List<Point2D> rawCandidate = DxfPolygonReader.ReadPrimaryPolygon(candidatePath);

        List<Point2D> baseReference = PolygonComparer.Normalize(rawReference);
        List<Point2D> compareReference = edgeService.IsActive
            ? EdgeServiceApplicator.Apply(baseReference, edgeService)
            : baseReference;

        ComparisonResult result = PolygonComparer.Compare(compareReference, rawCandidate, tolerance);

        SizeCheck size = SizeCheck.Measure(
            compareReference,
            PolygonComparer.Normalize(rawCandidate),
            baseReference,
            edgeService);

        if (askSize && expectedWidth is null && !edgeService.IsActive)
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

        double sizeTolBase = Math.Max(expectedWidth ?? size.ReferenceWidth, expectedHeight ?? size.ReferenceHeight);
        double sizeTol = Math.Max(sizeToleranceAbs, tolerance * Math.Max(sizeTolBase, 1e-8));

        if (expectedWidth is not null && expectedHeight is not null)
        {
            size = size.AgainstExpected(expectedWidth.Value, expectedHeight.Value, Math.Max(sizeTol, 1e-8));
        }
        else if (edgeService.IsActive)
        {
            size = size.AgainstEdgeService(edgeService, Math.Max(sizeTol, 1e-8));
        }

        if (json)
            PrintJson(referencePath, candidatePath, result, size, edgeService);
        else
            PrintText(referencePath, candidatePath, result, size, edgeService);

        bool ok = result.IsMatch && (!size.Checked || size.Passed);
        return ok ? 0 : 1;
    }

    private static bool TryParseEdgeService(string[] args, out EdgeServiceConfig config, out string? error)
    {
        config = EdgeServiceConfig.None;
        error = null;

        string? allSides = GetOptionValue(args, "--edge-service");
        double top = 0, bottom = 0, left = 0, right = 0;

        if (allSides is not null)
        {
            if (!TryParseNonNegative(allSides, out double uniform))
            {
                error = "--edge-service must be a non-negative number.";
                return false;
            }

            top = bottom = left = right = uniform;
        }

        if (!TryParseOptionalSide(args, "--edge-top", ref top, out error)) return false;
        if (!TryParseOptionalSide(args, "--edge-bottom", ref bottom, out error)) return false;
        if (!TryParseOptionalSide(args, "--edge-left", ref left, out error)) return false;
        if (!TryParseOptionalSide(args, "--edge-right", ref right, out error)) return false;

        config = new EdgeServiceConfig
        {
            Top = top,
            Bottom = bottom,
            Left = left,
            Right = right
        };
        return true;
    }

    private static bool TryParseOptionalSide(string[] args, string name, ref double value, out string? error)
    {
        error = null;
        string? raw = GetOptionValue(args, name);
        if (raw is null)
            return true;

        if (!TryParseNonNegative(raw, out double parsed))
        {
            error = $"{name} must be a non-negative number.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseNonNegative(string raw, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0)
            return true;
        value = 0;
        return false;
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

        if (!TryParseNonNegative(widthRaw, out double w) || w <= 0)
        {
            error = "Width must be a positive number.";
            return false;
        }

        if (!TryParseNonNegative(heightRaw, out double h) || h <= 0)
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
            return true;

        if (!TryParseNonNegative(widthRaw ?? "", out double w) || w <= 0)
        {
            error = "Width must be a positive number.";
            return false;
        }

        if (!TryParseNonNegative(heightRaw ?? "", out double h) || h <= 0)
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
        if (size.BaseReferenceWidth is not null && size.BaseReferenceHeight is not null)
        {
            Console.WriteLine($"Measured base size      : {size.BaseReferenceWidth:0.####} x {size.BaseReferenceHeight:0.####}");
            Console.WriteLine($"After edge service      : {size.ReferenceWidth:0.####} x {size.ReferenceHeight:0.####}");
        }
        else
        {
            Console.WriteLine($"Measured reference size : {size.ReferenceWidth:0.####} x {size.ReferenceHeight:0.####}");
        }

        Console.WriteLine($"Measured candidate size : {size.CandidateWidth:0.####} x {size.CandidateHeight:0.####}");
    }

    private static void PrintText(
        string referencePath,
        string candidatePath,
        ComparisonResult result,
        SizeCheck size,
        EdgeServiceConfig edgeService)
    {
        Console.WriteLine("DXF polygon comparison");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"Reference : {referencePath}");
        Console.WriteLine($"Candidate : {candidatePath}");
        Console.WriteLine($"Result    : {OverallLabel(result, size)}");
        Console.WriteLine($"Vertices  : {result.VertexCount}");
        if (edgeService.IsActive)
            Console.WriteLine($"Edge svc  : {edgeService.Describe()}");
        Console.WriteLine($"Transform : {result.TransformSummary}");
        Console.WriteLine($"Flipped   : {(result.IsFlipped ? "Yes" : "No")}");
        Console.WriteLine($"Flip side : {result.FlipDescription}");
        Console.WriteLine($"Rotation  : {result.RotationDegreesCcw:0.##}° CCW  ({result.RotationDegreesCw:0.##}° CW)");
        if (result.IsFlipped)
            Console.WriteLine($"Mirror    : {DescribeAxis(result.MirrorAxisDegrees)}");
        if (size.BaseReferenceWidth is not null && size.BaseReferenceHeight is not null)
            Console.WriteLine($"Base size : {size.BaseReferenceWidth:0.####} x {size.BaseReferenceHeight:0.####}");
        Console.WriteLine($"Size (ref): {size.ReferenceWidth:0.####} x {size.ReferenceHeight:0.####}");
        Console.WriteLine($"Size (cand): {size.CandidateWidth:0.####} x {size.CandidateHeight:0.####}");
        if (size.ExpectedWidth is not null && size.ExpectedHeight is not null)
            Console.WriteLine($"Expected  : {size.ExpectedWidth:0.####} x {size.ExpectedHeight:0.####}");
        Console.WriteLine($"Size check: {size.Summary}");
        Console.WriteLine($"Fit error : {result.FitError:G4}");
        Console.WriteLine($"Details   : {result.Message}");
    }

    private static void PrintJson(
        string referencePath,
        string candidatePath,
        ComparisonResult result,
        SizeCheck size,
        EdgeServiceConfig edgeService)
    {
        var payload = new
        {
            reference = referencePath,
            candidate = candidatePath,
            match = result.IsMatch && (!size.Checked || size.Passed),
            shapeMatch = result.IsMatch,
            vertexCount = result.VertexCount,
            edgeService = edgeService.IsActive
                ? new
                {
                    top = edgeService.Top,
                    bottom = edgeService.Bottom,
                    left = edgeService.Left,
                    right = edgeService.Right,
                    description = edgeService.Describe()
                }
                : null,
            flipped = result.IsFlipped,
            flipSide = result.FlipSide,
            flipDescription = result.FlipDescription,
            rotationDegreesCcw = Math.Round(result.RotationDegreesCcw, 4),
            rotationDegreesCw = Math.Round(result.RotationDegreesCw, 4),
            mirrorAxisDegrees = result.MirrorAxisDegrees,
            transform = result.TransformSummary,
            size = new
            {
                baseReferenceWidth = size.BaseReferenceWidth,
                baseReferenceHeight = size.BaseReferenceHeight,
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

            if (!ok)
                failed++;

            Console.WriteLine(
                $"{(ok ? "PASS" : "FAIL"),-4} {file,-26} match={result.IsMatch,-5} flip={result.FlipSide,-11} rot={result.RotationDegreesCcw,7:0.##}°  {result.TransformSummary}");
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
        failed += RunEdgeServiceSelfTests(dir);

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "All tests passed." : $"{failed} tests failed.");
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
            Console.WriteLine($"{(ok ? "PASS" : "FAIL"),-4} {name,-26} sizePass={check.Passed,-5}  {check.Summary}");
        }

        return failed;
    }

    private static int RunEdgeServiceSelfTests(string dir)
    {
        int failed = 0;

        // Rectangle 100 x 50 at origin
        List<Point2D> rect =
        [
            new(0, 0),
            new(100, 0),
            new(100, 50),
            new(0, 50)
        ];

        SampleDxfFactory.Save(Path.Combine(dir, "rect-100x50.dxf"), rect);

        var allAround = EdgeServiceConfig.Uniform(2);
        List<Point2D> expandedAll = EdgeServiceApplicator.Apply(rect, allAround);
        SampleDxfFactory.Save(Path.Combine(dir, "rect-100x50-edge-all.dxf"), expandedAll);

        var topOnly = new EdgeServiceConfig { Top = 2 };
        List<Point2D> expandedTop = EdgeServiceApplicator.Apply(rect, topOnly);
        SampleDxfFactory.Save(Path.Combine(dir, "rect-100x50-edge-top.dxf"), expandedTop);

        BoundingBox allBox = BoundingBox.From(expandedAll);
        BoundingBox topBox = BoundingBox.From(expandedTop);

        bool allSizeOk = Math.Abs(allBox.Width - 104) < 0.01 && Math.Abs(allBox.Height - 54) < 0.01;
        bool topSizeOk = Math.Abs(topBox.Width - 100) < 0.01 && Math.Abs(topBox.Height - 52) < 0.01;
        if (!allSizeOk)
            failed++;
        if (!topSizeOk)
            failed++;
        Console.WriteLine($"{(allSizeOk ? "PASS" : "FAIL"),-4} {"edge-all-104x54",-26} {allBox.Width:0.##} x {allBox.Height:0.##}");
        Console.WriteLine($"{(topSizeOk ? "PASS" : "FAIL"),-4} {"edge-top-100x52",-26} {topBox.Width:0.##} x {topBox.Height:0.##}");

        string basePath = Path.Combine(dir, "rect-100x50.dxf");
        string candAllPath = Path.Combine(dir, "rect-100x50-edge-all.dxf");
        string candTopPath = Path.Combine(dir, "rect-100x50-edge-top.dxf");

        List<Point2D> baseRef = PolygonComparer.Normalize(DxfPolygonReader.ReadPrimaryPolygon(basePath));
        List<Point2D> candAll = PolygonComparer.Normalize(DxfPolygonReader.ReadPrimaryPolygon(candAllPath));

        List<Point2D> expandedRef = EdgeServiceApplicator.Apply(baseRef, allAround);
        ComparisonResult shapeAll = PolygonComparer.Compare(expandedRef, candAll);
        SizeCheck sizeAll = SizeCheck.Measure(expandedRef, candAll, baseRef, allAround)
            .AgainstEdgeService(allAround, 0.01);

        bool flowAllOk = shapeAll.IsMatch && sizeAll.Passed;
        if (!flowAllOk)
            failed++;
        Console.WriteLine($"{(flowAllOk ? "PASS" : "FAIL"),-4} {"edge-flow-all-around",-26} shape={shapeAll.IsMatch} size={sizeAll.Passed}");

        List<Point2D> expandedTopRef = EdgeServiceApplicator.Apply(baseRef, topOnly);
        List<Point2D> candTop = PolygonComparer.Normalize(DxfPolygonReader.ReadPrimaryPolygon(candTopPath));
        ComparisonResult shapeTop = PolygonComparer.Compare(expandedTopRef, candTop);
        SizeCheck sizeTop = SizeCheck.Measure(expandedTopRef, candTop, baseRef, topOnly)
            .AgainstEdgeService(topOnly, 0.01);

        bool flowTopOk = shapeTop.IsMatch && sizeTop.Passed;
        if (!flowTopOk)
            failed++;
        Console.WriteLine($"{(flowTopOk ? "PASS" : "FAIL"),-4} {"edge-flow-top-only",-26} shape={shapeTop.IsMatch} size={sizeTop.Passed}");

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
              DxfCompare <reference.dxf> <candidate.dxf> --edge-service 2
              DxfCompare <reference.dxf> <candidate.dxf> --edge-top 2
              DxfCompare --write-samples [folder]
              DxfCompare --self-test
              DxfCompare --help

            Options:
              --edge-service <n>    Edge service on all sides (top, bottom, left, right)
              --edge-top <n>        Edge service on top side only
              --edge-bottom <n>     Edge service on bottom side
              --edge-left <n>       Edge service on left side
              --edge-right <n>      Edge service on right side
              --width, -w <n>       Expected final shape width (overrides edge service calc)
              --height <n>          Expected final shape height
              --ask-size            Prompt for width and height after measuring the DXFs
              --size-tolerance <n>  Absolute size tolerance (drawing units)
              --tolerance <n>       Relative geometry tolerance (default 0.0001)
              --json                Machine-readable output

            Edge service:
              Reference DXF is the base shape. Edge service expands it outward before comparing
              to the candidate DXF (which should already include edge service).

              Example: base 100 x 50 mm
                --edge-service 2        => expected 104 x 54 mm (2 mm on every side)
                --edge-top 2            => expected 100 x 52 mm (2 mm on top only)

            Exit codes:
              0  shapes match (and size matches when validated)
              1  shapes or size do not match
              2  usage or file error

            ASCII DXF R12 (AutoCAD 12 / AC1009) through current versions are supported.
            """);
    }
}
