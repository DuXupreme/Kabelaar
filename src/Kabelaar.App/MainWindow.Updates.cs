using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async void LocalUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _busy is not null) return;
        var folder = new OpenFolderDialog
        {
            Title = "Kies de lokale releasemap met releases.win.json en het volledige .nupkg-pakket",
            Multiselect = false
        };
        if (folder.ShowDialog(this) != true) return;
        try
        {
            // A filesystem source is explicit: this path never becomes an HTTP update feed.
            var updates = new UpdateManager(new SimpleFileSource(new DirectoryInfo(folder.FolderName)));
            if (!updates.IsInstalled || updates.IsPortable)
            {
                MessageBox.Show(this,
                    "Lokale updates werken in de geïnstalleerde versie. Sluit de portable versie en pak de nieuwe portable ZIP uit in een nieuwe map.",
                    "Lokale update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!CanRestartForUpdate()) return;
            UpdateInfo? candidate = null;
            bool checkedFeed = false;
            await BusyAsync("Lokale releasemap lezen…", async token =>
            {
                candidate = await updates.CheckForUpdatesAsync().WaitAsync(token);
                token.ThrowIfCancellationRequested();
                checkedFeed = true;
            });
            if (!checkedFeed) return;
            if (candidate is null)
            {
                MessageBox.Show(this, $"Geen nieuwere versie gevonden dan {updates.CurrentVersion}.",
                    "Lokale update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!string.Equals(candidate.TargetFullRelease.PackageId, "Kabelaar", StringComparison.Ordinal))
                throw new IOException("Dit updatepakket hoort niet bij Kabelaar.");
            if (MessageBox.Show(this,
                $"Kabelaar {updates.CurrentVersion} → {candidate.TargetFullRelease.Version}\n\n" +
                "Wil je deze lokale update voorbereiden? Vóór het herstarten kun je je werk opslaan of de herstart annuleren.",
                "Lokale update voorbereiden", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool downloaded = false;
            await BusyAsync("Lokaal updatepakket voorbereiden…", async token =>
            {
                await updates.DownloadUpdatesAsync(candidate, percent => Dispatcher.BeginInvoke(() =>
                {
                    if (_busy is not null) BusyText.Text = $"Lokaal updatepakket voorbereiden… {percent}%";
                }), token);
                downloaded = true;
            });
            if (!downloaded) return;
            if (MessageBox.Show(this,
                $"Versie {candidate.TargetFullRelease.Version} staat klaar. Wil je Kabelaar nu herstarten?\n\n" +
                "Als je wijzigingen hebt, krijg je eerst de keuze om die op te slaan.",
                "Update toepassen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Status("Update voorbereid. Herstart uitgesteld.");
                return;
            }
            if (!CanRestartForUpdate()) return;
            if (!await ConfirmLeaveAsync())
            {
                Status("Update voorbereid. Je werk blijft open; er is geen herstart uitgevoerd.");
                return;
            }
            if (!CanRestartForUpdate()) return;
            _closing = true;
            _autosave.Stop();
            _lease?.Dispose();
            _lease = null;
            try
            {
                updates.ApplyUpdatesAndRestart(candidate.TargetFullRelease, _path is null ? [] : [_path]);
            }
            catch
            {
                _closing = false;
                _autosave.Start();
                if (_path is not null)
                {
                    _lease = ProjectLease.TryAcquire(_path);
                    if (_lease is null)
                    {
                        // An external editor obtained the lease meanwhile. Keep this document as an unsaved copy.
                        _path = null;
                        _fingerprint = null;
                        _history.Execute(new DelegateCommand("Project behouden na mislukte update", () => { }, () => { }));
                    }
                }
                throw;
            }
        }
        catch (Exception error)
        {
            App.Log(error);
            MessageBox.Show(this, error.Message + "\n\nDe update is niet gestart. Je document blijft beschikbaar.",
                "Lokale update mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private bool CanRestartForUpdate()
    {
        var root = Velopack.Locators.VelopackLocator.Current.RootAppDir;
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var otherWindow = false;
        foreach (var candidate in Process.GetProcessesByName("Kabelaar"))
        {
            using (candidate)
            {
                if (candidate.Id == Environment.ProcessId) continue;
                try
                {
                    if (candidate.HasExited) continue;
                    var path = candidate.MainModule?.FileName;
                    if (path is null || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        otherWindow = true;
                }
                catch (InvalidOperationException) { /* Process has already exited. */ }
                catch (Win32Exception) { otherWindow = true; }
            }
        }
        if (!otherWindow) return true;
        MessageBox.Show(this,
            "Bewaar je werk en sluit eerst de andere vensters van deze Kabelaar-installatie. Start de update daarna vanuit dit venster.",
            "Andere documenten zijn geopend", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
}
