using System.IO.Compression;
using System.Text;
using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Infrastructure.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KabelaarTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectStore store = new();
    public PersistenceTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name = "test.kabelaar") => Path.Combine(directory, name);
    private static HarnessProject Project()
    {
        var asset = new ProjectAsset { FileName = "connector.step", Data = Encoding.UTF8.GetBytes("fixture bytes"), MediaType = "model/step" };
        var part = new PartDefinition { Name = "Housing", SourceAssetId = asset.Id };
        return new HarnessProject { Title = "Draagbaar", Assets = [asset], Parts = [part] };
    }
    [Fact] public async Task PortableArchivePreservesEmbeddedAssetsAndSnapshot()
    {
        var project = Project();
        var fingerprint = await store.SaveAsync(project, PathFor());
        var loaded = await store.LoadAsync(PathFor());
        Assert.Equal(fingerprint, loaded.Fingerprint);
        Assert.Equal(project.Assets[0].Data, loaded.Project.Assets[0].Data);
        Assert.Equal(project.Parts[0].SourceAssetId, loaded.Project.Parts[0].SourceAssetId);
        using var zip = ZipFile.OpenRead(PathFor());
        using var reader = new StreamReader(zip.GetEntry("project.json")!.Open());
        Assert.DoesNotContain("Zml4dHVyZSBieXRlcw==", await reader.ReadToEndAsync());
    }
    [Fact] public async Task SaveKeepsPreviousVersionAndChecksExternalChange()
    {
        var project = Project();
        var fingerprint = await store.SaveAsync(project, PathFor());
        project.Title = "Versie 2";
        var second = await store.SaveAsync(project, PathFor(), fingerprint);
        Assert.Equal("Draagbaar", (await store.LoadAsync(PathFor() + ".bak")).Project.Title);
        await store.SaveAsync(new HarnessProject { Title = "Extern" }, PathFor(), second);
        await Assert.ThrowsAsync<ProjectConflictException>(() => store.SaveAsync(project, PathFor(), second));
        Assert.Equal("Extern", (await store.LoadAsync(PathFor())).Project.Title);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public async Task FutureSchemaAndDuplicateEntriesAreRejected()
    {
        await store.SaveAsync(Project(), PathFor());
        using (var zip = ZipFile.Open(PathFor(), ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
            writer.Write(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999"));
        }
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
        using (var zip = ZipFile.Open(PathFor(), ZipArchiveMode.Update)) zip.CreateEntry("project.json");
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }
    [Fact] public async Task CorruptAssetAndDanglingReferenceAreRejected()
    {
        await store.SaveAsync(Project(), PathFor());
        using (var zip = ZipFile.Open(PathFor(), ZipArchiveMode.Update))
        {
            zip.GetEntry("assets/000000.bin")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("assets/000000.bin").Open());
            writer.Write("corrupt");
        }
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
        var project = Project();
        project.Parts[0].SourceAssetId = "missing";
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.SaveAsync(project, PathFor("missing.kabelaar")));
    }
    [Fact] public void LeaseBlocksConcurrentEditorAndReleasesAfterDispose()
    {
        using (var first = ProjectLease.Acquire(PathFor())) Assert.Null(ProjectLease.TryAcquire(PathFor()));
        using var second = ProjectLease.Acquire(PathFor());
        Assert.NotNull(second);
    }
    [Fact] public async Task RecoverySeparatesDocumentsSessionsAndIgnoresCorruptIndex()
    {
        var recovery = new RecoveryStore(directory);
        var project = Project();
        var first = await recovery.SaveAsync(project, null, "session1");
        project.Title = "Tweede sessie";
        var second = await recovery.SaveAsync(project, PathFor(), "session2");
        await File.WriteAllTextAsync(PathFor("bad.json"), "{");
        Assert.Equal(2, (await recovery.ListAsync()).Count);
        Assert.Equal("Draagbaar", (await recovery.LoadAsync(first)).Project.Title);
        Assert.Equal("Tweede sessie", (await recovery.LoadAsync(second)).Project.Title);
        recovery.Delete(first);
        Assert.Single(await recovery.ListAsync());
        Assert.Throws<ArgumentException>(() => recovery.Delete(first with { Id = "../escape" }));
    }
    [Fact] public async Task LibraryImportAddsNewRevisionWithoutChangingOldProjectDefinition()
    {
        var project = Project();
        project.Parts[0].Pins.Add(new() { Label = "1" });
        var oldId = project.Parts[0].Id;
        var libraries = new LibraryStore();
        await libraries.ExportAsync(project.Parts, project.Assets, PathFor("library.kabelaar"));
        var imported = await libraries.ImportAsync(PathFor("library.kabelaar"), project);
        Assert.Equal(2, project.Parts.Count);
        Assert.Equal(oldId, project.Parts[0].Id);
        Assert.NotEqual(oldId, imported[0].Id);
        Assert.NotEqual(project.Parts[0].Pins[0].Id, imported[0].Pins[0].Id);
        await store.SaveAsync(project, PathFor());
    }
    [Fact] public async Task CatalogMergePreservesUnrelatedDefinitionsAndStableIdentities()
    {
        var libraries = new LibraryStore();
        var first = Project();
        first.Parts[0].Pins.Add(new() { Label = "1" });
        var second = Project();
        second.Parts[0].Name = "Onderdeel uit ander project";
        var path = PathFor("catalog.kabelaarlib");
        await libraries.MergeCatalogAsync(first.Parts, first.Assets, path);
        await libraries.MergeCatalogAsync(second.Parts, second.Assets, path);
        // A new session reads the stable catalog IDs. Saving it again must not create duplicates.
        var reopened = (await store.LoadAsync(path)).Project;
        await libraries.MergeCatalogAsync(reopened.Parts, reopened.Assets, path);

        var saved = (await store.LoadAsync(path)).Project;
        Assert.Equal(2, saved.Parts.Count);
        Assert.Equal(2, saved.Assets.Count);
        Assert.Equal(first.Parts[0].Pins[0].Id, Assert.Single(saved.Parts, p => p.Id == first.Parts[0].Id).Pins[0].Id);
        Assert.Contains(saved.Parts, p => p.Id == second.Parts[0].Id && p.SourceAssetId == second.Assets[0].Id);
    }
    [Fact] public async Task CatalogRejectsChangedIdentityWithoutReplacingPriorData()
    {
        var libraries = new LibraryStore();
        var project = Project();
        var path = PathFor("catalog.kabelaarlib");
        await libraries.MergeCatalogAsync(project.Parts, project.Assets, path);
        var fingerprint = await ProjectStore.GetFingerprintAsync(path);
        project.Parts[0].Name = "Wijziging zonder nieuwe revisie";

        await Assert.ThrowsAsync<ProjectConflictException>(() => libraries.MergeCatalogAsync(project.Parts, project.Assets, path));
        Assert.Equal(fingerprint, await ProjectStore.GetFingerprintAsync(path));
        Assert.Equal("Housing", (await store.LoadAsync(path)).Project.Parts[0].Name);
    }
    [Fact] public async Task RecoveryArchiveRemainsDiscoverableAfterIndexWriteWasInterrupted()
    {
        var recovery = new RecoveryStore(directory);
        var saved = await recovery.SaveAsync(Project(), null, "session");
        File.Delete(PathFor(saved.Id + ".json"));
        var discovered = Assert.Single(await recovery.ListAsync());
        Assert.Equal("Draagbaar", (await recovery.LoadAsync(discovered)).Project.Title);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    public async Task IncompleteRecoveryIndexCannotHideAnyIntactArchive(string invalidIndex)
    {
        var recovery = new RecoveryStore(directory);
        var first = await recovery.SaveAsync(Project(), null, "session1");
        var second = await recovery.SaveAsync(new HarnessProject { Title = "Ander document" }, null, "session2");
        await File.WriteAllTextAsync(PathFor(first.Id + ".json"), invalidIndex);

        var entries = await recovery.ListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Draagbaar", (await recovery.LoadAsync(Assert.Single(entries, e => e.Id == first.Id))).Project.Title);
        Assert.Equal("Ander document", (await recovery.LoadAsync(Assert.Single(entries, e => e.Id == second.Id))).Project.Title);
    }
    [Fact] public async Task CancelledSaveDoesNotOverwriteExistingProject()
    {
        var project = Project();
        var fingerprint = await store.SaveAsync(project, PathFor());
        project.Title = "Dit mag niet worden opgeslagen";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(project, PathFor(), fingerprint, cancelled.Token));
        Assert.Equal(fingerprint, (await store.LoadAsync(PathFor())).Fingerprint);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public async Task MissingStoredFieldsCannotSilentlyBecomeEditorDefaults()
    {
        await store.SaveAsync(Project(), PathFor());
        using (var zip = ZipFile.Open(PathFor(), ZipArchiveMode.Update))
        {
            zip.GetEntry("project.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
            writer.Write("{}");
        }
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(PathFor()));
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KabelaarTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
}
