using Kabelaar.Core;
using Kabelaar.Rendering;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class LabelLayoutInvalidationTests
{
    [Fact]
    public void RouteEditsAndNewObstaclesInvalidateGeometryAndCollisionDecisions()
    {
        var project = DemoFactory.Create();
        var sheet = project.Sheets[0];
        var view = sheet.Views[0];
        project.Assembly.TwistGroups.Clear();
        var wire = project.Assembly.Conductors[0];
        var route = view.Routes.Single(r => r.BranchId == wire.BranchIds[0]);
        SceneText Label() => SceneBuilder.Build(project, sheet).Primitives.OfType<SceneText>()
            .Single(t => t.EntityId == wire.Id && t.Category == "wire-label");

        var original = Label();
        var before = route.Points.ToArray();
        for (var i = 1; i < route.Points.Count - 1; i++) route.Points[i] += new Point2(0, 45);
        Assert.NotEqual(original.Position, Label().Position);
        route.Points = [.. before];
        Assert.Equal(original.Position, Label().Position);

        var obstacle = new Annotation
        {
            Kind = AnnotationKind.Text, Text = "Dit gebied blijft vrij voor montageaanwijzingen",
            Points = [(original.Position - view.Origin) * (1 / (view.Scale * sheet.PrintScale))],
            FontSizeMm = original.HeightMm / (view.Scale * sheet.PrintScale)
        };
        view.Annotations.Add(obstacle);
        var scene = SceneBuilder.Build(project, sheet);
        var moved = scene.Primitives.OfType<SceneText>().Single(t => t.EntityId == wire.Id && t.Category == "wire-label");
        var reserved = scene.Primitives.OfType<SceneText>().Single(t => t.EntityId == obstacle.Id);
        Assert.False(moved.Bounds.Intersects(reserved.Bounds));
        view.Annotations.Remove(obstacle);
        Assert.Equal(original.Position, Label().Position);
    }
}
