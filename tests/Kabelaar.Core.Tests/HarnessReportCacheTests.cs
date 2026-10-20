using System.Diagnostics;
using Kabelaar.Core;
using Xunit;
using Xunit.Abstractions;

namespace Kabelaar.Core.Tests;

public sealed class HarnessReportCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void FailedRefreshRemainsInvalidOnRetryAndRecoversAfterCorrectedInput()
    {
        var project = DemoFactory.Create();
        var cache = new HarnessReportCache();
        var initial = cache.Get(project);
        var duplicate = project.Assembly.Conductors[1];
        var originalId = duplicate.Id;
        duplicate.Id = project.Assembly.Conductors[0].Id;
        Assert.Throws<ArgumentException>(() => cache.Get(project));
        Assert.Throws<ArgumentException>(() => cache.Get(project));
        duplicate.Id = originalId;
        project.Assembly.Conductors[0].StartAllowanceMm += 12.125;
        var recovered = cache.Get(project);
        AssertFresh(project, recovered);
        Assert.Equal(initial.Wires[0].CutLengthMm + 12.125, recovered.Wires[0].CutLengthMm, 8);
        Assert.Same(recovered, cache.Get(project));
    }

    [Fact]
    public void DrawingOnlyEditsReuseReportsButPhysicalLengthAndUndoStayCurrent()
    {
        var project = DemoFactory.Create();
        var cache = new HarnessReportCache();
        var first = cache.Get(project);
        var view = project.Sheets[0].Views[0];
        view.Components[0].Position += new Point2(100, 20);
        view.Routes[0].Points[1] += new Point2(30, 15);
        view.Scale = 2;
        view.Annotations[0].Text = "Gewijzigde montageweergave";
        Assert.Same(first, cache.Get(project));
        var branch = project.Assembly.Branches[0];
        var beforeLength = branch.PhysicalLengthMm;
        branch.PhysicalLengthMm += 37.5;
        var changed = cache.Get(project);
        Assert.NotSame(first, changed);
        AssertFresh(project, changed);
        Assert.Equal(first.Wires[0].CutLengthMm + 37.5, changed.Wires[0].CutLengthMm, 8);
        Assert.Same(first.Wires[1], changed.Wires[1]);
        Assert.Same(first.Bom.First(r => r.Kind == nameof(PartKind.Housing)), changed.Bom.First(r => r.Kind == nameof(PartKind.Housing)));
        branch.PhysicalLengthMm = beforeLength;
        var undone = cache.Get(project);
        AssertFresh(project, undone);
        Assert.Equal(first.Wires, undone.Wires);
        Assert.Equal(first.Bom, undone.Bom);
    }

    [Fact]
    public void EveryReportInputInvalidatesAfterDirectMutationIncludingSupplierAndNestedLists()
    {
        Action<HarnessProject>[] mutations =
        [
            p => p.Assembly.Conductors[0].Name = "W-last",
            p => p.Assembly.Conductors[0].From.Description = "Vrij nieuw uiteinde",
            p => p.Assembly.Conductors[0].From = new() { Description = "Knipzijde" },
            p => p.Assembly.Conductors[0].ColorCode = "YE",
            p => p.Assembly.Conductors[0].CrossSectionMm2 = 2.5,
            p => p.Assembly.Conductors[0].Signal = "Nieuwe functie",
            p => p.Assembly.Conductors[0].StartAllowanceMm += 7.125,
            p => { p.Assembly.Conductors[0].LengthSource = LengthSource.Manual; p.Assembly.Conductors[0].ManualCutLengthMm = 712.375; },
            p => p.Assembly.Conductors[0].BranchIds.Add(p.Assembly.Branches[1].Id),
            p => p.Assembly.Conductors.RemoveAt(0),
            p => p.Assembly.Connectors[0].Name = "J100",
            p => p.Assembly.Connectors[0].IncludedInAssembly = false,
            p => p.Assembly.Connectors[0].Cavities[0].Label = "A1",
            p => p.Assembly.Connectors[0].Cavities[0].TerminalPartId = null,
            p => p.Assembly.Connectors[0].Cavities[0].SealPartId = null,
            p => p.Assembly.Connectors[0].Cavities[^1].PlugPartId = null,
            p => p.Assembly.Connectors[0].Accessories.Add(new() { PartId = p.Parts[0].Id, Quantity = 2 }),
            p => p.Assembly.Splices.Add(new() { Name = "SP1", PartId = p.Parts[0].Id }),
            p => { var cable = new CableInstance { PartId = p.Parts[0].Id, Name = "C1", CutLengthMm = 500 }; p.Assembly.Cables.Add(cable); p.Assembly.Conductors[0].CableId = cable.Id; },
            p => p.Assembly.Materials[0].Quantity = .75,
            p => p.Assembly.Materials[0].Notes = "Nieuwe materiaalreferentie",
            p => p.Parts[0].PartNumber = "NIEUW-ARTIKEL",
            p => p.Parts[0].Name = "Nieuwe omschrijving",
            p => p.Parts[0].Supplier = "Andere leverancier",
            p => p.Parts.RemoveAt(0)
        ];
        foreach (var mutate in mutations)
        {
            var project = DemoFactory.Create();
            var cache = new HarnessReportCache();
            var before = cache.Get(project);
            mutate(project);
            var after = cache.Get(project);
            Assert.NotSame(before, after);
            AssertFresh(project, after);
        }
    }

    [Fact]
    public void ManualCutLengthKeepsBomStableWhenTheDisplayedRouteLengthChanges()
    {
        var project = DemoFactory.Create();
        var wire = project.Assembly.Conductors[0];
        wire.LengthSource = LengthSource.Manual; wire.ManualCutLengthMm = 500;
        var cache = new HarnessReportCache();
        var before = cache.Get(project);
        project.Assembly.Branches[0].PhysicalLengthMm += 100;
        var after = cache.Get(project);
        Assert.Same(before.Bom, after.Bom);
        AssertFresh(project, after);
    }

    [Fact]
    public void LengthRefreshKeepsTheFirstDuplicateBranchUntilIdentityValidationIsResolved()
    {
        var project = DemoFactory.Create();
        var firstBranch = project.Assembly.Branches[0];
        var duplicate = new RouteBranch { Id = firstBranch.Id, PhysicalLengthMm = 9900 };
        project.Assembly.Branches.Add(duplicate);
        var cache = new HarnessReportCache();
        var before = cache.Get(project);
        duplicate.PhysicalLengthMm += 500;
        Assert.Same(before, cache.Get(project));
        firstBranch.PhysicalLengthMm += 37.25;
        var changed = cache.Get(project);
        AssertFresh(project, changed);
        Assert.Equal(before.Wires[0].RouteLengthMm + 37.25, changed.Wires[0].RouteLengthMm, 8);
        project.Assembly.Branches.Reverse();
        AssertFresh(project, cache.Get(project));
    }

    [Fact]
    public void Actual1000WireScaledEditsPreserveExactReportsAndReuseUnaffectedRows()
    {
        var project = DemoFactory.CreateAcceptancePerformance();
        var view = project.Sheets[0].Views[0];
        var cache = new HarnessReportCache();
        var first = cache.Get(project);
        var elapsed = new List<double>();
        var allocations = new List<long>();
        var history = new CommandHistory();
        for (var i = 0; i < 12; i++)
        {
            var before = cache.Get(project);
            history.Execute(HarnessOperations.MoveComponent(project, view, view.Components[0],
                view.Components[0].Position + new Point2(i % 2 == 0 ? 1 : -1, 0)));
            var watch = Stopwatch.StartNew();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var current = cache.Get(project);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            watch.Stop(); elapsed.Add(watch.Elapsed.TotalMilliseconds);
            AssertFresh(project, current);
            Assert.Same(first.Wires[999], current.Wires[999]);
            history.Undo();
            var undone = cache.Get(project);
            AssertFresh(project, undone);
            Assert.Equal(before.Wires, undone.Wires);
            Assert.Equal(before.Bom, undone.Bom);
            history.Redo();
            var redone = cache.Get(project);
            AssertFresh(project, redone);
            Assert.Equal(current.Wires, redone.Wires);
            Assert.Equal(current.Bom, redone.Bom);
        }
        elapsed.Sort();
        output.WriteLine($"100 connectors / 1,000 wires, exact scaled edits: cached report median {elapsed[elapsed.Count / 2]:F2} ms.");
        output.WriteLine($"Managed allocation per length refresh: {allocations.Max()} bytes maximum.");
        Assert.All(allocations, bytes => Assert.True(bytes < 128_000,
            $"A length-only refresh allocated {bytes} bytes for 1,000 wires; expected less than 128 KB."));
    }

    private static void AssertFresh(HarnessProject project, HarnessReportSnapshot actual)
    {
        Assert.Equal(HarnessReports.Wires(project), actual.Wires);
        Assert.Equal(HarnessReports.Bom(project), actual.Bom);
        foreach (var part in project.Parts) Assert.Equal(part.Supplier, actual.Suppliers[part.Id]);
    }
}
