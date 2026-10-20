using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    /// <summary>
    /// Optional local acceptance on a user-supplied example. No customer path or data
    /// is embedded in the application, fixtures or repository.
    /// </summary>
    private async Task CheckExampleProjectAsync(string[] args, Action<bool, string> check)
    {
        var source = AcceptanceArguments.Value(args, "--example-project");
        if (source is null) return;
        var output = AcceptanceArguments.Value(args, "--example-output")
            ?? throw new ArgumentException("Een voorbeeldcontrole vereist een afzonderlijke --example-output map.");
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Gebruik een nieuwe lege map voor de voorbeeldcontrole.");
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var loaded = await _store.LoadAsync(source);
        var example = loaded.Project;
        var productionBefore = ProductionValidator.Validate(example).Where(i => i.Severity == Severity.Error)
            .Select(i => (i.Code, i.EntityId)).OrderBy(i => i.Code).ThenBy(i => i.EntityId).ToArray();
        var physicalBefore = JsonSerializer.Serialize(example.Assembly);
        var bomBefore = HarnessReports.Bom(example).ToArray();
        Directory.CreateDirectory(output);
        var copyPath = Path.Combine(output, "Voorbeeld-heropend.kabelaar");
        await _store.SaveAsync(example, copyPath);
        var reopened = (await _store.LoadAsync(copyPath)).Project;
        check(JsonSerializer.Serialize(reopened.Assembly) == physicalBefore &&
              bomBefore.SequenceEqual(HarnessReports.Bom(reopened)),
            "Lokaal voorbeeld migreert en heropent met dezelfde fysieke gegevens en stuklijst");
        check(productionBefore.SequenceEqual(ProductionValidator.Validate(reopened).Where(i => i.Severity == Severity.Error)
                .Select(i => (i.Code, i.EntityId)).OrderBy(i => i.Code).ThenBy(i => i.EntityId)),
            "Lokaal voorbeeld behoudt bestaande ontbrekende productiegegevens zonder verzonnen invulling");
        var unchanged = await ExportService.ExportSetAsync(reopened, Path.Combine(output, "heropend"), true);
        check(unchanged.Files.All(File.Exists), "Lokaal voorbeeld exporteert alle bladen en lijsten als concept");

        // Deliberately change the disposable copy, then prove all related reports follow it.
        var wire = reopened.Assembly.Conductors.First();
        var oldCutLength = HarnessReports.CutLength(reopened, wire);
        var oldWireId = wire.Id;
        var connector = reopened.Assembly.Connectors.First();
        var part = reopened.Parts.Single(p => p.Id == connector.PartId);
        var oldPartNumber = part.PartNumber;
        part.PartNumber += "-CONTROLE";
        wire.LengthSource = LengthSource.Manual;
        wire.ManualCutLengthMm = Math.Max(1, oldCutLength) + 17;
        wire.From = new Endpoint { Kind = EndpointKind.Free, Description = "Expliciet vrij controledoeleinde" };
        var changedPath = Path.Combine(output, "Voorbeeld-wijzigingscontrole.kabelaar");
        await _store.SaveAsync(reopened, changedPath);
        var changed = (await _store.LoadAsync(changedPath)).Project;
        var changedWire = changed.Assembly.Conductors.Single(w => w.Id == oldWireId);
        check(HarnessReports.CutLength(changed, changedWire) == Math.Max(1, oldCutLength) + 17 &&
              changedWire.From.Kind == EndpointKind.Free &&
              changed.Parts.Single(p => p.Id == part.Id).PartNumber == oldPartNumber + "-CONTROLE",
            "Onderdeel-, aansluitings- en lengtewijziging blijven in de lokale controlekopie bewaard");
        var changedExports = await ExportService.ExportSetAsync(changed, Path.Combine(output, "gewijzigd"), true);
        check(changedExports.Files.All(File.Exists) && changed.Assembly.Conductors.Count == example.Assembly.Conductors.Count,
            "Gewijzigde lokale controlekopie produceert opnieuw tekeningen en lijsten zonder extra aders");
        var finalSourceBytes = await File.ReadAllBytesAsync(source);
        check(originalHash.SequenceEqual(SHA256.HashData(finalSourceBytes)),
            "Het oorspronkelijke lokale voorbeeldbestand is byte voor byte ongewijzigd");
        await File.WriteAllTextAsync(Path.Combine(output, "voorbeeldcontrole.json"), JsonSerializer.Serialize(new
        {
            sourceSha256 = Convert.ToHexString(originalHash),
            schema = changed.SchemaVersion,
            physicalAssemblyPreservedOnMigration = true,
            sourceUnchanged = true,
            existingProductionErrors = productionBefore.Length,
            originalExports = unchanged.Files,
            changedExports = changedExports.Files,
            practicalUserAcceptance = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
