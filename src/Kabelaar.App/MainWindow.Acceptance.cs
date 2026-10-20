using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Kabelaar.App;

internal static class AcceptanceArguments
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true
    };
    internal static string? Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    internal static string Output(string[] args)
    {
        var path = Value(args, "--output") ?? throw new ArgumentException("Acceptance requires --output.");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Acceptance output must be an absolute path.");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }
    internal static async Task Write(string output, string name, object value)
    {
        var target = Path.Combine(output, name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static async Task WaitForAcknowledgement(string output, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(output, name)))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(45))
                throw new TimeoutException("The acceptance driver did not acknowledge the result.");
            await Task.Delay(100);
        }
    }
    internal static object Identity() => new
    {
        version = VelopackLocator.Current.CurrentlyInstalledVersion?.ToString(),
        root = VelopackLocator.Current.RootAppDir,
        executable = Environment.ProcessPath, processId = Environment.ProcessId,
        assembly = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    };
    internal static void RequireInstalled()
    {
        var locator = VelopackLocator.Current;
        var root = locator.RootAppDir;
        if (locator.IsPortable || locator.CurrentlyInstalledVersion is null || string.IsNullOrWhiteSpace(root) ||
            Environment.ProcessPath is not { } executable ||
            !Path.GetFullPath(executable).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This acceptance stage must run from the real installed application.");
    }
}

public partial class App
{
    private async Task HoldAcceptanceProcessAsync(string[] args)
    {
        string? output = null;
        try
        {
            AcceptanceArguments.RequireInstalled();
            output = AcceptanceArguments.Output(args);
            await AcceptanceArguments.Write(output, "ready.json", AcceptanceArguments.Identity());
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!File.Exists(Path.Combine(output, "release")) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Shutdown(0);
        }
        catch (Exception error)
        {
            if (output is not null) await AcceptanceArguments.Write(output, "error.json", new { error = error.ToString() });
            Log(error); Shutdown(2);
        }
    }
}

public partial class MainWindow
{
    private sealed record CrashAcceptanceState(string ProjectId, string Notes, string RecoveryId, string ArchiveSha256,
        double ElapsedSeconds);
    private sealed record UpdateAcceptanceState(string ProjectId, string Notes, string ProjectPath, string FromVersion, string ToVersion);
    private sealed record ProfileAcceptanceState(bool CreatedByThisRun, string LibraryPath, string Fingerprint,
        string ProjectId, string[] PartIds, string[] AssetIds);

    public async Task RunInstalledAcceptanceAsync(string[] args)
    {
        var output = AcceptanceArguments.Output(args);
        var stage = AcceptanceArguments.Value(args, "--stage") ?? "guards";
        _autosave.Stop();
        try
        {
            AcceptanceArguments.RequireInstalled();
            switch (stage)
            {
                case "guards": await CheckInstalledUpdateGuardsAsync(output); break;
                case "autosave-crash": await CheckTimedAutosaveBeforeTerminationAsync(output); break;
                case "recovery": await CheckRecoveryAfterTerminationAsync(output); break;
                case "update": await ApplyAcceptanceUpdateAsync(args, output); break;
                case "verify-update": await VerifyAcceptanceUpdateAsync(output); break;
                case "seed-profile": await SeedAcceptanceProfileAsync(output); break;
                case "cleanup-profile": await CleanupAcceptanceProfileAsync(output); break;
                case "clean-close": await CheckNormalCleanCloseAsync(output); break;
                default: throw new ArgumentException("Unknown installed acceptance stage: " + stage);
            }
        }
        catch (Exception error)
        {
            await AcceptanceArguments.Write(output, stage + "-error.json", new { success = false, identity = AcceptanceArguments.Identity(), error = error.ToString() });
            throw;
        }
        finally
        {
            _closing = true; _autosave.Stop(); _validationTimer.Stop(); _lease?.Dispose(); _lease = null;
        }
    }

