namespace Kabelaar.Infrastructure;

/// <summary>Cooperating editor instances hold a sidecar open for their entire editing session.</summary>
public sealed class ProjectLease : IDisposable
{
    private readonly FileStream stream;
    public string Path { get; }
    private ProjectLease(string path, FileStream stream) { Path = path; this.stream = stream; }
    public static ProjectLease? TryAcquire(string projectPath)
    {
        var path = System.IO.Path.GetFullPath(projectPath) + ".lock";
        try { return new(path, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public static ProjectLease Acquire(string projectPath) => TryAcquire(projectPath)
        ?? throw new IOException("Dit project is al geopend voor bewerken, of de map is niet schrijfbaar. Open een kopie.");
    public void Dispose() => stream.Dispose();
}
