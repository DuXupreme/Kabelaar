using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class CurveMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectStore store = new();
    public CurveMigrationTests() => Directory.CreateDirectory(directory);
    private string FilePath => Path.Combine(directory, "curves.kabelaar");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderSchemaKeepsExactLegacySamplesAndAllPhysicalData(int version)
    {
        var original = DemoFactory.CreatePerformance(2);
        await store.SaveAsync(original, FilePath);
        Rewrite(root =>
        {
            root["schemaVersion"] = version;
            foreach (var branch in root["assembly"]!["branches"]!.AsArray()) branch!.AsObject().Remove("geometry");
            foreach (var sheet in root["sheets"]!.AsArray())
                foreach (var viewNode in sheet!["views"]!.AsArray())
                {
                    var view = viewNode!.AsObject();
                    view.Remove("wirePresentations");
                    foreach (var route in view["routes"]!.AsArray()) route!.AsObject().Remove("geometry");
                    if (version != 1) continue;
                    view.Remove("style");
                    foreach (var component in view["components"]!.AsArray())
                        foreach (var field in new[] { "displayScale", "showPins", "showPinNumbers", "showLabel", "showPartNumber", "labelFontSizeMm" })
                            component!.AsObject().Remove(field);
                }
        });
        var originalBytes = await File.ReadAllBytesAsync(FilePath);
        var loaded = (await store.LoadAsync(FilePath)).Project;
        Assert.Equal(HarnessProject.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(original.Assembly), JsonSerializer.Serialize(loaded.Assembly));
        var actualView = loaded.Sheets[0].Views[0];
        Assert.Empty(actualView.WirePresentations);
        for (var i = 0; i < actualView.Routes.Count; i++)
        {
            Assert.Null(actualView.Routes[i].Geometry);
            Assert.Equal(Geometry.Flatten(original.Sheets[0].Views[0].Routes[i]), Geometry.Flatten(actualView.Routes[i]));
        }
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(FilePath));
    }

    [Fact]
    public async Task CurvesHandleModesMixedSegmentsAndIndividualOverridesRoundTripWithoutIdentityChanges()
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0]; view.Mode = LayoutMode.Free;
        var wire = project.Assembly.Conductors[0]; var route = view.Routes[0];
        var geometry = Geometry.EditableRouteGeometry(view, route, wire.Id);
        geometry = Geometry.WithCurveKind(geometry, RouteSegmentKind.Line, segmentIndex: 0);
        geometry.Knots[1].TangentMode = TangentMode.Independent;
        var history = new CommandHistory();
        history.Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, route, geometry));
        history.Execute(HarnessOperations.SetWireLineWidth(view, wire.Id, 1.2));
        await store.SaveAsync(project, FilePath);
        var loaded = (await store.LoadAsync(FilePath)).Project;
        Assert.Equal(JsonSerializer.Serialize(project), JsonSerializer.Serialize(loaded));
        var template = await new LibraryStore().CreateFromTemplateAsync(FilePath);
        Assert.Equal(JsonSerializer.Serialize(view.WirePresentations), JsonSerializer.Serialize(template.Sheets[0].Views[0].WirePresentations));
    }

    [Theory]
    [InlineData("wirePresentations")]
    [InlineData("geometry")]
    [InlineData("null-presentations")]
    [InlineData("missing-handle")]
    [InlineData("invalid-knot-reference")]
    [InlineData("duplicate-knot")]
    [InlineData("unknown-wire")]
    public async Task SchemaThreeRejectsMissingOrDamagedCurveFieldsWithoutReplacingAnyFile(string corruption)
    {
        var project = DemoFactory.CreatePerformance(2);
        var view = project.Sheets[0].Views[0]; view.Mode = LayoutMode.Free;
        var wire = project.Assembly.Conductors[0];
        new CommandHistory().Execute(HarnessOperations.SetWireRouteGeometry(project, view, wire, view.Routes[0],
            Geometry.EditableRouteGeometry(view, view.Routes[0], wire.Id)));
        await store.SaveAsync(project, FilePath);
        Rewrite(root =>
        {
            var v = root["sheets"]![0]!["views"]![0]!.AsObject();
            var presentation = v["wirePresentations"]![0]!.AsObject();
            var geometry = presentation["routes"]![0]!["geometry"]!.AsObject();
            switch (corruption)
            {
                case "wirePresentations": v.Remove("wirePresentations"); break;
                case "geometry": v["routes"]![0]!.AsObject().Remove("geometry"); break;
                case "null-presentations": v["wirePresentations"] = null; break;
                case "missing-handle": geometry["knots"]![0]!.AsObject().Remove("outHandle"); break;
                case "invalid-knot-reference": geometry["segments"]![0]!["endKnotId"] = "missing"; break;
                case "duplicate-knot": geometry["knots"]![1]!["id"] = geometry["knots"]![0]!["id"]!.GetValue<string>(); break;
                case "unknown-wire": presentation["conductorId"] = "missing"; break;
            }
        });
        var bytes = await File.ReadAllBytesAsync(FilePath);
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(FilePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(FilePath));
    }

    [Fact]
    public async Task SchemaTwoMigrationDoesNotRepairMissingSchemaTwoFields()
    {
        await store.SaveAsync(DemoFactory.CreatePerformance(2), FilePath);
        Rewrite(root =>
        {
            root["schemaVersion"] = 2;
            root["sheets"]![0]!["views"]![0]!.AsObject().Remove("style");
        });
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(FilePath));
    }

    private void Rewrite(Action<JsonObject> change)
    {
        using var zip = ZipFile.Open(FilePath, ZipArchiveMode.Update);
        var entry = zip.GetEntry("project.json")!;
        JsonObject root;
        using (var stream = entry.Open()) root = JsonNode.Parse(stream)!.AsObject();
        change(root); entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
        writer.Write(root.ToJsonString(ProjectStore.JsonOptions));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
}
