using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Kabelaar.Rendering;
using Microsoft.Win32;

namespace Kabelaar.App;

public partial class MainWindow
{
    private void ShowCanvasContextMenu(Point2 point)
    {
        var hit = Canvas.Scene?.Index.HitTest(point, 5 / Canvas.PixelsPerMillimetre)
            .FirstOrDefault(p => p.ViewId == _view.Id && !string.IsNullOrEmpty(p.EntityId) &&
                p.Category is not ("frame" or "title-block" or "watermark" or "grid"));
        if (hit is not null && !Canvas.Selection.Contains(hit.EntityId)) Canvas.Select(hit.EntityId);
        var menu = new ContextMenu();
        void Item(string title, RoutedEventHandler action, bool enabled = true)
        { var item = new MenuItem { Header = title, IsEnabled = enabled }; item.Click += action; menu.Items.Add(item); }
        var selected = Canvas.Selection.Count > 0;
        Item("Ongedaan maken", Undo_Click, _history.CanUndo);
        Item("Opnieuw", Redo_Click, _history.CanRedo);
        menu.Items.Add(new Separator());
        Item("Dupliceren…", Duplicate_Click, selected);
        Item("Parallelle kopieën…", ParallelCopies_Click, _project.Assembly.Conductors.Any(w => Canvas.Selection.Contains(w.Id)));
        Item("Selectieblok opslaan…", SaveBlock_Click, selected);
        Item("Selectieblok invoegen…", InsertBlock_Click);
        Item("Plakken", Paste_Click);
        if (selected)
        {
            menu.Items.Add(new Separator());
            Item("90° roteren", Rotate_Click); Item("Horizontaal spiegelen", MirrorHorizontal_Click);
            Item("Verticaal spiegelen", MirrorVertical_Click); Item("Naar voorgrond", BringForward_Click);
            Item("Naar achtergrond", SendBackward_Click); Item("Verbindingen voorstellen…", SuggestConnections_Click);
            Item("Verwijderen…", Delete_Click);
        }
        menu.PlacementTarget = Canvas; menu.IsOpen = true;
    }

    private void AddExtendedSnapTargets(List<ScenePrimitive> targets)
    {
        var visible = _sheet.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        foreach (var wire in _project.Assembly.Conductors)
        {
            if (wire.BranchIds.Count == 0) continue;
            if (wire.From.Kind != EndpointKind.Free && wire.To.Kind != EndpointKind.Free) continue;
            var points = SceneBuilder.ConductorPoints(_project, _view, wire);
            foreach (var (end, branchId, first) in new[] { (wire.From, wire.BranchIds[0], true), (wire.To, wire.BranchIds[^1], false) })
            {
                if (end.Kind != EndpointKind.Free) continue;
                var route = _view.Routes.Find(r => r.BranchId == branchId && visible.Contains(r.LayerId));
                if (route is null) continue;
                if (points.Count > 0) targets.Add(new SceneEllipse(wire.Id, "snap-endpoint", ToSheet(first ? points[0] : points[^1]), 0, 0));
            }
        }
        foreach (var annotation in _view.Annotations.Where(a => visible.Contains(a.LayerId)))
        {
            if (annotation.Kind == AnnotationKind.Leader)
                foreach (var p in annotation.Points) targets.Add(new SceneEllipse(annotation.Id, "snap-leader", ToSheet(p), 0, 0));
            if (!IsTable(annotation) || annotation.Points.Count == 0) continue;
            var origin = annotation.Points[0];
            var rows = annotation.Kind == AnnotationKind.FreeTable ? annotation.Rows.Count : SceneBuilder.TableData(_project, annotation.Kind).Rows.Count;
            var height = SceneBuilder.TableHeight(_project, annotation);
            foreach (var p in new[] { new Point2(0, 0), new Point2(annotation.WidthMm, 0), new Point2(0, height), new Point2(annotation.WidthMm, height) })
                targets.Add(new SceneEllipse(annotation.Id, "snap-table", ToSheet(origin + Geometry.Transform(p, annotation.RotationDegrees, new())), 0, 0));
        }
    }

    private static bool IsTable(Annotation annotation) => annotation.Kind is AnnotationKind.FreeTable or AnnotationKind.WireTable
        or AnnotationKind.PinTable or AnnotationKind.BomTable or AnnotationKind.RevisionTable;

    private void DuplicateSelection()
    {
        if (Canvas.Selection.Count == 0) return;
        RunEditorAction(() =>
        {
            RequireEditableSelection();
            var block = SelectionOperations.Capture(_project, _view, Canvas.Selection);
            PreviewInsertion(SelectionOperations.PrepareInsert(_project, _view, block, new Point2(15, 15)), "Selectie dupliceren");
        });
    }

