using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class PresentationMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectStore store = new();
    public PresentationMigrationTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name = "project.kabelaar") => Path.Combine(directory, name);

    [Fact]
    public async Task SchemaOneMigratesWithLegacyAppearanceAndLeavesTheOriginalArchiveUntouched()
    {
        await SaveLegacy();
        var original = await File.ReadAllBytesAsync(PathFor());
        var loaded = (await store.LoadAsync(PathFor())).Project;
        Assert.Equal(2, loaded.SchemaVersion);
        var view = loaded.Sheets[0].Views[0];
        Assert.True(view.Style.ShowWireLabels);
        Assert.True(view.Style.ShowTwistLabels);
        Assert.Equal(.45, view.Style.WireLineWidthMm);
        Assert.Equal(2.2, view.Style.WireLabelFontSizeMm);
        Assert.Equal(.3, view.Style.ConnectorLineWidthMm);
        Assert.Equal(1.1, view.Style.TwistAmplitudeMm);
        Assert.Equal(0, view.Style.TwistDisplayPitchMm);
        Assert.Equal(TwistDisplayMode.Full, view.Style.TwistDisplayMode);
        Assert.All(view.Components, c =>
        {
            Assert.Equal(1, c.DisplayScale);
            Assert.True(c.ShowPins && c.ShowPinNumbers && c.ShowLabel && c.ShowPartNumber);
            Assert.Equal(3, c.LabelFontSizeMm);
        });
        Assert.Equal(original, await File.ReadAllBytesAsync(PathFor()));
        await store.SaveAsync(loaded, PathFor("upgraded.kabelaar"));
        Assert.Equal(2, (await store.LoadAsync(PathFor("upgraded.kabelaar"))).Project.SchemaVersion);
    }

    [Fact]
    public async Task SchemaOneSpliceLabelsRetainTheirSmallerLegacyFont()
    {
        await SaveLegacy();
        var splice = new Splice { Name = "SP1" };
        var placement = new PlacedComponent { EntityId = splice.Id, Position = new(50, 50) };
        Rewrite(root =>
        {
            root["assembly"]!["splices"]!.AsArray().Add(JsonSerializer.SerializeToNode(splice, ProjectStore.JsonOptions));
            var oldPlacement = JsonSerializer.SerializeToNode(placement, ProjectStore.JsonOptions)!.AsObject();
            foreach (var name in new[] { "displayScale", "showPins", "showPinNumbers", "showLabel", "showPartNumber", "labelFontSizeMm" })
                oldPlacement.Remove(name);
            root["sheets"]![0]!["views"]![0]!["components"]!.AsArray().Add(oldPlacement);
        });
        var loaded = (await store.LoadAsync(PathFor())).Project;
        Assert.Equal(2.5, loaded.Sheets[0].Views[0].Components.Single(p => p.EntityId == splice.Id).LabelFontSizeMm);
        Assert.All(loaded.Sheets[0].Views[0].Components.Where(p => p.EntityId != splice.Id), p => Assert.Equal(3, p.LabelFontSizeMm));
    }

    [Fact]
    public async Task OversizedFiniteStoredPresentationIsRejectedOnLoad()
    {
        await store.SaveAsync(Fixture(), PathFor());
        Rewrite(root => root["sheets"]![0]!["views"]![0]!["style"]!["wireLabelFontSizeMm"] = double.MaxValue);
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }

    [Fact]
    public async Task ExplicitPresentationRoundTripsWithEmbeddedLibraryAndStableConnections()
    {
        var project = Fixture();
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        view.Style = new() { TwistDisplayMode = TwistDisplayMode.Markers, TwistDisplayPitchMm = 6, ShowWireLabels = false };
        var component = view.Components[0];
        component.DisplayScale = .5;
        component.ShowPins = false;
        component.ShowPinNumbers = false;
        component.ShowLabel = false;
        component.ShowPartNumber = false;
        component.LabelFontSizeMm = 2.5;
        await store.SaveAsync(project, PathFor());
        var loaded = (await store.LoadAsync(PathFor())).Project;
        Assert.Equal(JsonSerializer.Serialize(project, ProjectStore.JsonOptions), JsonSerializer.Serialize(loaded, ProjectStore.JsonOptions));
    }

    [Theory]
    [InlineData("old-route-points")]
    [InlineData("old-component-position")]
    [InlineData("explicit-null-style")]
    [InlineData("explicit-null-scale")]
    [InlineData("partial-style")]
    public async Task MigrationNeverRepairsDamagedFields(string corruption)
    {
        await SaveLegacy();
        Rewrite(root =>
        {
            var view = root["sheets"]![0]!["views"]![0]!.AsObject();
            switch (corruption)
            {
                case "old-route-points": view["routes"]![0]!.AsObject().Remove("points"); break;
                case "old-component-position": view["components"]![0]!.AsObject().Remove("position"); break;
                case "explicit-null-style": view["style"] = null; break;
                case "explicit-null-scale": view["components"]![0]!["displayScale"] = null; break;
                case "partial-style": view["style"] = new JsonObject { ["showWireLabels"] = true }; break;
            }
        });
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }

    [Theory]
    [InlineData("style")]
    [InlineData("displayScale")]
    [InlineData("showPartNumber")]
    [InlineData("twistDisplayMode")]
    public async Task SchemaTwoStillRequiresEveryStoredPresentationField(string missing)
    {
        await store.SaveAsync(Fixture(), PathFor());
        Rewrite(root =>
        {
            var view = root["sheets"]![0]!["views"]![0]!.AsObject();
            if (missing == "style") view.Remove(missing);
            else if (missing == "twistDisplayMode") view["style"]!.AsObject().Remove(missing);
            else view["components"]![0]!.AsObject().Remove(missing);
        });
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }

    [Fact]
    public async Task LibrariesAndTemplatesUseTheSameLegacyMigrationAndStrictValidation()
    {
        await SaveLegacy();
        var libraries = new LibraryStore();
        var target = new HarnessProject();
        Assert.NotEmpty(await libraries.ImportAsync(PathFor(), target));
        var template = await libraries.CreateFromTemplateAsync(PathFor());
        Assert.Equal(2, template.SchemaVersion);
        Assert.Equal(.45, template.Sheets[0].Views[0].Style.WireLineWidthMm);

        Rewrite(root => root["parts"]![0]!.AsObject().Remove("pins"));
        await Assert.ThrowsAsync<ProjectFormatException>(() => libraries.ImportAsync(PathFor(), new HarnessProject()));
        await Assert.ThrowsAsync<ProjectFormatException>(() => libraries.CreateFromTemplateAsync(PathFor()));
    }

    private async Task SaveLegacy()
    {
        await store.SaveAsync(Fixture(), PathFor());
        Rewrite(root =>
        {
            root["schemaVersion"] = 1;
            foreach (var sheet in root["sheets"]!.AsArray())
                foreach (var view in sheet!["views"]!.AsArray())
                {
                    view!.AsObject().Remove("style");
                    foreach (var component in view["components"]!.AsArray())
                        foreach (var name in new[] { "displayScale", "showPins", "showPinNumbers", "showLabel", "showPartNumber", "labelFontSizeMm" })
                            component!.AsObject().Remove(name);
                }
        });
    }
    private void Rewrite(Action<JsonObject> edit)
    {
        using var zip = ZipFile.Open(PathFor(), ZipArchiveMode.Update);
        var entry = zip.GetEntry("project.json")!;
        JsonObject root;
        using (var stream = entry.Open()) root = JsonNode.Parse(stream)!.AsObject();
        edit(root);
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
        writer.Write(root.ToJsonString(ProjectStore.JsonOptions));
    }
    private static HarnessProject Fixture() => DemoFactory.CreatePerformance(2);
    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
}
