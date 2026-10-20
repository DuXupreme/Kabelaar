using System.Text;
using System.Xml.Linq;
using Kabelaar.Core;
using Kabelaar.Rendering;
using SkiaSharp;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class RenderingTests
{
    [Fact]
    public void ComponentCacheReusesUnchangedConnectorWhileMovedPinsAndWiresFollowTheirSource()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var left = project.Assembly.Connectors[0]; var right = project.Assembly.Connectors[1];
        ScenePath Symbol(DrawingScene scene, string id) => scene.Primitives.OfType<ScenePath>().First(p => p.EntityId == id && p.Category == "connector-symbol");
        var first = SceneBuilder.Build(project, sheet);
        var same = SceneBuilder.Build(project, sheet);
        Assert.Same(Symbol(first, left.Id), Symbol(same, left.Id));
        var before = Symbol(first, left.Id).Points.ToArray();
        HarnessOperations.MoveComponent(project, view, view.Components[0], view.Components[0].Position + new Point2(7, 4)).Execute();
        var moved = SceneBuilder.Build(project, sheet);
        Assert.NotSame(Symbol(first, left.Id), Symbol(moved, left.Id));
        Assert.Same(Symbol(first, right.Id), Symbol(moved, right.Id));
        Assert.Equal(before, Symbol(first, left.Id).Points);
        var wire = project.Assembly.Conductors[0];
        var line = moved.Primitives.OfType<ScenePath>().First(p => p.EntityId == wire.Id && p.Category == "twisted-wire");
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value, line.Points[0]);
    }

    [Fact]
    public void ComponentCacheDetectsInPlacePartAndCavityEditsButSealOnlyChangesDoNotChangeGraphics()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var connector = project.Assembly.Connectors[0]; var other = project.Assembly.Connectors[1];
        var part = project.Parts.Single(p => p.Id == connector.PartId);
        ScenePath Symbol(DrawingScene scene, string id) => scene.Primitives.OfType<ScenePath>().First(p => p.EntityId == id && p.Category == "connector-symbol");
        var first = SceneBuilder.Build(project, sheet);
        connector.Cavities[0].SealPartId = "another-seal";
        var seal = SceneBuilder.Build(project, sheet);
        Assert.Same(Symbol(first, connector.Id), Symbol(seal, connector.Id));
        connector.Cavities[0].Label = "NEW-PIN"; connector.Cavities[0].State = CavityState.Plugged;
        var cavity = SceneBuilder.Build(project, sheet);
        Assert.Contains(cavity.Primitives.OfType<SceneText>(), p => p.EntityId == connector.Id && p.Category == "pin-label" && p.Text == "NEW-PIN");
        Assert.Same(Symbol(first, other.Id), Symbol(cavity, other.Id));
        var oldPoint = part.Pins[0].Position;
        part.Pins[0].Position += new Point2(3, 2);
        part.Symbol[0].Points[0] += new Point2(-2, -2);
        part.PartNumber = "CHANGED-PART";
        var changed = SceneBuilder.Build(project, sheet);
        Assert.NotSame(Symbol(cavity, other.Id), Symbol(changed, other.Id)); // Shared part update reaches both placements.
        Assert.Contains(changed.Primitives.OfType<SceneText>(), p => p.EntityId == other.Id && p.Text == "CHANGED-PART");
        var expected = view.Origin + Geometry.Transform(part.Pins[0].Position, view.Components[0].RotationDegrees, view.Components[0].Position);
        Assert.Contains(changed.Primitives.OfType<SceneEllipse>(), p => p.EntityId == connector.Id && p.Category == "pin" && p.Center == expected && p.Fill == "#6C7A86");
        var oldExpected = view.Origin + Geometry.Transform(oldPoint, view.Components[0].RotationDegrees, view.Components[0].Position);
        Assert.Contains(first.Primitives.OfType<SceneEllipse>(), p => p.EntityId == connector.Id && p.Category == "pin" && p.Center == oldExpected);
    }

    [Fact]
    public void RepeatedRasterPatchesPreserveUnchangedPixelsAndReleaseSnapshots()
    {
        using var cache = new ScreenSceneCache();
        using var output = SKSurface.Create(new SKImageInfo(200, 200));
        var stable = new SceneEllipse("stable", "test", new(70, 70), 6, 6, "#FF0000", 0, "#FF0000");
        for (var i = 0; i < 20; i++)
        {
            var x = i % 2 == 0 ? 20 : 40;
            var scene = new DrawingScene("sheet", 90, 90, false,
                [stable, new SceneEllipse("moving", "test", new(x, 20), 5, 5, "#0000FF", 0, "#0000FF")]);
            output.Canvas.ResetMatrix(); output.Canvas.Clear(SKColors.White); output.Canvas.Scale(2);
            SkiaSceneRenderer.RenderScreen(output.Canvas, scene, new(0, 0, 90, 90), cache);
            using var image = output.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            Assert.Equal(SKColors.Red, bitmap.GetPixel(140, 140));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(x * 2, 40));
            Assert.Equal(SKColors.White, bitmap.GetPixel((x == 20 ? 40 : 20) * 2, 40));
            Assert.Equal(1, cache.ImageCount);
            Assert.InRange(cache.CachedBytes, 1, 64L * 1024 * 1024);
        }
    }

    [Fact]
    public void SceneContainsEverySupportedDrawingCategory()
    {
        var project = CompleteFixture();
        var sheet = project.Sheets[0];
        var scene = SceneBuilder.Build(project, sheet, true);
        var categories = scene.Primitives.Select(p => p.Category).ToHashSet();
        foreach (var category in new[] { "sheet-frame", "title-block", "connector-symbol", "symbol-label", "pin", "pin-label",
            "connector-label", "part-number", "twisted-wire", "twist-label", "splice", "splice-label", "sleeve", "sleeve-label",
            "cable-jacket", "cable-label", "annotation-text", "annotation-line", "leader", "leader-text", "arc", "dimension",
            "dimension-text", "image", "logo", "table-grid", "table-cell", "view-label", "draft-watermark" })
            Assert.Contains(category, categories);
        Assert.Contains(scene.Primitives.OfType<SceneText>(), t => t.Category == "dimension-text" && t.Text.Contains("480"));
        Assert.All(scene.Primitives, p => Assert.True(double.IsFinite(p.Bounds.X) && double.IsFinite(p.Bounds.Y)));
    }

    [Fact]
    public void ScaleTransformsGeometryWithoutChangingManufacturingLengths()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        sheet.PrintScale = .5; view.Scale = 2; view.Origin = new(70, 80);
        var wire = project.Assembly.Conductors[0];
        var before = HarnessReports.CutLength(project, wire);
        var scene = SceneBuilder.Build(project, sheet);
        var pin = Geometry.EndpointPosition(project, view, wire.From)!.Value;
        var path = scene.Primitives.OfType<ScenePath>().First(p => p.EntityId == wire.Id);
        Assert.Equal(view.Origin + pin, path.Points[0]);
        Assert.Equal(before, HarnessReports.CutLength(project, wire));
    }

    [Fact]
    public void MovedConnectorKeepsRenderedWireAtActualCavity()
    {
        var project = DemoFactory.Create(); var view = project.Sheets[0].Views[0];
        var wire = project.Assembly.Conductors[0];
        view.Components[0].Position += new Point2(10, 20);
        var path = SceneBuilder.ConductorPoints(project, view, wire);
        Assert.Equal(Geometry.EndpointPosition(project, view, wire.From), path[0]);
        Assert.Equal(Geometry.EndpointPosition(project, view, wire.To), path[^1]);
    }

    [Fact]
    public void RouteCacheDetectsInPlacePointEdits()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0];
        project.Assembly.TwistGroups.Clear();
        var wire = project.Assembly.Conductors[0];
        var before = SceneBuilder.Build(project, sheet).Primitives.OfType<ScenePath>().First(p => p.EntityId == wire.Id).Points.ToArray();
        var route = sheet.Views[0].Routes.First(r => r.BranchId == wire.BranchIds[0]);
        route.Points[1] += new Point2(15, 8);
        var after = SceneBuilder.Build(project, sheet).Primitives.OfType<ScenePath>().First(p => p.EntityId == wire.Id).Points;
        Assert.False(before.SequenceEqual(after));
    }

    [Fact]
    public void HiddenAndNonPrintingLayersAreRespected()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var reference = new Annotation { Kind = AnnotationKind.Text, Text = "REF", LayerId = "reference", Points = [new(10, 10)] };
        view.Annotations.Add(reference);
        Assert.Contains(SceneBuilder.Build(project, sheet).Primitives, p => p.EntityId == reference.Id);
        Assert.DoesNotContain(SceneBuilder.Build(project, sheet, forPrint: true).Primitives, p => p.EntityId == reference.Id);
        sheet.Layers.Single(l => l.Id == "reference").Visible = false;
        Assert.DoesNotContain(SceneBuilder.Build(project, sheet).Primitives, p => p.EntityId == reference.Id);
    }

    [Fact]
    public void HitTestUsesPolylineDistanceAndRespectsLockedLayer()
    {
        var line = new ScenePath("wire", "wire", [new(0, 0), new(100, 100)]);
        var locked = new SceneText("locked", "note", "A", new(50, 51)) { Locked = true };
        var index = new SceneSpatialIndex([line, locked]);
        Assert.Contains(index.HitTest(new(50, 50)), p => p.EntityId == "wire");
        Assert.DoesNotContain(index.HitTest(new(50, 20)), p => p.EntityId == "wire");
        Assert.DoesNotContain(index.HitTest(new(50, 50)), p => p.EntityId == "locked");
        Assert.Contains(index.HitTest(new(50, 50), includeLocked: true), p => p.EntityId == "locked");
    }

    [Fact]
    public void HitTestDoesNotSelectEmptyArcCentreOrOutsideSweep()
    {
        var arc = new SceneArc("arc", "arc", new(20, 20), 10, 0, 90);
        var index = new SceneSpatialIndex([arc]);
        Assert.Empty(index.HitTest(new(20, 20), .2));
        Assert.Empty(index.HitTest(new(10, 20), .2));
        Assert.Single(index.HitTest(new(30, 20), .2));
        Assert.Empty(index.Query(new(double.NaN, 0, 1, 1)));
    }

    [Fact]
    public void ConnectorCanBeSelectedInsideItsContourButEmptyIllustrationCannot()
    {
        Point2[] triangle = [new(0, 0), new(20, 0), new(0, 20)];
        var connector = new ScenePath("connector", "connector-symbol", triangle, Closed: true);
        var illustration = new ScenePath("illustration", "annotation-line", triangle, Closed: true);
        var index = new SceneSpatialIndex([connector, illustration]);
        Assert.Equal("connector", Assert.Single(index.HitTest(new(5, 5), .1)).EntityId);
        Assert.Empty(index.HitTest(new(18, 18), .1)); // Inside the bounding box, outside the actual contour.
    }

    [Fact]
    public void WireProjectionCacheReusesUnchangedPathsAndKeepsOldScenesImmutable()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        project.Assembly.TwistGroups.Clear();
        var wire = project.Assembly.Conductors[0]; var other = project.Assembly.Conductors[1];
        ScenePath PathFor(DrawingScene scene, string id) => scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == id && p.Category == "wire");
        var first = SceneBuilder.Build(project, sheet);
        var second = SceneBuilder.Build(project, sheet);
        Assert.Same(PathFor(first, wire.Id), PathFor(second, wire.Id));
        var oldStart = PathFor(first, wire.Id).Points[0];
        wire.From.CavityId = project.Assembly.Connectors[0].Cavities[2].Id;
        var changed = SceneBuilder.Build(project, sheet);
        Assert.NotEqual(oldStart, PathFor(changed, wire.Id).Points[0]);
        Assert.Equal(oldStart, PathFor(first, wire.Id).Points[0]);
        Assert.Same(PathFor(first, other.Id), PathFor(changed, other.Id));
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value, PathFor(changed, wire.Id).Points[0]);
    }

    [Fact]
    public void WireProjectionCacheTracksStyleLabelsTransformAndLayerChanges()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        project.Assembly.TwistGroups.Clear();
        var wire = project.Assembly.Conductors[0];
        _ = SceneBuilder.Build(project, sheet);
        wire.Color = "#123456"; wire.ColorCode = "CUSTOM"; wire.Name = "W-NEW"; wire.CrossSectionMm2 = .125;
        view.Origin = new(12, 34); view.Scale = 2; sheet.PrintScale = .5;
        var route = view.Routes.First(r => r.BranchId == wire.BranchIds[0]);
        route.LayerId = "reference"; route.Id = "new-placement";
        var updated = SceneBuilder.Build(project, sheet);
        var line = updated.Primitives.OfType<ScenePath>().Single(p => p.EntityId == wire.Id && p.Category == "wire");
        var label = updated.Primitives.OfType<SceneText>().Single(p => p.EntityId == wire.Id && p.Category == "wire-label");
        Assert.Equal("#123456", line.Stroke);
        Assert.Equal("new-placement", line.PlacementId);
        Assert.Equal("reference", line.LayerId);
        Assert.True(line.Locked);
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value, line.Points[0]);
        Assert.Equal("W-NEW  CUSTOM  0.125 mm²", label.Text);
        Assert.DoesNotContain(SceneBuilder.Build(project, sheet, forPrint: true).Primitives, p => p.EntityId == wire.Id);
        sheet.Layers.Single(l => l.Id == "reference").Print = true;
        Assert.Contains(SceneBuilder.Build(project, sheet, forPrint: true).Primitives, p => p.EntityId == wire.Id);
        view.Scale = 3;
        var scaled = SceneBuilder.Build(project, sheet).Primitives.OfType<ScenePath>().Single(p => p.EntityId == wire.Id && p.Category == "wire");
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value * 1.5, scaled.Points[0]);
    }

    [Fact]
    public void WireProjectionCacheTracksTwistPitchAndFreeEndpointChanges()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0];
        var wire = project.Assembly.Conductors[0];
        ScenePath Wire(DrawingScene scene) => scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == wire.Id && p.Category == "twisted-wire");
        var before = Wire(SceneBuilder.Build(project, sheet));
        project.Assembly.TwistGroups[0].PitchMm = 11;
        var after = Wire(SceneBuilder.Build(project, sheet));
        Assert.False(before.Points.SequenceEqual(after.Points));
        wire.From = new() { Kind = EndpointKind.Free };
        var free = SceneBuilder.Build(project, sheet);
        Assert.Contains(free.Primitives, p => p.EntityId == wire.Id && p.Category == "free-end");
    }

    [Fact]
    public void TwistedStrandsShareBundleCentreAndPreserveTheirOwnPins()
    {
        var project = DemoFactory.Create(); var view = project.Sheets[0].Views[0];
        var scene = SceneBuilder.Build(project, project.Sheets[0]);
        var pair = project.Assembly.TwistGroups[0];
        var paths = pair.ConductorIds.Select(id => scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == id && p.Category == "twisted-wire")).ToArray();
        Assert.True(paths[0].Points[paths[0].Points.Count / 2].DistanceTo(paths[1].Points[paths[1].Points.Count / 2]) < 2.3);
        foreach (var path in paths)
        {
            var wire = project.Assembly.Conductors.Single(w => w.Id == path.EntityId);
            Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value, path.Points[0]);
            Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.To)!.Value, path.Points[^1]);
        }
    }

    [Fact]
    public void CustomTableColumnsReadAuthoritativeReports()
    {
        var project = DemoFactory.Create(); var view = project.Sheets[0].Views[0];
        view.Annotations.RemoveAll(a => a.Kind == AnnotationKind.WireTable);
        var table = new Annotation { Kind = AnnotationKind.WireTable, Points = [new(10, 150)], Columns = ["Name", "CutLengthMm", "LengthSource"], WidthMm = 200 };
        view.Annotations.Add(table);
        var cells = SceneBuilder.Build(project, project.Sheets[0]).Primitives.OfType<SceneText>().Where(p => p.EntityId == table.Id).ToArray();
        Assert.Contains(cells, p => p.Text == HarnessReports.Wires(project)[0].CutLengthMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(cells, p => p.Text == "Knip mm");
        Assert.Contains(cells, p => p.Text == "Route + toeslagen");
    }

    [Fact]
    public void ManufacturingTablesPreserveMillimetresWhenExpressedInMetres()
    {
        var project = DemoFactory.Create();
        var (_, rows) = SceneBuilder.TableData(project, AnnotationKind.BomTable);
        var expected = HarnessReports.Bom(project).First(row => Math.Abs(row.Quantity - .505) < 1e-10);
        Assert.Contains(rows, row => row["PartNumber"] == expected.PartNumber && row["Quantity"] == "0.505");
    }

    [Fact]
    public void ScreenRasterCacheReusesZoomLevelAndReleasesPreviousScene()
    {
        using var cache = new ScreenSceneCache();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var red = new DrawingScene("one", 80, 80, false,
            [new SceneEllipse("object", "test", new(20, 20), 5, 5, "#FF0000", 0, "#FF0000")]);
        var blue = new DrawingScene("two", 80, 80, false,
            [new SceneEllipse("object", "test", new(20, 20), 5, 5, "#0000FF", 0, "#0000FF")]);
        void Paint(DrawingScene scene, double zoom)
        {
            surface.Canvas.ResetMatrix();
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.Scale((float)zoom);
            SkiaSceneRenderer.RenderScreen(surface.Canvas, scene, new(0, 0, 80, 80), cache);
        }
        Paint(red, 2);
        var bytes = cache.CachedBytes;
        Paint(red, 2.02);
        Assert.Equal(bytes, cache.CachedBytes);
        Assert.Equal(1, cache.ImageCount);
        Paint(blue, 2);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(40, 40));
        Assert.Equal(1, cache.ImageCount);
        var moved = new DrawingScene("two", 80, 80, false,
            [new SceneEllipse("object", "test", new(40, 20), 5, 5, "#0000FF", 0, "#0000FF")]);
        Paint(moved, 2);
        using var movedImage = surface.Snapshot();
        using var movedBitmap = SKBitmap.FromImage(movedImage);
        Assert.Equal(SKColors.White, movedBitmap.GetPixel(40, 40));
        Assert.Equal(SKColors.Blue, movedBitmap.GetPixel(80, 40));
        cache.Clear();
        Assert.Equal(0, cache.CachedBytes);
    }

    [Fact]
    public void ScreenRasterTilesRenderVisibleGeometryWithinMemoryBudget()
    {
        var scene = new DrawingScene("large", 1189, 841, false,
            [new SceneEllipse("object", "test", new(20, 20), 5, 5, "#FF0000", 0, "#FF0000")]);
        using var cache = new ScreenSceneCache();
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Scale(8);
        surface.Canvas.Clear(SKColors.White);
        SkiaSceneRenderer.RenderScreen(surface.Canvas, scene, new(0, 0, 100, 75), cache);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(160, 160));
        Assert.InRange(cache.CachedBytes, 1, 64L * 1024 * 1024);
        Assert.True(cache.ImageCount > 1);
    }

    [Fact]
    public async Task ExportSetWritesRealVectorPdfPortableSvgPngAndThreeReports()
    {
        var project = DemoFactory.Create();
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await ExportService.ExportSetAsync(project, directory, true);
            Assert.Equal(5 + project.Sheets.Count * 2, result.Files.Count);
            var pdf = await File.ReadAllBytesAsync(result.Files.Single(p => p.EndsWith(".pdf")));
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf.AsSpan(0, 8)));
            var pdfText = Encoding.Latin1.GetString(pdf);
            Assert.Contains("/Font", pdfText);
            Assert.DoesNotContain("/Subtype /Image", pdfText); // This fixture has no raster inputs.
            Assert.Equal(project.Sheets.Count, result.Files.Count(p => p.EndsWith(".svg")));
            var svg = XDocument.Load(result.Files.First(p => p.EndsWith(".svg")));
            Assert.Equal("420mm", svg.Root!.Attribute("width")!.Value);
            Assert.Equal("297mm", svg.Root.Attribute("height")!.Value);
            Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "path");
            Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "text"); // Fonts are outlined.
            using var png = SKBitmap.Decode(result.Files.First(p => p.EndsWith(".png")));
            Assert.NotNull(png);
            Assert.True(png.Width > 2000);
            var csv = await File.ReadAllTextAsync(result.Files.Single(p => p.EndsWith("-draden.csv")));
            Assert.Contains("\"CONCEPT\"", csv);
            Assert.Contains("\"500\"", csv);
            var cuts = await File.ReadAllTextAsync(result.Files.Single(p => p.EndsWith("-kniplijst.csv")));
            Assert.Contains("\"Knip mm\"", cuts);
            Assert.Contains("\"500\"", cuts);
            var bom = await File.ReadAllTextAsync(result.Files.Single(p => p.EndsWith("-stuklijst.csv")));
            Assert.Contains("\"0.505\"", bom);
        }
        finally { DeleteTestDirectory(directory); }
    }

    [Fact]
    public async Task RepeatedExportSetsCreateSeparateCompletePackages()
    {
        var project = DemoFactory.Create();
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await ExportService.ExportSetAsync(project, directory, true);
            var second = await ExportService.ExportSetAsync(project, directory, true);
            Assert.NotEqual(Path.GetDirectoryName(first.Files[0]), Path.GetDirectoryName(second.Files[0]));
            Assert.All(first.Files.Concat(second.Files), file => Assert.True(File.Exists(file)));
            Assert.DoesNotContain(Directory.EnumerateDirectories(directory), path => Path.GetFileName(path).StartsWith(".kabelaar-export-"));
        }
        finally { DeleteTestDirectory(directory); }
    }

    [Fact]
    public async Task ProductionExportIsBlockedAndDoesNotOverwriteExistingFile()
    {
        var project = DemoFactory.Create(); project.Assembly.Conductors[0].WirePartId = null;
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "production.pdf"); File.WriteAllText(path, "keep existing");
        try
        {
            var exception = await Assert.ThrowsAsync<ProductionExportException>(() => ExportService.ExportPdfAsync(project, path, false));
            Assert.NotEmpty(exception.Issues);
            Assert.Equal("keep existing", await File.ReadAllTextAsync(path));
        }
        finally { DeleteTestDirectory(directory); }
    }

    [Fact]
    public async Task ExportCapturesSnapshotBeforeBackgroundWork()
    {
        var project = DemoFactory.Create();
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var task = ExportService.ExportCsvAsync(project, Path.Combine(directory, "snapshot.csv"));
            project.Assembly.Conductors[0].StartAllowanceMm = 9000;
            var result = await task;
            var csv = await File.ReadAllTextAsync(result.Files.Single(p => p.EndsWith("-draden.csv")));
            Assert.Contains("\"500\"", csv);
            Assert.DoesNotContain("\"9490\"", csv);
        }
        finally { DeleteTestDirectory(directory); }
    }

    [Fact]
    public async Task CancelledExportDoesNotCreateFiles()
    {
        var project = DemoFactory.Create();
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportService.ExportSetAsync(project, directory, cancellationToken: cancelled.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task StructurallyMalformedDraftIsRejectedBeforeCreatingOutput()
    {
        var project = DemoFactory.Create(); project.Assembly.Conductors = null!;
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<ProductionExportException>(() => ExportService.ExportSetAsync(project, directory, true));
        Assert.Contains(error.Issues, i => i.IsStructural && i.Code == "data.null");
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CorruptReferencedImageBlocksProductionBeforeCreatingOutput()
    {
        var project = DemoFactory.Create();
        var badImage = new ProjectAsset { FileName = "broken.png", MediaType = "image/png", Data = [1, 2, 3] };
        project.Assets.Add(badImage); project.Sheets[0].LogoAssetId = badImage.Id;
        var directory = Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests", Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<ProductionExportException>(() => ExportService.ExportSetAsync(project, directory, false));
        Assert.Contains(error.Issues, i => i.Code == "image.invalid");
        Assert.False(Directory.Exists(directory));
    }

    private static void DeleteTestDirectory(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kabelaar-rendering-tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        Assert.StartsWith(root, target, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    private static HarnessProject CompleteFixture()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var splice = new Splice { Name = "SP1" }; project.Assembly.Splices.Add(splice);
        view.Components.Add(new() { EntityId = splice.Id, Position = new(180, 80) });
        project.Parts[0].Labels.Add(new() { Text = "TOP", Position = new(-10, -10) });
        project.Assembly.Materials[0].BranchId = project.Assembly.Branches[0].Id;
        var cablePart = new PartDefinition { Name = "Cable", PartNumber = "CAB", Kind = PartKind.Cable };
        project.Parts.Add(cablePart);
        var cable = new CableInstance { Name = "C1", PartId = cablePart.Id, CutLengthMm = 500 }; project.Assembly.Cables.Add(cable);
        project.Assembly.Conductors[0].CableId = cable.Id;
        using var surface = SKSurface.Create(new SKImageInfo(10, 10));
        surface.Canvas.Clear(SKColors.Teal);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var asset = new ProjectAsset { FileName = "test.png", MediaType = "image/png", Data = data.ToArray() };
        project.Assets.Add(asset); sheet.LogoAssetId = asset.Id;
        view.Annotations.AddRange([
            new() { Kind = AnnotationKind.Line, Points = [new(10, 100), new(80, 100)] },
            new() { Kind = AnnotationKind.Arc, Points = [new(80, 100), new(90, 100), new(80, 110)] },
            new() { Kind = AnnotationKind.Leader, Text = "detail", Points = [new(100, 50), new(120, 40)] },
            new() { Kind = AnnotationKind.Image, AssetId = asset.Id, Points = [new(10, 10)] },
            new() { Kind = AnnotationKind.PinTable, Points = [new(10, 10)] },
            new() { Kind = AnnotationKind.RevisionTable, Points = [new(10, 10)] }
        ]);
        sheet.Views.Add(new() { Name = "Detail", Origin = new(300, 50), Scale = 2 });
        return project;
    }
}
