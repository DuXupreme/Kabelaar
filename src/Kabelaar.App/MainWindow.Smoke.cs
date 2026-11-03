using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    public async Task RunSmokeTestAsync(string[] args)
    {
        _autosave.Stop();
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) :
            Path.Combine(App.DataDirectory, "Acceptatietest");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Stage(string text) => File.AppendAllText(Path.Combine(output, "progress.txt"), $"{DateTime.Now:HH:mm:ss} {text}\n");
        void Check(bool passed, string text) { if (!passed) throw new InvalidOperationException("Acceptatietest: " + text); checks.Add(text); Stage(text); }
        try
        {
            Stage("Voorbeeld openen");
            ReplaceProject(DemoFactory.Create(), null, null);
            Stage("Voorbeeld geopend");
            Check(!ProductionValidator.Validate(_project).Any(i => i.Severity == Severity.Error), "Synthetisch voorbeeld is productiecontroleerbaar");
            var firstPlacement = _view.Components[0];
            Canvas.Select(firstPlacement.EntityId);
            var pendingButton = PropertiesPanel.Children.OfType<System.Windows.Controls.StackPanel>()
                .SelectMany(p => p.Children.OfType<System.Windows.Controls.Button>())
                .Single(b => b.Content as string == "Connector toepassen");
            _history.Execute(HarnessOperations.MoveComponent(_project, _view, firstPlacement, firstPlacement.Position + new Point2(1, 0)));
            var pendingState = _history.StateToken;
            // A button pressed before undo/drag may deliver Click after the model changed.
            pendingButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(_history.StateToken == pendingState, "Een oude propertyknop voert tijdens uitgestelde verversing geen bewerking uit");
            _history.Undo();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await CheckPresentationEditingAsync(Check);
            await CheckRestoredEditingAsync(output, Check);
            await CheckGeometryMovesAsync(Check);
            await CheckExtendedEditingAsync(output, Check);
            var wire = _project.Assembly.Conductors[0];
            Canvas.Select(wire.Id);
            Check(WireGrid.SelectedItem is WireDisplay row && row.Id == wire.Id, "Tekeningselectie koppelt naar aansluitlijst");
            WireGrid.SelectedIndex = 1;
            Check(WireGrid.SelectedItem is WireDisplay chosen && Canvas.Selection.Contains(chosen.Id), "Aansluitlijstselectie markeert de fysieke ader");
            Canvas.Select(wire.Id);
            var branch = _project.Assembly.Branches.First(b => b.Id == wire.BranchIds[0]);
            var route = _view.Routes.First(r => r.BranchId == branch.Id);
            var before = HarnessReports.Bom(_project).ToArray();
            var count = _project.Assembly.Conductors.Count;
            var cut = HarnessReports.CutLength(_project, wire);
            _history.Execute(HarnessOperations.InsertRoutePoint(_project, _view, route, 1, (route.Points[1] + route.Points[2]) * .5));
            Check(count == _project.Assembly.Conductors.Count && cut == HarnessReports.CutLength(_project, wire), "Extra routepunt in vrije opmaak behoudt aderaantal en kniplengte");
            Check(before.SequenceEqual(HarnessReports.Bom(_project)), "Extra routepunt behoudt stuklijst");
            _history.Undo(); _history.Redo(); _history.Undo();
            var originalView = _view;
            var oldSpliceCount = _project.Assembly.Splices.Count;
            SetTool(EditorTool.Splice);
            CanvasClick(new(new Point2(40, 50), 1, System.Windows.Input.ModifierKeys.None));
            _sheet = _project.Sheets[1]; _view = _sheet.Views[0];
            _history.Undo();
            Check(_project.Assembly.Splices.Count == oldSpliceCount && originalView.Components.All(p => _project.Assembly.Connectors.Any(c => c.Id == p.EntityId)),
                "Undo na wisselen van blad verwijdert de oorspronkelijke spliceweergave");
            _sheet = _project.Sheets[0]; _view = originalView; SetTool(EditorTool.Select); Canvas.ClearSelection(); Refresh();
            var originalAnnotations = _view.Annotations.ToArray();
            _history.Execute(new CompositeCommand("Meervoudige verwijdering testen",
                RemoveItem("A", _view.Annotations, originalAnnotations[0]), RemoveItem("B", _view.Annotations, originalAnnotations[1])));
            _history.Undo();
            Check(_view.Annotations.SequenceEqual(originalAnnotations), "Meervoudige verwijdering en undo behouden de objectvolgorde");
            var expected = JsonSerializer.Serialize(_project);
            var path = Path.Combine(output, "Synthetische-kabelboom.kabelaar");
            await _store.SaveAsync(_project, path);
            var loaded = await _store.LoadAsync(path);
            Check(expected == JsonSerializer.Serialize(loaded.Project), "Opslaan en heropenen behouden het volledige document");
            var exported = await ExportService.ExportSetAsync(loaded.Project, Path.Combine(output, "productie"), false);
            Check(exported.Files.All(File.Exists), "Productiepakket PDF SVG PNG en CSV geschreven");
            var portability = Path.Combine(output, "overdraagbaar");
            Directory.CreateDirectory(portability); File.Copy(path, Path.Combine(portability, "project.kabelaar"), true);
            var portable = await _store.LoadAsync(Path.Combine(portability, "project.kabelaar"));
            Check(HarnessReports.Bom(portable.Project).SequenceEqual(HarnessReports.Bom(_project)), "Zelfstandig pakket heropent zonder externe bronpaden");
            var recovery = new RecoveryStore(Path.Combine(output, "recovery"));
            var entry = await recovery.SaveAsync(_project, null, "smoke");
            Check((await recovery.LoadAsync(entry)).Project.Id == _project.Id, "Naamloos document herstelt via autosaveformaat");
            recovery.Delete(entry);
            var cad = new CadImportService();
            Check(cad.IsAvailable, "Native STEP-worker is meegeleverd");
            var fixture = Path.Combine(output, "worker-fixture-mm.step");
            var workerStart = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CadWorker", "Kabelaar.CadWorker.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            workerStart.ArgumentList.Add("--fixture-box");
            workerStart.ArgumentList.Add("--protocol"); workerStart.ArgumentList.Add("1");
            workerStart.ArgumentList.Add("--output"); workerStart.ArgumentList.Add(fixture);
            using (var worker = Process.Start(workerStart) ?? throw new IOException("STEP-testworker start niet."))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await worker.WaitForExitAsync(timeout.Token); }
                catch { worker.Kill(true); throw; }
                Check(worker.ExitCode == 0 && File.Exists(fixture), "Meegeleverde native worker maakt gecontroleerde STEP-testgeometrie");
            }
            var imported = await cad.ImportAsync(fixture);
            Check(imported.Views.Count == 6 && imported.Mesh.Triangles.Count > 0, "Meegeleverde native worker verwerkt synthetische STEP");
            var embeddedAsset = new ProjectAsset { FileName = "ingesloten.step", MediaType = "model/step",
                Data = await File.ReadAllBytesAsync(fixture) };
            var embeddedProject = new HarnessProject { Assets = [embeddedAsset],
                Parts = [new PartDefinition { SourceAssetId = embeddedAsset.Id }] };
            var embeddedPath = Path.Combine(output, "Ingesloten-STEP.kabelaar");
            await _store.SaveAsync(embeddedProject, embeddedPath);
            var reopenedAsset = (await _store.LoadAsync(embeddedPath)).Project.Assets.Single();
            var embeddedPreview = await ImportEmbeddedStepPreviewAsync(reopenedAsset, CancellationToken.None);
            Check(embeddedPreview.Views.Count == 6 && embeddedPreview.Mesh.Triangles.Count == imported.Mesh.Triangles.Count,
                "STEP-preview heropent uit projectasset zonder oorspronkelijk bronpad");
            await CheckExampleProjectAsync(args, Check);
            if (args.Contains("--functional-smoke"))
            {
                var functionalReport = new
                {
                    utc = DateTimeOffset.UtcNow, version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    scope = "functional-only", performanceMeasured = false, checks,
                    reference = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                        memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, activatesWindow = ShowActivated },
                    humanAcceptance = "Functionele installatiecontrole; geen prestatiemeting of visuele praktijkacceptatie."
                };
                await File.WriteAllTextAsync(Path.Combine(output, "acceptance-report.json"),
                    JsonSerializer.Serialize(functionalReport, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.json"),
                    JsonSerializer.Serialize(new { success = true, scope = "functional-only", performanceMeasured = false, report = "acceptance-report.json" }));
                ReplaceProject(DemoFactory.Create(), null, null); _closing = true;
                return;
            }
            ReplaceProject(DemoFactory.CreateAcceptancePerformance(), null, null);
            Check(_project.Assembly.Connectors.Count == 100 && _project.Assembly.Conductors.Count == 1000, "Vast prestatieblad bevat 100 connectoren en 1000 aders");
            Canvas.Fit(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            var frameStart = Canvas.FrameMilliseconds.Count;
            for (var i = 0; i < 100; i++)
            {
                Canvas.ZoomBy(i % 2 == 0 ? 1.01 : 1 / 1.01);
                Canvas.PanBy(i % 2 == 0 ? 3 : -3, i % 2 == 0 ? 2 : -2);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(17);
            }
            var frames = Canvas.FrameMilliseconds.Skip(frameStart).OrderBy(x => x).ToArray();
            var editing = new List<double>();
            var coldEditing = new List<double>();
            var editDetails = new List<object>();
            var placement = _view.Components[0];
            Canvas.Select(placement.EntityId);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            for (var i = 0; i < 110; i++)
            {
                var beforeFrame = Canvas.FrameMilliseconds.Count;
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var watch = Stopwatch.StartNew();
                var command = HarnessOperations.MoveComponent(_project, _view, placement, placement.Position + new Point2(i % 2 == 0 ? 1 : -1, 0));
                var commandMilliseconds = watch.Elapsed.TotalMilliseconds;
                _history.Execute(command);
                var executeMilliseconds = watch.Elapsed.TotalMilliseconds - commandMilliseconds;
                // Measure until the edited drawing can be presented; the full project check is
                // deliberately coalesced at background priority after interaction settles.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                while (Canvas.FrameMilliseconds.Count == beforeFrame && watch.ElapsedMilliseconds < 2000)
                    await Task.Delay(1);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                if (Canvas.FrameMilliseconds.Count == beforeFrame)
                    throw new InvalidOperationException($"Bewerking {i + 1} verscheen niet in een nieuw frame.");
                if (i < 10) coldEditing.Add(elapsed); else editing.Add(elapsed);
                editDetails.Add(new { warmup = i < 10, elapsedMilliseconds = elapsed,
                    commandMilliseconds, executeMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                    collections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - collectionsBefore[g]).ToArray(),
                    sceneStages = new Dictionary<string, double>(_sceneTimings),
                    stages = new Dictionary<string, double>(_refreshTimings), lastPaintMilliseconds = Canvas.FrameMilliseconds.LastOrDefault() });
                // Return to the real event loop between user gestures, including property updates.
                await Task.Delay(25);
            }
            Check(true, "Alle 110 connectorbewerkingen verschijnen in nieuwe frames; 10 opwarmmetingen en 100 meetwaarden");
            var p95 = frames.Length > 0 ? frames[(int)Math.Ceiling(frames.Length * .95) - 1] : double.PositiveInfinity;
            var editP95 = editing.OrderBy(x => x).ElementAt((int)Math.Ceiling(editing.Count * .95) - 1);
            var curvePreview = await MeasureCurvePreviewAsync(Check);
            var report = new { utc = DateTimeOffset.UtcNow, version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                scope = "full", performanceMeasured = true,
                checks, rendering = new { frames = frames.Length, p95Milliseconds = p95, targetMilliseconds = 33, passes = p95 <= 33 },
                editing = new { samples = editing.Count, warmupSamples = coldEditing.Count, coldMilliseconds = coldEditing,
                    p95Milliseconds = editP95, targetMilliseconds = 100, passes = editP95 <= 100 },
                editDetails, curvePreview,
                lastRefreshMilliseconds = new Dictionary<string, double>(_refreshTimings),
                reference = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                    memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    canvasWidthDip = Canvas.ActualWidth, canvasHeightDip = Canvas.ActualHeight,
                    dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(Canvas).DpiScaleX,
                    activatesWindow = ShowActivated },
                humanAcceptance = "Tweede pc, schermschaling 100–200%, echte productietekening en gebruikersbeoordeling nog afzonderlijk uitvoeren." };
            await File.WriteAllTextAsync(Path.Combine(output, "acceptance-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.json"), JsonSerializer.Serialize(new
                { success = p95 <= 33 && editP95 <= 100, scope = "full", performanceMeasured = true, report = "acceptance-report.json" }));
            if (p95 > 33 || editP95 > 100) throw new InvalidOperationException($"Prestatiedoel nog niet bereikt: frame p95={p95:0.0} ms; bewerking p95={editP95:0.0} ms.");
            ReplaceProject(DemoFactory.Create(), null, null); _closing = true;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "acceptance-error.txt"), ex.ToString());
            _closing = true; throw;
        }
    }
}
