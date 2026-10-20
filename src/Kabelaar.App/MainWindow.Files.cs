using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Kabelaar.Rendering;
using Microsoft.Win32;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveAsync()) return;
        var project = new HarnessProject { Parts = EditData.Clone(_project.Parts), Assets = EditData.CloneAssets(_project.Assets) };
        ReplaceProject(project, null, null);
    }
    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveAsync()) return;
        ReplaceProject(DemoFactory.Create(), null, null);
        Status("Synthetisch voorbeeld — fictieve onderdelen, geen bedrijfsgegevens.");
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Kabelaar-project (*.kabelaar)|*.kabelaar", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true && await ConfirmLeaveAsync()) await OpenPathAsync(dialog.FileName);
    }
    private async Task OpenPathAsync(string path)
    {
        if (_path is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        { Status("Dit project is al geopend."); return; }
        await BusyAsync("Project openen…", async token =>
        {
            var lease = ProjectLease.TryAcquire(path);
            if (lease is null) throw new IOException("Dit project is al in gebruik. Sluit de andere bewerker of open een afzonderlijke kopie.");
            try
            {
                var loaded = await Task.Run(() => _store.LoadAsync(path, token), token);
                ReplaceProject(loaded.Project, path, loaded.Fingerprint, lease);
                lease = null;
            }
            finally { lease?.Dispose(); }
        });
        Canvas.Fit();
    }
    private void ReplaceProject(HarnessProject project, string? path, string? fingerprint, ProjectLease? lease = null)
    {
        _lease?.Dispose(); _lease = lease;
        _project = project; _path = path; _fingerprint = fingerprint;
        _sheet = project.Sheets[0]; _view = _sheet.Views[0];
        _recoveryEntry = null; _recoveredFrom = null; Canvas.Selection.Clear(); Canvas.Cancel();
        _history.Clear(); Refresh(); Canvas.Fit();
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_saving || _busy is not null) return false;
        var target = _path;
        if (saveAs || target is null)
        {
            var dialog = new SaveFileDialog { Filter = "Kabelaar-project (*.kabelaar)|*.kabelaar", DefaultExt = ".kabelaar",
                FileName = _path is not null ? Path.GetFileName(_path) : SafeFileName(_project.Title) + ".kabelaar" };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }
        _saving = true;
        while (_autosaving) await Task.Delay(20);
        ProjectLease? newLease = null;
        try
        {
            var samePath = string.Equals(target, _path, StringComparison.OrdinalIgnoreCase);
            if (!samePath || _lease is null)
                newLease = ProjectLease.Acquire(target);
            var state = _history.StateToken;
            var snapshot = EditData.Clone(_project);
            snapshot.ModifiedUtc = DateTimeOffset.UtcNow;
            var fingerprint = await Task.Run(() => _store.SaveAsync(snapshot, target, samePath ? _fingerprint : null));
            if (newLease is not null) { _lease?.Dispose(); _lease = newLease; newLease = null; }
            _path = target; _fingerprint = fingerprint;
            if (_history.StateToken == state)
            {
                _history.MarkSaved();
                ClearRecoveryCopies();
            }
            Status($"Opgeslagen: {Path.GetFileName(target)}"); Refresh(); return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, ex.Message + "\n\nJe huidige werk blijft geopend. Gebruik Opslaan als om een afzonderlijke versie te bewaren.",
                "Opslaan niet gelukt", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { newLease?.Dispose(); _saving = false; }
    }
    private async Task<bool> ConfirmLeaveAsync(Func<MessageBoxResult>? decision = null)
    {
        if (_saving || _busy is not null) return false;
        _suspendAutosave = true;
        try
        {
            while (_autosaving) await Task.Delay(20);
            if (!_history.IsDirty) { ClearRecoveryCopies(); return true; }
            var result = decision?.Invoke() ?? MessageBox.Show(this, "Wil je de wijzigingen opslaan voordat je dit project sluit?",
                "Niet-opgeslagen werk", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes) return await SaveAsync(false) && !_history.IsDirty;
            ClearRecoveryCopies();
            return true;
        }
        finally { _suspendAutosave = false; }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        if (!await ConfirmLeaveAsync()) return;
        _autosave.Stop(); _validationTimer.Stop(); _lease?.Dispose();
        _closing = true; Close();
    }
    private async Task AutoSaveAsync()
    {
        if (!_history.IsDirty || _saving || _autosaving || _suspendAutosave || _busy is not null) return;
        _autosaving = true;
        try
        {
            var snapshot = EditData.Clone(_project);
            var originalPath = _path;
            _recoveryEntry = await Task.Run(() => _recovery.SaveAsync(snapshot, originalPath, _sessionId));
            Status($"Herstelkopie bijgewerkt om {DateTime.Now:HH:mm}.");
        }
        catch (Exception ex) { App.Log(ex); Status("Herstelkopie mislukt: " + ex.Message); }
        finally { _autosaving = false; }
    }
    private async void Recovery_Click(object sender, RoutedEventArgs e) => await ShowRecoveryAsync();
    private void ClearRecoveryCopies()
    {
        if (_recoveryEntry is not null) _recovery.Delete(_recoveryEntry);
        if (_recoveredFrom is not null) _recovery.Delete(_recoveredFrom);
        _recoveryEntry = null; _recoveredFrom = null;
    }
    private async Task ShowRecoveryAsync()
    {
        var items = await _recovery.ListAsync();
        if (items.Count == 0) { MessageBox.Show(this, "Geen herstelbestanden gevonden.", "Herstel"); return; }
        var dialog = new FormDialog(this, "Niet-opgeslagen werk herstellen", 560);
        var choice = dialog.Form.Choice("Herstelbestand", items, items[0], x => $"{x.Title} — {x.SavedUtc.ToLocalTime():dd-MM-yyyy HH:mm}");
        dialog.Form.Note("Herstel opent een afzonderlijk document. Het oorspronkelijke bestand wordt niet overschreven.");
        if (dialog.ShowDialog() != true) return;
        var entry = PropertyForm.Value<RecoveryEntry>(choice);
        LoadedProject? restored = null;
        await BusyAsync("Herstelbestand openen…", async token =>
        {
            restored = await _recovery.LoadAsync(entry, token);
        });
        if (restored is null || !await ConfirmLeaveAsync()) return;
        ReplaceProject(restored.Project, null, null);
        _recoveredFrom = entry;
        _history.Execute(new DelegateCommand("Hersteld document", () => { }, () => { }));
        Canvas.Fit();
    }
    private async void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Kabelaar-sjabloon (*.kabelaartemplate)|*.kabelaartemplate", FileName = SafeFileName(_project.Title) };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = EditData.Clone(_project);
        await BusyAsync("Sjabloon bewaren…", async token => { await _libraries.ExportTemplateAsync(snapshot, dialog.FileName, token); });
    }
    private async void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Kabelaar-sjabloon (*.kabelaartemplate)|*.kabelaartemplate" };
        if (dialog.ShowDialog(this) != true || !await ConfirmLeaveAsync()) return;
        await BusyAsync("Nieuw uit sjabloon…", async token => ReplaceProject(await _libraries.CreateFromTemplateAsync(dialog.FileName, token), null, null));
        Canvas.Fit();
    }
    private async void ImportLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Kabelaar-bibliotheek (*.kabelaarlib)|*.kabelaarlib" };
        if (dialog.ShowDialog(this) != true) return;
        HarnessProject? incoming = null;
        await BusyAsync("Bibliotheek lezen…", async token =>
        {
            incoming = new();
            await _libraries.ImportAsync(dialog.FileName, incoming, token);
        });
        if (incoming is null || incoming.Parts.Count == 0) return;
        var parts = incoming.Parts; var assets = incoming.Assets;
        Execute(new DelegateCommand("Bibliotheek importeren",
            () => { _project.Parts.AddRange(parts); _project.Assets.AddRange(assets); },
            () => { foreach (var p in parts) _project.Parts.Remove(p); foreach (var a in assets) _project.Assets.Remove(a); }));
        await PersistLibraryAsync();
    }
    private async void ExportLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Kabelaar-bibliotheek (*.kabelaarlib)|*.kabelaarlib", FileName = "Onderdelen" };
        if (dialog.ShowDialog(this) != true) return;
        var parts = EditData.Clone(_project.Parts); var assets = EditData.CloneAssets(_project.Assets);
        await BusyAsync("Bibliotheek exporteren…", async token => { await _libraries.ExportAsync(parts, assets, dialog.FileName, token); });
    }
    private async Task PersistLibraryAsync()
    {
        // Capture the originating document before waiting: the user can open another project during disk I/O.
        var parts = EditData.Clone(_project.Parts); var assets = EditData.CloneAssets(_project.Assets);
        await _librarySaveGate.WaitAsync();
        try
        {
            await Task.Run(() => _libraries.MergeCatalogAsync(parts, assets, _userLibrary));
        }
        catch (Exception ex) { App.Log(ex); Status("Bibliotheek blijft in het project; lokaal bewaren lukte niet: " + ex.Message); }
        finally { _librarySaveGate.Release(); }
    }
    private static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(safe) ? "Kabelaar" : safe;
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "KABELAAR — BEDIENING\n\n" +
        "1. Definieer onderdelen of importeer een STEP-model. Leg de pinnummers vast.\n" +
        "2. Selecteer een connector in de bibliotheek en plaats hem op het blad.\n" +
        "3. Kies Ader: klik een pin, eventuele bochten en de eindpin. Enter of rechtsklik rondt af.\n" +
        "4. Vul aansluitingen, draadartikel, kleuren, maten en gemonteerde terminals in bij Eigenschappen.\n" +
        "5. Plaats maatlijnen en gegenereerde tabellen. Controleer het project vóór productie-export.\n\n" +
        "Muiswiel = zoomen bij cursor • Middenmuis / spatie+slepen = pannen\n" +
        "Ctrl+klik = meerdere objecten selecteren • Leeg gebied slepen = selectiekader\n" +
        "Esc = selecteren • W = ader • R = 90° draaien • Delete = verwijderen\n" +
        "Ctrl+Z/Y = ongedaan/opnieuw • Ctrl+D = dupliceren • Home = passend • F7 = controleren\n\n" +
        "OP SCHAAL: de route bepaalt fysieke lengte. VRIJE OPMAAK: vormgeving verandert geen productiematen.\n" +
        "Kniplengtes gebruiken expliciete toeslagen of een handmatige waarde. Een getekende twist is geen kniplengteberekening.\n\n" +
        "De aansluit- en stuklijsten komen uit dezelfde fysieke gegevens. Een detailaanzicht voegt geen materiaal toe.",
        "Bediening en werkwijze", MessageBoxButton.OK, MessageBoxImage.Information);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Kabelaar\nProductietekeningen voor kabelbomen\n\n" +
        $"Lokale acceptatieversie {typeof(App).Assembly.GetName().Version?.ToString(3)}\nC# / .NET 10 · WPF · SkiaSharp · Open CASCADE\n\n" +
        "Versie 1.0 wordt pas vrijgegeven na de volledige praktijkacceptatie.\n" +
        "Projecten en bibliotheken blijven lokaal. De app verzendt geen projectgegevens.",
        "Over Kabelaar", MessageBoxButton.OK, MessageBoxImage.Information);
}
