using System.Text.Json;
using Kabelaar.Core;

namespace Kabelaar.Infrastructure;

public sealed record RecoveryEntry(string Id, string ProjectId, string Title, string? OriginalPath, string SessionId,
    DateTimeOffset SavedUtc, string ArchivePath);

public sealed class RecoveryStore
{
    private readonly string directory;
    private readonly ProjectStore projects = new();
    public RecoveryStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kabelaar.UserData", "Recovery");
    }
    public async Task<RecoveryEntry> SaveAsync(HarnessProject project, string? originalPath, string sessionId,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        // Hash untrusted identifiers so neither project data nor session IDs can escape this directory.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(project.Id + ":" + sessionId)));
        var archive = Path.Combine(directory, key + ".kabelaar");
        await projects.SaveAsync(project, archive, cancellationToken: cancellationToken);
        var entry = new RecoveryEntry(key, project.Id, project.Title, originalPath, sessionId, DateTimeOffset.UtcNow, archive);
        var temporary = Path.Combine(directory, key + ".json.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry, ProjectStore.JsonOptions), cancellationToken);
        File.Move(temporary, Path.Combine(directory, key + ".json"), true);
        return entry;
    }
    public async Task<IReadOnlyList<RecoveryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RecoveryEntry>();
        if (!Directory.Exists(directory)) return results;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = JsonSerializer.Deserialize<RecoveryEntry>(await File.ReadAllTextAsync(path, cancellationToken), ProjectStore.JsonOptions);
                if (entry is not null && ValidKey(entry.Id))
                {
                    var archive = Path.Combine(directory, entry.Id + ".kabelaar");
                    if (File.Exists(archive)) results.Add(entry with { ArchivePath = archive });
                }
            }
            catch (JsonException) { /* One corrupt index must not hide other recovered projects. */ }
            catch (IOException) { }
        }
        // A crash between writing the archive and its small index must not hide otherwise intact work.
        foreach (var archive in Directory.EnumerateFiles(directory, "*.kabelaar"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(archive);
            if (!ValidKey(key) || results.Any(r => r.Id == key)) continue;
            try
            {
                var project = (await projects.LoadAsync(archive, cancellationToken)).Project;
                results.Add(new(key, project.Id, project.Title, null, "", File.GetLastWriteTimeUtc(archive), archive));
            }
            catch (IOException) { }
        }
        return results.OrderByDescending(r => r.SavedUtc).ToArray();
    }
    public Task<LoadedProject> LoadAsync(RecoveryEntry entry, CancellationToken cancellationToken = default) =>
        projects.LoadAsync(OwnedPath(entry.Id, ".kabelaar"), cancellationToken);
    public void Delete(RecoveryEntry entry)
    {
        File.Delete(OwnedPath(entry.Id, ".json"));
        File.Delete(OwnedPath(entry.Id, ".kabelaar"));
        File.Delete(OwnedPath(entry.Id, ".kabelaar.bak"));
    }
    private static bool ValidKey(string? key) => key is { Length: 64 } && key.All(Uri.IsHexDigit);
    private string OwnedPath(string key, string extension)
    {
        if (!ValidKey(key)) throw new ArgumentException("Ongeldig herstel-ID.");
        return Path.Combine(directory, key + extension);
    }
}
