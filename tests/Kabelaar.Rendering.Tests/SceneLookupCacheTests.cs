using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class SceneLookupCacheTests
{
    [Fact]
    public void TopologyEditsAndReplacementsMatchAnUncachedBuild()
    {
        var project = DemoFactory.Create();
        var assembly = project.Assembly;
        var view = project.Sheets[0].Views[0];
        AssertFresh(project);

        assembly.Conductors[0].BranchIds.Add(assembly.Branches[1].Id);
        AssertFresh(project);
        assembly.Conductors[0].BranchIds[0] = assembly.Branches[2].Id;
        AssertFresh(project);
        assembly.Conductors[0].BranchIds.Reverse();
        AssertFresh(project);
        assembly.Conductors[0] = Clone(assembly.Conductors[0]);
        assembly.Conductors[0].Color = "#135790";
        assembly.Conductors.Reverse();
        AssertFresh(project);

        // Same IDs, different objects: cached dictionary values must refer to the replacements.
        var housingIndex = project.Parts.FindIndex(p => p.Id == assembly.Connectors[0].PartId);
        project.Parts[housingIndex] = Clone(project.Parts[housingIndex]);
        project.Parts[housingIndex].Pins[0].Position += new Point2(5, 7);
        project.Parts[housingIndex].PartNumber = "REPLACED";
        assembly.Connectors[0] = Clone(assembly.Connectors[0]);
        assembly.Connectors[0].Name = "REPLACED-J";
        assembly.Connectors[0].Cavities[0].Label = "NEW-CAVITY";
        AssertFresh(project);

        var branch = assembly.Branches[0] = Clone(assembly.Branches[0]);
        view.Routes.First(r => r.BranchId == branch.Id).Points.Clear();
        branch.Points[1] += new Point2(17, 9); // Route falls back to the new domain branch.
        AssertFresh(project);
        var oldId = branch.Id;
        branch.Id = "renamed-branch";
        foreach (var route in view.Routes.Where(r => r.BranchId == oldId)) route.BranchId = branch.Id;
        foreach (var wire in assembly.Conductors)
            for (var i = 0; i < wire.BranchIds.Count; i++)
                if (wire.BranchIds[i] == oldId) wire.BranchIds[i] = branch.Id;
        AssertFresh(project);

        var added = Clone(assembly.Conductors[0]);
        added.Id = "new-wire"; added.Name = "ADDED"; assembly.Conductors.Add(added);
        AssertFresh(project);
        assembly.Conductors.RemoveAt(1);
        AssertFresh(project);
        assembly.Conductors.Clear(); assembly.TwistGroups.Clear();
        AssertFresh(project);
    }

    [Fact]
    public void MaterialCableAndTwistMembershipEditsMatchAnUncachedBuild()
    {
        var project = DemoFactory.Create();
        var assembly = project.Assembly;
        AssertFresh(project);
        var cable = new CableInstance { Name = "CABLE-1", PartId = project.Parts[0].Id };
        assembly.Cables.Add(cable);
        assembly.Conductors[0].CableId = cable.Id;
        AssertFresh(project);
        assembly.Cables[0] = Clone(cable);
        assembly.Cables[0].Name = "CABLE-REPLACED";
        AssertFresh(project);
        assembly.Cables[0].Id = "new-cable";
        assembly.Conductors[0].CableId = "new-cable";
        AssertFresh(project);

        var material = assembly.Materials[0];
        material.BranchId = assembly.Branches[0].Id;
        AssertFresh(project);
        material.BranchId = assembly.Branches[1].Id;
        AssertFresh(project);
        assembly.Materials[0] = Clone(material);
        assembly.Materials[0].BranchId = null;
        AssertFresh(project);

        var twist = assembly.TwistGroups[0];
        twist.ConductorIds[0] = assembly.Conductors[^1].Id;
        twist.ConductorIds.Reverse();
        AssertFresh(project);
        assembly.TwistGroups[0] = Clone(twist);
        assembly.TwistGroups[0].PitchMm = 14;
        AssertFresh(project);
        assembly.TwistGroups.Clear(); assembly.Cables.Clear(); assembly.Materials.Clear();
        AssertFresh(project);
    }

    [Fact]
    public void MultipleViewsAndPrintingDoNotReuseDifferentLayerResults()
    {
        var project = DemoFactory.Create();
        var sheet = project.Sheets[0];
        var detail = Clone(sheet.Views[0]);
        detail.Id = "detail"; detail.Origin = new(200, 30); detail.Scale = .5;
        detail.Routes[0].LayerId = "reference";
        sheet.Views.Add(detail);
        AssertFresh(project);
        _ = SceneBuilder.Build(project, sheet, draft: true, forPrint: true);
        detail.Components[0].Position += new Point2(40, 10);
        sheet.Layers[0].Locked = true;
        AssertFresh(project);
        sheet.Views.Reverse();
        AssertFresh(project);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static string[] Describe(DrawingScene scene) =>
        scene.Primitives.Select(p => JsonSerializer.Serialize(p, p.GetType())).ToArray();
    private static void AssertFresh(HarnessProject project)
    {
        var before = SceneBuilder.Build(project, project.Sheets[0]);
        var retained = Describe(before);
        var fresh = Clone(project);
        var expected = SceneBuilder.Build(fresh, fresh.Sheets[0]);
        Assert.Equal(Describe(expected), retained);
        Assert.Equal(retained, Describe(before));
    }
}
