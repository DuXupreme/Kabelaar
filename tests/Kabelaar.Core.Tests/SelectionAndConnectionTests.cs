using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class SelectionAndConnectionTests
{
    [Fact]
    public void CaptureAndParallelProposalAreReadOnlyAndInsertionRemapsOnlyInternalConnections()
    {
        var (p, v, wire) = Fixture();
        var original = JsonSerializer.Serialize(p); var connectorId = wire.From.EntityId!;
        var block = SelectionOperations.Capture(p, v, [connectorId, wire.Id]);
        Assert.Equal(original, JsonSerializer.Serialize(p));
        Assert.Equal(EndpointKind.Cavity, block.Assembly.Conductors[0].From.Kind);
        Assert.Equal(EndpointKind.Free, block.Assembly.Conductors[0].To.Kind);
        var proposal = SelectionOperations.PrepareInsert(p, v, block, new(0, 20), 3, new(0, 20));
        Assert.Equal(original, JsonSerializer.Serialize(p));
        Assert.Equal(4, proposal.PreviewProject.Assembly.Conductors.Count);
        var history = new CommandHistory(); history.Execute(proposal.Command);
        Assert.Equal(4, p.Assembly.Conductors.Count); Assert.Equal(5, p.Assembly.Connectors.Count);
        Assert.Equal(2, p.Parts.Count);
        Assert.Same(v, p.Sheets[0].Views[0]);
        Assert.Equal(4, p.Assembly.Conductors.Select(w => w.Name).Distinct().Count());
        foreach (var copy in p.Assembly.Conductors.Skip(1))
        {
            Assert.NotEqual(connectorId, copy.From.EntityId); Assert.NotEqual(wire.From.CavityId, copy.From.CavityId);
            Assert.Equal(EndpointKind.Free, copy.To.Kind); Assert.Equal(100, HarnessReports.CutLength(p, copy));
        }
        Assert.Empty(StructuralValidator.Validate(p));
        var inserted = JsonSerializer.Serialize(p); history.Undo(); Assert.Equal(original, JsonSerializer.Serialize(p));
        history.Redo(); Assert.Equal(inserted, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void PreviewCanBeInspectedOrChangedWithoutChangingPreparedInsertion()
    {
        var (p, v, wire) = Fixture();
        var proposal = SelectionOperations.PrepareInsert(p, v, SelectionOperations.Capture(p, v, [wire.Id]), new(0, 20));
        proposal.PreviewProject.Assembly.Conductors.Clear();
        new CommandHistory().Execute(proposal.Command);
        Assert.Equal(2, p.Assembly.Conductors.Count);
    }

    [Fact]
    public void StaleProposalRejectsBeforeAnyMutation()
    {
        var (p, v, wire) = Fixture();
        var proposal = SelectionOperations.PrepareInsert(p, v, SelectionOperations.Capture(p, v, [wire.Id]), new(0, 20));
        wire.Name = "changed"; var before = JsonSerializer.Serialize(p); var history = new CommandHistory();
        Assert.Throws<InvalidOperationException>(() => history.Execute(proposal.Command));
        Assert.Equal(before, JsonSerializer.Serialize(p)); Assert.False(history.CanUndo);
    }

    [Fact]
    public void LayoutModeChangeAlsoInvalidatesPreparedProposal()
    {
        var (p, v, wire) = Fixture();
        var proposal = SelectionOperations.PrepareInsert(p, v, SelectionOperations.Capture(p, v, [wire.Id]), new(0, 20));
        v.Mode = LayoutMode.Scaled; var before = JsonSerializer.Serialize(p);
        Assert.Throws<InvalidOperationException>(() => proposal.Command.Execute());
        Assert.Equal(before, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void DrawOrderIncludesUnstyledWiresAndPreservesPriorOrdering()
    {
        var (p, v, first) = Fixture();
        var second = Clone(first); second.Id = Ids.New(); second.Name = "W2"; p.Assembly.Conductors.Add(second);
        var third = Clone(first); third.Id = Ids.New(); third.Name = "W3"; p.Assembly.Conductors.Add(third);
        var history = new CommandHistory(); history.Execute(SelectionOperations.SetDrawOrder(p, v, [second.Id], true));
        Assert.True(v.DrawOrder.IndexOf(first.Id) < v.DrawOrder.IndexOf(second.Id));
        history.Execute(SelectionOperations.SetDrawOrder(p, v, [third.Id], false));
        Assert.True(v.DrawOrder.IndexOf(third.Id) < v.DrawOrder.IndexOf(first.Id));
        Assert.True(v.DrawOrder.IndexOf(first.Id) < v.DrawOrder.IndexOf(second.Id));
        history.Undo(); Assert.Equal(second.Id, v.DrawOrder[^1]); history.Undo(); Assert.Empty(v.DrawOrder);
    }

    [Fact]
    public void CompleteCableAndTwistAreCopiedOnceButPartialGroupsAreDetachedAndReported()
    {
        var (p, v, first) = Fixture();
        var second = Clone(first); second.Id = Ids.New(); second.Name = "W2"; p.Assembly.Conductors.Add(second);
        foreach (var c in p.Assembly.Connectors) c.Cavities[0].MaxWires = 2;
        p.Parts[0].Pins[0].MaxWires = 2;
        var cablePart = new PartDefinition { Kind = PartKind.Cable, PartNumber = "C" }; p.Parts.Add(cablePart);
        var cable = new CableInstance { Name = "C1", PartId = cablePart.Id, CutLengthMm = 100 }; p.Assembly.Cables.Add(cable);
        first.CableId = second.CableId = cable.Id;
        p.Assembly.TwistGroups.Add(new() { ConductorIds = [first.Id, second.Id] });
        var partial = SelectionOperations.Capture(p, v, [first.Id]);
        Assert.Empty(partial.Assembly.Cables); Assert.Empty(partial.Assembly.TwistGroups); Assert.Null(partial.Assembly.Conductors[0].CableId);
        Assert.Contains("onvolledige kabelgroep", partial.Notes);
        var block = SelectionOperations.Capture(p, v, [first.Id, second.Id]);
        var proposal = SelectionOperations.PrepareInsert(p, v, block, new(0, 10)); proposal.Command.Execute();
        Assert.Equal(2, p.Assembly.Cables.Count); Assert.Equal(2, p.Assembly.TwistGroups.Count);
        Assert.Equal(4, p.Assembly.Conductors.Count); Assert.Equal(2, p.Assembly.Branches.Count);
        Assert.Equal(p.Assembly.Conductors[2].CableId, p.Assembly.Conductors[3].CableId);
        Assert.NotEqual(cable.Id, p.Assembly.Conductors[2].CableId);
    }

    [Fact]
    public void MirroringAndRotationPreservePhysicalDataAndRestoreExactState()
    {
        var (p, v, wire) = Fixture(); var before = JsonSerializer.Serialize(p);
        var physical = JsonSerializer.Serialize(p.Assembly); var history = new CommandHistory();
        var ids = p.Assembly.Connectors.Select(c => c.Id).Append(wire.Id).ToArray();
        history.Execute(SelectionOperations.TransformSelection(p, v, ids, new(50, 0), 90, mirrorX: true));
        Assert.Equal(physical, JsonSerializer.Serialize(p.Assembly));
        Assert.All(v.Components, c => Assert.True(c.MirrorX));
        var changedWire = p.Assembly.Conductors[0];
        Assert.Equal(Geometry.EndpointPosition(p, v, changedWire.From), v.Routes[0].Points[0]);
        Assert.Equal(Geometry.EndpointPosition(p, v, changedWire.To), v.Routes[0].Points[^1]);
        history.Undo(); Assert.Equal(before, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void TransformingOneSharedWireCreatesIndividualFreeGeometry()
    {
        var (p, v, wire) = Fixture();
        wire.From = new(); wire.To = new();
        var second = Clone(wire); second.Id = Ids.New(); second.Name = "W2"; p.Assembly.Conductors.Add(second);
        var route = JsonSerializer.Serialize(v.Routes); var assembly = JsonSerializer.Serialize(p.Assembly);
        SelectionOperations.TransformSelection(p, v, [wire.Id], new(0, 0), 90).Execute();
        Assert.Equal(route, JsonSerializer.Serialize(v.Routes)); Assert.Equal(assembly, JsonSerializer.Serialize(p.Assembly));
        Assert.Single(v.WirePresentations); Assert.Equal(wire.Id, v.WirePresentations[0].ConductorId);
        Assert.Equal(new Point2(0, 100).X, v.WirePresentations[0].Routes[0].Geometry.Knots[^1].Position.X, 8);
    }

    [Fact]
    public void ReferencePointConnectsAndReportsWithoutAddingMaterial()
    {
        var (p, v, wire) = Fixture(); var history = new CommandHistory();
        var point = new PhysicalReferencePoint { Name = "RP1", MaxWires = 1 };
        history.Execute(HarnessOperations.AddReferencePoint(p, v, point, new(120, 0)));
        var before = JsonSerializer.Serialize(p); var bom = HarnessReports.Bom(p).Select(r => (r.PartId, r.Quantity)).ToArray();
        var proposal = ConnectionOperations.PrepareConnect(p, v, wire, false, new() { Kind = EndpointKind.ReferencePoint, EntityId = point.Id });
        Assert.Equal(before, JsonSerializer.Serialize(p)); history.Execute(proposal.Command);
        Assert.Equal("RP1", HarnessReports.Wires(p)[0].To); Assert.Equal(bom, HarnessReports.Bom(p).Select(r => (r.PartId, r.Quantity)));
        Assert.Equal(new Point2(120, 0), v.Routes[0].Points[^1]); Assert.Equal(100, HarnessReports.CutLength(p, p.Assembly.Conductors[0]));
        history.Undo(); Assert.Equal(before, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void ConnectionProposalRejectsAnOverfullPinWithoutChangingAnything()
    {
        var (p, v, wire) = Fixture();
        var second = Clone(wire); second.Id = Ids.New(); second.Name = "W2"; second.From = new(); second.To = new(); p.Assembly.Conductors.Add(second);
        var before = JsonSerializer.Serialize(p);
        Assert.Throws<InvalidOperationException>(() => ConnectionOperations.PrepareConnect(p, v, second, true, wire.From));
        Assert.Equal(before, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void ExistingCapacityErrorCannotBeWorsenedByAProposal()
    {
        var (p, v, wire) = Fixture();
        var second = Clone(wire); second.Id = Ids.New(); second.Name = "W2"; p.Assembly.Conductors.Add(second);
        var third = Clone(wire); third.Id = Ids.New(); third.Name = "W3"; third.From = new(); third.To = new(); p.Assembly.Conductors.Add(third);
        var before = JsonSerializer.Serialize(p);
        Assert.Throws<InvalidOperationException>(() => ConnectionOperations.PrepareConnect(p, v, third, true, wire.From));
        Assert.Equal(before, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void GroundReferenceAndMirroredPinsKeepPhysicalEndpointGeometryConsistent()
    {
        var (p, v, wire) = Fixture();
        p.Parts[0].Pins[0].Position = new(5, 2);
        v.Routes[0].Points[0] = new(5, 2); v.Routes[0].Points[1] = new(105, 2);
        SelectionOperations.TransformSelection(p, v, p.Assembly.Connectors.Select(c => c.Id).Append(wire.Id), new(), mirrorX: true).Execute();
        Assert.Equal(new Point2(-5, 2), Geometry.EndpointPosition(p, v, p.Assembly.Conductors[0].From));
        Assert.Equal(new Point2(-5, 2), v.Routes[0].Points[0]);
        var ground = new PhysicalReferencePoint { Name = "GND", Symbol = NodeSymbol.Ground };
        HarnessOperations.AddReferencePoint(p, v, ground, new(-120, 2)).Execute();
        ConnectionOperations.PrepareConnect(p, v, p.Assembly.Conductors[0], false,
            new() { Kind = EndpointKind.ReferencePoint, EntityId = ground.Id }).Command.Execute();
        Assert.Equal("GND", HarnessReports.EndpointLabel(p, p.Assembly.Conductors[0].To));
        Assert.Single(p.Assembly.ReferencePoints); Assert.Empty(StructuralValidator.Validate(p));
    }

    [Fact]
    public void PhysicalJunctionDoesNotDuplicateManualCutLengthAndUndoesAsOneEdit()
    {
        var (p, v, wire) = Fixture(); wire.LengthSource = LengthSource.Manual; wire.ManualCutLengthMm = 150;
        var before = JsonSerializer.Serialize(p);
        var proposal = ConnectionOperations.PrepareJunction(p, v, wire, v.Routes[0], .4, "SP1");
        Assert.Equal(before, JsonSerializer.Serialize(p)); var history = new CommandHistory(); history.Execute(proposal.Command);
        Assert.Equal(2, p.Assembly.Conductors.Count); Assert.Single(p.Assembly.Splices);
        Assert.All(p.Assembly.Conductors, w => { Assert.True(w.LengthNeedsConfirmation); Assert.Equal(0, w.ManualCutLengthMm); });
        Assert.All(p.Assembly.Branches, b => Assert.Equal(0, b.PhysicalLengthMm));
        Assert.Equal(2, ProductionValidator.Validate(p).Count(i => i.Code == "wire.lengthUnconfirmed"));
        Assert.Empty(StructuralValidator.Validate(p)); var after = JsonSerializer.Serialize(p);
        history.Undo(); Assert.Equal(before, JsonSerializer.Serialize(p)); history.Redo(); Assert.Equal(after, JsonSerializer.Serialize(p));
    }

    [Fact]
    public void JunctionOnSharedBranchPreservesOtherWireAndMeasuredBranch()
    {
        var (p, v, wire) = Fixture();
        var other = Clone(wire); other.Id = Ids.New(); other.Name = "W2"; p.Assembly.Conductors.Add(other);
        p.Parts[0].Pins[0].MaxWires = 2; foreach (var c in p.Assembly.Connectors) c.Cavities[0].MaxWires = 2;
        var originalOther = JsonSerializer.Serialize(other); var branch = JsonSerializer.Serialize(p.Assembly.Branches[0]);
        var proposal = ConnectionOperations.PrepareJunction(p, v, wire, v.Routes[0], .4, "SP1", firstPhysicalLengthMm: 40); proposal.Command.Execute();
        Assert.Equal(originalOther, JsonSerializer.Serialize(p.Assembly.Conductors.Single(w => w.Id == other.Id)));
        Assert.Equal(branch, JsonSerializer.Serialize(p.Assembly.Branches.Single(b => b.Id == other.BranchIds[0])));
        Assert.Equal(3, p.Assembly.Branches.Count); Assert.Equal(3, p.Assembly.Conductors.Count);
        Assert.Equal(100, p.Assembly.Branches.Where(b => b.Id != other.BranchIds[0]).Sum(b => b.PhysicalLengthMm));
    }

    [Fact]
    public void GraphicalNodesNeverCreatePhysicalConnections()
    {
        var (p, v, _) = Fixture(); var assembly = JsonSerializer.Serialize(p.Assembly);
        v.Annotations.Add(new() { Kind = AnnotationKind.GraphicalNode, Points = [new(50, 0)], WidthMm = 2 });
        Assert.Equal(assembly, JsonSerializer.Serialize(p.Assembly)); Assert.Empty(StructuralValidator.Validate(p));
    }

    [Fact]
    public void MetadataAndReferenceNamesInvalidateReportCache()
    {
        var (p, _, wire) = Fixture(); var point = new PhysicalReferencePoint { Name = "RP1" }; p.Assembly.ReferencePoints.Add(point);
        wire.To = new() { Kind = EndpointKind.ReferencePoint, EntityId = point.Id };
        var cache = new HarnessReportCache(); _ = cache.Get(p);
        point.Name = "RP2"; wire.NetName = "NET"; wire.Shielding = "screen"; wire.From.Notes = "crimp";
        var row = cache.Get(p).Wires[0]; Assert.Equal("RP2", row.To); Assert.Equal("NET", row.NetName);
        Assert.Equal("screen", row.Shielding); Assert.Equal("crimp", row.FromNote);
        Assert.Equal(HarnessReports.Wires(p), cache.Get(p).Wires);
    }

    [Fact]
    public void ExtendedValidationRejectsMalformedTableAndMissingReference()
    {
        var (p, v, wire) = Fixture();
        wire.From = new() { Kind = EndpointKind.ReferencePoint, EntityId = "missing" };
        v.Annotations.Add(new() { Kind = AnnotationKind.FreeTable, Points = [new()], Columns = ["A", "B"], Rows = [new() { Cells = ["one"] }] });
        var errors = ProductionValidator.Validate(p); Assert.Contains(errors, e => e.Code == "endpoint.referencePoint" && e.IsStructural);
        Assert.Contains(errors, e => e.Code == "annotation.tableShape" && e.IsStructural);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static (HarnessProject Project, DrawingView View, Conductor Wire) Fixture()
    {
        var p = new HarnessProject { DrawingNumber = "SYNTHETIC" };
        var part = new PartDefinition { PartNumber = "H", RequiresTerminal = false, Pins = [new() { Position = new(0, 0), Function = "signal" }] };
        var wirePart = new PartDefinition { PartNumber = "W", Kind = PartKind.Wire }; p.Parts.AddRange([part, wirePart]);
        var a = HarnessOperations.NewConnector(part, "J1"); var b = HarnessOperations.NewConnector(part, "J2");
        a.Cavities[0].State = b.Cavities[0].State = CavityState.Occupied; p.Assembly.Connectors.AddRange([a, b]);
        var v = p.Sheets[0].Views[0]; v.Mode = LayoutMode.Free;
        v.Components.AddRange([new() { EntityId = a.Id }, new() { EntityId = b.Id, Position = new(100, 0) }]);
        var branch = new RouteBranch { Points = [new(0, 0), new(100, 0)], PhysicalLengthMm = 100 }; p.Assembly.Branches.Add(branch);
        v.Routes.Add(new() { BranchId = branch.Id, Points = [.. branch.Points] });
        var wire = new Conductor { Name = "W1", WirePartId = wirePart.Id, BranchIds = [branch.Id],
            From = new() { Kind = EndpointKind.Cavity, EntityId = a.Id, CavityId = a.Cavities[0].Id },
            To = new() { Kind = EndpointKind.Cavity, EntityId = b.Id, CavityId = b.Cavities[0].Id } };
        p.Assembly.Conductors.Add(wire); return (p, v, wire);
    }
}
