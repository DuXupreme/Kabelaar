using System.Diagnostics;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class CadImportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    private static string Worker
    {
        get
        {
            var location = new DirectoryInfo(AppContext.BaseDirectory);
            while (location is not null && !Directory.Exists(Path.Combine(location.FullName, "native", "Kabelaar.CadWorker"))) location = location.Parent;
            return Environment.GetEnvironmentVariable("KABELAAR_CAD_WORKER")
                ?? Path.Combine(location?.FullName ?? "", "artifacts", "native-worker", "Kabelaar.CadWorker.exe");
        }
    }
    public CadImportTests() => Directory.CreateDirectory(directory);
    private async Task<string> Fixture(string kind)
    {
        Assert.True(File.Exists(Worker), "Build the native worker first with native/Kabelaar.CadWorker/Build-Native.ps1.");
        string path = Path.Combine(directory, kind + ".step");
        using var process = new Process { StartInfo = new(Worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string arg in new[] { "--protocol", "1", "--fixture-" + kind, "--output", path }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        Assert.True(process.ExitCode == 0, await stderr);
        return path;
    }
    [Theory]
    [InlineData("box", 10, 20, 30)]
    [InlineData("box-inch", 25.4, 50.8, 76.2)]
    public async Task StepUnitsMeshAndSixViewsAgree(string fixture, double x, double y, double z)
    {
        var result = await new CadImportService(Worker).ImportAsync(await Fixture(fixture));
        Assert.Equal("mm", result.Units);
        Assert.Equal(1, result.ProtocolVersion);
        Assert.Equal(6, result.Views.Count);
        Assert.NotEmpty(result.Mesh.Triangles);
        Assert.Equal(x, result.Mesh.Vertices.Max(p => p.X) - result.Mesh.Vertices.Min(p => p.X), 5);
        Assert.Equal(y, result.Mesh.Vertices.Max(p => p.Y) - result.Mesh.Vertices.Min(p => p.Y), 5);
        Assert.Equal(z, result.Mesh.Vertices.Max(p => p.Z) - result.Mesh.Vertices.Min(p => p.Z), 5);
        Assert.Equal(x, result.Views.Single(v => v.Name == "Top").WidthMm, 5);
        Assert.Equal(y, result.Views.Single(v => v.Name == "Top").HeightMm, 5);
        Assert.Equal(z, result.Views.Single(v => v.Name == "Front").HeightMm, 5);
    }
    [Fact]
    public async Task RealCurvedFacesProduceMeshAndVisibleHiddenProjectedCurves()
    {
        var result = await new CadImportService(Worker).ImportAsync(await Fixture("connector"));
        Assert.True(result.Mesh.Triangles.Count > 12);
        Assert.Contains(result.Views.SelectMany(v => v.Paths), p => p.Points.Count > 4);
        Assert.Contains(result.Views.SelectMany(v => v.Paths), p => p.Hidden);
        Assert.Contains(result.Views.SelectMany(v => v.Paths), p => !p.Hidden);
    }
    [Fact]
    public async Task InvalidStepAndDeadlineProduceExplicitFailures()
    {
        var invalid = Path.Combine(directory, "bad.step");
        await File.WriteAllTextAsync(invalid, "this is not STEP");
        var service = new CadImportService(Worker);
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(invalid));
        var valid = await Fixture("connector");
        await Assert.ThrowsAsync<TimeoutException>(() => service.ImportAsync(valid, timeout: TimeSpan.FromMilliseconds(1)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(valid, cancelled.Token));
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (full.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(full, true);
    }
}
