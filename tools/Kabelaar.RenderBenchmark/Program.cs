using System.Diagnostics;
using System.Text.Json;
using Kabelaar.Core;
using Kabelaar.Rendering;
using SkiaSharp;

var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/render-benchmark");
var editsOnly = args.Contains("--edits-only");
Directory.CreateDirectory(output);
var project = DemoFactory.CreateAcceptancePerformance();
var sheet = project.Sheets[0];
var scene = SceneBuilder.Build(project, sheet);
var viewport = new SceneBounds(0, 0, sheet.WidthMm, sheet.HeightMm);
const int width = 1150, height = 850;
var zoom = Math.Min((width - 40) / sheet.WidthMm, (height - 40) / sheet.HeightMm);
using var surface = SKSurface.Create(new SKImageInfo(width, height));
using var cache = new ScreenSceneCache();
void Paint(DrawingScene drawing, double scale, bool raster)
{
    var canvas = surface.Canvas;
    canvas.ResetMatrix();
    canvas.Clear(SKColors.White);
    canvas.Translate(20, 20);
    canvas.Scale((float)scale);
    if (raster) SkiaSceneRenderer.RenderScreen(canvas, drawing, viewport, cache);
    else SkiaSceneRenderer.Render(canvas, drawing, viewport);
    canvas.Flush();
}
double Measure(Action action)
{
    var start = Stopwatch.GetTimestamp();
    action();
    return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
object Summary(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return new { count = sorted.Length, meanMs = sorted.Average(), p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], maxMs = sorted[^1] };
}
Paint(scene, zoom, false);
Paint(scene, zoom, true);
var vectors = editsOnly ? [] : Enumerable.Range(0, 100).Select(i => Measure(() => Paint(scene, zoom * (i % 2 == 0 ? 1.01 : 1), false))).ToArray();
var rasters = Enumerable.Range(0, 100).Select(i => Measure(() => Paint(scene, zoom * (i % 2 == 0 ? 1.01 : 1), true))).ToArray();
var builds = new List<double>();
var edits = new List<double>();
var phases = new Dictionary<string, List<double>>();
var coldEdits = new List<double>();
var buildAllocations = new List<long>();
var changed = sheet.Views[0].Components[0];
for (var i = 0; i < 110; i++)
{
    HarnessOperations.MoveComponent(project, sheet.Views[0], changed, changed.Position + new Point2(i % 2 == 0 ? 1 : -1, 0)).Execute();
    var start = Stopwatch.GetTimestamp();
    var allocationStart = GC.GetAllocatedBytesForCurrentThread();
    var buildTime = Measure(() => scene = SceneBuilder.Build(project, sheet, timing: (name, milliseconds) =>
    {
        if (i < 10) return;
        if (!phases.TryGetValue(name, out var samples)) phases[name] = samples = [];
        samples.Add(milliseconds);
    }));
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    Paint(scene, zoom, true);
    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    if (i < 10) coldEdits.Add(elapsed);
    else { builds.Add(buildTime); edits.Add(elapsed); buildAllocations.Add(allocated); }
}
using (var image = surface.Snapshot())
using (var data = image.Encode(SKEncodedImageFormat.Png, 90))
using (var stream = File.Create(Path.Combine(output, "acceptance-render.png")))
    data.SaveTo(stream);
var report = new
{
    configuration =
#if DEBUG
        "Debug",
#else
        "Release",
#endif
    utc = DateTime.UtcNow, environment = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
    connectors = project.Assembly.Connectors.Count, conductors = project.Assembly.Conductors.Count,
    primitives = scene.Primitives.Count, width, height,
    vectorFrames = vectors.Length == 0 ? null : Summary(vectors), cachedScreenFrames = Summary(rasters),
    sceneBuild = Summary(builds), sceneBuildAndFirstPaint = Summary(edits),
    coldSceneBuildAndFirstPaint = Summary(coldEdits), averageManagedBytesPerBuild = buildAllocations.Average(),
    buildPhases = phases.ToDictionary(p => p.Key, p => Summary(p.Value)),
    cacheBytes = cache.CachedBytes, cacheImages = cache.ImageCount
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "render-benchmark.json"), json);
Console.WriteLine(json);