    private async Task CheckNormalCleanCloseAsync(string output)
    {
        if (_closing || _history.IsDirty || !IsLoaded)
            throw new InvalidOperationException("Clean-close acceptance requires a normally open, clean window.");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closingCalls = 0;
        Closing += (_, _) => closingCalls++;
        Closed += (_, _) => closed.TrySetResult();
        // Do not set _closing: this deliberately exercises the real first Closing callback,
        // including synchronous confirmation and its dispatcher-deferred second Close.
        Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (IsVisible || closingCalls != 2 || _lease is not null)
            throw new InvalidOperationException("Normal clean close did not complete exactly one deferred closing sequence.");
        await AcceptanceArguments.Write(output, "clean-close-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), closingCalls, closed = true,
            productionClosingHandler = true, firstConfirmationCompletedSynchronously = true
        });
    }

    private async Task SeedAcceptanceProfileAsync(string output)
    {
        // A pre-existing catalog is read and hashed only. The lease prevents a second editor
        // from creating a catalog between the existence check and the first synthetic save.
        using var lease = ProjectLease.Acquire(_userLibrary);
        var created = !File.Exists(_userLibrary);
        LoadedProject loaded;
        if (created)
        {
            var asset = new ProjectAsset
            {
                FileName = "acceptance-part-source.txt", MediaType = "text/plain",
                Data = System.Text.Encoding.UTF8.GetBytes("Synthetic embedded library source " + Guid.NewGuid().ToString("N"))
            };
            var part = new PartDefinition
            {
                Name = "Synthetic installation acceptance part", PartNumber = "ACCEPTANCE-" + Ids.New(),
                Kind = PartKind.Accessory, SourceAssetId = asset.Id
            };
            var catalog = new HarnessProject { Title = "Synthetic installation acceptance catalog", Parts = [part], Assets = [asset] };
            var fingerprint = await _store.SaveAsync(catalog, _userLibrary);
            loaded = new LoadedProject(catalog, fingerprint);
        }
        else loaded = await _store.LoadAsync(_userLibrary);
        var state = new ProfileAcceptanceState(created, Path.GetFullPath(_userLibrary), loaded.Fingerprint,
            loaded.Project.Id, loaded.Project.Parts.Select(p => p.Id).ToArray(), loaded.Project.Assets.Select(a => a.Id).ToArray());
        await AcceptanceArguments.Write(output, "profile-state.json", state);
        await AcceptanceArguments.Write(output, "seed-profile-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), createdByThisRun = created,
            libraryFingerprint = loaded.Fingerprint, parts = state.PartIds.Length, assets = state.AssetIds.Length
        });
    }

    private async Task CleanupAcceptanceProfileAsync(string output)
    {
        var state = JsonSerializer.Deserialize<ProfileAcceptanceState>(
            await File.ReadAllTextAsync(Path.Combine(output, "profile-state.json")), AcceptanceArguments.Json)!;
        if (!Path.GetFullPath(_userLibrary).Equals(state.LibraryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acceptance profile belongs to another user-data location.");
        using var lease = ProjectLease.Acquire(_userLibrary);
        var loaded = await _store.LoadAsync(_userLibrary);
        if (loaded.Fingerprint != state.Fingerprint || loaded.Project.Id != state.ProjectId ||
            !loaded.Project.Parts.Select(p => p.Id).SequenceEqual(state.PartIds) ||
            !loaded.Project.Assets.Select(a => a.Id).SequenceEqual(state.AssetIds))
            throw new InvalidDataException("The catalog changed; acceptance cleanup leaves it untouched.");
        if (state.CreatedByThisRun) File.Delete(_userLibrary);
        await AcceptanceArguments.Write(output, "cleanup-profile-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), libraryPreservedExactly = true,
            removedOwnSyntheticCatalog = state.CreatedByThisRun, state.Fingerprint
        });
    }

    private async Task CheckInstalledUpdateGuardsAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
        var document = DemoFactory.Create();
        var path = Path.Combine(output, "guard-document.kabelaar");
        var fingerprint = await _store.SaveAsync(document, path);
        ReplaceProject(document, path, fingerprint, ProjectLease.Acquire(path));
        var originalNotes = document.Notes;
        _history.Execute(new DelegateCommand("Synthetic unsaved change", () => document.Notes = "Unsaved acceptance edit", () => document.Notes = originalNotes));
        var applies = 0;
        var blocked = new List<string>();
        Check(!await RestartForUpdateAsync(() => applies++, () => ConfirmLeaveAsync(() => MessageBoxResult.Cancel), blocked.Add),
            "Cancel prevents update application");
        Check(applies == 0 && _history.IsDirty && document.Notes == "Unsaved acceptance edit" && _lease is not null,
            "Cancel retains the dirty document and its file lease");
        Check((await _store.LoadAsync(path)).Project.Notes == originalNotes, "Cancel does not silently save the document");

        var peer = await StartAcceptancePeerAsync(output, "peer-before-confirmation");
        try
        {
            var confirmations = 0;
            Check(!await RestartForUpdateAsync(() => applies++, () => { confirmations++; return Task.FromResult(true); }, blocked.Add),
                "Another real process from this installation blocks the update");
            Check(confirmations == 0 && applies == 0 && !peer.Process.HasExited, "Peer guard runs before confirming or closing either document");
        }
        finally { await ReleaseAcceptancePeerAsync(peer.Process, peer.Directory); }

        (Process Process, string Directory)? latePeer = null;
        try
        {
            Check(!await RestartForUpdateAsync(() => applies++, async () =>
            {
                var saved = await ConfirmLeaveAsync(() => MessageBoxResult.Yes);
                latePeer = await StartAcceptancePeerAsync(output, "peer-after-saving");
                return saved;
            }, blocked.Add), "A real peer opened during saving blocks the final restart");
            Check(applies == 0 && !_history.IsDirty && _lease is not null &&
                (await _store.LoadAsync(path)).Project.Notes == "Unsaved acceptance edit",
                "Successful save is retained when the final peer check blocks restart");
        }
        finally { if (latePeer is { } p) await ReleaseAcceptancePeerAsync(p.Process, p.Directory); }

        _history.Execute(new DelegateCommand("Synthetic final save", () => document.Notes = "Saved before applying acceptance update",
            () => document.Notes = "Unsaved acceptance edit"));
        Check(await RestartForUpdateAsync(() => applies++, () => ConfirmLeaveAsync(() => MessageBoxResult.Yes), blocked.Add),
            "Save allows the shared restart sequence");
        Check(applies == 1 && !_history.IsDirty && _lease is null &&
            (await _store.LoadAsync(path)).Project.Notes == "Saved before applying acceptance update",
            "The file is committed and lease released before apply is invoked");
        using (ProjectLease.Acquire(path)) { }
        await AcceptanceArguments.Write(output, "guards-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), checks, blockedChecks = blocked.Count, applyInvocations = applies,
            actualRestart = false, confirmation = "Scripted decisions; production document save and process guards",
            projectPath = path
        });
    }

    private static async Task<(Process Process, string Directory)> StartAcceptancePeerAsync(string output, string name)
    {
        var directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--acceptance-hold", "--output", directory }) start.ArgumentList.Add(arg);
        start.Environment.Remove("KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT");
        start.Environment.Remove("KABELAAR_ACCEPTANCE_NONCE");
        var process = Process.Start(start) ?? throw new IOException("Could not start the owned acceptance peer.");
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready.json")))
            {
                if (process.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(15))
                    throw new IOException("Owned acceptance peer did not become ready.");
                await Task.Delay(100);
            }
            return (process, directory);
        }
        catch { await ReleaseAcceptancePeerAsync(process, directory); throw; }
    }
    private static async Task ReleaseAcceptancePeerAsync(Process process, string directory)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "release"), "acceptance complete");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // This handle is only the helper process created by this acceptance run.
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        finally { process.Dispose(); }
    }

    private async Task CheckTimedAutosaveBeforeTerminationAsync(string output)
    {
        var document = DemoFactory.Create();
        var notes = "Recovery acceptance " + Guid.NewGuid().ToString("N");
        ReplaceProject(document, null, null);
        _history.Execute(new DelegateCommand("Synthetic crash-recovery change", () => document.Notes = notes, () => document.Notes = ""));
        var watch = Stopwatch.StartNew();
        _autosave.Start();
        while (_recoveryEntry is null || _recoveryEntry.ProjectId != document.Id)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("The real 20-second autosave timer did not create a recovery archive.");
            await Task.Delay(100);
        }
        var recovery = _recoveryEntry;
        var hash = await ProjectStore.GetFingerprintAsync(recovery.ArchivePath);
        await AcceptanceArguments.Write(output, "autosave-before-crash.json",
            new CrashAcceptanceState(document.Id, notes, recovery.Id, hash, watch.Elapsed.TotalSeconds));
        // Terminate only this explicitly started synthetic test process. No graceful close/recovery cleanup runs.
        Process.GetCurrentProcess().Kill();
        await Task.Delay(Timeout.Infinite);
    }
    private async Task CheckRecoveryAfterTerminationAsync(string output)
    {
        var state = JsonSerializer.Deserialize<CrashAcceptanceState>(
            await File.ReadAllTextAsync(Path.Combine(output, "autosave-before-crash.json")), AcceptanceArguments.Json)!;
        var entry = (await _recovery.ListAsync()).Single(r => r.Id == state.RecoveryId);
        var restored = await _recovery.LoadAsync(entry);
        if (restored.Project.Id != state.ProjectId || restored.Project.Notes != state.Notes ||
            restored.Fingerprint != state.ArchiveSha256)
            throw new InvalidDataException("The recovery archive did not preserve the unsaved document exactly.");
        ReplaceProject(restored.Project, null, null);
        await AcceptanceArguments.Write(output, "recovery-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), state.ProjectId, state.RecoveryId,
            timedAutosaveSeconds = state.ElapsedSeconds, restoredNotes = restored.Project.Notes,
            processTermination = "Owned acceptance process killed after real timer autosave"
        });
        _recovery.Delete(entry); // Only this run's synthetic recovery; all other entries remain untouched.
    }

    private async Task ApplyAcceptanceUpdateAsync(string[] args, string output)
    {
        var feed = AcceptanceArguments.Value(args, "--release-directory") ?? throw new ArgumentException("--release-directory is required.");
        var expected = AcceptanceArguments.Value(args, "--expected-version") ?? throw new ArgumentException("--expected-version is required.");
        var blocks = new List<string>();
        if (!CanRestartForUpdate(blocks.Add)) throw new InvalidOperationException(string.Join("\n", blocks));
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)));
        var update = await manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("No newer local update exists.");
        if (update.TargetFullRelease.PackageId != "Kabelaar" || update.TargetFullRelease.Version.ToString() != expected)
            throw new InvalidDataException("The local feed target is not the explicitly requested Kabelaar version.");
        await manager.DownloadUpdatesAsync(update);
        var document = DemoFactory.Create();
        var path = Path.Combine(output, "update-document.kabelaar");
        var fingerprint = await _store.SaveAsync(document, path);
        ReplaceProject(document, path, fingerprint, ProjectLease.Acquire(path));
        var notes = "Saved across actual application update " + Guid.NewGuid().ToString("N");
        _history.Execute(new DelegateCommand("Synthetic unsaved update work", () => document.Notes = notes, () => document.Notes = ""));
        await AcceptanceArguments.Write(output, "update-before-restart.json",
            new UpdateAcceptanceState(document.Id, notes, path, manager.CurrentVersion!.ToString(), expected));
        var restarted = await RestartForUpdateAsync(() => manager.ApplyUpdatesAndRestart(update.TargetFullRelease,
            ["--installed-acceptance", "--stage", "verify-update", "--output", output]),
            () => ConfirmLeaveAsync(() => MessageBoxResult.Yes), blocks.Add);
        // The real ApplyUpdatesAndRestart exits the process. Returning is a test failure, never a substitute for applying.
        throw new InvalidOperationException($"The real updater did not exit this process (accepted={restarted}, blocks={string.Join("; ", blocks)}).");
    }
    private async Task VerifyAcceptanceUpdateAsync(string output)
    {
        var state = JsonSerializer.Deserialize<UpdateAcceptanceState>(
            await File.ReadAllTextAsync(Path.Combine(output, "update-before-restart.json")), AcceptanceArguments.Json)!;
        var version = VelopackLocator.Current.CurrentlyInstalledVersion!.ToString();
        if (version != state.ToVersion) throw new InvalidOperationException($"Restarted version {version} differs from requested {state.ToVersion}.");
        if (!Path.GetFullPath(state.ProjectPath).StartsWith(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Acceptance document is outside its output directory.");
        var loaded = await _store.LoadAsync(state.ProjectPath);
        if (loaded.Project.Id != state.ProjectId || loaded.Project.Notes != state.Notes)
            throw new InvalidDataException("Unsaved work was not saved and reopened across the actual update.");
        ReplaceProject(loaded.Project, state.ProjectPath, loaded.Fingerprint, ProjectLease.Acquire(state.ProjectPath));
        await AcceptanceArguments.Write(output, "verify-update-result.json", new
        {
            success = true, identity = AcceptanceArguments.Identity(), fromVersion = state.FromVersion, toVersion = version,
            actualRestart = true, confirmation = "Scripted Yes; real SaveAsync, SimpleFileSource, download and ApplyUpdatesAndRestart",
            state.ProjectId, state.ProjectPath, fingerprint = loaded.Fingerprint
        });
        await AcceptanceArguments.WaitForAcknowledgement(output, "verify-update.ack");
    }

    public async Task RunLaunchAcceptanceAsync(string[] args, string output, string nonce)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Launch acceptance output must be absolute.");
        Directory.CreateDirectory(output);
        _autosave.Stop();
        try
        {
            AcceptanceArguments.RequireInstalled();
            var project = args.FirstOrDefault(a => a.EndsWith(".kabelaar", StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                await OpenPathAsync(project);
                if (_path is null || !Path.GetFullPath(project).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The shell-associated project was not opened.");
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !IsLoaded || !IsVisible) throw new InvalidOperationException("The actual desktop window was not created.");
            await AcceptanceArguments.Write(output, "launch-result.json", new
            {
                success = true, nonce, identity = AcceptanceArguments.Identity(), windowHandle = handle.ToInt64(),
                isLoaded = IsLoaded, isVisible = IsVisible, showActivated = ShowActivated, projectPath = _path, projectId = _path is null ? null : _project.Id
            });
            await AcceptanceArguments.WaitForAcknowledgement(output, "launch.ack");
        }
        catch (Exception error)
        {
            await AcceptanceArguments.Write(output, "launch-error.json", new { success = false, nonce, error = error.ToString() });
            throw;
        }
        finally { _closing = true; _autosave.Stop(); _validationTimer.Stop(); _lease?.Dispose(); _lease = null; }
    }
}
