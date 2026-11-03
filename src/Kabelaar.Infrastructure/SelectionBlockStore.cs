using Kabelaar.Core;

namespace Kabelaar.Infrastructure;

/// <summary>A portable selection with its own format/version; never mistaken for a whole project.</summary>
public sealed class SelectionBlockStore
{
    private readonly ProjectStore store = new();
    public Task<string> SaveAsync(HarnessProject block, string path, CancellationToken cancellationToken = default)
    {
        Validate(block);
        return store.SavePackageAsync(block, path, "Kabelaar.SelectionBlock", cancellationToken: cancellationToken);
    }
    public async Task<HarnessProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var block = (await store.LoadPackageAsync(path, "Kabelaar.SelectionBlock", cancellationToken)).Project;
        Validate(block);
        return block;
    }
    private static void Validate(HarnessProject block)
    {
        ProjectStore.ValidateStructure(block);
        if (block.Sheets.Count != 1 || block.Sheets[0].Views.Count != 1)
            throw new ProjectFormatException("Een selectieblok moet precies één blad met één aanzicht bevatten.");
    }
}
