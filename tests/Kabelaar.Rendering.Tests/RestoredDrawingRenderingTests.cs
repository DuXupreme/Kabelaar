using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kabelaar.Core;
using Kabelaar.Rendering;
using SkiaSharp;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class RestoredDrawingRenderingTests
{
    [Theory]
    [InlineData(AnnotationKind.Dimension)]
    [InlineData(AnnotationKind.WireTable)]
    [InlineData(AnnotationKind.PinTable)]
    [InlineData(AnnotationKind.BomTable)]
    [InlineData(AnnotationKind.RevisionTable)]
    public void AnnotationStrokeAndColorAreHonoredAtViewAndPrintScale(AnnotationKind kind)
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        view.Scale = .75; sheet.PrintScale = 2;
        var annotation = new Annotation
        {
            Kind = kind, Points = [new(10, 20), new(70, 20)], WidthMm = 150,
            Color = "#BC328A", LineWidthMm = .8
        };
        view.Annotations = [annotation];
        var screen = SceneBuilder.Build(project, sheet);
        var print = SceneBuilder.Build(project, sheet, forPrint: true);
        var strokes = screen.Primitives.OfType<ScenePath>().Where(p => p.EntityId == annotation.Id).ToArray();
        Assert.NotEmpty(strokes);
        Assert.All(strokes, p => { Assert.Equal("#BC328A", p.Stroke); Assert.Equal(1.2, p.StrokeWidth, 10); });
        Assert.All(screen.Primitives.OfType<SceneText>().Where(p => p.EntityId == annotation.Id),
            p => Assert.Equal("#BC328A", p.Color));
        Assert.Equal(strokes.Select(p => (p.Stroke, p.StrokeWidth, p.Points.ToArray())),
            print.Primitives.OfType<ScenePath>().Where(p => p.EntityId == annotation.Id)
                .Select(p => (p.Stroke, p.StrokeWidth, p.Points.ToArray())),
            AnnotationStrokeComparer.Instance);

        annotation.LineWidthMm = .3; annotation.Color = "#1469AD";
        var changed = SceneBuilder.Build(project, sheet);
        Assert.All(changed.Primitives.OfType<ScenePath>().Where(p => p.EntityId == annotation.Id),
            p => { Assert.Equal("#1469AD", p.Stroke); Assert.Equal(.45, p.StrokeWidth, 10); });
    }

    [Fact]
    public void IndividualCubicAndWidthChangeOnlySelectedWireInSharedFreeRoute()
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var first = project.Assembly.Conductors[0]; var other = project.Assembly.Conductors[1];
        var baseline = SceneBuilder.Build(project, sheet);
        var untouched = WirePath(baseline, other.Id);
        var production = HarnessReports.Wires(project).Select(w => w.CutLengthMm).ToArray();
        var geometry = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 40);
        view.WirePresentations.Add(new()
        {
            ConductorId = first.Id, LineWidthMm = 1.25,
            Routes = [new() { BranchId = view.Routes[0].BranchId, Geometry = geometry }]
        });
        var changed = SceneBuilder.Build(project, sheet);
        var curve = WirePath(changed, first.Id);
        Assert.Equal(1.25, curve.StrokeWidth);
        Assert.True(curve.Points.Max(p => p.Y) > 45);
        Assert.Equal(untouched.Points, WirePath(changed, other.Id).Points);
        Assert.Equal(untouched.StrokeWidth, WirePath(changed, other.Id).StrokeWidth);
        Assert.Equal(2, project.Assembly.Conductors.Count);
        Assert.Single(project.Assembly.Branches);
        Assert.Equal(production, HarnessReports.Wires(project).Select(w => w.CutLengthMm));
        Assert.Equal(Geometry.Flatten(geometry), SceneBuilder.ConductorPoints(project, view, first));
        var mid = curve.Points[curve.Points.Count / 2];
        Assert.Contains(changed.Index.HitTest(mid, .01), p => p.EntityId == first.Id && p.Category == "wire");
    }

    [Fact]
    public void CurveCacheTracksInPlaceHandlesSegmentKindAndOverrideRemoval()
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        project.Assembly.Conductors.RemoveAt(1);
        var wire = project.Assembly.Conductors[0];
        var geometry = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 40);
        var presentation = new WirePresentation
        {
            ConductorId = wire.Id, Routes = [new() { BranchId = view.Routes[0].BranchId, Geometry = geometry }]
        };
        view.WirePresentations.Add(presentation);
        var first = WirePath(SceneBuilder.Build(project, sheet), wire.Id);
        var same = WirePath(SceneBuilder.Build(project, sheet), wire.Id);
        Assert.Same(first, same);
        geometry.Knots[0].OutHandle += new Point2(0, 25);
        var changed = WirePath(SceneBuilder.Build(project, sheet), wire.Id);
        Assert.NotEqual(first.Points, changed.Points);
        Assert.True(changed.Points.Max(p => p.Y) > first.Points.Max(p => p.Y));
        geometry.Segments[0].Kind = RouteSegmentKind.Line;
        var straight = WirePath(SceneBuilder.Build(project, sheet), wire.Id);
        Assert.Equal(2, straight.Points.Count);
        Assert.All(straight.Points, p => Assert.Equal(20, p.Y));
        presentation.Routes.Clear();
        Assert.Equal(straight.Points, WirePath(SceneBuilder.Build(project, sheet), wire.Id).Points);
    }

    [Theory]
    [InlineData(TwistDisplayMode.Full)]
    [InlineData(TwistDisplayMode.Markers)]
    [InlineData(TwistDisplayMode.Parallel)]
    public void IndividualTwistedStrandDoesNotPullOtherStrandOntoItsEditedPath(TwistDisplayMode mode)
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var first = project.Assembly.Conductors[0]; var other = project.Assembly.Conductors[1];
        view.Style.TwistDisplayMode = mode;
        project.Assembly.TwistGroups.Add(new() { ConductorIds = [first.Id, other.Id], PitchMm = 12 });
        var baseline = SceneBuilder.Build(project, sheet);
        var before = WirePath(baseline, other.Id);
        view.WirePresentations.Add(new()
        {
            ConductorId = first.Id, Routes = [new()
            {
                BranchId = view.Routes[0].BranchId,
                Geometry = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 50)
            }]
        });
        var changed = SceneBuilder.Build(project, sheet);
        Assert.Equal(before.Points, WirePath(changed, other.Id).Points);
        Assert.True(WirePath(changed, first.Id).Points.Max(p => p.Y) > 50);
        Assert.Equal(WirePath(baseline, first.Id).Points[0], WirePath(changed, first.Id).Points[0]);
        Assert.Equal(WirePath(baseline, first.Id).Points[^1], WirePath(changed, first.Id).Points[^1]);
        Assert.Equal(2, project.Assembly.Conductors.Count);
        Assert.Equal(12, project.Assembly.TwistGroups[0].PitchMm);
    }

    [Fact]
    public void WirePreviewDoesNotMutateModelOrReplaceCommittedCache()
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var wire = project.Assembly.Conductors[0];
        var baseline = SceneBuilder.Build(project, sheet);
        var before = JsonSerializer.Serialize(project);
        var geometry = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 35);
        var points = Geometry.Flatten(geometry);
        var preview = SceneBuilder.WirePreview(project, sheet, view,
            new Dictionary<string, IReadOnlyList<Point2>> { [wire.Id] = points });
        var path = preview.OfType<ScenePath>().Single(p => p.Category == "wire");
        Assert.Equal(points, path.Points);
        Assert.All(preview, p => { Assert.Equal(wire.Id, p.EntityId); Assert.Equal(view.Id, p.ViewId); });
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.Same(WirePath(baseline, wire.Id), WirePath(SceneBuilder.Build(project, sheet), wire.Id));
    }

    [Theory]
    [InlineData(TwistDisplayMode.Full)]
    [InlineData(TwistDisplayMode.Markers)]
    [InlineData(TwistDisplayMode.Parallel)]
    public void SharedTwistPreviewMatchesCommitForDifferentMemberRoutes(TwistDisplayMode mode)
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var first = project.Assembly.Conductors[0]; var second = project.Assembly.Conductors[1];
        var secondBranch = new RouteBranch { Points = [new(20, 30), new(100, 30)], PhysicalLengthMm = 500 };
        project.Assembly.Branches.Add(secondBranch);
        var secondRoute = new PlacedRoute { BranchId = secondBranch.Id, Points = [.. secondBranch.Points] };
        view.Routes.Add(secondRoute); second.BranchIds = [secondBranch.Id];
        project.Assembly.TwistGroups.Add(new() { ConductorIds = [first.Id, second.Id], PitchMm = 12 });
        view.Style.TwistDisplayMode = mode;
        var firstShape = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 25);
        var secondShape = Geometry.WithBend(Geometry.FromLegacy(secondRoute.Points, false), -10);
        var pending = new Dictionary<string, IReadOnlyList<Point2>>
        {
            [first.Id] = Geometry.Flatten(firstShape),
            [second.Id] = Geometry.Flatten(secondShape)
        };
        var preview = SceneBuilder.WirePreview(project, sheet, view, pending, false, new HashSet<string>());
        view.Routes[0].Geometry = firstShape; secondRoute.Geometry = secondShape;
        var committed = SceneBuilder.Build(project, sheet);
        foreach (var id in new[] { first.Id, second.Id })
            Assert.Equal(WirePath(committed, id).Points,
                preview.OfType<ScenePath>().Single(p => p.EntityId == id && p.Category == "twisted-wire").Points);
    }

    [Fact]
    public void PerWireWidthOverridesSectionWeightAndInvalidatesTheRetainedCache()
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var wire = project.Assembly.Conductors[0];
        wire.CrossSectionMm2 = 8; view.Style.UseWireSectionWidth = true;
        view.Scale = 2; sheet.PrintScale = .5;
        var presentation = new WirePresentation { ConductorId = wire.Id, LineWidthMm = .7 };
        view.WirePresentations.Add(presentation);
        Assert.Equal(.7, WirePath(SceneBuilder.Build(project, sheet), wire.Id).StrokeWidth);
        presentation.LineWidthMm = 1.1;
        Assert.Equal(1.1, WirePath(SceneBuilder.Build(project, sheet), wire.Id).StrokeWidth);
        presentation.LineWidthMm = null;
        Assert.Equal(view.Style.WireLineWidthMm * 2, WirePath(SceneBuilder.Build(project, sheet), wire.Id).StrokeWidth);
    }

    [Fact]
    public void CanonicalBranchCurveDoesNotReplaceAnExistingFreeViewLayout()
    {
        var project = SharedRoute(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var wire = project.Assembly.Conductors[0];
        var before = SceneBuilder.ConductorPoints(project, view, wire).ToArray();
        project.Assembly.Branches[0].Geometry = Geometry.WithBend(
            Geometry.FromLegacy(project.Assembly.Branches[0].Points, false), 90);
        Assert.Equal(before, SceneBuilder.ConductorPoints(project, view, wire));
        view.Routes[0].Points.Clear();
        Assert.True(SceneBuilder.ConductorPoints(project, view, wire).Max(p => p.Y) > 80);
    }

    [Fact]
    public async Task ExportSnapshotIncludesCubicAndAnnotationStylesInVectorAndRasterFiles()
    {
        var project = SharedRoute(); var view = project.Sheets[0].Views[0];
        project.Assembly.Conductors.RemoveAt(1);
        var wire = project.Assembly.Conductors[0]; wire.Color = "#C13D8A";
        view.Routes[0].Geometry = Geometry.WithBend(Geometry.FromLegacy(view.Routes[0].Points, false), 40);
        view.WirePresentations.Add(new() { ConductorId = wire.Id, LineWidthMm = 2 });
        view.Annotations.Add(new()
        {
            Kind = AnnotationKind.Dimension, Points = [new(20, 100), new(80, 100)], OffsetMm = 0,
            Color = "#238F52", LineWidthMm = 2
        });
        view.Annotations.Add(new()
        {
            Kind = AnnotationKind.WireTable, Points = [new(20, 130)], WidthMm = 100,
            Color = "#2255CC", LineWidthMm = 2, Columns = ["Name"]
        });
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-restored-render-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await ExportService.ExportSetAsync(project, directory, new ExportOptions(true, 127));
            var svg = XDocument.Load(result.Files.Single(p => p.EndsWith(".svg")));
            var strokes = svg.Descendants().Attributes("stroke").Select(a => a.Value).ToArray();
            foreach (var expected in new[] { "#C13D8A", "#238F52", "#2255CC" })
                Assert.True(strokes.Any(c => SKColor.TryParse(c, out var parsed) && parsed == SKColor.Parse(expected)),
                    $"SVG stroke {expected} ontbreekt. Werkelijke kleuren: {string.Join(", ", strokes.Distinct())}");
            var pdf = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(result.Files.Single(p => p.EndsWith(".pdf"))));
            Assert.StartsWith("%PDF-", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf);
            using var bitmap = SKBitmap.Decode(result.Files.Single(p => p.EndsWith(".png")));
            Assert.NotNull(bitmap);
            // At 127 dpi there are exactly five pixels per mm. The two-point cubic peaks at y=50.
            Assert.Equal(SKColor.Parse("#C13D8A"), bitmap.GetPixel(300, 250));
            Assert.Equal(SKColor.Parse("#238F52"), bitmap.GetPixel(250, 500));
            Assert.Equal(SKColor.Parse("#2255CC"), bitmap.GetPixel(250, 650));
        }
        finally
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kabelaar-restored-render-tests"))
                + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private static HarnessProject SharedRoute()
    {
        var project = new HarnessProject();
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free; view.Origin = new();
        view.Style.ShowWireLabels = false;
        var branch = new RouteBranch { Points = [new(20, 20), new(100, 20)], PhysicalLengthMm = 500 };
        project.Assembly.Branches.Add(branch);
        view.Routes.Add(new() { BranchId = branch.Id, Points = [.. branch.Points] });
        for (var i = 0; i < 2; i++)
            project.Assembly.Conductors.Add(new()
            {
                Name = $"W{i + 1}", BranchIds = [branch.Id], LengthSource = LengthSource.Manual, ManualCutLengthMm = 530,
                From = new() { Description = "Bewust vrij" }, To = new() { Description = "Bewust vrij" }
            });
        return project;
    }

    private static ScenePath WirePath(DrawingScene scene, string id) =>
        scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == id && p.Category is "wire" or "twisted-wire");

    private sealed class AnnotationStrokeComparer : IEqualityComparer<(string Stroke, double StrokeWidth, Point2[] Points)>
    {
        public static readonly AnnotationStrokeComparer Instance = new();
        public bool Equals((string Stroke, double StrokeWidth, Point2[] Points) x,
            (string Stroke, double StrokeWidth, Point2[] Points) y) =>
            x.Stroke == y.Stroke && x.StrokeWidth == y.StrokeWidth && x.Points.SequenceEqual(y.Points);
        public int GetHashCode((string Stroke, double StrokeWidth, Point2[] Points) obj) => HashCode.Combine(obj.Stroke, obj.StrokeWidth);
    }
}
