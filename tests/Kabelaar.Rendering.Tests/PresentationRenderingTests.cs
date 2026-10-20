using Kabelaar.Core;
using Kabelaar.Rendering;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class PresentationRenderingTests
{
    [Fact]
    public void CompactOverviewKeepsRealEndpointsSelectableWithScaledRotatedSymbols()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        view.Mode = LayoutMode.Free;
        var wire = project.Assembly.Conductors[0];
        var lengths = project.Assembly.Branches.Select(b => b.PhysicalLengthMm).ToArray();
        var cuts = HarnessReports.Wires(project).Select(w => w.CutLengthMm).ToArray();
        foreach (var placement in view.Components)
            HarnessOperations.SetComponentPresentation(project, view, placement, .6, false, false, true, false, 2.5,
                placement.Position, 90).Execute();
        view.Style.ShowWireLabels = view.Style.ShowTwistLabels = false;
        view.Style.ConnectorLineWidthMm = .18;
        var scene = SceneBuilder.Build(project, sheet);
        Assert.DoesNotContain(scene.Primitives, p => p.Category is "pin-label" or "pin" or "wire-label" or "part-number" or "twist-label");
        Assert.Contains(scene.Primitives, p => p.Category == "connector-label");
        var path = scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == wire.Id && p.Category == "twisted-wire");
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value * view.Scale, path.Points[0]);
        Assert.Equal(view.Origin + Geometry.EndpointPosition(project, view, wire.To)!.Value * view.Scale, path.Points[^1]);
        Assert.Contains(scene.Index.HitTest(path.Points[path.Points.Count / 2], .1), p => p.EntityId == wire.Id);
        Assert.Equal(lengths, project.Assembly.Branches.Select(b => b.PhysicalLengthMm));
        Assert.Equal(cuts, HarnessReports.Wires(project).Select(w => w.CutLengthMm));
    }

    [Theory]
    [InlineData(TwistDisplayMode.Full)]
    [InlineData(TwistDisplayMode.Markers)]
    [InlineData(TwistDisplayMode.Parallel)]
    public void OppositeElectricalDirectionsDoNotCollapseTwistedBundle(TwistDisplayMode mode)
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var twist = project.Assembly.TwistGroups[0];
        var second = project.Assembly.Conductors.Single(w => w.Id == twist.ConductorIds[1]);
        (second.From, second.To) = (second.To, second.From);
        foreach (var branch in second.BranchIds)
            view.Routes.First(r => r.BranchId == branch).Points.Reverse();
        view.Style.TwistDisplayMode = mode;
        view.Style.TwistDisplayPitchMm = 6;
        view.Style.TwistAmplitudeMm = .5;
        var cut = HarnessReports.CutLength(project, second);
        var physicalPitch = twist.PitchMm;
        var scene = SceneBuilder.Build(project, sheet);
        var line = scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == second.Id && p.Category == "twisted-wire");
        var start = view.Origin + Geometry.EndpointPosition(project, view, second.From)!.Value;
        var end = view.Origin + Geometry.EndpointPosition(project, view, second.To)!.Value;
        Assert.Equal(start, line.Points[0]);
        Assert.Equal(end, line.Points[^1]);
        Assert.True(line.Bounds.Width > Math.Abs(end.X - start.X) * .9);
        Assert.Equal(physicalPitch, twist.PitchMm);
        Assert.Equal(cut, HarnessReports.CutLength(project, second));
        twist.PitchMm = physicalPitch * 2;
        var changed = SceneBuilder.Build(project, sheet).Primitives.OfType<ScenePath>()
            .Single(p => p.EntityId == second.Id && p.Category == "twisted-wire");
        Assert.Equal(line.Points, changed.Points); // A display pitch override never rewrites physical pitch.
    }

    [Fact]
    public void WireLabelsMoveAwayFromPinsAndDoNotOverlapEachOther()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        view.Style.ShowWireLabels = true;
        var first = SceneBuilder.Build(project, sheet);
        var labels = first.Primitives.OfType<SceneText>().Where(p => p.Category == "wire-label").ToArray();
        Assert.NotEmpty(labels);
        for (var i = 0; i < labels.Length; i++)
            for (var j = i + 1; j < labels.Length; j++)
                Assert.False(labels[i].Bounds.Intersects(labels[j].Bounds));
        foreach (var label in labels)
        {
            var wire = project.Assembly.Conductors.Single(w => w.Id == label.EntityId);
            var start = view.Origin + Geometry.EndpointPosition(project, view, wire.From)!.Value;
            Assert.True(label.Position.DistanceTo(start) > 10);
        }
        var rebuilt = SceneBuilder.Build(project, sheet).Primitives.OfType<SceneText>()
            .Where(p => p.Category == "wire-label").Select(p => p.Position);
        Assert.Equal(labels.Select(p => p.Position), rebuilt);
    }

    [Fact]
    public void PerViewStylesInvalidateCachedPrimitivesAndPowerWiresGetHeavierStrokes()
    {
        var project = DemoFactory.Create(); var sheet = project.Sheets[0]; var view = sheet.Views[0];
        var wire = project.Assembly.Conductors[0];
        wire.CrossSectionMm2 = 4;
        var first = SceneBuilder.Build(project, sheet);
        view.Style.ShowWireLabels = false;
        view.Style.UseWireSectionWidth = true;
        view.Style.WireLineWidthMm = .25;
        view.Style.TwistDisplayMode = TwistDisplayMode.Parallel;
        var changed = SceneBuilder.Build(project, sheet);
        Assert.DoesNotContain(changed.Primitives, p => p.Category == "wire-label");
        Assert.Contains(first.Primitives, p => p.Category == "wire-label");
        Assert.Equal(.5, changed.Primitives.OfType<ScenePath>()
            .Single(p => p.EntityId == wire.Id && p.Category == "twisted-wire").StrokeWidth);
    }

    [Fact]
    public void ConnectorFilteredWireTableUsesIdsAndOrientsBothWireDirectionsWithoutEditingModel()
    {
        var project = DemoFactory.Create();
        var selected = project.Assembly.Connectors[0];
        var wire = project.Assembly.Conductors[0];
        (wire.From, wire.To) = (wire.To, wire.From);
        selected.Name = "New:label";
        var before = wire.From.EntityId;
        var rows = SceneBuilder.TableData(project, AnnotationKind.WireTable, selected.Id).Rows;
        Assert.Equal(project.Assembly.Conductors.Count(w => w.From.EntityId == selected.Id || w.To.EntityId == selected.Id), rows.Count);
        Assert.All(rows, row => Assert.StartsWith("New:label:", row["From"]));
        Assert.Equal(before, wire.From.EntityId);
        Assert.Empty(SceneBuilder.TableData(project, AnnotationKind.WireTable, "missing-connector").Rows);
        Assert.Equal(project.Assembly.Conductors.Count, SceneBuilder.TableData(project, AnnotationKind.WireTable).Rows.Count);
        project.Assembly.Connectors[1].Name = selected.Name;
        Assert.Equal(selected.Cavities.Count, SceneBuilder.TableData(project, AnnotationKind.PinTable, selected.Id).Rows.Count);
        Assert.Empty(SceneBuilder.TableData(project, AnnotationKind.PinTable, "missing-connector").Rows);
    }
}
