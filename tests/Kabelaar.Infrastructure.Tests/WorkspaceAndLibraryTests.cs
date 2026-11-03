using System.Text.Json;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class WorkspaceAndLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    public WorkspaceAndLibraryTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name) => Path.Combine(directory, name);

    [Fact]
    public async Task DeletedCatalogPartStaysHiddenAfterProjectMergeAndCanBeRestoredWithoutLoss()
    {
        var asset = new ProjectAsset { FileName = "part.step", MediaType = "model/step", Data = [1, 3, 7] };
        var part = new PartDefinition { Name = "Housing", SourceAssetId = asset.Id };
        var project = new HarnessProject { Parts = [part], Assets = [asset] };
        var snapshot = JsonSerializer.Serialize(project);
        var libraries = new LibraryStore(); var path = PathFor("catalog.kabelaarlib");
        await libraries.MergeCatalogAsync(project.Parts, project.Assets, path);
        await libraries.SetDeletedAsync(path, part.Id, true);
        Assert.Contains(part.Id, libraries.GetDeletedPartIds(path));
        await libraries.MergeCatalogAsync(project.Parts, project.Assets, path);
        Assert.Contains(part.Id, new LibraryStore().GetDeletedPartIds(path));
        var loaded = (await new ProjectStore().LoadAsync(path)).Project;
        Assert.Single(loaded.Parts); Assert.Equal(asset.Data, loaded.Assets.Single().Data);
        Assert.Equal(snapshot, JsonSerializer.Serialize(project));
        await libraries.SetDeletedAsync(path, part.Id, false);
        Assert.Empty(libraries.GetDeletedPartIds(path));
        Assert.Equal(part.Id, (await new ProjectStore().LoadAsync(path)).Project.Parts.Single().Id);
    }

    [Fact]
    public async Task WorkspacePreferencesSurviveRestartAndInvalidChangeDoesNotOverwritePreviousFile()
    {
        var store = new WorkspacePreferencesStore(); var path = PathFor("workspace.json");
        var preferences = new WorkspacePreferences { DarkMode = true, ShowLibrary = false, UiScale = 1.25, WireColor = "#AABBCC", LeaderArrowSizeMm = 4 };
        await store.SaveAsync(preferences, path);
        Assert.Equal(JsonSerializer.Serialize(preferences), JsonSerializer.Serialize(new WorkspacePreferencesStore().Load(path)));
        var bytes = await File.ReadAllBytesAsync(path);
        preferences.UiScale = double.NaN;
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.SaveAsync(preferences, path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DamagedOrFutureLocalStateIsReportedWithoutBeingReplaced()
    {
        var path = PathFor("workspace.json"); var bytes = "{\"version\":99}"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Throws<ProjectFormatException>(() => new WorkspacePreferencesStore().Load(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var catalog = PathFor("catalog.kabelaarlib");
        await File.WriteAllTextAsync(catalog + ".deleted.json", "{\"version\":1}");
        Assert.Throws<ProjectFormatException>(() => new LibraryStore().GetDeletedPartIds(catalog));
    }

    [Fact]
    public async Task CancelledPreferencesSaveKeepsOriginalAndNoTemporaryFile()
    {
        var path = PathFor("workspace.json"); var store = new WorkspacePreferencesStore();
        await store.SaveAsync(new(), path); var bytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new() { DarkMode = true }, path, cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory); var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
}