    private void ParallelCopies_Click(object sender, RoutedEventArgs e)
    {
        var ids = Canvas.Selection.Where(id => _project.Assembly.Conductors.Any(w => w.Id == id)).ToArray();
        if (ids.Length == 0) { Status("Selecteer één of meer aders."); return; }
        RunEditorAction(() =>
        {
            RequireEditableSelection();
            var dialog = new FormDialog(this, "Parallelle kopieën");
            var count = dialog.Form.Number("Aantal nieuwe kopieën per ader", 1);
            var distance = dialog.Form.Number("Afstand (mm)", 5);
            var angle = dialog.Form.Number("Richting van de verschuiving (graden)", 90);
            dialog.Form.Note("Nieuwe fysieke aders krijgen eigen nummers en open externe uiteinden. Kniplengtes en aantallen worden vóór bevestiging getoond.");
            dialog.OnAccept = () =>
            {
                var n = EditData.Number(count.Text, "Aantal", 1);
                if (n != Math.Truncate(n) || n > 100) throw new ArgumentException("Kies een geheel aantal van 1 tot 100.");
                _ = EditData.Number(distance.Text, "Afstand", .01); _ = EditData.Number(angle.Text, "Richting"); return true;
            };
            if (dialog.ShowDialog() != true) return;
            var step = Geometry.Transform(new Point2(EditData.Number(distance.Text, "Afstand"), 0), EditData.Number(angle.Text, "Richting"), new());
            var block = SelectionOperations.Capture(_project, _view, ids);
            PreviewInsertion(SelectionOperations.PrepareInsert(_project, _view, block, step,
                (int)EditData.Number(count.Text, "Aantal"), step), "Parallelle aders toevoegen");
        });
    }

    private void PreviewInsertion(SelectionInsertProposal proposal, string title)
    {
        var state = _history.StateToken;
        var dialog = new FormDialog(this, title, 760);
        dialog.Form.Note(string.Join(Environment.NewLine, proposal.Summary));
        var previewSheet = proposal.PreviewProject.Sheets.Find(s => s.Id == _sheet.Id) ?? proposal.PreviewProject.Sheets[0];
        var preview = new HarnessCanvas { Height = 300, Focusable = false, IsHitTestVisible = false };
        preview.SetScene(SceneBuilder.Build(proposal.PreviewProject, previewSheet));
        dialog.Form.Panel.Children.Add(preview); preview.Loaded += (_, _) => preview.Fit();
        dialog.Form.Note("De voorvertoning is nog niet toegepast. Annuleren laat de tekening en undo-geschiedenis intact.");
        if (dialog.ShowDialog() != true) return;
        if (_history.StateToken != state) throw new InvalidOperationException("De tekening is gewijzigd; maak het voorstel opnieuw.");
        Execute(proposal.Command); Canvas.ClearSelection();
        foreach (var id in proposal.SelectionIds) Canvas.Select(id, true);
    }

    private async void SaveBlock_Click(object sender, RoutedEventArgs e)
    {
        if (Canvas.Selection.Count == 0) { Status("Selecteer eerst de onderdelen, aders en annotaties voor het blok."); return; }
        var chooser = new SaveFileDialog { Filter = "Kabelaar-selectieblok|*.kabelaarblock", DefaultExt = ".kabelaarblock" };
        if (chooser.ShowDialog(this) != true) return;
        await BusyAsync("Selectieblok opslaan…", async token =>
        {
            var block = SelectionOperations.Capture(_project, _view, Canvas.Selection);
            await new SelectionBlockStore().SaveAsync(block, chooser.FileName, token);
        });
    }

    private async void InsertBlock_Click(object sender, RoutedEventArgs e)
    {
        var chooser = new OpenFileDialog { Filter = "Kabelaar-selectieblok|*.kabelaarblock" };
        if (chooser.ShowDialog(this) != true) return;
        HarnessProject? block = null;
        await BusyAsync("Selectieblok lezen…", async token => block = await new SelectionBlockStore().LoadAsync(chooser.FileName, token));
        if (block is null) return;
        RunEditorAction(() =>
        {
            var position = new FormDialog(this, "Blokverschuiving");
            var x = position.Form.Number("Verschuiving X (mm)", 20); var y = position.Form.Number("Verschuiving Y (mm)", 20);
            position.OnAccept = () => { _ = EditData.Number(x.Text, "X"); _ = EditData.Number(y.Text, "Y"); return true; };
            if (position.ShowDialog() != true) return;
            PreviewInsertion(SelectionOperations.PrepareInsert(_project, _view, block,
                new Point2(EditData.Number(x.Text, "X"), EditData.Number(y.Text, "Y"))), "Selectieblok invoegen");
        });
    }

