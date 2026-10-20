using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void DemoIsCompleteAndUsesIndividualPairAndTriadConductors()
    {
        var project = DemoFactory.Create();
        Assert.Empty(ProductionValidator.Validate(project));
        Assert.Equal(5, HarnessReports.Wires(project).Count);
        Assert.Equal(new[] { 2, 3 }, project.Assembly.TwistGroups.Select(g => g.ConductorIds.Count));
        Assert.Equal(10, HarnessReports.Bom(project).Single(r => r.Kind == nameof(PartKind.Terminal)).Quantity);
        Assert.Equal(2, HarnessReports.Bom(project).Single(r => r.Kind == nameof(PartKind.Plug)).Quantity);
    }

    [Fact]
    public void FreeRouteBendsDoNotDuplicateMaterialOrAlterCutLength()
    {
        var project = DemoFactory.Create();
        var view = project.Sheets[0].Views[0];
        var wire = project.Assembly.Conductors[0];
        var route = view.Routes[0];
        var before = HarnessReports.CutLength(project, wire);
        var bom = HarnessReports.Bom(project).ToArray();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.InsertRoutePoint(project, view, route, 0, new(80, 120)));
        Assert.Equal(before, HarnessReports.CutLength(project, wire));
        Assert.Equal(5, project.Assembly.Conductors.Count);
        Assert.Equal(bom, HarnessReports.Bom(project));
        history.Undo();
        Assert.Equal(4, route.Points.Count);
    }

    [Fact]
    public void NewlyDrawnFreeRouteRequiresAnExplicitPhysicalLengthBeforeProduction()
    {
        var project = DemoFactory.Create();
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        var branch = new RouteBranch { Points = [new(0, 0), new(150, 0)], PhysicalLengthMm = 0 };
        var wire = new Conductor
        {
            Name = "W-FREE", WirePartId = project.Parts.First(p => p.Kind == PartKind.Wire).Id,
            From = new() { Description = "Vrij begin" }, To = new() { Description = "Vrij einde" }
        };
        var history = new CommandHistory();
        history.Execute(HarnessOperations.AddRoute(project, view, branch, wire));
        Assert.Equal(0, branch.PhysicalLengthMm);
        Assert.Contains(ProductionValidator.Validate(project), issue => issue.Code == "branch.length" && issue.EntityId == branch.Id);
        history.Execute(HarnessOperations.SetPhysicalLength(project, branch, 600, true));
        Assert.Equal(600, HarnessReports.CutLength(project, wire));
        Assert.DoesNotContain(ProductionValidator.Validate(project), issue => issue.EntityId == branch.Id);
        history.Undo();
        Assert.Equal(0, branch.PhysicalLengthMm);
    }

    [Fact]
    public void ScaledGeometryUpdatesLengthAndUndoRestoresIt()
    {
        var (project, view, route, branch) = Scaled();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetRoutePoints(project, view, route, [new(0, 0), new(150, 0)]));
        Assert.Equal(150, branch.PhysicalLengthMm);
        history.Undo();
        Assert.Equal(100, branch.PhysicalLengthMm);
        history.Redo();
        Assert.Equal(150, branch.PhysicalLengthMm);
    }

    [Fact]
    public void LockRejectsMutationBeforeHistoryOrGeometryChanges()
    {
        var (project, view, route, branch) = Scaled();
        branch.LengthLocked = true;
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetRoutePoints(project, view, route, [new(0, 0), new(101, 0)]));
        Assert.Equal(new Point2(100, 0), route.Points[^1]);
        Assert.Equal(100, branch.PhysicalLengthMm);
    }

    [Fact]
    public void ScaledModeRejectsFreeDrawingThatDisagreesWithProductionLength()
    {
        var (project, view, route, branch) = Scaled();
        view.Mode = LayoutMode.Free;
        branch.PhysicalLengthMm = 700;
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetLayoutMode(project, view, LayoutMode.Scaled));
        Assert.Equal(LayoutMode.Free, view.Mode);
        Assert.Equal(700, branch.PhysicalLengthMm);
    }

    [Fact]
    public void DetailViewsDoNotChangeBomAndExcludedMatingConnectorIsNotCounted()
    {
        var project = DemoFactory.Create();
        var before = HarnessReports.Bom(project).ToArray();
        project.Sheets[0].Views.Add(new DrawingView
        {
            Components = project.Sheets[0].Views[0].Components.Select(c => new PlacedComponent { EntityId = c.EntityId, Position = new(10, 10) }).ToList()
        });
        Assert.Equal(before, HarnessReports.Bom(project));
        project.Assembly.Connectors[1].IncludedInAssembly = false;
        Assert.Equal(1, HarnessReports.Bom(project).Single(r => r.Kind == nameof(PartKind.Housing)).Quantity);
        Assert.Equal(5, HarnessReports.Bom(project).Single(r => r.Kind == nameof(PartKind.Terminal)).Quantity);
    }

    [Fact]
    public void MixedTerminalsAndDoubleCrimpAreCountedPerInstalledCavity()
    {
        var project = DemoFactory.Create();
        var connector = project.Assembly.Connectors[0];
        var otherTerminal = new PartDefinition { Name = "Ander", PartNumber = "OTHER", Kind = PartKind.Terminal };
        project.Parts.Add(otherTerminal);
        connector.Cavities[0].TerminalPartId = otherTerminal.Id;
        connector.Cavities[0].MaxWires = 2;
        project.Assembly.Conductors[1].From.CavityId = connector.Cavities[0].Id;
        connector.Cavities[1].TerminalPartId = null;
        connector.Cavities[1].SealPartId = null;
        connector.Cavities[1].State = CavityState.Unused;
        Assert.Equal(1, HarnessReports.Bom(project).Single(r => r.PartId == otherTerminal.Id).Quantity);
        Assert.Equal(9, HarnessReports.Bom(project).Where(r => r.Kind == nameof(PartKind.Terminal)).Sum(r => r.Quantity));
    }

    [Fact]
    public void CableMembersAreListedIndividuallyButNotBoughtAsLooseWire()
    {
        var project = DemoFactory.Create();
        var part = new PartDefinition { Name = "Kabel", PartNumber = "CAB", Kind = PartKind.Cable };
        project.Parts.Add(part);
        var cable = new CableInstance { Name = "C1", PartId = part.Id, CutLengthMm = 600 };
        project.Assembly.Cables.Add(cable);
        foreach (var wire in project.Assembly.Conductors) wire.CableId = cable.Id;
        Assert.Equal(5, HarnessReports.Wires(project).Count);
        Assert.DoesNotContain(HarnessReports.Bom(project), r => r.Kind == nameof(PartKind.Wire));
        Assert.Equal(.6, HarnessReports.Bom(project).Single(r => r.Kind == nameof(PartKind.Cable)).Quantity);
    }

    [Fact]
    public void MissingProductionInformationIsDifferentFromBrokenReference()
    {
        var project = DemoFactory.Create();
        project.Assembly.Conductors[0].ColorCode = "";
        Assert.Empty(StructuralValidator.Validate(project));
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "wire.color" && !i.IsStructural);
        project.Assembly.Conductors[0].From.CavityId = "missing";
        Assert.NotEmpty(StructuralValidator.Validate(project));
    }

    [Fact]
    public void DuplicateIdsInvalidEnumsAndNonfiniteCoordinatesAreReportedWithoutThrowing()
    {
        var project = DemoFactory.Create();
        project.Assembly.Conductors[1].Id = project.Assembly.Conductors[0].Id;
        project.Sheets[0].Views[0].Mode = (LayoutMode)99;
        project.Sheets[0].Views[0].Origin = new(double.NaN, 1);
        var issues = ProductionValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "data.duplicateId");
        Assert.Contains(issues, i => i.Code == "data.enum");
        Assert.Contains(issues, i => i.Code == "data.nonfinite");
    }

    [Fact]
    public void CompositeRollsBackEarlierCommandsWhenALaterCommandFails()
    {
        var value = 0;
        var command = new CompositeCommand("Samen",
            new DelegateCommand("Eén", () => value++, () => value--),
            new DelegateCommand("Fout", () => throw new InvalidOperationException(), () => { }));
        var history = new CommandHistory();
        Assert.Throws<InvalidOperationException>(() => history.Execute(command));
        Assert.Equal(0, value);
        Assert.False(history.IsDirty);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void DirtyTokenTracksUndoToSavedStateAndNewBranches()
    {
        var history = new CommandHistory();
        var value = 0;
        IEditCommand Edit() => new DelegateCommand("Wijzigen", () => value++, () => value--);
        history.Execute(Edit()); history.MarkSaved();
        history.Execute(Edit()); Assert.True(history.IsDirty);
        history.Undo(); Assert.False(history.IsDirty);
        history.Undo(); Assert.True(history.IsDirty);
        history.Execute(Edit()); Assert.True(history.IsDirty);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void SplineMeasurementUsesExactlyTheExportTessellation()
    {
        Point2[] points = [new(0, 0), new(20, 30), new(80, -20), new(100, 0)];
        var flattened = Geometry.Flatten(points, true);
        Assert.Equal(points[0], flattened[0]);
        Assert.Equal(points[^1], flattened[^1]);
        Assert.Equal(Geometry.Length(flattened), Geometry.Length(points, true), 10);
    }

    [Fact]
    public void PerformanceFixtureRetainsAllConductorsInReports()
    {
        var project = DemoFactory.CreatePerformance(2000);
        Assert.Equal(2000, HarnessReports.Wires(project).Count);
        Assert.Equal(4000, HarnessReports.Pins(project).Count);
        Assert.Empty(ProductionValidator.Validate(project));
    }

    [Fact]
    public void MoveSelectionTranslatesSharedRouteOnceAndPreservesLockedLength()
    {
        var project = DemoFactory.CreatePerformance(1);
        var view = project.Sheets[0].Views[0];
        var route = view.Routes[0];
        var before = route.Points.ToArray();
        var branch = project.Assembly.Branches[0];
        branch.LengthLocked = true;
        var physicalBefore = branch.PhysicalLengthMm;
        var delta = new Point2(15, 7);
        var history = new CommandHistory();
        history.Execute(HarnessOperations.MoveSelection(project, view, view.Components.ToDictionary(c => c.Id, c => c.Position + delta)));
        Assert.Equal(before.Select(p => p + delta), route.Points);
        Assert.Equal(physicalBefore, branch.PhysicalLengthMm);
        history.Undo();
        Assert.Equal(before, route.Points);
    }

    [Fact]
    public void MovingOneLockedEndpointRejectsEntireEdit()
    {
        var project = DemoFactory.CreatePerformance(1);
        var view = project.Sheets[0].Views[0];
        var placement = view.Components[0];
        var before = placement.Position;
        project.Assembly.Branches[0].LengthLocked = true;
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.MoveComponent(project, view, placement, before + new Point2(50, 0)));
        Assert.Equal(before, placement.Position);
    }

    [Fact]
    public void ReconnectingUpdatesViewGeometryAndBothCavityStatesAndCanUndo()
    {
        var project = DemoFactory.Create();
        var view = project.Sheets[0].Views[0];
        var wire = project.Assembly.Conductors[0];
        var connector = project.Assembly.Connectors[0];
        var oldCavity = connector.Cavities[0];
        var newCavity = connector.Cavities[5];
        var before = view.Routes[0].Points.ToArray();
        var next = new Endpoint { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = newCavity.Id };
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetConductorEndpoints(project, wire, next, wire.To));
        Assert.Equal(CavityState.Unused, oldCavity.State);
        Assert.Equal(CavityState.Occupied, newCavity.State);
        Assert.Equal(Geometry.EndpointPosition(project, view, next), view.Routes[0].Points[0]);
        Assert.Equal(newCavity.Id, wire.From.CavityId);
        history.Undo();
        Assert.Equal(CavityState.Occupied, oldCavity.State);
        Assert.Equal(CavityState.Plugged, newCavity.State);
        Assert.Equal(before, view.Routes[0].Points);
        Assert.Equal(oldCavity.Id, wire.From.CavityId);
    }

    [Fact]
    public void ReconnectLockFailureLeavesMetadataAndGeometryUnchanged()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        var wire = project.Assembly.Conductors[0];
        var oldCavity = wire.From.CavityId;
        project.Assembly.Branches[0].LengthLocked = true;
        var connector = project.Assembly.Connectors[0];
        var next = new Endpoint { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = connector.Cavities[1].Id };
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetConductorEndpoints(project, wire, next, wire.To));
        Assert.Equal(oldCavity, wire.From.CavityId);
        Assert.Equal(Geometry.EndpointPosition(project, view, wire.From), view.Routes[0].Points[0]);
    }

    [Fact]
    public void ScaledChangePreservesOtherViewsLayoutAndReportsItsMismatch()
    {
        var (project, view, route, _) = Scaled();
        var siblingRoute = new PlacedRoute { BranchId = route.BranchId, Points = [new(10, 10), new(110, 10)] };
        project.Sheets[0].Views.Add(new DrawingView { Routes = [siblingRoute] });
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetRoutePoints(project, view, route, [new(0, 0), new(200, 0)]));
        Assert.Equal(new Point2(110, 10), siblingRoute.Points[^1]);
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "route.scaleMismatch" && i.EntityId == siblingRoute.Id);
    }

    [Fact]
    public void NullRequiredDataReturnsStructuralIssuesInsteadOfThrowing()
    {
        var project = DemoFactory.Create();
        project.Parts[0].Id = null!;
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "data.null");
        project.Assembly = null!;
        Assert.NotEmpty(StructuralValidator.Validate(project));
    }

    [Fact]
    public void RingTerminalIsItsOwnMountedTerminationAndDoesNotRequireAnotherTerminal()
    {
        var project = DemoFactory.CreatePerformance(1);
        var connector = project.Assembly.Connectors[0];
        var ring = new PartDefinition
        {
            Name = "Ringterminal", PartNumber = "RING-05-M4", Kind = PartKind.RingTerminal,
            RequiresTerminal = true, Pins = [new() { Label = "1" }]
        };
        project.Parts.Add(ring);
        connector.PartId = ring.Id;
        connector.Cavities[0].PinDefinitionId = ring.Pins[0].Id;
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code is "part.kind" or "part.required");
        Assert.Equal(1, HarnessReports.Bom(project).Single(row => row.PartId == ring.Id).Quantity);
    }

    [Fact]
    public void AcceptanceFixtureContainsExactly100ConnectorsAnd1000ValidConductors()
    {
        var project = DemoFactory.CreateAcceptancePerformance();
        Assert.Equal(100, project.Assembly.Connectors.Count);
        Assert.Equal(1000, project.Assembly.Conductors.Count);
        Assert.Equal(2000, HarnessReports.Pins(project).Count);
        Assert.Empty(ProductionValidator.Validate(project));
    }

    [Fact]
    public void PinRevisionUsesPreviousPositionToKeepTheCorrectRouteEnd()
    {
        var project = DemoFactory.CreatePerformance(1);
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        var placement = view.Components[0];
        var connector = project.Assembly.Connectors[0];
        var cavity = connector.Cavities[0];
        var oldPin = Geometry.PinWorld(project, placement, cavity.Id)!.Value;
        var farEnd = view.Routes[0].Points[^1];
        var part = project.Parts.Find(p => p.Id == connector.PartId)!;
        part.Pins[0].Position = new(600, 0);
        var history = new CommandHistory();
        history.Execute(HarnessOperations.MoveComponent(project, view, placement, placement.Position,
            previousPinPositions: new Dictionary<string, Point2> { [cavity.Id] = oldPin }));
        Assert.Equal(Geometry.PinWorld(project, placement, cavity.Id), view.Routes[0].Points[0]);
        Assert.Equal(farEnd, view.Routes[0].Points[^1]);
        history.Undo();
        Assert.Equal(oldPin, view.Routes[0].Points[0]);
    }

    [Fact]
    public void EmptySheetAndViewCollectionsAreRejectedBeforeTheEditorLoadsThem()
    {
        var project = DemoFactory.Create();
        project.Sheets[0].Views.Clear();
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "view.required" && i.IsStructural);
        project.Sheets.Clear();
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "sheet.required" && i.IsStructural);
    }

    private static (HarnessProject, DrawingView, PlacedRoute, RouteBranch) Scaled()
    {
        var project = new HarnessProject();
        var view = project.Sheets[0].Views[0];
        var branch = new RouteBranch { Points = [new(0, 0), new(100, 0)], PhysicalLengthMm = 100 };
        var route = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points] };
        project.Assembly.Branches.Add(branch); view.Routes.Add(route);
        return (project, view, route, branch);
    }
}
