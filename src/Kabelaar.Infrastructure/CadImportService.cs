using System.Diagnostics;
using System.Text.Json;
using Kabelaar.Core;

namespace Kabelaar.Infrastructure;

public readonly record struct Point3(double X, double Y, double Z);
public sealed class CadMesh
{
    public List<Point3> Vertices { get; set; } = [];
    public List<int[]> Triangles { get; set; } = [];
}
public sealed class CadProjection
{
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public List<SymbolPath> Paths { get; set; } = [];
}
public sealed class CadImportResult
{
    public int ProtocolVersion { get; set; }
    public string Units { get; set; } = "mm";
    public string KernelVersion { get; set; } = "";
    public CadMesh Mesh { get; set; } = new();
    public List<CadProjection> Views { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Untrusted CAD parsing is isolated from the editor in a killable native process.</summary>
public sealed class CadImportService
{
    private readonly string workerPath;
    public CadImportService(string? workerPath = null) =>
        this.workerPath = workerPath ?? Path.Combine(AppContext.BaseDirectory, "CadWorker", "Kabelaar.CadWorker.exe");
    public bool IsAvailable => File.Exists(workerPath);
    public async Task<CadImportResult> ImportAsync(string stepPath, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null, double deflectionMm = 0.1)
    {
        if (!IsAvailable) throw new FileNotFoundException("De STEP-module ontbreekt. Installeer het volledige Kabelaar-pakket.", workerPath);
        if (!File.Exists(stepPath)) throw new FileNotFoundException("STEP-bestand ontbreekt.", stepPath);
        if (new FileInfo(stepPath).Length > 256L * 1024 * 1024) throw new IOException("STEP-bestanden groter dan 256 MB worden niet ondersteund.");
        if (!double.IsFinite(deflectionMm) || deflectionMm < 0.001 || deflectionMm > 10) throw new ArgumentOutOfRangeException(nameof(deflectionMm));
        var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kabelaar", "CadImport"));
        var directory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Copy into an owned working directory: native code never opens a mutable source in a shared folder.
        var input = Path.Combine(directory, "input.step");
        var output = Path.Combine(directory, "result.json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath)!
            }
        };
        foreach (var argument in new[] { "--protocol", "1", "--input", input, "--output", output, "--deflection", deflectionMm.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            await using (var source = File.OpenRead(stepPath))
            await using (var destination = File.Create(input))
                await source.CopyToAsync(destination, cancellationToken);
            if (!process.Start()) throw new IOException("De STEP-module kon niet worden gestart.");
            // Drain both pipes while the worker runs; OCC may emit transfer diagnostics.
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("STEP-import duurde langer dan de toegestane tijd en is gestopt.");
            }
            var errors = await stderr;
            await stdout;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new IOException("STEP-import mislukt. " + (string.IsNullOrWhiteSpace(errors) ? $"Foutcode {process.ExitCode}." : errors));
            if (new FileInfo(output).Length > 256L * 1024 * 1024) throw new IOException("STEP-resultaat is te groot.");
            await using var resultStream = File.OpenRead(output);
            var result = await JsonSerializer.DeserializeAsync<CadImportResult>(resultStream, ProjectStore.JsonOptions, cancellationToken)
                ?? throw new IOException("De STEP-module gaf geen resultaat.");
            Validate(result);
            return result;
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 16000) result.Append(buffer, 0, Math.Min(count, 16000 - result.Length));
        return result.ToString();
    }
    private static void Validate(CadImportResult result)
    {
        if (result.ProtocolVersion != 1 || result.Units != "mm" || result.Mesh is null || result.Views is null ||
            result.Mesh.Vertices.Count == 0 || result.Mesh.Triangles.Count == 0 || result.Views.Count != 6)
            throw new IOException("De STEP-module gaf een onvolledig of incompatibel resultaat.");
        foreach (var vertex in result.Mesh.Vertices)
            if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)) throw new IOException("Ongeldige STEP-geometrie.");
        foreach (var triangle in result.Mesh.Triangles)
            if (triangle.Length != 3 || triangle.Any(i => i < 0 || i >= result.Mesh.Vertices.Count)) throw new IOException("Ongeldig STEP-mesh.");
        foreach (var view in result.Views)
            if (!double.IsFinite(view.WidthMm) || !double.IsFinite(view.HeightMm) || view.WidthMm < 0 || view.HeightMm < 0 ||
                view.Paths.Any(p => p.Points.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y)))) throw new IOException("Ongeldig STEP-aanzicht.");
    }
}
