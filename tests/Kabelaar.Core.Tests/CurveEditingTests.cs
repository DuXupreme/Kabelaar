using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class CurveEditingTests
{
    [Fact]
    public void TwoPointWireCanBendFlipAndReturnStraightWithoutLosingItsHandles()
    {
        var straight = Geometry.FromLegacy([new(0, 0), new(100, 0)], false);
        var curve = Geometry.WithCurveKind(straight, RouteSegmentKind.Cubic, 20);
        Assert.Equal(new Point2(50, 15), Geometry.Evaluate(curve, 0, .5));
        Assert.Equal(new Point2(50, -15), Geometry.Evaluate(Geometry.FlipBend(curve), 0, .5));
        var toggled = Geometry.WithCurveKind(Geometry.WithCurveKind(curve, RouteSegmentKind.Line), RouteSegmentKind.Cubic);
        Assert.Equal(JsonSerializer.Serialize(curve), JsonSerializer.Serialize(toggled));
        Assert.Equal(RouteSegmentKind.Line, straight.Segments[0].Kind);
    }

    [Fact]
    public void LegacyConversionMatchesCatmullAtEveryOriginalSample()
    {
        Point2[] points = [new(0, 0), new(40, 20), new(70, -30), new(100, 0)];
        var legacy = Geometry.Flatten(points, true);
        var converted = Geometry.FromLegacy(points, true);
        var offset = 0;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var samples = (int)Math.Clamp(Math.Ceiling(points[i].DistanceTo(points[i + 1]) / 2), 16, 256);
            for (var sample = 1; sample <= samples; sample++)
                Assert.InRange(legacy[++offset].DistanceTo(Geometry.Evaluate(converted, i, sample / (double)samples)), 0, 1e-10);
        }
    }

    [Fact]
    public void DeliberatelyCollinearCurveHandlesSurviveStraightToggle()
    {
        var curve = Geometry.WithBend(Geometry.FromLegacy([new(0, 0), new(100, 0)], false), 0);
        var straight = Geometry.WithCurveKind(curve, RouteSegmentKind.Line);
        var restored = Geometry.WithCurveKind(straight, RouteSegmentKind.Cubic, 50);
        Assert.Equal(JsonSerializer.Serialize(curve), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void DeCasteljauSplitPreservesCurveEndpointsIdsLengthAndPhysicalAssembly()
    {
        var (project, view, route, wire) = Fixture();
        var originalAssembly = JsonSerializer.Serialize(project.Assembly);
        var geometry = Geometry.WithBend(Geometry.EditableRouteGeometry(view, route, wire.Id), 30);
        var split = Geometry.Split(geometry, 0, .37);
        Assert.Equal(geometry.Knots[0].Id, split.Knots[0].Id);
        Assert.Equal(geometry.Segments[0].Id, split.Segments[0].Id);
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100d;
            var actual = t <= .37 ? Geometry.Evaluate(split, 0, t / .37) : Geometry.Evaluate(split, 1, (t - .37) / .63);
            Assert.InRange(actual.DistanceTo(Geometry.Evaluate(geometry, 0, t)), 0, 1e-10);
        }
        Assert.InRange(Math.Abs(Geometry.Length(geometry) - Geometry.Length(split)), 0, .001);
        new CommandHistory().Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, split));
        Assert.Equal(originalAssembly, JsonSerializer.Serialize(project.Assembly));
        Assert.Single(project.Assembly.Conductors);
        Assert.Single(project.Assembly.Branches);
    }

    [Fact]
    public void IndividualGeometryAndWidthComposeInOneUndoWithoutClobberingAndWithoutChangingAnotherWire()
    {
        var (project, view, route, wire) = Fixture();
        var other = new Conductor { Name = "W2", BranchIds = [route.BranchId] };
        project.Assembly.Conductors.Add(other);
        var before = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        var draft = Geometry.WithBend(Geometry.EditableRouteGeometry(view, route, wire.Id), 25);
        history.Execute(new CompositeCommand("Aderweergave",
            HarnessOperations.SetWireLineWidth(view, wire.Id, 1.2),
            HarnessOperations.SetWireRouteGeometry(project, view, wire, route, draft)));
        draft.Knots[0].Position = new(999, 999);
        Assert.Equal(1.2, view.WirePresentations.Single().LineWidthMm);
        Assert.NotEqual(Geometry.Flatten(view, route, wire.Id), Geometry.Flatten(view, route, other.Id));
        Assert.Equal(route.Points, Geometry.Flatten(view, route, other.Id));
        Assert.Equal(120, HarnessReports.CutLength(project, wire));
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
        history.Redo();
        Assert.Equal(new Point2(0, 0), Geometry.EffectiveRouteGeometry(view, route, wire.Id)!.Knots[0].Position);
        Assert.Empty(StructuralValidator.Validate(project));
    }

    [Fact]
    public void BundleEditExplicitlyReplacesPerWireShapesAndUndoRestoresThem()
    {
        var (project, view, route, wire) = Fixture();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route,
            Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20)));
        var before = JsonSerializer.Serialize(view);
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route,
            Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), -20), wholeBundle: true));
        Assert.Empty(view.WirePresentations.Single().Routes);
        Assert.NotNull(route.Geometry);
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(view));
    }

    [Fact]
    public void LockedScaledCurveRejectsChangesBeforeAnyMutation()
    {
        var (project, view, route, wire) = Fixture();
        view.Mode = LayoutMode.Scaled;
        var branch = project.Assembly.Branches.Single(); branch.LengthLocked = true;
        var before = JsonSerializer.Serialize(project);
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetWireRouteGeometry(project, view, wire, route,
            Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 30)));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void ConflictingOtherScaledViewBlocksPhysicalLengthChange()
    {
        var (project, view, route, wire) = Fixture();
        view.Mode = LayoutMode.Scaled;
        project.Sheets[0].Views.Add(new() { Routes = [new() { BranchId = route.BranchId, Points = [.. route.Points] }] });
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetWireRouteGeometry(project, view, wire, route,
            Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 30)));
        Assert.Equal(100, project.Assembly.Branches.Single().PhysicalLengthMm);
    }

    [Fact]
    public void LegacyScaledEditReplacesCanonicalExplicitGeometryAndUndoRestoresIt()
    {
        var (project, view, route, _) = Fixture();
        view.Mode = LayoutMode.Scaled;
        var branch = project.Assembly.Branches.Single();
        branch.Geometry = Geometry.FromLegacy(branch.Points, false);
        var original = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetRoutePoints(project, view, route, [new(0, 10), new(100, 10)]));
        Assert.Null(branch.Geometry);
        Assert.Equal(new Point2(0, 10), Geometry.Flatten(branch)[0]);
        Assert.Empty(StructuralValidator.Validate(project));
        history.Undo();
        Assert.Equal(original, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void ReassigningPhysicalRouteRemovesOnlyStaleShapeOverridesAndUndoRestoresThem()
    {
        var (project, view, route, wire) = Fixture();
        var other = new RouteBranch { Points = [new(100, 0), new(150, 0)], PhysicalLengthMm = 50 };
        project.Assembly.Branches.Add(other);
        view.Routes.Add(new() { BranchId = other.Id, Points = [.. other.Points] });
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20)));
        var before = JsonSerializer.Serialize(project);
        history.Execute(HarnessRouteOperations.SetConductorRoute(project, wire, [other.Id]));
        Assert.Empty(view.WirePresentations.Single().Routes);
        Assert.Empty(StructuralValidator.Validate(project));
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void IndividualFreeGeometryMustBeExplicitlyResolvedBeforeSwitchingToScale()
    {
        var (project, view, route, wire) = Fixture();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20)));
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetLayoutMode(project, view, LayoutMode.Scaled));
        history.Execute(HarnessOperations.ResetWireRouteGeometry(view, wire.Id, route.BranchId));
        history.Execute(HarnessOperations.SetLayoutMode(project, view, LayoutMode.Scaled));
        Assert.Equal(LayoutMode.Scaled, view.Mode);
    }

    [Fact]
    public void MatchingIndividualGeometryCanConvertToScaledAndUndoRestoresTheOriginalFreeShape()
    {
        var (project, view, route, wire) = Fixture();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.EditableRouteGeometry(view, route)));
        var before = JsonSerializer.Serialize(project);
        history.Execute(HarnessOperations.SetLayoutMode(project, view, LayoutMode.Scaled));
        Assert.Empty(view.WirePresentations.Single().Routes);
        Assert.Equal(LayoutMode.Scaled, view.Mode);
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void ConnectorMoveFollowsIndividualCurveWithoutChangingItsHandlesOrOtherPhysicalData()
    {
        var (project, view, route, wire) = Fixture();
        var part = new PartDefinition { Pins = [new() { Position = new(0, 0) }] };
        project.Parts.Add(part);
        var connector = HarnessOperations.NewConnector(part, "J1");
        project.Assembly.Connectors.Add(connector);
        var placement = new PlacedComponent { EntityId = connector.Id, Position = new(0, 0) };
        view.Components.Add(placement);
        wire.From = new() { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = connector.Cavities[0].Id };
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20)));
        var before = JsonSerializer.Serialize(project);
        var handle = Geometry.EffectiveRouteGeometry(view, route, wire.Id)!.Knots[0].OutHandle;
        history.Execute(HarnessOperations.MoveComponent(project, view, placement, new(10, 20), 90));
        var moved = Geometry.EffectiveRouteGeometry(view, route, wire.Id)!;
        Assert.Equal(new Point2(10, 20), moved.Knots[0].Position);
        Assert.Equal(handle, moved.Knots[0].OutHandle);
        Assert.Equal(120, HarnessReports.CutLength(project, wire));
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void EndpointReassignmentAndDetachPreserveIndividualCurveAndUndo()
    {
        var (project, view, route, wire) = Fixture();
        var splice = new Splice();
        project.Assembly.Splices.Add(splice);
        view.Components.Add(new() { EntityId = splice.Id, Position = new(0, 10) });
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20)));
        var before = JsonSerializer.Serialize(project);
        history.Execute(HarnessOperations.SetConductorEndpoints(project, wire, new() { Kind = EndpointKind.Splice, EntityId = splice.Id }, wire.To));
        Assert.Equal(new Point2(0, 10), Geometry.EffectiveRouteGeometry(view, route, wire.Id)!.Knots[0].Position);
        var attached = JsonSerializer.Serialize(view);
        history.Execute(HarnessOperations.SetConductorEndpoints(project, wire, new(), wire.To));
        Assert.Equal(attached, JsonSerializer.Serialize(view));
        Assert.Null(wire.From.EntityId);
        history.Undo(); history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void ReconnectingCannotSilentlyInvalidateAScaledViewWithoutThatConnectorPlacement()
    {
        var (project, view, route, wire) = Fixture(); view.Mode = LayoutMode.Scaled;
        var splice = new Splice(); project.Assembly.Splices.Add(splice);
        view.Components.Add(new() { EntityId = splice.Id, Position = new(0, 20) });
        project.Sheets[0].Views.Add(new() { Routes = [new() { BranchId = route.BranchId, Points = [.. route.Points] }] });
        var before = JsonSerializer.Serialize(project);
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.SetConductorEndpoints(project, wire,
            new() { Kind = EndpointKind.Splice, EntityId = splice.Id }, wire.To));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void BatchComponentMoveUpdatesMatchingDuplicateRoutePlacementsAtomicallyAndUndoes()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        var placement = view.Components[0];
        var route = view.Routes[0];
        var duplicate = new PlacedRoute { BranchId = route.BranchId, Points = [.. route.Points], Smooth = route.Smooth };
        view.Routes.Add(duplicate);
        var before = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        history.Execute(HarnessOperations.MoveComponent(project, view, placement, placement.Position + new Point2(5, 0)));
        Assert.Equal(route.Points, duplicate.Points);
        Assert.Equal(Geometry.Length(route), Geometry.Length(duplicate), 10);
        Assert.DoesNotContain(ProductionValidator.Validate(project), issue => issue.Code == "route.scaleMismatch");
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void BatchComponentMoveRejectsConflictingDuplicateRoutePlacementsWithoutPartialMutation()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        var placement = view.Components[0];
        var route = view.Routes[0];
        var reflected = route.Points.Select(point => new Point2(2 * placement.Position.X - point.X, point.Y)).ToList();
        // Equal initial lengths; moving the shared connector changes the reflected route
        // in the opposite direction, so the two representations cannot share a new length.
        view.Routes.Add(new() { BranchId = route.BranchId, Points = reflected, Smooth = route.Smooth });
        var before = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        Assert.Throws<InvalidOperationException>(() =>
            history.Execute(HarnessOperations.MoveComponent(project, view, placement, placement.Position + new Point2(5, 0))));
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.False(history.IsDirty);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void BatchComponentMoveStillChecksUnchangedRoutesInOtherScaledViews()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        var route = view.Routes[0];
        project.Sheets[0].Views.Add(new() { Routes = [new() { BranchId = route.BranchId, Points = [.. route.Points], Smooth = route.Smooth }] });
        var before = JsonSerializer.Serialize(project);
        Assert.Throws<InvalidOperationException>(() => HarnessOperations.MoveComponent(project, view, view.Components[0],
            view.Components[0].Position + new Point2(5, 0)));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void PhysicalSplitRetainsCubicShapeAndIndividualOverridesWithoutAddingConductors()
    {
        var (project, view, route, wire) = Fixture();
        var history = new CommandHistory();
        var curve = Geometry.Split(Geometry.WithBend(Geometry.EditableRouteGeometry(view, route), 20), 0, .5);
        history.Execute(HarnessOperations.SetRouteGeometry(project, view, route, curve));
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, Geometry.FlipBend(curve)));
        var before = JsonSerializer.Serialize(project);
        var physical = HarnessReports.CutLength(project, wire);
        var shapeLength = Geometry.Length(Geometry.EffectiveRouteGeometry(view, route, wire.Id)!);
        history.Execute(HarnessRouteOperations.SplitPhysicalBranch(project, view, route, 1, 40));
        Assert.Single(project.Assembly.Conductors);
        Assert.Equal(physical, HarnessReports.CutLength(project, wire));
        Assert.Equal(2, view.WirePresentations.Single().Routes.Count);
        Assert.InRange(Math.Abs(view.WirePresentations.Single().Routes.Sum(r => Geometry.Length(r.Geometry)) - shapeLength), 0, .001);
        Assert.All(view.Routes, r => Assert.NotNull(r.Geometry));
        Assert.Empty(StructuralValidator.Validate(project));
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Theory]
    [InlineData(TangentMode.Independent)]
    [InlineData(TangentMode.Aligned)]
    [InlineData(TangentMode.Mirrored)]
    public void TangentModesRespectIndependentDirectionAndAlignedOrMirroredOppositeHandle(TangentMode mode)
    {
        var curve = Geometry.FromLegacy([new(0, 0), new(50, 20), new(100, 0)], true);
        var knot = curve.Knots[1]; var before = knot.InHandle;
        var changed = Geometry.MoveHandle(curve, knot.Id, true, new(0, 30), mode).Knots[1];
        Assert.Equal(new Point2(0, 30), changed.OutHandle);
        if (mode == TangentMode.Independent) Assert.Equal(before, changed.InHandle);
        else
        {
            Assert.Equal(0, changed.InHandle.X);
            Assert.Equal(mode == TangentMode.Mirrored ? -30 : -before.Length, changed.InHandle.Y, 10);
        }
        Assert.Equal(before, knot.InHandle);
    }

    [Fact]
    public void DamagedCurveReferencesAndOutOfScopeWireOverridesAreStructuralErrors()
    {
        var (project, view, route, wire) = Fixture();
        var curve = Geometry.EditableRouteGeometry(view, route);
        curve.Segments[0].EndKnotId = "unknown";
        route.Geometry = curve;
        view.WirePresentations.Add(new() { ConductorId = "unknown", LineWidthMm = -1 });
        var issues = ProductionValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "route.curve" && i.IsStructural);
        Assert.Contains(issues, i => i.Code == "wirePresentation.conductor" && i.IsStructural);
        Assert.Contains(issues, i => i.Code == "wirePresentation.width" && i.IsStructural);
    }

    private static (HarnessProject Project, DrawingView View, PlacedRoute Route, Conductor Wire) Fixture()
    {
        var branch = new RouteBranch { Points = [new(0, 0), new(100, 0)], PhysicalLengthMm = 100 };
        var route = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points] };
        var wire = new Conductor { BranchIds = [branch.Id], LengthSource = LengthSource.Manual, ManualCutLengthMm = 120 };
        var view = new DrawingView { Mode = LayoutMode.Free, Routes = [route] };
        var project = new HarnessProject { Sheets = [new() { Views = [view] }], Assembly = new() { Branches = [branch], Conductors = [wire] } };
        return (project, view, route, wire);
    }
}
