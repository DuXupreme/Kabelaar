using System.Globalization;
using System.Text;
using System.Text.Json;
using Kabelaar.Core;
using SkiaSharp;

namespace Kabelaar.Rendering;

public enum ExportFormat { Pdf, Svg, Png, Csv }
public sealed record ExportOptions(bool Draft = true, double PngDpi = 150);
public sealed record ExportResult(IReadOnlyList<string> Files, IReadOnlyList<ValidationIssue> Issues, bool Draft);
public sealed class ProductionExportException(IReadOnlyList<ValidationIssue> issues)
    : InvalidOperationException("Productie-export geblokkeerd: " + string.Join(" ", issues.Select(i => i.Message)))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

/// <summary>Exports consume a detached snapshot, use a single validation gate and publish complete files atomically.</summary>
public static class ExportService
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNameCaseInsensitive = true };

    public static Task<ExportResult> ExportAsync(HarnessProject project, string path, ExportFormat format,
        ExportOptions? options = null, CancellationToken cancellationToken = default) =>
        ExportSnapshotAsync(Snapshot(project), path, [format], options ?? new(), false, cancellationToken);

    public static Task<ExportResult> ExportSetAsync(HarnessProject project, string directory,
        ExportOptions? options = null, CancellationToken cancellationToken = default) =>
        ExportSnapshotAsync(Snapshot(project), directory, [ExportFormat.Pdf, ExportFormat.Svg, ExportFormat.Png, ExportFormat.Csv],
            options ?? new(), true, cancellationToken);

    public static Task<ExportResult> ExportSetAsync(HarnessProject project, string directory,
        bool draft, CancellationToken cancellationToken = default) => ExportSetAsync(project, directory, new ExportOptions(draft), cancellationToken);

    public static Task<ExportResult> ExportPdfAsync(HarnessProject project, string path, bool draft = true, CancellationToken cancellationToken = default) =>
        ExportAsync(project, path, ExportFormat.Pdf, new(draft), cancellationToken);
    public static Task<ExportResult> ExportSvgAsync(HarnessProject project, string path, bool draft = true, CancellationToken cancellationToken = default) =>
        ExportAsync(project, path, ExportFormat.Svg, new(draft), cancellationToken);
    public static Task<ExportResult> ExportPngAsync(HarnessProject project, string path, bool draft = true, double dpi = 150, CancellationToken cancellationToken = default) =>
        ExportAsync(project, path, ExportFormat.Png, new(draft, dpi), cancellationToken);
    public static Task<ExportResult> ExportCsvAsync(HarnessProject project, string path, bool draft = true, CancellationToken cancellationToken = default) =>
        ExportAsync(project, path, ExportFormat.Csv, new(draft), cancellationToken);

    private static HarnessProject Snapshot(HarnessProject project) =>
        JsonSerializer.Deserialize<HarnessProject>(JsonSerializer.SerializeToUtf8Bytes(project, SnapshotJson), SnapshotJson)
        ?? throw new InvalidOperationException("Projectsnapshot kon niet worden gemaakt.");

    public static IReadOnlyList<ValidationIssue> Validate(HarnessProject project)
    {
        var issues = ProductionValidator.Validate(project).ToList();
        if (issues.Any(i => i.IsStructural)) return issues;
        var imageIds = project.Sheets.Select(s => s.LogoAssetId)
            .Concat(project.Sheets.SelectMany(s => s.Views).SelectMany(v => v.Annotations)
                .Where(a => a.Kind == AnnotationKind.Image).Select(a => a.AssetId))
            .OfType<string>().Distinct();
        foreach (var id in imageIds)
        {
            var asset = project.Assets.Find(a => a.Id == id);
            if (asset is null) continue;
            using var data = SKData.CreateCopy(asset.Data);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000)
                issues.Add(new(Severity.Error, "image.invalid", id, $"Afbeelding '{asset.FileName}' is onleesbaar of groter dan 40 megapixels."));
        }
        return issues;
    }

    private static Task<ExportResult> ExportSnapshotAsync(HarnessProject snapshot, string destination, IReadOnlyList<ExportFormat> formats,
        ExportOptions options, bool set, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = Validate(snapshot);
        var blockers = issues.Where(i => i.IsStructural || (!options.Draft && i.Severity == Severity.Error)).ToArray();
        if (blockers.Length > 0) throw new ProductionExportException(blockers);
        if (!double.IsFinite(options.PngDpi) || options.PngDpi is < 36 or > 600)
            throw new ArgumentOutOfRangeException(nameof(options), "PNG-resolutie moet tussen 36 en 600 dpi liggen.");
        var documentName = SafeName(string.IsNullOrWhiteSpace(snapshot.DrawingNumber) ? snapshot.Title : snapshot.DrawingNumber);
        var selectedDirectory = set ? Path.GetFullPath(destination) : null;
        var packageName = set ? $"{documentName}-rev-{SafeName(snapshot.Revision)}-{(options.Draft ? "concept" : "productie")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}" : null;
        var packageDirectory = set ? Path.Combine(selectedDirectory!, packageName!) : null;
        var stagingDirectory = set ? Path.Combine(selectedDirectory!, ".kabelaar-export-" + Guid.NewGuid().ToString("N")) : null;
        var basePath = set ? Path.Combine(stagingDirectory!, documentName) : Path.GetFullPath(destination);
        var folder = Path.GetDirectoryName(basePath) ?? throw new ArgumentException("Geen exportmap.");
        Directory.CreateDirectory(folder);
        var staged = new List<(string Temporary, string Final)>();
        void Stage(string final, Action<string> write)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = Path.Combine(folder, $".kabelaar-export-{Guid.NewGuid():N}.tmp");
            staged.Add((temporary, final));
            write(temporary);
        }
        try
        {
            var scenes = snapshot.Sheets.Select(sheet => SceneBuilder.Build(snapshot, sheet, options.Draft, true)).ToArray();
            foreach (var format in formats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = set ? basePath + "." + format.ToString().ToLowerInvariant() : Path.ChangeExtension(basePath, format.ToString().ToLowerInvariant());
                switch (format)
                {
                    case ExportFormat.Pdf:
                        Stage(path, temp => WritePdf(snapshot, scenes, temp, cancellationToken));
                        break;
                    case ExportFormat.Svg:
                        for (var i = 0; i < scenes.Length; i++)
                        {
                            var scene = scenes[i]; var sheetPath = SheetPath(path, i, scenes.Length);
                            Stage(sheetPath, temp => WriteSvg(scene, temp));
                        }
                        break;
                    case ExportFormat.Png:
                        for (var i = 0; i < scenes.Length; i++)
                        {
                            var scene = scenes[i]; var sheetPath = SheetPath(path, i, scenes.Length);
                            Stage(sheetPath, temp => WritePng(scene, temp, options.PngDpi));
                        }
                        break;
                    case ExportFormat.Csv:
                        foreach (var (kind, suffix) in new[] { (AnnotationKind.WireTable, "draden"), (AnnotationKind.WireTable, "kniplijst"), (AnnotationKind.PinTable, "pinnen"), (AnnotationKind.BomTable, "stuklijst") })
                        {
                            var csvPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-" + suffix + ".csv");
                            Stage(csvPath, temp => File.WriteAllText(temp, Csv(snapshot, kind, options.Draft, suffix == "kniplijst"), new UTF8Encoding(true)));
                        }
                        break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Staging prevents a cancellation/renderer failure from replacing existing output with half a file.
            foreach (var (temporary, final) in staged) File.Move(temporary, final, true);
            if (set)
            {
                // An export set is published as one new directory. Existing production packages are immutable.
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingDirectory!, packageDirectory!);
                return new ExportResult(staged.Select(item => Path.Combine(packageDirectory!, Path.GetFileName(item.Final))).ToArray(), issues, options.Draft);
            }
            return new ExportResult(staged.Select(item => item.Final).ToArray(), issues, options.Draft);
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                if (File.Exists(temporary)) File.Delete(temporary);
            if (set && Directory.Exists(stagingDirectory))
            {
                // Remove only files explicitly created by this attempt, never enumerate or recursively delete user data.
                foreach (var (_, final) in staged) if (File.Exists(final)) File.Delete(final);
                Directory.Delete(stagingDirectory);
            }
        }
    }, cancellationToken);

    private static void WritePdf(HarnessProject project, DrawingScene[] scenes, string path, CancellationToken cancellationToken)
    {
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = project.Title, Author = project.Author, Subject = $"{project.DrawingNumber} — revisie {project.Revision}",
            Creator = "Kabelaar", Producer = "Kabelaar / SkiaSharp"
        });
        if (document is null) throw new InvalidOperationException("PDF-backend kon niet worden gestart.");
        foreach (var scene in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canvas = document.BeginPage((float)(scene.WidthMm * 72 / 25.4), (float)(scene.HeightMm * 72 / 25.4));
            canvas.Scale((float)(72 / 25.4));
            canvas.Clear(SKColors.White);
            SkiaSceneRenderer.Render(canvas, scene, scene.Bounds);
            document.EndPage();
        }
        document.Close();
    }

    private static void WriteSvg(DrawingScene scene, string path)
    {
        // SVG text is outlined to remain visually identical on machines without the chosen font.
        using (var stream = File.Create(path))
        using (var canvas = SKSvgCanvas.Create(new(0, 0, (float)scene.WidthMm, (float)scene.HeightMm), stream))
            SkiaSceneRenderer.Render(canvas, scene, scene.Bounds, textAsPaths: true);
        var document = System.Xml.Linq.XDocument.Load(path);
        if (document.Root is { } root)
        {
            root.SetAttributeValue("width", scene.WidthMm.ToString("0.####", CultureInfo.InvariantCulture) + "mm");
            root.SetAttributeValue("height", scene.HeightMm.ToString("0.####", CultureInfo.InvariantCulture) + "mm");
            root.SetAttributeValue("viewBox", $"0 0 {scene.WidthMm.ToString("0.####", CultureInfo.InvariantCulture)} {scene.HeightMm.ToString("0.####", CultureInfo.InvariantCulture)}");
            document.Save(path);
        }
    }

    private static void WritePng(DrawingScene scene, string path, double dpi)
    {
        var scale = dpi / 25.4;
        var width = Math.Max(1, (int)Math.Ceiling(scene.WidthMm * scale));
        var height = Math.Max(1, (int)Math.Ceiling(scene.HeightMm * scale));
        if ((long)width * height > 100_000_000) throw new InvalidOperationException("PNG te groot; kies een lagere resolutie.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height)) ?? throw new InvalidOperationException("PNG-oppervlak kon niet worden gemaakt.");
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        SkiaSceneRenderer.Render(surface.Canvas, scene, scene.Bounds);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    public static string Csv(HarnessProject project, AnnotationKind tableKind, bool draft, bool cutList = false)
    {
        var (defaults, rows) = SceneBuilder.TableData(project, tableKind);
        var columns = cutList
            ? ["Name", "PartNumber", "Color", "CrossSectionMm2", "CutLengthMm", "AllowanceMm", "LengthSource", "Signal", "From", "To"]
            : rows.Count > 0 ? rows[0].Keys.ToArray() : defaults.ToArray();
        var output = new StringBuilder();
        output.AppendLine(string.Join(";", new[] { "Documentstatus", "Tekening", "Revisie" }.Concat(columns.Select(SceneBuilder.TableColumnTitle)).Select(Escape)));
        foreach (var row in rows)
            output.AppendLine(string.Join(";", new[] { draft ? "CONCEPT" : "PRODUCTIE", project.DrawingNumber, project.Revision }.Concat(columns.Select(key => row.GetValueOrDefault(key, ""))).Select(Escape)));
        return output.ToString();
    }

    private static string Escape(string text)
    {
        // Spreadsheet applications must treat entered labels as data, not executable formulas.
        if (text.TrimStart().FirstOrDefault() is '=' or '+' or '@' or '-')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    private static string SheetPath(string path, int index, int count) => count == 1 ? path
        : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-blad-{index + 1}{Path.GetExtension(path)}");
    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "kabelboom" : safe;
    }
}
