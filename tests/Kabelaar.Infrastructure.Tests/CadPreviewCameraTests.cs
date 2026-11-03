using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class CadPreviewCameraTests
{
    [Fact]
    public void OrbitPanZoomAndFitAreIndependentFromMeshAndProductionProjection()
    {
        var result = new CadImportResult { Mesh = new() { Vertices = [new(-20, -10, -5), new(20, 10, 5)] },
            Views = [new() { Name = "Front", WidthMm = 40, HeightMm = 20 }] };
        var part = new PartDefinition { Projection = "Front", WidthMm = 40, HeightMm = 20 };
        var before = JsonSerializer.Serialize(new { result, part });
        var camera = new CadPreviewCamera(result.Mesh.Vertices); var start = camera.Pose;
        camera.Orbit(43, 22); Assert.NotEqual(start.Position, camera.Pose.Position); Assert.Equal(start.Target, camera.Pose.Target);
        var rotated = camera.Pose; camera.Pan(50, -30, 500); Assert.NotEqual(rotated.Target, camera.Pose.Target);
        var panned = camera.Pose; camera.Zoom(3);
        Assert.True(Distance(camera.Pose) < Distance(panned));
        camera.Fit(2); Assert.Equal(start.Target, camera.Pose.Target); Assert.True(Distance(camera.Pose) > Distance(start));
        Assert.Equal(before, JsonSerializer.Serialize(new { result, part }));
    }

    [Fact]
    public void LargeOrbitAndZoomInputsKeepCameraFiniteAndUsable()
    {
        var camera = new CadPreviewCamera([new(-1, -1, -1), new(1, 1, 1)]);
        camera.Orbit(1e6, 1e6); camera.Zoom(1e6);
        var pose = camera.Pose;
        Assert.True(double.IsFinite(Distance(pose)) && Distance(pose) > 0);
        Assert.True(pose.NearPlane > 0 && pose.FarPlane > pose.NearPlane);
        camera.Zoom(-1e6); Assert.True(double.IsFinite(Distance(camera.Pose)));
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.Fit(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.Zoom(double.NaN));
        Assert.Throws<ArgumentException>(() => new CadPreviewCamera([new(double.NaN, 0, 0)]));
    }
    private static double Distance(CadCameraPose p) => Math.Sqrt(Math.Pow(p.Position.X - p.Target.X, 2) +
        Math.Pow(p.Position.Y - p.Target.Y, 2) + Math.Pow(p.Position.Z - p.Target.Z, 2));
}
