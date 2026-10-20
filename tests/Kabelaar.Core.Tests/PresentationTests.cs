using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class PresentationTests
{
    [Fact]
    public void SymbolPresentationMovesConnectedPinsWithoutChangingPhysicalAssemblyAndUndoesTogether()
    {
        var (project, view, placement, route, wire) = Fixture();
        var beforeAssembly = JsonSerializer.Serialize(project.Assembly);
        var beforePart = JsonSerializer.Serialize(project.Parts);
        var beforePoints = route.Points.ToArray();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetComponentPresentation(project, view, placement,
            .5, false, false, true, false, 2.5, new(55, 35), 90));

        Assert.Equal(new Point2(54, 40), Geometry.PinWorld(project, placement, wire.From.CavityId));
        Assert.Equal(new Point2(54, 40), route.Points[0]);
        Assert.Equal(beforePoints[^1], route.Points[^1]);
        Assert.Equal(beforeAssembly, JsonSerializer.Serialize(project.Assembly));
        Assert.Equal(beforePart, JsonSerializer.Serialize(project.Parts));
        Assert.False(placement.ShowPinNumbers);
        Assert.False(placement.ShowPartNumber);
        Assert.Equal(2.5, placement.LabelFontSizeMm);

        history.Undo();
        Assert.Equal(beforePoints, route.Points);
        Assert.Equal(new Point2(50, 30), placement.Position);
        Assert.Equal(1, placement.DisplayScale);
        Assert.True(placement.ShowPins);
        history.Redo();
        Assert.Equal(new Point2(54, 40), route.Points[0]);
        Assert.Equal(beforeAssembly, JsonSerializer.Serialize(project.Assembly));
    }

    [Fact]
    public void LabelsOnlyEditPreservesDeliberateLeadGeometry()
    {
        var (project, view, placement, route, _) = Fixture();
        route.Points[0] = new(80, 32);
        var beforePoints = route.Points.ToArray();
        new CommandHistory().Execute(HarnessOperations.SetComponentPresentation(project, view, placement, 1, false, false, false, false, 2.5));
        Assert.Equal(beforePoints, route.Points);
    }

    [Fact]
    public void SubsequentMoveAndRotationKeepDisplayScaleAndEndpointAttachment()
    {
        var (project, view, placement, route, wire) = Fixture();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetComponentPresentation(project, view, placement, .5, true, true, true, true, 3));
        history.Execute(HarnessOperations.MoveComponent(project, view, placement, new(80, 50), 180));

        Assert.Equal(.5, placement.DisplayScale);
        Assert.Equal(Geometry.PinWorld(project, placement, wire.From.CavityId), route.Points[0]);
        Assert.Equal(new Point2(75, 49), route.Points[0]);
        history.Undo();
        Assert.Equal(new Point2(55, 31), route.Points[0]);
    }

    [Fact]
    public void IndependentSymbolScaleCannotBeMistakenForMeasuredGeometry()
    {
        var (project, view, placement, route, _) = Fixture();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetComponentPresentation(project, view, placement, .5, true, true, true, true, 3));
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetLayoutMode(project, view, LayoutMode.Scaled));
        Assert.Equal(LayoutMode.Free, view.Mode);

        view.Mode = LayoutMode.Scaled;
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "placement.scaledSymbol" && i.IsStructural);
        Assert.Throws<InvalidOperationException>(() =>
            HarnessOperations.SetComponentPresentation(project, view, placement, .75, true, true, true, true, 3));
        Assert.Equal(.5, placement.DisplayScale);
    }

    [Fact]
    public void DrawingStyleCommandCapturesValuesAndNeverChangesSpecifiedTwistOrCutLength()
    {
        var (project, view, _, _, wire) = Fixture();
        project.Assembly.TwistGroups.Add(new() { ConductorIds = [wire.Id, "synthetic-second-wire"], PitchMm = 20 });
        var before = JsonSerializer.Serialize(project.Assembly);
        var style = new DrawingStyle
        {
            ShowWireLabels = false, ShowTwistLabels = false, WireLineWidthMm = .25,
            TwistAmplitudeMm = .45, TwistDisplayPitchMm = 6, TwistDisplayMode = TwistDisplayMode.Markers
        };
        var history = new CommandHistory();
        var command = HarnessOperations.SetDrawingStyle(view, style);
        style.TwistDisplayPitchMm = 999;
        history.Execute(command);
        Assert.Equal(6, view.Style.TwistDisplayPitchMm);
        Assert.Equal(before, JsonSerializer.Serialize(project.Assembly));
        history.Undo();
        Assert.Equal(0, view.Style.TwistDisplayPitchMm);
        Assert.Equal(TwistDisplayMode.Full, view.Style.TwistDisplayMode);
        view.Style.WireLineWidthMm = 123;
        history.Redo();
        Assert.Equal(.25, view.Style.WireLineWidthMm);
        Assert.Equal(before, JsonSerializer.Serialize(project.Assembly));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, 3)]
    [InlineData(double.MaxValue, 3)]
    [InlineData(1, double.MaxValue)]
    public void InvalidPresentationCannotBeApplied(double scale, double fontSize)
    {
        var (project, view, placement, _, _) = Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HarnessOperations.SetComponentPresentation(project, view, placement, scale, true, true, true, true, fontSize));
        Assert.Equal(1, placement.DisplayScale);
    }

    [Fact]
    public void InvalidPersistedStyleAndNonfinitePlacementAreStructuralErrors()
    {
        var (project, view, placement, _, _) = Fixture();
        view.Style.WireLineWidthMm = 0;
        view.Style.TwistAmplitudeMm = -1;
        placement.DisplayScale = double.NaN;
        var issues = ProductionValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "view.style" && i.IsStructural);
        Assert.Contains(issues, i => i.Code == "data.nonfinite" && i.EntityId == placement.Id);
    }

    [Theory]
    [InlineData("wireWidth")]
    [InlineData("connectorWidth")]
    [InlineData("wireFont")]
    [InlineData("amplitude")]
    [InlineData("pitch")]
    public void FiniteButUnrenderableStyleValuesCannotEnterLiveOrSavedDocuments(string field)
    {
        var (project, view, _, _, _) = Fixture();
        var oversized = new DrawingStyle();
        switch (field)
        {
            case "wireWidth": oversized.WireLineWidthMm = double.MaxValue; break;
            case "connectorWidth": oversized.ConnectorLineWidthMm = double.MaxValue; break;
            case "wireFont": oversized.WireLabelFontSizeMm = double.MaxValue; break;
            case "amplitude": oversized.TwistAmplitudeMm = double.MaxValue; break;
            case "pitch": oversized.TwistDisplayPitchMm = double.MaxValue; break;
        }
        Assert.Throws<ArgumentException>(() => HarnessOperations.SetDrawingStyle(view, oversized));
        Assert.Equal(.45, view.Style.WireLineWidthMm);
        view.Style = oversized;
        Assert.Contains(StructuralValidator.Validate(project), message => message.StartsWith("view.style:"));
    }

    [Fact]
    public void MaximumPresentationValuesRemainSupportedAndOverflowingComponentValuesAreRejected()
    {
        var (project, view, placement, _, _) = Fixture();
        var style = new DrawingStyle
        {
            WireLineWidthMm = PresentationLimits.MaximumLineWidthMm,
            ConnectorLineWidthMm = PresentationLimits.MaximumLineWidthMm,
            WireLabelFontSizeMm = PresentationLimits.MaximumFontSizeMm,
            TwistAmplitudeMm = PresentationLimits.MaximumTwistAmplitudeMm,
            TwistDisplayPitchMm = PresentationLimits.MaximumTwistDisplayPitchMm
        };
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetDrawingStyle(view, style));
        history.Execute(HarnessOperations.SetComponentPresentation(project, view, placement,
            PresentationLimits.MaximumDisplayScale, true, true, true, true, PresentationLimits.MaximumFontSizeMm));
        Assert.Empty(StructuralValidator.Validate(project));
        placement.DisplayScale = double.MaxValue;
        Assert.Contains(StructuralValidator.Validate(project), message => message.StartsWith("placement.style:"));
    }

    [Theory]
    [InlineData(AnnotationKind.WireTable)]
    [InlineData(AnnotationKind.PinTable)]
    public void ConnectionTableFilterRequiresAConnectorInsteadOfSilentlyProducingAnEmptyTable(AnnotationKind kind)
    {
        var (project, view, placement, route, wire) = Fixture();
        var table = new Annotation { Kind = kind, EntityId = route.BranchId, Points = [new(10, 10)] };
        view.Annotations.Add(table);
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "annotation.connectorFilter" && i.IsStructural);
        table.EntityId = wire.Id;
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "annotation.connectorFilter" && i.IsStructural);
        table.EntityId = placement.EntityId;
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code.StartsWith("annotation."));
        table.EntityId = null;
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code.StartsWith("annotation."));
    }

    private static (HarnessProject Project, DrawingView View, PlacedComponent Placement, PlacedRoute Route, Conductor Wire) Fixture()
    {
        var part = new PartDefinition { Pins = [new() { Position = new(10, 2), Label = "1" }], RequiresTerminal = false };
        var first = HarnessOperations.NewConnector(part, "J1");
        var second = HarnessOperations.NewConnector(part, "J2");
        var placement = new PlacedComponent { EntityId = first.Id, Position = new(50, 30) };
        var other = new PlacedComponent { EntityId = second.Id, Position = new(150, 30) };
        var branch = new RouteBranch { Points = [new(60, 32), new(110, 32), new(160, 32)], PhysicalLengthMm = 100 };
        var route = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points] };
        var wire = new Conductor
        {
            From = new() { Kind = EndpointKind.Cavity, EntityId = first.Id, CavityId = first.Cavities[0].Id },
            To = new() { Kind = EndpointKind.Cavity, EntityId = second.Id, CavityId = second.Cavities[0].Id },
            BranchIds = [branch.Id], LengthSource = LengthSource.Manual, ManualCutLengthMm = 120
        };
        var view = new DrawingView { Mode = LayoutMode.Free, Components = [placement, other], Routes = [route] };
        var project = new HarnessProject
        {
            Parts = [part], Sheets = [new() { Views = [view] }],
            Assembly = new() { Connectors = [first, second], Branches = [branch], Conductors = [wire] }
        };
        return (project, view, placement, route, wire);
    }
}
