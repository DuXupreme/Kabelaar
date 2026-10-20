using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Core.Tests;

public sealed class EndpointBatchTests
{
    [Fact]
    public void DetachingBothEndsOfBothWiresUsesFinalSharedCavityOccupancyAndUndoesExactly()
    {
        var (project, first, second, from, to) = Fixture();
        var before = JsonSerializer.Serialize(project);
        var originalRoutes = JsonSerializer.Serialize(project.Assembly.Branches);
        var cutLengths = project.Assembly.Conductors.Select(w => HarnessReports.CutLength(project, w)).ToArray();
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetConductorEndpointsBatch(project,
            new Dictionary<Conductor, (Endpoint From, Endpoint To)>
            {
                [first] = (new(), new()),
                [second] = (new(), new())
            }));
        Assert.All(project.Assembly.Conductors, w =>
        {
            Assert.Equal(EndpointKind.Free, w.From.Kind); Assert.Equal(EndpointKind.Free, w.To.Kind);
            Assert.Null(w.From.EntityId); Assert.Null(w.To.CavityId);
        });
        Assert.Equal(CavityState.Unused, from.Cavities[0].State);
        Assert.Equal(CavityState.Unused, to.Cavities[0].State);
        Assert.Equal(originalRoutes, JsonSerializer.Serialize(project.Assembly.Branches));
        Assert.Equal(cutLengths, project.Assembly.Conductors.Select(w => HarnessReports.CutLength(project, w)));
        Assert.Equal(2, project.Assembly.Conductors.Count);
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code is "cavity.emptyOccupied" or "cavity.state");
        var after = JsonSerializer.Serialize(project);
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.False(history.IsDirty);
        history.Redo();
        Assert.Equal(after, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void CavityRemainsOccupiedWhenAnotherPlannedOrUnchangedEndpointStillUsesIt()
    {
        var (project, first, second, from, to) = Fixture();
        new CommandHistory().Execute(HarnessOperations.SetConductorEndpointsBatch(project,
            new Dictionary<Conductor, (Endpoint From, Endpoint To)>
            {
                [first] = (new(), first.To),
                [second] = (second.From, new())
            }));
        Assert.Equal(CavityState.Occupied, from.Cavities[0].State);
        Assert.Equal(CavityState.Occupied, to.Cavities[0].State);
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code is "cavity.emptyOccupied" or "cavity.state");
    }

    [Fact]
    public void ReassigningBothWiresMovesOccupancyToTheNewPinAndCapturesInputValues()
    {
        var (project, first, second, from, to) = Fixture();
        var next = new Endpoint { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = from.Cavities[1].Id };
        var history = new CommandHistory();
        var before = JsonSerializer.Serialize(project);
        var command = HarnessOperations.SetConductorEndpointsBatch(project,
            new Dictionary<Conductor, (Endpoint From, Endpoint To)>
            {
                [first] = (next, first.To),
                [second] = (next, second.To)
            });
        next.CavityId = "changed-after-command-construction";
        history.Execute(command);
        Assert.Equal(CavityState.Unused, from.Cavities[0].State);
        Assert.Equal(CavityState.Occupied, from.Cavities[1].State);
        Assert.Equal(CavityState.Occupied, to.Cavities[0].State);
        Assert.Equal(from.Cavities[1].Id, first.From.CavityId);
        Assert.Equal(from.Cavities[1].Id, second.From.CavityId);
        Assert.DoesNotContain(ProductionValidator.Validate(project), i => i.Code is "cavity.emptyOccupied" or "cavity.state");
        history.Undo();
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void InvalidSecondReplacementRejectsTheCompleteBatchBeforeChangingTheFirst()
    {
        var (project, first, second, _, _) = Fixture();
        var before = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        Assert.Throws<InvalidOperationException>(() => history.Execute(HarnessOperations.SetConductorEndpointsBatch(project,
            new Dictionary<Conductor, (Endpoint From, Endpoint To)>
            {
                [first] = (new(), new()),
                [second] = (new() { Kind = EndpointKind.Cavity, EntityId = "unknown", CavityId = "unknown" }, new())
            })));
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void ConflictingSharedScaledRouteRejectsBatchInsteadOfLettingTheLastReplacementWin()
    {
        var (project, first, second, from, _) = Fixture();
        project.Sheets[0].Views[0].Mode = LayoutMode.Scaled;
        var before = JsonSerializer.Serialize(project);
        var history = new CommandHistory();
        Assert.Throws<InvalidOperationException>(() => history.Execute(HarnessOperations.SetConductorEndpointsBatch(project,
            new Dictionary<Conductor, (Endpoint From, Endpoint To)>
            {
                [first] = (new(), first.To),
                [second] = (new() { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = from.Cavities[1].Id }, second.To)
            })));
        Assert.Equal(before, JsonSerializer.Serialize(project));
        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);
    }

    private static (HarnessProject Project, Conductor First, Conductor Second, ConnectorInstance From, ConnectorInstance To) Fixture()
    {
        var part = new PartDefinition
        {
            PartNumber = "SYNTHETIC-H", RequiresTerminal = false,
            Pins = [new() { Label = "1", Position = new(0, 0), MaxWires = 2 }, new() { Label = "2", Position = new(0, 10), MaxWires = 2 }]
        };
        var from = HarnessOperations.NewConnector(part, "J1"); var to = HarnessOperations.NewConnector(part, "J2");
        from.Cavities[0].State = to.Cavities[0].State = CavityState.Occupied;
        from.Cavities[1].State = to.Cavities[1].State = CavityState.Unused;
        var branch = new RouteBranch { Points = [new(0, 0), new(100, 0)], PhysicalLengthMm = 100 };
        Conductor Wire(string name) => new()
        {
            Name = name, BranchIds = [branch.Id], LengthSource = LengthSource.Manual, ManualCutLengthMm = 120,
            From = new() { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = from.Cavities[0].Id },
            To = new() { Kind = EndpointKind.Cavity, EntityId = to.Id, CavityId = to.Cavities[0].Id }
        };
        var first = Wire("W1"); var second = Wire("W2");
        var view = new DrawingView
        {
            Mode = LayoutMode.Free,
            Components = [new() { EntityId = from.Id, Position = new(0, 0) }, new() { EntityId = to.Id, Position = new(100, 0) }],
            Routes = [new() { BranchId = branch.Id, Points = [.. branch.Points] }]
        };
        var project = new HarnessProject
        {
            Parts = [part], Sheets = [new() { Views = [view] }],
            Assembly = new() { Branches = [branch], Connectors = [from, to], Conductors = [first, second] }
        };
        return (project, first, second, from, to);
    }
}
