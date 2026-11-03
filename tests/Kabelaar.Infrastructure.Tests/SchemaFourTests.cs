using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class SchemaFourTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectStore store = new();
    public SchemaFourTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name = "schema.kabelaar") => Path.Combine(directory, name);

    [Fact]
    public async Task SchemaThreePreservesLegacyContentAndAddsOnlySchemaFourDefaults()
    {
        var original = DemoFactory.CreatePerformance(2);
        var view = original.Sheets[0].Views[0];
        view.WirePresentations.Add(new() { ConductorId = original.Assembly.Conductors[0].Id, LineWidthMm = .7 });
        view.Annotations.Add(new() { Kind = AnnotationKind.Leader, Points = [new(10, 15), new(25, 30)], Text = "Oude notitie" });
        await store.SaveAsync(original, PathFor());
        Rewrite(PathFor(), root =>
        {
            root["schemaVersion"] = 3;
            RemoveSchemaFour(root);
        });
        var bytes = await File.ReadAllBytesAsync(PathFor());
        var loaded = (await store.LoadAsync(PathFor())).Project;
        Assert.Equal(4, loaded.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(loaded));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PathFor()));
    }

    [Fact]
    public async Task NewPresentationMetadataAndReferencePointsSurviveSaveTemplateAndRecovery()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        var wire = project.Assembly.Conductors[0];
        var reference = new PhysicalReferencePoint { Name = "CHASSIS", Description = "Extern aansluitpunt", MaxWires = 3 };
        project.Assembly.ReferencePoints.Add(reference);
        view.Components.Add(new() { EntityId = reference.Id, Position = new(60, 40), MirrorX = true });
        wire.From = new() { Kind = EndpointKind.ReferencePoint, EntityId = reference.Id, Notes = "M6" };
        wire.NetName = "POWER_GND"; wire.Shielding = "Afscherming aan bron"; wire.LengthNeedsConfirmation = true;
        project.Parts[0].Pins[0].Function = "Power";
        project.Assembly.Connectors[0].Cavities[0].Function = "Ingang";
        project.Assembly.Connectors[0].Cavities[0].Notes = "Controleer contact";
        view.WirePresentations.Add(new() { ConductorId = wire.Id, LabelText = "", LabelPosition = new(32, 14),
            LabelRotationDegrees = 45, ShowLabel = true, ShowCrossingBridges = true, CrossingRadiusMm = 3 });
        var table = new Annotation { Kind = AnnotationKind.FreeTable, Points = [new(5, 8)], Columns = ["Naam", "Waarde"],
            Rows = [new() { Cells = ["Kleur", "Blauw"] }], ColumnWidthsMm = [30, 40], RowHeightMm = 8,
            CellAlignment = TableCellAlignment.Center, ColumnAlignments = [TableCellAlignment.Left, TableCellAlignment.Right],
            FillColor = "#FEFEFE", TableHeaderColor = "#CCCCCC", TextFrame = true, ArrowSizeMm = 4 };
        view.Annotations.Add(table); view.DrawOrder.Add(table.Id);
        project.Sheets[0].TitleBlock = new() { Enabled = true, DrawnBy = "Tekenaar", CheckedBy = "Controleur", Date = "2026-11-03",
            CheckedDate = "2026-11-04", GeneralTolerances = "±1 mm", Fields = [new() { Name = "Order", Value = "123" }] };
        project.Sheets[0].ZoneColumns = 4; project.Sheets[0].ZoneRows = 3;
        await store.SaveAsync(project, PathFor());
        Assert.Equal(JsonSerializer.Serialize(project), JsonSerializer.Serialize((await store.LoadAsync(PathFor())).Project));
        var template = await new LibraryStore().CreateFromTemplateAsync(PathFor());
        Assert.Equal(JsonSerializer.Serialize(view), JsonSerializer.Serialize(template.Sheets[0].Views[0]));
        Assert.Equal(JsonSerializer.Serialize(project.Assembly), JsonSerializer.Serialize(template.Assembly));
        var recovery = new RecoveryStore(PathFor("recovery"));
        var entry = await recovery.SaveAsync(project, null, "schema-four");
        Assert.Equal(JsonSerializer.Serialize(project), JsonSerializer.Serialize((await recovery.LoadAsync(entry)).Project));
    }

    [Theory]
    [InlineData("referencePoints")]
    [InlineData("titleBlock")]
    [InlineData("drawOrder")]
    [InlineData("labelText")]
    [InlineData("rows")]
    [InlineData("function")]
    public async Task CurrentSchemaRejectsMissingNewFieldsWithoutRepairingFile(string field)
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0];
        view.WirePresentations.Add(new() { ConductorId = project.Assembly.Conductors[0].Id });
        view.Annotations.Add(new() { Kind = AnnotationKind.Text, Points = [new(1, 2)], Text = "test" });
        await store.SaveAsync(project, PathFor());
        Rewrite(PathFor(), root =>
        {
            var v = root["sheets"]![0]!["views"]![0]!.AsObject();
            var node = field switch
            {
                "referencePoints" => root["assembly"]!.AsObject(),
                "titleBlock" => root["sheets"]![0]!.AsObject(),
                "drawOrder" => v,
                "labelText" => v["wirePresentations"]![0]!.AsObject(),
                "rows" => v["annotations"]!.AsArray().Last()!.AsObject(),
                _ => root["parts"]![0]!["pins"]![0]!.AsObject()
            };
            node.Remove(field);
        });
        var bytes = await File.ReadAllBytesAsync(PathFor());
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PathFor()));
    }

    [Fact]
    public async Task MigrationDoesNotReplaceExplicitNullOrRepairMissingOldFields()
    {
        await store.SaveAsync(DemoFactory.CreatePerformance(2), PathFor());
        Rewrite(PathFor(), root => { root["schemaVersion"] = 3; root["assembly"]!["referencePoints"] = null; });
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
        await store.SaveAsync(DemoFactory.CreatePerformance(2), PathFor());
        Rewrite(PathFor(), root => { root["schemaVersion"] = 3; root["assembly"]!["conductors"]![0]!.AsObject().Remove("branchIds"); });
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }

    [Fact]
    public async Task BlocksHaveIndependentFormatAndEmbeddedAssetsAndRejectFutureVersions()
    {
        var asset = new ProjectAsset { FileName = "source.step", MediaType = "model/step", Data = [10, 20, 30] };
        var block = new HarnessProject { Assets = [asset], Parts = [new() { SourceAssetId = asset.Id }] };
        var blocks = new SelectionBlockStore(); var path = PathFor("selection.kabelaarblock");
        await blocks.SaveAsync(block, path);
        var loaded = await blocks.LoadAsync(path);
        Assert.Equal(JsonSerializer.Serialize(block), JsonSerializer.Serialize(loaded));
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        await store.SaveAsync(block, PathFor());
        await Assert.ThrowsAsync<ProjectFormatException>(() => blocks.LoadAsync(PathFor()));
        Rewrite(path, root => root["version"] = 2, "manifest.json");
        await Assert.ThrowsAsync<ProjectFormatException>(() => blocks.LoadAsync(path));
    }

    private static void RemoveSchemaFour(JsonObject root)
    {
        static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : [];
        static void Remove(JsonObject node, params string[] fields) { foreach (var field in fields) node.Remove(field); }
        var assembly = root["assembly"]!.AsObject(); Remove(assembly, "referencePoints");
        foreach (var connector in Objects(assembly["connectors"]))
        {
            Remove(connector, "matingPartId", "matingConnectorId");
            foreach (var cavity in Objects(connector["cavities"])) Remove(cavity, "function", "notes");
        }
        foreach (var wire in Objects(assembly["conductors"]))
        {
            Remove(wire, "netName", "shielding", "lengthNeedsConfirmation");
            Remove(wire["from"]!.AsObject(), "notes"); Remove(wire["to"]!.AsObject(), "notes");
        }
        foreach (var part in Objects(root["parts"])) foreach (var pin in Objects(part["pins"])) Remove(pin, "function");
        foreach (var sheet in Objects(root["sheets"]))
        {
            Remove(sheet, "titleBlock", "zoneColumns", "zoneRows");
            foreach (var view in Objects(sheet["views"]))
            {
                Remove(view, "drawOrder");
                foreach (var component in Objects(view["components"])) Remove(component, "mirrorX", "mirrorY");
                foreach (var presentation in Objects(view["wirePresentations"]))
                    Remove(presentation, "showLabel", "labelText", "labelPosition", "labelRotationDegrees", "showCrossingBridges", "crossingRadiusMm");
                foreach (var note in Objects(view["annotations"]))
                    Remove(note, "rotationDegrees", "arrowSizeMm", "textFrame", "fillColor", "tableHeaderColor", "rows", "columnWidthsMm",
                        "rowHeightMm", "cellPaddingMm", "showTableHeader", "cellAlignment", "columnAlignments", "nodeSymbol");
            }
        }
    }
    private static void Rewrite(string path, Action<JsonObject> change, string name = "project.json")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry(name)!;
        JsonObject root; using (var stream = entry.Open()) root = JsonNode.Parse(stream)!.AsObject();
        change(root); entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(root.ToJsonString(ProjectStore.JsonOptions));
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(directory); var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
}
