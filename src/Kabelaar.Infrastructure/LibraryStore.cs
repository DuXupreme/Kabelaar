using System.Text.Json;
using Kabelaar.Core;

namespace Kabelaar.Infrastructure;

/// <summary>Library and template packages reuse the portable, validated project container.</summary>
public sealed class LibraryStore
{
    private readonly ProjectStore store = new();
    /// <summary>Adds immutable definitions to the user's catalog without replacing unrelated project libraries.</summary>
    public async Task MergeCatalogAsync(IEnumerable<PartDefinition> parts, IEnumerable<ProjectAsset> assets, string path,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = ProjectLease.Acquire(path);
        var loaded = File.Exists(path) ? await store.LoadAsync(path, cancellationToken) : null;
        var catalog = loaded?.Project ?? new HarnessProject { Title = "Onderdelenbibliotheek" };
        var knownParts = catalog.Parts.ToDictionary(p => p.Id);
        var neededAssets = new HashSet<string>();
        foreach (var part in parts)
        {
            if (part.SourceAssetId is not null) neededAssets.Add(part.SourceAssetId);
            if (knownParts.TryGetValue(part.Id, out var previous))
            {
                if (JsonSerializer.Serialize(previous, ProjectStore.JsonOptions) != JsonSerializer.Serialize(part, ProjectStore.JsonOptions))
                    throw new ProjectConflictException($"Onderdeel {part.Name} heeft dezelfde identiteit als een andere catalogusdefinitie. Bewaar een nieuwe revisie.");
            }
            else { catalog.Parts.Add(part); knownParts.Add(part.Id, part); }
        }
        var knownAssets = catalog.Assets.ToDictionary(a => a.Id);
        foreach (var asset in assets.Where(a => neededAssets.Contains(a.Id)))
        {
            if (knownAssets.TryGetValue(asset.Id, out var previous))
            {
                if (previous.FileName != asset.FileName || previous.MediaType != asset.MediaType ||
                    !previous.Data.AsSpan().SequenceEqual(asset.Data))
                    throw new ProjectConflictException($"Bronbestand {asset.FileName} verschilt van de bestaande catalogusdefinitie.");
            }
            else { catalog.Assets.Add(asset); knownAssets.Add(asset.Id, asset); }
        }
        await store.SaveAsync(catalog, path, loaded?.Fingerprint, cancellationToken);
    }
    public Task<string> ExportAsync(IEnumerable<PartDefinition> parts, IEnumerable<ProjectAsset> assets, string path,
        CancellationToken cancellationToken = default)
    {
        var package = new HarnessProject { Title = "Onderdelenbibliotheek", Parts = parts.ToList(), Assets = assets.ToList() };
        return store.SaveAsync(package, path, cancellationToken: cancellationToken);
    }
    public async Task<IReadOnlyList<PartDefinition>> ImportAsync(string path, HarnessProject target,
        CancellationToken cancellationToken = default)
    {
        var incoming = (await store.LoadAsync(path, cancellationToken)).Project;
        // No replacement of definitions used by old instances. Every imported revision gets a new identity.
        var partMap = incoming.Parts.ToDictionary(p => p.Id, _ => Ids.New());
        var assetMap = incoming.Assets.ToDictionary(a => a.Id, _ => Ids.New());
        foreach (var asset in incoming.Assets) { asset.Id = assetMap[asset.Id]; target.Assets.Add(asset); }
        foreach (var part in incoming.Parts)
        {
            part.Id = partMap[part.Id];
            foreach (var pin in part.Pins) pin.Id = Ids.New();
            if (part.SourceAssetId is not null) part.SourceAssetId = assetMap[part.SourceAssetId];
            target.Parts.Add(part);
        }
        return incoming.Parts;
    }
    public Task<string> ExportTemplateAsync(HarnessProject project, string path, CancellationToken cancellationToken = default) =>
        store.SaveAsync(project, path, cancellationToken: cancellationToken);
    public async Task<HarnessProject> CreateFromTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        var project = (await store.LoadAsync(path, cancellationToken)).Project;
        project.Id = Ids.New();
        project.DrawingNumber = "";
        project.Revision = "A";
        project.Revisions.Clear();
        project.ModifiedUtc = DateTimeOffset.UtcNow;
        return project;
    }
}
