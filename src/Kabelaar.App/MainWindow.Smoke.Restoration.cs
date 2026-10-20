using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async Task CheckRestoredEditingAsync(string output, Action<bool, string> check)
    {
        var original = _project;
        var fixture = new HarnessProject { Title = "Synthetische draadbediening", DrawingNumber = "CURVE-001" };
        var view = fixture.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        var branch = new RouteBranch { Name = "Gedeelde fysieke tak", Points = [new(30, 60), new(230, 60)], PhysicalLengthMm = 350 };
        var route = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points] };
        fixture.Assembly.Branches.Add(branch); view.Routes.Add(route);
        var article = new PartDefinition { Kind = PartKind.Wire, Name = "Synthetische draad", PartNumber = "TEST-W" };
        fixture.Parts.Add(article);
        var first = new Conductor { Name = "W1", WirePartId = article.Id, BranchIds = [branch.Id], StartAllowanceMm = 12, EndAllowanceMm = 8 };
        var second = new Conductor { Name = "W2", WirePartId = article.Id, BranchIds = [branch.Id], Color = "#2563EB" };
        fixture.Assembly.Conductors.AddRange([first, second]);
        try
        {
            ReplaceProject(fixture, null, null);
            Canvas.Select(first.Id);
            await Idle();
            var physical = JsonSerializer.Serialize(fixture.Assembly);
            var baseline = JsonSerializer.Serialize(fixture);
            var token = _history.StateToken;
            var kind = Field<ComboBox>("Draadtype");
            kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().Single(i => i.Content?.ToString() == "Gebogen");
            Field<TextBox>("Bochtuitwijking (mm)").Text = "24";
            Field<TextBox>("Lijndikte ader (mm; leeg = aanzichtstijl)").Text = "0.9";
            Click("Vorm toepassen");
            await Idle();
            var shape = view.WirePresentations.Single(p => p.ConductorId == first.Id);
            var geometry = shape.Routes.Single().Geometry;
            check(shape.LineWidthMm == .9 && geometry.Segments.Single().Kind == RouteSegmentKind.Cubic &&
                  Math.Abs(Geometry.Evaluate(geometry, 0, .5).Y - 60) > 1,
                "Directe draadeigenschappen maken een tweepuntsdraad gebogen met eigen lijndikte");
            check(JsonSerializer.Serialize(fixture.Assembly) == physical &&
                  view.WirePresentations.All(p => p.ConductorId != second.Id),
                "Vrije individuele bochtbewerking behoudt de andere ader en alle fysieke gegevens");
            var scene = SceneBuilder.Build(fixture, fixture.Sheets[0]);
            check(scene.Primitives.OfType<ScenePath>().Single(p => p.EntityId == first.Id && p.Category == "wire").StrokeWidth == .9,
                "De eigen aderbreedte bereikt de gedeelde tekenbeschrijving");
            _history.Undo(); await Idle();
            check(JsonSerializer.Serialize(fixture) == baseline && _history.StateToken == token,
                "Eén undo herstelt vorm en lijndikte samen");
            _history.Redo(); await Idle();

            var handle = Canvas.GeometryHandles.First(h => h.Kind == GeometryHandleKind.Outgoing);
            var start = new CanvasEditGesture(handle.Position, handle.Position, new HashSet<string> { first.Id }, handle);
            var moved = start with { Current = start.Current + new Point2(0, 15) };
            var beforeDrag = JsonSerializer.Serialize(fixture);
            var dragToken = _history.StateToken;
            BeginGeometryEdit(start); UpdateGeometryEdit(moved);
            check(Canvas.HasGeometryPreview && !Canvas.PreviewRejected &&
                  beforeDrag == JsonSerializer.Serialize(fixture) && _history.StateToken == dragToken,
                "Handvatsleep toont tijdelijke geometrie zonder project of undo-geschiedenis te wijzigen");
            CancelGeometryEdit();
            check(!Canvas.HasGeometryPreview && beforeDrag == JsonSerializer.Serialize(fixture) && _history.StateToken == dragToken,
                "Annuleren van een handvatsleep laat document en geschiedenis intact");
            BeginGeometryEdit(start); UpdateGeometryEdit(moved); CommitGeometryEdit(moved); await Idle();
            check(beforeDrag != JsonSerializer.Serialize(fixture) && !Canvas.HasGeometryPreview &&
                  physical == JsonSerializer.Serialize(fixture.Assembly),
                "Bevestigde handvatsleep wijzigt uitsluitend de geselecteerde aderweergave");
            _history.Undo(); await Idle();
            check(beforeDrag == JsonSerializer.Serialize(fixture) && _history.StateToken == dragToken,
                "Eén undo herstelt de gehele handvatsleep");
            _history.Redo(); await Idle();

            var beforeMulti = JsonSerializer.Serialize(fixture);
            var multiToken = _history.StateToken;
            Canvas.Select(second.Id, true); await Idle();
            check(Field<TextBox>("Tekenkleur (#RRGGBB)").Text == "Meerdere waarden",
                "Meervoudige selectie toont verschillende bestaande kleuren als meerdere waarden");
            Field<TextBox>("Lijndikte (mm; leeg = aanzichtstijl)").Text = "1.2";
            Click("Op selectie toepassen"); await Idle();
            check(view.WirePresentations.Count(p => p.LineWidthMm == 1.2) == 2 &&
                  first.Color == "#202D3A" && second.Color == "#2563EB" &&
                  physical == JsonSerializer.Serialize(fixture.Assembly),
                "Meervoudige eigenschappen wijzigen alleen het ingevulde veld voor alle geselecteerde aders");
            _history.Undo(); await Idle();
            check(beforeMulti == JsonSerializer.Serialize(fixture) && multiToken == _history.StateToken,
                "Eén undo herstelt de volledige meervoudige eigenschapswijziging");
            var rejected = false;
            try { ApplyDrawingPropertyPatch(new(SetColor: true, Color: "#00FF00", SetWidth: true, Width: -1)); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && beforeMulti == JsonSerializer.Serialize(fixture) && multiToken == _history.StateToken,
                "Een ongeldige meervoudige patch past ook de overige geldige velden niet gedeeltelijk toe");
            Canvas.Select(first.Id); await Idle();

            // Palette interaction exercises the actual property control without opening a dialog.
            var swatch = PropertiesPanel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<WrapPanel>())
                .SelectMany(p => p.Children.OfType<Button>()).Single(b => AutomationProperties.GetName(b) == "Kleur #DC2626");
            swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(Field<TextBox>("Tekenkleur (#RRGGBB)").Text == "#DC2626", "Het kleurpalet vult de gewone tekenkleur in");
            Click("Ader toepassen"); await Idle();
            check(first.Color == "#DC2626" && first.ColorCode == "BK", "Tekenkleur wijzigen behoudt de afzonderlijke fysieke kleurcode");
            _history.Undo(); await Idle();

            var path = Path.Combine(output, "Bewerkte-bochten.kabelaar");
            var saved = JsonSerializer.Serialize(fixture);
            await _store.SaveAsync(fixture, path);
            var reopened = (await _store.LoadAsync(path)).Project;
            check(JsonSerializer.Serialize(reopened) == saved, "Bewerkte handvatten en aderopmaak heropenen zonder gegevensverlies");
            var exported = await ExportService.ExportSetAsync(reopened, Path.Combine(output, "bochten-uitvoer"), false);
            check(exported.Files.All(File.Exists), "Bewerkte bochten exporteren via de productie-uitvoer");
            Canvas.Fit(); await Idle();
            SaveAcceptanceWindowSnapshot(Path.Combine(output, "draadbediening.png"));
        }
        finally { CancelGeometryEdit(); ReplaceProject(original, null, null); }

        async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        T Field<T>(string name) where T : FrameworkElement => PropertiesPanel.Children.OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<T>()).Single(c => AutomationProperties.GetName(c) == name);
        void Click(string content) => PropertiesPanel.Children.OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Button>()).Single(b => b.Content as string == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void SaveAcceptanceWindowSnapshot(string path)
    {
        // Capture this test's own WPF content, never the user's desktop.
        if (Content is not FrameworkElement root || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task<object> MeasureCurvePreviewAsync(Action<bool, string> check)
    {
        // Same large fixture and frame deadlines as the existing benchmark, now exercising
        // an actual temporary Bézier edit. No global input and no persistent per-frame edit.
        _view.Mode = LayoutMode.Free;
        var wire = _project.Assembly.Conductors[0];
        var route = _view.Routes.First(r => wire.BranchIds.Contains(r.BranchId));
        _history.Execute(HarnessOperations.SetWireRouteGeometry(_project, _view, wire, route,
            Geometry.WithBend(Geometry.EditableRouteGeometry(_view, route, wire.Id), 12)));
        Canvas.Select(wire.Id);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var handle = Canvas.GeometryHandles.First(h => h.Kind == GeometryHandleKind.Outgoing);
        var gesture = new CanvasEditGesture(handle.Position, handle.Position, new HashSet<string> { wire.Id }, handle);
        var before = JsonSerializer.Serialize(_project);
        var token = _history.StateToken;
        var samples = new List<double>();
        var paints = new List<double>();
        BeginGeometryEdit(gesture);
        try
        {
            for (var i = 0; i < 110; i++)
            {
                var frames = Canvas.FrameMilliseconds.Count;
                var watch = Stopwatch.StartNew();
                UpdateGeometryEdit(gesture with { Current = gesture.Current + new Point2(0, i % 2 == 0 ? 5 : 8) });
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                while (Canvas.FrameMilliseconds.Count == frames && watch.ElapsedMilliseconds < 2000) await Task.Delay(1);
                if (Canvas.FrameMilliseconds.Count == frames) throw new InvalidOperationException("Bochtvoorvertoning bereikte geen nieuw frame.");
                if (i >= 10) { samples.Add(watch.Elapsed.TotalMilliseconds); paints.Add(Canvas.FrameMilliseconds.Last()); }
                await Task.Delay(25);
            }
        }
        finally { CancelGeometryEdit(); }
        check(before == JsonSerializer.Serialize(_project) && token == _history.StateToken,
            "100 gemeten bochtvoorvertoningen laten fysiek model, document en undo-geschiedenis intact");
        double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
        var editP95 = P95(samples); var paintP95 = P95(paints);
        check(editP95 <= 100 && paintP95 <= 33,
            $"Bochtvoorvertoning op 100 connectoren/1000 aders: p95 bewerking {editP95:0.00} ms, tekenen {paintP95:0.00} ms");
        return new { samples = samples.Count, p95Milliseconds = editP95, paintP95Milliseconds = paintP95,
            targetMilliseconds = 100, paintTargetMilliseconds = 33, passes = true, milliseconds = samples, paintMilliseconds = paints };
    }
}
