using System.Text.Json;
using Kabelaar.Core;
using Kabelaar.Rendering;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class Drawing094Tests
{
    [Fact]
    public void LabelOverrideEmptyAndAutomaticAreDistinctAndInvalidateCaches()
    {
        var project = CrossingProject(); var view = project.Sheets[0].Views[0]; var wire = project.Assembly.Conductors[0];
        view.Scale = 2; project.Sheets[0].PrintScale = .5;
        var custom = new WirePresentation { ConductorId = wire.Id, LabelText = "Eigen naam", LabelPosition = new(12, 17), LabelRotationDegrees = 30 };
        view.WirePresentations.Add(custom);
        var scene = SceneBuilder.Build(project, project.Sheets[0]);
        var label = Assert.Single(scene.Primitives.OfType<SceneText>(), p => p.EntityId == wire.Id && p.Category == "wire-label");
        Assert.Equal("Eigen naam", label.Text); Assert.Equal(view.Origin + new Point2(12, 17), label.Position); Assert.Equal(30, label.RotationDegrees);
        custom.LabelText = "";
        Assert.DoesNotContain(SceneBuilder.Build(project, project.Sheets[0]).Primitives, p => p.EntityId == wire.Id && p.Category == "wire-label");
        custom.LabelText = null;
        label = Assert.Single(SceneBuilder.Build(project, project.Sheets[0]).Primitives.OfType<SceneText>(), p => p.EntityId == wire.Id && p.Category == "wire-label");
        Assert.Contains(wire.Name, label.Text); Assert.DoesNotContain("Eigen naam", label.Text);
        view.Style.ShowWireLabels = false; custom.ShowLabel = true;
        Assert.Contains(SceneBuilder.Build(project, project.Sheets[0]).Primitives, p => p.EntityId == wire.Id && p.Category == "wire-label");
        custom.ShowLabel = false;
        Assert.DoesNotContain(SceneBuilder.Build(project, project.Sheets[0]).Primitives, p => p.EntityId == wire.Id && p.Category == "wire-label");
    }

    [Fact]
    public void ExplicitLabelPreviewCreatesLeaderAndGeometryPreviewKeepsAbsoluteLabelPosition()
    {
        var project = CrossingProject(); var sheet = project.Sheets[0]; var view = sheet.Views[0]; var wire = project.Assembly.Conductors[0];
        var style = new WirePresentation { ConductorId = wire.Id, LabelText = "Label", LabelPosition = new(50, 49) };
        view.WirePresentations.Add(style);
        SceneBuilder.Build(project, sheet);
        var detached = style.Copy(); detached.LabelPosition = new(80, 10);
        var preview = SceneBuilder.WireLabelPreview(project, sheet, view, wire, detached);
        Assert.Contains(preview, p => p.Category == "wire-label-leader");
        Assert.Equal(new Point2(50, 49), style.LabelPosition);
        style.LabelPosition = detached.LabelPosition;
        var committed = SceneBuilder.Build(project, sheet);
        var previewLabel = Assert.Single(preview.OfType<SceneText>(), p => p.Category == "wire-label");
        var committedLabel = Assert.Single(committed.Primitives.OfType<SceneText>(), p => p.EntityId == wire.Id && p.Category == "wire-label");
        Assert.Equal(committedLabel, previewLabel);
        Assert.Equal(Assert.Single(committed.Primitives.OfType<ScenePath>(), p => p.EntityId == wire.Id && p.Category == "wire-label-leader").Points,
            Assert.Single(preview.OfType<ScenePath>(), p => p.Category == "wire-label-leader").Points);
        var geometryPreview = SceneBuilder.WirePreview(project, sheet, view,
            new Dictionary<string, IReadOnlyList<Point2>> { [wire.Id] = [new(0, 70), new(100, 70)] });
        Assert.Equal(committedLabel.Position, Assert.Single(geometryPreview.OfType<SceneText>(), p => p.Category == "wire-label").Position);
    }

    [Fact]
    public void CrossingBridgesFollowDrawOrderWithoutChangingProductionOrEndpointJoins()
    {
        var project = CrossingProject(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var horizontal = project.Assembly.Conductors[0]; var vertical = project.Assembly.Conductors[1];
        view.WirePresentations = [new() { ConductorId = horizontal.Id, ShowCrossingBridges = true, CrossingRadiusMm = 3 },
            new() { ConductorId = vertical.Id, ShowCrossingBridges = true, CrossingRadiusMm = 3 }];
        var before = JsonSerializer.Serialize(project.Assembly);
        var scene = SceneBuilder.Build(project, sheet);
        Assert.Equal(2, Wire(scene, horizontal.Id).Points.Count);
        Assert.True(Wire(scene, vertical.Id).Points.Count > 2);
        Assert.Contains(Wire(scene, vertical.Id).Points, p => Math.Abs(p.X - view.Origin.X - 50) > 2);
        view.DrawOrder = [vertical.Id, horizontal.Id];
        scene = SceneBuilder.Build(project, sheet);
        Assert.Equal(2, Wire(scene, vertical.Id).Points.Count);
        Assert.True(Wire(scene, horizontal.Id).Points.Count > 2);
        Assert.True(scene.Primitives.ToList().FindIndex(p => p.EntityId == horizontal.Id) > scene.Primitives.ToList().FindIndex(p => p.EntityId == vertical.Id));
        Assert.Equal(before, JsonSerializer.Serialize(project.Assembly));

        // A real T joins at a physical splice: both incoming and outgoing conductors
        // terminate there, so no bridge may cover the junction.
        var splice = new Splice { Name = "SP1" }; project.Assembly.Splices.Add(splice);
        view.Components.Add(new() { EntityId = splice.Id, Position = new(50, 50) });
        horizontal.To = new() { Kind = EndpointKind.Splice, EntityId = splice.Id };
        project.Assembly.Branches[0].Points = [new(0, 50), new(50, 50)]; view.Routes[0].Points = [new(0, 50), new(50, 50)];
        vertical.To = new() { Kind = EndpointKind.Splice, EntityId = splice.Id };
        project.Assembly.Branches[1].Points = [new(50, 0), new(50, 50)]; view.Routes[1].Points = [new(50, 0), new(50, 50)];
        scene = SceneBuilder.Build(project, sheet);
        Assert.Equal(2, Wire(scene, horizontal.Id).Points.Count); Assert.Equal(2, Wire(scene, vertical.Id).Points.Count);
    }

    [Fact]
    public void MirroredConnectorPinIdentityAndRenderedEndpointStayTogether()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var connector = project.Assembly.Connectors[0]; var placement = view.Components.First(p => p.EntityId == connector.Id);
        var cavity = connector.Cavities[0]; var part = project.Parts.First(p => p.Id == connector.PartId);
        var pin = part.Pins.First(p => p.Id == cavity.PinDefinitionId); pin.Position = new(4, 7);
        SceneBuilder.Build(project, sheet); // Populate component projection cache.
        placement.MirrorX = true; placement.RotationDegrees = 90;
        var expected = Geometry.PinWorld(project, placement, cavity.Id)!.Value;
        var scene = SceneBuilder.Build(project, sheet);
        var actual = scene.Primitives.OfType<SceneEllipse>().Where(p => p.EntityId == connector.Id && p.Category == "pin").First();
        Assert.True(actual.Center.DistanceTo(view.Origin + expected * (view.Scale * sheet.PrintScale)) < 1e-8);
        Assert.Equal(expected, Geometry.EndpointPosition(project, view, new() { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = cavity.Id }));
        Assert.Equal(cavity.PinDefinitionId, pin.Id);
    }

    [Fact]
    public void FreeTableHasIndependentCellsWidthsAlignmentAndRotationInSharedScene()
    {
        var project = new HarnessProject(); var sheet = project.Sheets[0]; var view = sheet.Views[0]; view.Scale = 2;
        var table = new Annotation { Kind = AnnotationKind.FreeTable, Points = [new(10, 20)], Columns = ["A", "B"],
            Rows = [new() { Cells = ["left", "right"] }], WidthMm = 60, ColumnWidthsMm = [20, 40], RowHeightMm = 12,
            CellPaddingMm = 2, ColumnAlignments = [TableCellAlignment.Left, TableCellAlignment.Right],
            Color = "#AB2367", LineWidthMm = .6, TableHeaderColor = "#FECCAA", RotationDegrees = 90 };
        view.Annotations.Add(table);
        var scene = SceneBuilder.Build(project, sheet); var print = SceneBuilder.Build(project, sheet, forPrint: true);
        var cells = scene.Primitives.OfType<SceneText>().Where(p => p.EntityId == table.Id).ToArray();
        Assert.Equal(4, cells.Length); Assert.All(cells, p => Assert.Equal(90, p.RotationDegrees));
        Assert.Equal(TableCellAlignment.Right, cells.Single(p => p.Text == "right").Alignment);
        Assert.Equal(72, cells.Single(p => p.Text == "right").MaxWidthMm);
        var header = Assert.Single(scene.Primitives.OfType<ScenePath>(), p => p.EntityId == table.Id && p.Category == "table-header");
        Assert.Equal("#FECCAA", header.Fill); Assert.Equal(1.2, header.StrokeWidth, 8);
        Assert.Equal(120, header.Points[0].DistanceTo(header.Points[1]), 8);
        Assert.Equal(scene.Primitives.Where(p => p.EntityId == table.Id).Select(p => p.Bounds), print.Primitives.Where(p => p.EntityId == table.Id).Select(p => p.Bounds));
        table.ShowTableHeader = false;
        scene = SceneBuilder.Build(project, sheet);
        Assert.DoesNotContain(scene.Primitives, p => p.EntityId == table.Id && p.Category == "table-header");
        Assert.Equal(2, scene.Primitives.OfType<SceneText>().Count(p => p.EntityId == table.Id));
    }

    [Fact]
    public void ExtendedFrameIsOptInAndGraphicalNodesCannotConnectWires()
    {
        var project = CrossingProject(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var legacy = SceneBuilder.Build(project, sheet).Primitives.Where(p => p.Category == "title-block").ToArray();
        sheet.TitleBlock.CheckedBy = "Controleur"; sheet.TitleBlock.ApprovedBy = "Goedkeurder";
        Assert.Equal(legacy, SceneBuilder.Build(project, sheet).Primitives.Where(p => p.Category == "title-block"));
        sheet.TitleBlock.Enabled = true; sheet.TitleBlock.GeneralTolerances = "±1 mm"; sheet.ZoneColumns = 4; sheet.ZoneRows = 3;
        var reference = new PhysicalReferencePoint { Name = "REF1" }; project.Assembly.ReferencePoints.Add(reference);
        view.Components.Add(new() { EntityId = reference.Id, Position = new(0, 50) });
        project.Assembly.Conductors[0].From = new() { Kind = EndpointKind.ReferencePoint, EntityId = reference.Id };
        var symbol = new Annotation { Kind = AnnotationKind.GraphicalNode, Points = [new(50, 50)], WidthMm = 2, Text = "Symbool" };
        view.Annotations.Add(symbol);
        var snapshot = JsonSerializer.Serialize(project.Assembly);
        var scene = SceneBuilder.Build(project, sheet);
        Assert.Contains(scene.Primitives.OfType<SceneText>(), p => p.Text == "Controleur");
        Assert.Contains(scene.Primitives.OfType<SceneText>(), p => p.Text.Contains("±1 mm"));
        Assert.Equal(14, scene.Primitives.Count(p => p.Category == "sheet-zone-label"));
        Assert.Contains(scene.Primitives, p => p.EntityId == reference.Id && p.Category == "reference-point");
        Assert.Contains(scene.Primitives, p => p.EntityId == symbol.Id && p.Category == "graphical-node");
        Assert.Equal(snapshot, JsonSerializer.Serialize(project.Assembly));
        Assert.Equal(view.Origin + new Point2(0, 50), Wire(scene, project.Assembly.Conductors[0].Id).Points[0]);
        reference.Symbol = NodeSymbol.Ground; symbol.NodeSymbol = NodeSymbol.Ground; symbol.RotationDegrees = 90;
        scene = SceneBuilder.Build(project, sheet);
        var referenceLines = scene.Primitives.OfType<ScenePath>().Where(p => p.EntityId == reference.Id && p.Category == "reference-point").ToArray();
        var graphicalLines = scene.Primitives.OfType<ScenePath>().Where(p => p.EntityId == symbol.Id && p.Category == "graphical-node").ToArray();
        Assert.Equal(4, referenceLines.Length); Assert.Equal(4, graphicalLines.Length);
        Assert.Equal(view.Origin + new Point2(0, 50), referenceLines[0].Points[0]);
        Assert.True(referenceLines[1].Points[0].DistanceTo(referenceLines[1].Points[1]) > referenceLines[2].Points[0].DistanceTo(referenceLines[2].Points[1]));
        Assert.True(graphicalLines[0].Points[1].X < graphicalLines[0].Points[0].X);
        Assert.DoesNotContain(scene.Primitives.OfType<SceneEllipse>(), p => p.EntityId == reference.Id || p.EntityId == symbol.Id);
    }

    [Fact]
    public void MetadataTableColumnsKeepEndpointNotesWithTheirActualEndpoint()
    {
        var project = DemoFactory.Create(); var wire = project.Assembly.Conductors.First(w => w.From.Kind == EndpointKind.Cavity && w.To.Kind == EndpointKind.Cavity);
        wire.NetName = "POWER"; wire.Shielding = "Braided"; wire.From.Notes = "Start note"; wire.To.Notes = "End note";
        var data = SceneBuilder.TableData(project, AnnotationKind.WireTable, wire.To.EntityId);
        var row = data.Rows.Single(r => r["Name"] == wire.Name);
        Assert.Equal("POWER", row["NetName"]); Assert.Equal("Braided", row["Shielding"]);
        Assert.Equal("End note", row["FromNote"]); Assert.Equal("Start note", row["ToNote"]);
        Assert.DoesNotContain("NetName", data.Columns); // Existing table defaults retain their original layout.
        var csv = ExportService.Csv(project, AnnotationKind.WireTable, true);
        Assert.Contains("Netnaam", csv); Assert.Contains("POWER", csv);
    }

    [Fact]
    public async Task NewDocumentDetailsExportThroughAllThreeSharedGraphicsBackends()
    {
        var project = new HarnessProject(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        sheet.TitleBlock.Enabled = true; sheet.TitleBlock.CheckedBy = "Checked"; sheet.ZoneColumns = 4;
        view.Annotations.Add(new() { Kind = AnnotationKind.FreeTable, Points = [new(10, 20)], Columns = ["Item"], Rows = [new() { Cells = ["Test"] }], ColumnWidthsMm = [40], RotationDegrees = 15 });
        view.Annotations.Add(new() { Kind = AnnotationKind.Leader, Points = [new(60, 20), new(80, 35)], Text = "Leader", TextFrame = true, ArrowSizeMm = 4 });
        var directory = Path.Combine(Path.GetTempPath(), "kabelaar-render-094-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach (var format in new[] { ExportFormat.Pdf, ExportFormat.Svg, ExportFormat.Png })
            {
                var result = await ExportService.ExportAsync(project, Path.Combine(directory, "document"), format, new(true, 36));
                Assert.NotEmpty(result.Files); Assert.All(result.Files, file => Assert.True(new FileInfo(file).Length > 100));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ScenePath Wire(DrawingScene scene, string id) => Assert.Single(scene.Primitives.OfType<ScenePath>(), p => p.EntityId == id && p.Category == "wire");
    private static HarnessProject CrossingProject()
    {
        var project = new HarnessProject(); var view = project.Sheets[0].Views[0]; view.Mode = LayoutMode.Free;
        foreach (var points in new[] { new[] { new Point2(0, 50), new Point2(100, 50) }, new[] { new Point2(50, 0), new Point2(50, 100) } })
        {
            var branch = new RouteBranch { Points = points.ToList(), PhysicalLengthMm = 100 }; project.Assembly.Branches.Add(branch);
            view.Routes.Add(new() { BranchId = branch.Id, Points = points.ToList() });
            project.Assembly.Conductors.Add(new() { Name = "W" + project.Assembly.Conductors.Count, BranchIds = [branch.Id] });
        }
        return project;
    }
}
