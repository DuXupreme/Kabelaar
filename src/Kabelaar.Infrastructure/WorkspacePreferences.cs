using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kabelaar.Infrastructure;

public sealed class WorkspacePreferences
{
    public int Version { get; set; } = 1;
    public bool ShowLibrary { get; set; } = true;
    public bool ShowProperties { get; set; } = true;
    public bool ShowTables { get; set; } = true;
    public bool DarkMode { get; set; }
    public double UiScale { get; set; } = 1;
    public string WireColor { get; set; } = "#202D3A";
    public double WireLineWidthMm { get; set; } = .45;
    public string AnnotationColor { get; set; } = "#243244";
    public double AnnotationFontSizeMm { get; set; } = 3;
    public double AnnotationLineWidthMm { get; set; } = .25;
    public double LeaderArrowSizeMm { get; set; } = 2;

    public void Validate()
    {
        static bool Positive(double value, double max) => double.IsFinite(value) && value > 0 && value <= max;
        static bool Color(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
        if (Version != 1 || !double.IsFinite(UiScale) || UiScale is < .75 or > 2 ||
            !Color(WireColor) || !Color(AnnotationColor) || !Positive(WireLineWidthMm, 20) ||
            !Positive(AnnotationFontSizeMm, 100) || !Positive(AnnotationLineWidthMm, 20) || !Positive(LeaderArrowSizeMm, 50))
            throw new ProjectFormatException("De werkruimtevoorkeuren bevatten een onbekende versie of ongeldige instellingen.");
    }
}

public sealed class WorkspacePreferencesStore
{
    public WorkspacePreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var preferences = LocalStateFile.Read<WorkspacePreferences>(path);
        preferences.Validate();
        return preferences;
    }
    public Task SaveAsync(WorkspacePreferences preferences, string path, CancellationToken cancellationToken = default)
    {
        preferences.Validate();
        return LocalStateFile.WriteAsync(path, preferences, cancellationToken);
    }
}

internal static class LocalStateFile
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            foreach (var property in info.Properties.Where(p => p.Set is not null)) property.IsRequired = true;
        });
        return new(ProjectStore.JsonOptions) { TypeInfoResolver = resolver, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    }
    public static T Read<T>(string path) where T : class
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ProjectFormatException("Het instellingenbestand is te groot.");
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) ??
                throw new ProjectFormatException("Het instellingenbestand is leeg.");
        }
        catch (JsonException error) { throw new ProjectFormatException("Het instellingenbestand is beschadigd; het origineel is behouden.", error); }
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true); }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
