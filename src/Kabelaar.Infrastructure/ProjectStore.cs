using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kabelaar.Core;

namespace Kabelaar.Infrastructure;

public sealed record LoadedProject(HarnessProject Project, string Fingerprint);
public sealed class ProjectConflictException(string message) : IOException(message);
public sealed class ProjectFormatException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class ProjectStore
{
    public const long MaxUncompressedBytes = 512L * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions SnapshotOptions = CreateSnapshotOptions();
    private static readonly JsonSerializerOptions LoadOptions = CreateLoadOptions();
    private static JsonSerializerOptions CreateLoadOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Model constructors supply editor defaults, which must never silently replace
            // fields missing from a damaged saved project.
            foreach (var property in info.Properties.Where(p => p.Set is not null))
                property.IsRequired = true;
        });
        return new(JsonOptions) { TypeInfoResolver = resolver };
    }
    private static JsonSerializerOptions CreateSnapshotOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(HarnessProject))
                foreach (var property in info.Properties.Where(p => p.Name == "assets"))
                    property.ShouldSerialize = (_, _) => false;
        });
        return new(JsonOptions) { TypeInfoResolver = resolver };
    }
    private sealed record AssetEntry(string Id, string FileName, string MediaType, string Entry, string Sha256);
    private sealed record Manifest(string Format, int Version, string ProjectEntry, List<AssetEntry> Assets);

    public Task<LoadedProject> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadPackageAsync(path, "Kabelaar.Project", cancellationToken);

    internal async Task<LoadedProject> LoadPackageAsync(string path, string format, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        stream.Position = 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            long total = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName) || !SafeEntryName(entry.FullName))
                    throw new ProjectFormatException("Het project bevat dubbele of ongeldige bestandspaden.");
                if (entry.Length > MaxUncompressedBytes || (total += entry.Length) > MaxUncompressedBytes)
                    throw new ProjectFormatException("Het project is te groot om veilig te openen (maximaal 512 MB).");
            }
            var manifest = await ReadJsonAsync<Manifest>(zip, "manifest.json", cancellationToken);
            if (manifest.Format != format || manifest.Version != 1)
                throw new ProjectFormatException("Dit projectformaat is onbekend of vereist een nieuwere Kabelaar-versie.");
            var project = await ReadProjectAsync(zip, manifest.ProjectEntry, cancellationToken);
            if (project.SchemaVersion != HarnessProject.CurrentSchemaVersion)
                throw new ProjectFormatException("Deze projectschemaversie wordt niet ondersteund. Het origineel is niet gewijzigd.");
            if (project.Assets.Count != 0)
                throw new ProjectFormatException("Assetgegevens horen in het manifest.");
            if (manifest.Assets.Select(a => a.Entry).Distinct(StringComparer.Ordinal).Count() != manifest.Assets.Count)
                throw new ProjectFormatException("Meerdere assets verwijzen naar hetzelfde archiefbestand.");
            foreach (var asset in manifest.Assets)
            {
                if (project.Assets.Any(a => a.Id == asset.Id))
                    throw new ProjectFormatException("Dubbel asset-ID in het project.");
                var entry = zip.GetEntry(asset.Entry) ?? throw new ProjectFormatException($"Asset ontbreekt: {asset.FileName}");
                await using var contents = entry.Open();
                using var bytes = new MemoryStream();
                await contents.CopyToAsync(bytes, cancellationToken);
                var data = bytes.ToArray();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ProjectFormatException($"Asset is beschadigd: {asset.FileName}");
                project.Assets.Add(new() { Id = asset.Id, FileName = asset.FileName, MediaType = asset.MediaType, Data = data });
            }
            ValidateStructure(project);
            return new(project, fingerprint);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NullReferenceException or ArgumentException)
        {
            throw new ProjectFormatException("Het project is beschadigd of bevat ongeldige gegevens. Het origineel is niet gewijzigd.", error);
        }
    }

    public Task<string> SaveAsync(HarnessProject project, string path, string? expectedFingerprint = null,
        CancellationToken cancellationToken = default) =>
        SavePackageAsync(project, path, "Kabelaar.Project", expectedFingerprint, cancellationToken);

    internal async Task<string> SavePackageAsync(HarnessProject project, string path, string format, string? expectedFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStructure(project);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Serialize a snapshot so autosave never removes or mutates live asset objects.
            var snapshot = JsonSerializer.SerializeToNode(project, SnapshotOptions)!.AsObject();
            snapshot["assets"] = new System.Text.Json.Nodes.JsonArray();
            var projectBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (projectBytes.Length > 64 * 1024 * 1024 ||
                project.Assets.Sum(a => (long)a.Data.Length) + projectBytes.Length + 1024 * 1024 > MaxUncompressedBytes)
                throw new ProjectFormatException("Het project is te groot om veilig op te slaan (maximaal 512 MB).");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    var assets = new List<AssetEntry>();
                    for (int index = 0; index < project.Assets.Count; index++)
                    {
                        var asset = project.Assets[index];
                        string entryName = $"assets/{index:D6}.bin";
                        await using var destination = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                        await destination.WriteAsync(asset.Data, cancellationToken);
                        assets.Add(new(asset.Id, asset.FileName, asset.MediaType, entryName, Convert.ToHexString(SHA256.HashData(asset.Data))));
                    }
                    await using (var data = zip.CreateEntry("project.json", CompressionLevel.Optimal).Open())
                        await data.WriteAsync(projectBytes, cancellationToken);
                    await WriteJsonAsync(zip, "manifest.json", new Manifest(format, 1, "project.json", assets), cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var savedFingerprint = await GetFingerprintAsync(temporary, cancellationToken);
            // Fingerprint is checked immediately before replacement. The UI additionally holds a ProjectLease.
            if (expectedFingerprint is not null)
            {
                if (!File.Exists(path) || !string.Equals(await GetFingerprintAsync(path, cancellationToken), expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new ProjectConflictException("Dit project is buiten Kabelaar gewijzigd. Open de gewijzigde versie of gebruik Opslaan als.");
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
            // Commit has completed. A cancellation arriving now must not report an unsuccessful save.
            return savedFingerprint;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<string> GetFingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }
    private static bool SafeEntryName(string name) => name.Length != 0 && !name.StartsWith('/') &&
        !name.Contains('\\') && !name.Contains(':') && name.Split('/').All(p => p is not ("" or "." or ".."));
    private static async Task<T> ReadJsonAsync<T>(ZipArchive zip, string name, CancellationToken token)
    {
        var entry = zip.GetEntry(name) ?? throw new ProjectFormatException($"Projectbestand ontbreekt: {name}");
        if (entry.Length > 64 * 1024 * 1024) throw new ProjectFormatException("Projectmetadata is te groot.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, LoadOptions, token)
            ?? throw new ProjectFormatException($"Leeg projectbestand: {name}");
    }
    private static async Task<HarnessProject> ReadProjectAsync(ZipArchive zip, string name, CancellationToken token)
    {
        var entry = zip.GetEntry(name) ?? throw new ProjectFormatException($"Projectbestand ontbreekt: {name}");
        if (entry.Length > 64 * 1024 * 1024) throw new ProjectFormatException("Projectmetadata is te groot.");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
            throw new ProjectFormatException("De projectschemaversie ontbreekt of is ongeldig.");
        if (version is < 1 or > HarnessProject.CurrentSchemaVersion)
            throw new ProjectFormatException("Deze projectschemaversie wordt niet ondersteund. Het origineel is niet gewijzigd.");
        if (version == HarnessProject.CurrentSchemaVersion)
            return root.Deserialize<HarnessProject>(LoadOptions) ?? throw new ProjectFormatException("Het projectbestand is leeg.");

        // Schema 1 had no presentation controls. Add exactly these newly introduced
        // fields, retaining its original appearance. Every pre-existing field remains
        // mandatory, and explicit null or malformed values are never repaired.
        var upgraded = JsonNode.Parse(root.GetRawText())!.AsObject();
        if (version == 1)
        {
        var spliceIds = new HashSet<string>(StringComparer.Ordinal);
        if (upgraded["assembly"] is JsonObject assembly && assembly["splices"] is JsonArray splices)
            foreach (var splice in splices.OfType<JsonObject>())
                if (splice["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && id is not null)
                    spliceIds.Add(id);
        if (upgraded["sheets"] is JsonArray sheets)
            foreach (var sheet in sheets.OfType<JsonObject>())
                if (sheet["views"] is JsonArray views)
                    foreach (var view in views.OfType<JsonObject>())
                    {
                        if (!view.ContainsKey("style"))
                            view["style"] = JsonSerializer.SerializeToNode(new DrawingStyle(), JsonOptions);
                        if (view["components"] is not JsonArray components) continue;
                        foreach (var component in components.OfType<JsonObject>())
                        {
                            Add("displayScale", 1d);
                            Add("showPins", true);
                            Add("showPinNumbers", true);
                            Add("showLabel", true);
                            Add("showPartNumber", true);
                            var isSplice = component["entityId"] is JsonValue entityValue &&
                                entityValue.TryGetValue<string>(out var entityId) && entityId is not null && spliceIds.Contains(entityId);
                            Add("labelFontSizeMm", isSplice ? 2.5 : 3d);
                            void Add<T>(string key, T value)
                            {
                                if (!component.ContainsKey(key)) component[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
                            }
                        }
                    }
        }
        // Schema 3 adds optional explicit curves and per-view wire presentation.
        // Null geometry deliberately keeps every legacy Catmull sample unchanged.
        if (version <= 2)
        {
            if (upgraded["assembly"] is JsonObject curveAssembly && curveAssembly["branches"] is JsonArray branches)
                foreach (var branch in branches.OfType<JsonObject>())
                    if (!branch.ContainsKey("geometry")) branch["geometry"] = null;
            if (upgraded["sheets"] is JsonArray curveSheets)
                foreach (var sheet in curveSheets.OfType<JsonObject>())
                    if (sheet["views"] is JsonArray views)
                        foreach (var view in views.OfType<JsonObject>())
                        {
                            if (!view.ContainsKey("wirePresentations")) view["wirePresentations"] = new JsonArray();
                            if (view["routes"] is JsonArray routes)
                                foreach (var route in routes.OfType<JsonObject>())
                                    if (!route.ContainsKey("geometry")) route["geometry"] = null;
                        }
        }
        if (version <= 3) MigrateSchemaFour(upgraded);
        upgraded["schemaVersion"] = HarnessProject.CurrentSchemaVersion;
        return upgraded.Deserialize<HarnessProject>(LoadOptions) ?? throw new ProjectFormatException("Het projectbestand is leeg.");
    }
    private static void MigrateSchemaFour(JsonObject project)
    {
        // Only fields introduced in schema 4 get defaults. Existing malformed or
        // explicitly null fields stay untouched and are rejected by the normal loader.
        static void Add<T>(JsonObject node, string key, T value)
        {
            if (!node.ContainsKey(key)) node[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
        }
        static IEnumerable<JsonObject> Objects(JsonObject node, string key) =>
            node[key] is JsonArray array ? array.OfType<JsonObject>() : [];
        if (project["assembly"] is JsonObject assembly)
        {
            Add(assembly, "referencePoints", new JsonArray());
            foreach (var connector in Objects(assembly, "connectors"))
            {
                Add<string?>(connector, "matingPartId", null); Add<string?>(connector, "matingConnectorId", null);
                foreach (var cavity in Objects(connector, "cavities")) { Add(cavity, "function", ""); Add(cavity, "notes", ""); }
            }
            foreach (var wire in Objects(assembly, "conductors"))
            {
                Add(wire, "netName", ""); Add(wire, "shielding", ""); Add(wire, "lengthNeedsConfirmation", false);
                foreach (var key in new[] { "from", "to" })
                    if (wire[key] is JsonObject endpoint) Add(endpoint, "notes", "");
            }
        }
        foreach (var part in Objects(project, "parts"))
            foreach (var pin in Objects(part, "pins")) Add(pin, "function", "");
        foreach (var sheet in Objects(project, "sheets"))
        {
            Add(sheet, "titleBlock", new TitleBlockSettings()); Add(sheet, "zoneColumns", 0); Add(sheet, "zoneRows", 0);
            foreach (var view in Objects(sheet, "views"))
            {
                Add(view, "drawOrder", new JsonArray());
                foreach (var component in Objects(view, "components"))
                { Add(component, "mirrorX", false); Add(component, "mirrorY", false); }
                foreach (var presentation in Objects(view, "wirePresentations"))
                {
                    Add<bool?>(presentation, "showLabel", null); Add<string?>(presentation, "labelText", null);
                    Add<Point2?>(presentation, "labelPosition", null); Add(presentation, "labelRotationDegrees", 0d);
                    Add(presentation, "showCrossingBridges", false); Add(presentation, "crossingRadiusMm", 2d);
                }
                foreach (var note in Objects(view, "annotations"))
                {
                    Add(note, "rotationDegrees", 0d); Add(note, "arrowSizeMm", 2d); Add(note, "textFrame", false);
                    Add(note, "fillColor", "#FFFFFF"); Add(note, "tableHeaderColor", "#E5ECEE"); Add(note, "rows", new JsonArray()); Add(note, "columnWidthsMm", new JsonArray());
                    Add(note, "rowHeightMm", 0d); Add(note, "cellPaddingMm", 1d); Add(note, "showTableHeader", true);
                    Add(note, "cellAlignment", TableCellAlignment.Left); Add(note, "columnAlignments", new JsonArray());
                    Add(note, "nodeSymbol", NodeSymbol.General);
                }
            }
        }
    }
    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken token)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
    }
    public static void ValidateStructure(HarnessProject project)
    {
        var structuralErrors = StructuralValidator.Validate(project);
        if (structuralErrors.Count > 0)
            throw new ProjectFormatException("Het project bevat ongeldige verwijzingen of gegevens:\n" + string.Join("\n", structuralErrors.Take(20)));
        if (project.SchemaVersion != HarnessProject.CurrentSchemaVersion || string.IsNullOrWhiteSpace(project.Id) ||
            project.Assembly is null || project.Sheets is null || project.Sheets.Count == 0 || project.Parts is null || project.Assets is null)
            throw new ProjectFormatException("De projectstructuur is onvolledig of heeft een onbekende versie.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Unique(string id) { if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new ProjectFormatException("Leeg of dubbel object-ID."); }
        void Finite(double number) { if (!double.IsFinite(number)) throw new ProjectFormatException("Een maat of coördinaat is ongeldig."); }
        void Point(Point2 point) { Finite(point.X); Finite(point.Y); }
        foreach (var part in project.Parts)
        {
            Unique(part.Id); Finite(part.WidthMm); Finite(part.HeightMm);
            foreach (var pin in part.Pins) { Unique(pin.Id); Point(pin.Position); }
            foreach (var line in part.Symbol) foreach (var point in line.Points) Point(point);
        }
        foreach (var connector in project.Assembly.Connectors) { Unique(connector.Id); foreach (var cavity in connector.Cavities) Unique(cavity.Id); }
        foreach (var splice in project.Assembly.Splices) Unique(splice.Id);
        foreach (var reference in project.Assembly.ReferencePoints) Unique(reference.Id);
        foreach (var conductor in project.Assembly.Conductors) Unique(conductor.Id);
        foreach (var branch in project.Assembly.Branches) { Unique(branch.Id); Finite(branch.PhysicalLengthMm); foreach (var p in branch.Points) Point(p); }
        foreach (var twist in project.Assembly.TwistGroups) Unique(twist.Id);
        foreach (var cable in project.Assembly.Cables) Unique(cable.Id);
        foreach (var asset in project.Assets) { Unique(asset.Id); if (asset.Data is null) throw new ProjectFormatException("Lege assetgegevens."); }
        foreach (var sheet in project.Sheets)
        {
            Unique(sheet.Id);
            if (sheet.WidthMm <= 0 || sheet.HeightMm <= 0 || sheet.PrintScale <= 0) throw new ProjectFormatException("Bladmaat en schaal moeten positief zijn.");
            Finite(sheet.WidthMm); Finite(sheet.HeightMm); Finite(sheet.PrintScale);
            foreach (var view in sheet.Views)
            {
                Unique(view.Id); Point(view.Origin); Finite(view.Scale);
                foreach (var component in view.Components) { Unique(component.Id); Point(component.Position); }
                foreach (var route in view.Routes) { Unique(route.Id); foreach (var p in route.Points) Point(p); }
                foreach (var note in view.Annotations) { Unique(note.Id); foreach (var p in note.Points) Point(p); }
            }
        }
        var assets = project.Assets.Select(a => a.Id).ToHashSet();
        foreach (var part in project.Parts) if (part.SourceAssetId is not null && !assets.Contains(part.SourceAssetId)) throw new ProjectFormatException("Een onderdeel verwijst naar een ontbrekend asset.");
        foreach (var sheet in project.Sheets)
        {
            if (sheet.LogoAssetId is not null && !assets.Contains(sheet.LogoAssetId)) throw new ProjectFormatException("Bladlogo ontbreekt.");
            foreach (var note in sheet.Views.SelectMany(v => v.Annotations))
                if (note.AssetId is not null && !assets.Contains(note.AssetId)) throw new ProjectFormatException("Afbeelding ontbreekt.");
        }
    }
}
