using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class RouteTopologyTests
{
    [Fact]
    public void PhysicalSplitPreservesWireCountCutLengthBomAndAllViewGeometry()
    {
        var project = Fixture(LayoutMode.Scaled, true);
        var view = project.Sheets[0].Views[0];
        var route = view.Routes[0];
        var secondView = new DrawingView { Mode = LayoutMode.Scaled, Routes = [new() { BranchId = route.BranchId, Points = [.. route.Points], Smooth = true }] };
        project.Sheets[0].Views.Add(secondView);
        var conductor = project.Assembly.Conductors[0];
        var beforeLength = HarnessReports.CutLength(project, conductor);
        var beforeBom = HarnessReports.Bom(project).ToArray();
        var beforeDrawnLength = Geometry.Length(route.Points, true);
        var history = new CommandHistory();
        history.Execute(HarnessRouteOperations.SplitPhysicalBranch(project, view, route, 1));
        Assert.Single(project.Assembly.Conductors);
        Assert.Equal(2, conductor.BranchIds.Count);
        Assert.Equal(beforeLength, HarnessReports.CutLength(project, conductor), 9);
        Assert.Equal(beforeBom, HarnessReports.Bom(project));
        Assert.Equal(beforeDrawnLength, view.Routes.Sum(r => Geometry.Length(r.Points, r.Smooth)), 8);
        Assert.Equal(2, secondView.Routes.Count);
        Assert.True(HarnessRouteOperations.IsContinuous(project, conductor.BranchIds));
        history.Undo();
        Assert.Single(project.Assembly.Branches);
        Assert.Single(conductor.BranchIds);
        Assert.Same(route, view.Routes[0]);
        history.Redo();
        Assert.Equal(beforeLength, HarnessReports.CutLength(project, conductor), 9);
    }

    [Fact]
    public void FreeSplitRequiresExplicitPhysicalPartitionAndPreservesTotal()
    {
        var project = Fixture(LayoutMode.Free);
        var view = project.Sheets[0].Views[0];
        project.Assembly.Branches[0].PhysicalLengthMm = 1000;
        Assert.Throws<InvalidOperationException>(() => HarnessRouteOperations.SplitPhysicalBranch(project, view, view.Routes[0], 1));
        var history = new CommandHistory();
        history.Execute(HarnessRouteOperations.SplitPhysicalBranch(project, view, view.Routes[0], 1, 350));
        Assert.Equal(new[] { 350d, 650d }, project.Assembly.Branches.Select(b => b.PhysicalLengthMm));
        Assert.Equal(1000, HarnessReports.RouteLength(project, project.Assembly.Conductors[0]));
    }

    [Fact]
    public void SharedRouteSplitUpdatesEveryWireWithoutDuplicatingAny()
    {
        var project = Fixture(LayoutMode.Scaled);
        var route = project.Sheets[0].Views[0].Routes[0];
        var original = project.Assembly.Conductors[0];
        var other = new Conductor { Name = "W2", WirePartId = original.WirePartId, BranchIds = [.. original.BranchIds] };
        var history = new CommandHistory();
        history.Execute(HarnessRouteOperations.AddConductor(project, other));
        history.Execute(HarnessRouteOperations.SplitPhysicalBranch(project, project.Sheets[0].Views[0], route, 1));
        Assert.Equal(2, project.Assembly.Conductors.Count);
        Assert.All(project.Assembly.Conductors, w => Assert.Equal(2, w.BranchIds.Count));
        Assert.Equal(original.BranchIds, other.BranchIds);
    }

    [Fact]
    public void SplitRejectsUnresolvedMaterialAndDimensionMeaningWithoutAnyMutation()
    {
        var project = Fixture(LayoutMode.Free);
        var branch = project.Assembly.Branches[0];
        project.Assembly.Materials.Add(new() { BranchId = branch.Id, Quantity = 1 });
        Assert.Throws<InvalidOperationException>(() => HarnessRouteOperations.SplitPhysicalBranch(project, project.Sheets[0].Views[0], project.Sheets[0].Views[0].Routes[0], 1, 50));
        Assert.Single(project.Assembly.Branches);
        Assert.Single(project.Assembly.Conductors[0].BranchIds);
        Assert.Equal(branch.Id, project.Assembly.Materials[0].BranchId);
    }

    [Fact]
    public void RouteAssignmentRejectsDisconnectedAndRepeatedSections()
    {
        var project = Fixture(LayoutMode.Free);
        var first = project.Assembly.Branches[0];
        var next = new RouteBranch { Points = [new(100, 0), new(150, 0)], PhysicalLengthMm = 50 };
        var disconnected = new RouteBranch { Points = [new(900, 0), new(950, 0)], PhysicalLengthMm = 50 };
        project.Assembly.Branches.AddRange([next, disconnected]);
        var wire = project.Assembly.Conductors[0];
        Assert.Throws<InvalidOperationException>(() => HarnessRouteOperations.SetConductorRoute(project, wire, [first.Id, disconnected.Id]));
        Assert.Throws<InvalidOperationException>(() => HarnessRouteOperations.SetConductorRoute(project, wire, [first.Id, first.Id]));
        var history = new CommandHistory();
        history.Execute(HarnessRouteOperations.SetConductorRoute(project, wire, [first.Id, next.Id]));
        Assert.Equal(2, wire.BranchIds.Count);
        history.Undo();
        Assert.Single(wire.BranchIds);
    }

    [Fact]
    public void InvalidSerializedAnnotationGeometryAndStyleAreStructuralErrors()
    {
        var project = Fixture(LayoutMode.Free);
        var annotation = new Annotation { Kind = AnnotationKind.Image, WidthMm = -1, HeightMm = 0, FontSizeMm = 0, LineWidthMm = -1, Points = [] };
        project.Sheets[0].Views[0].Annotations.Add(annotation);
        var issues = ProductionValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "annotation.geometry" && i.IsStructural);
        Assert.Contains(issues, i => i.Code == "annotation.style" && i.IsStructural);
        Assert.Contains(issues, i => i.Code == "annotation.size" && i.IsStructural);
    }

    [Fact]
    public void SharedScaledRouteRequiresOwnLeadBeforeDifferentPinCanBeAssigned()
    {
        var project = DemoFactory.CreatePerformance(2);
        var original = project.Assembly.Conductors[0];
        var extra = new Conductor { Name = "W3", WirePartId = original.WirePartId, BranchIds = [.. original.BranchIds] };
        project.Assembly.Conductors.Add(extra);
        var connector = project.Assembly.Connectors[0];
        var changed = new Endpoint { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = connector.Cavities[1].Id };
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetConductorEndpoints(project, extra, changed, original.To));
        Assert.Equal(EndpointKind.Free, extra.From.Kind);
    }

    [Fact]
    public void SharedFreeRouteKeepsCommonGeometryAndEveryPhysicalLengthWhenReconnected()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        var original = project.Assembly.Conductors[0];
        var extra = new Conductor { Name = "W3", WirePartId = original.WirePartId, BranchIds = [.. original.BranchIds] };
        project.Assembly.Conductors.Add(extra);
        var connector = project.Assembly.Connectors[0];
        var changed = new Endpoint { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = connector.Cavities[1].Id };
        var beforePoints = view.Routes[0].Points.ToArray();
        var beforeLengths = project.Assembly.Branches.Select(b => b.PhysicalLengthMm).ToArray();
        new CommandHistory().Execute(HarnessOperations.SetConductorEndpoints(project, extra, changed, original.To));
        Assert.Equal(beforePoints, view.Routes[0].Points);
        Assert.Equal(beforeLengths, project.Assembly.Branches.Select(b => b.PhysicalLengthMm));
        Assert.Equal(changed.CavityId, extra.From.CavityId);
    }

    [Fact]
    public void ProductionGateDetectsUnmeasuredScaledLeadFromImportedReferences()
    {
        var project = DemoFactory.CreatePerformance(2);
        project.Assembly.Conductors[0].From.CavityId = project.Assembly.Connectors[0].Cavities[1].Id;
        Assert.Contains(ProductionValidator.Validate(project), i => i.Code == "wire.endpointGeometry");
    }

    [Fact]
    public void OppositeTraversalGetsOppositeChildOrderAfterSplit()
    {
        var project = Fixture(LayoutMode.Free);
        var view = project.Sheets[0].Views[0];
        var splice = new Splice { Name = "SP1" };
        project.Assembly.Splices.Add(splice);
        view.Components.Add(new() { EntityId = splice.Id, Position = new(100, 0) });
        var original = project.Assembly.Conductors[0];
        var reverse = new Conductor { Name = "W2", BranchIds = [.. original.BranchIds], From = new() { Kind = EndpointKind.Splice, EntityId = splice.Id } };
        project.Assembly.Conductors.Add(reverse);
        new CommandHistory().Execute(HarnessRouteOperations.SplitPhysicalBranch(project, view, view.Routes[0], 1, 50));
        Assert.Equal(original.BranchIds.AsEnumerable().Reverse(), reverse.BranchIds);
    }

    private static HarnessProject Fixture(LayoutMode mode, bool smooth = false)
    {
        var project = new HarnessProject { DrawingNumber = "TOPO-001" };
        var wirePart = new PartDefinition { Kind = PartKind.Wire, PartNumber = "TEST-W" };
        project.Parts.Add(wirePart);
        var branch = new RouteBranch { Name = "B1", Points = [new(0, 0), new(40, 20), new(100, 0)], Smooth = smooth };
        branch.PhysicalLengthMm = Geometry.Length(branch.Points, smooth);
        project.Assembly.Branches.Add(branch);
        var view = project.Sheets[0].Views[0];
        view.Mode = mode;
        view.Routes.Add(new() { BranchId = branch.Id, Points = [.. branch.Points], Smooth = smooth });
        project.Assembly.Conductors.Add(new() { Name = "W1", WirePartId = wirePart.Id, BranchIds = [branch.Id], StartAllowanceMm = 5, EndAllowanceMm = 7 });
        return project;
    }
}