    private void MirrorHorizontal_Click(object sender, RoutedEventArgs e) => TransformCurrentSelection(0, true, false);
    private void MirrorVertical_Click(object sender, RoutedEventArgs e) => TransformCurrentSelection(0, false, true);
    private void TransformCurrentSelection(double angle, bool mirrorX, bool mirrorY)
    {
        if (Canvas.Selection.Count == 0) return;
        RunEditorAction(() =>
        {
            RequireEditableSelection();
            var objects = Canvas.Scene?.Primitives.Where(p => p.ViewId == _view.Id && Canvas.Selection.Contains(p.EntityId)).ToArray() ?? [];
            if (objects.Length == 0) return;
            var bounds = SceneBounds.Around(objects.SelectMany(p => new[] { new Point2(p.Bounds.Left, p.Bounds.Top), new Point2(p.Bounds.Right, p.Bounds.Bottom) }));
            var singleComponent = Canvas.Selection.Count == 1 ? _view.Components.Find(p => Canvas.Selection.Contains(p.EntityId)) : null;
            var pivot = singleComponent?.Position ?? ToModel(new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            Execute(SelectionOperations.TransformSelection(_project, _view, Canvas.Selection, pivot, angle, mirrorX, mirrorY));
        });
    }
    private void BringForward_Click(object sender, RoutedEventArgs e) => ChangeDrawOrder(true);
    private void SendBackward_Click(object sender, RoutedEventArgs e) => ChangeDrawOrder(false);
    private void ChangeDrawOrder(bool front) => RunEditorAction(() =>
    { RequireEditableSelection(); Execute(SelectionOperations.SetDrawOrder(_project, _view, Canvas.Selection, front)); });

    private void RequireEditableSelection()
    {
        if (Canvas.Scene?.Primitives.Any(p => p.ViewId == _view.Id && p.Locked && Canvas.Selection.Contains(p.EntityId)) == true)
            throw new InvalidOperationException("Ontgrendel eerst de geselecteerde objecten en lagen.");
        if (_busy is not null || _saving) throw new InvalidOperationException("Wacht tot de actieve bewerking klaar is.");
    }
    private void RunEditorAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { App.Log(ex); if (App.IsBackgroundAcceptance) throw; MessageBox.Show(this, ex.Message, "Bewerking niet toegepast", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteFromClipboard(false);
    private void PasteReference_Click(object sender, RoutedEventArgs e) => PasteFromClipboard(true);
    private void PasteFromClipboard(bool reference)
    {
        RunEditorAction(() =>
        {
            if (Keyboard.FocusedElement is TextBox or ComboBox) return;
            if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage(); if (source is null) return;
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new MemoryStream(); encoder.Save(stream);
                InsertClipboardContent(null, stream.ToArray(), source.PixelWidth, source.PixelHeight, reference);
            }
            else if (Clipboard.ContainsText()) InsertClipboardContent(Clipboard.GetText(), null, 0, 0, reference);
            else Status("Het klembord bevat geen tekst of afbeelding.");
        });
    }
    internal void InsertClipboardContent(string? text, byte[]? image, int width, int height, bool reference)
    {
        var view = _view; var project = _project; var sheet = _sheet;
        if (image is null && string.IsNullOrEmpty(text)) return;
        if (image is { Length: > 104857600 } || (image is not null && (width <= 0 || height <= 0)))
            throw new ArgumentException("Gebruik een geldige afbeelding kleiner dan 100 MB.");
        var asset = image is null ? null : new ProjectAsset { FileName = "Geplakte afbeelding.png", MediaType = "image/png", Data = image };
        var annotation = new Annotation { Kind = asset is null ? AnnotationKind.Text : AnnotationKind.Image,
            Text = text ?? "", AssetId = asset?.Id, Points = [new Point2(20, 20)] };
        ApplyNewAnnotationDefaults(annotation);
        if (asset is not null) { annotation.WidthMm = Math.Min(200, width * 25.4 / 96); annotation.HeightMm = annotation.WidthMm * height / width; }
        DrawingLayer? layer = null;
        if (reference)
        {
            layer = sheet.Layers.FirstOrDefault(l => l.Id == "reference" && !l.Print) ??
                new DrawingLayer { Id = Ids.New(), Name = "Geplakte onderlegger", Locked = true, Print = false };
            annotation.LayerId = layer.Id; annotation.Locked = true;
        }
        else RequireEditableLayer(annotation.LayerId);
        var addLayer = layer is not null && !sheet.Layers.Contains(layer);
        Execute(new DelegateCommand("Klembordinhoud plaatsen", () =>
        { if (addLayer) sheet.Layers.Add(layer!); if (asset is not null) project.Assets.Add(asset); view.Annotations.Add(annotation); }, () =>
        { view.Annotations.Remove(annotation); if (asset is not null) project.Assets.Remove(asset); if (addLayer) sheet.Layers.Remove(layer!); }));
        Canvas.Select(annotation.Id);
    }
}
