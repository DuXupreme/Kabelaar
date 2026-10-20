using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;
using Kabelaar.Rendering;
using Microsoft.Win32;

namespace Kabelaar.App;

public partial class MainWindow
{
    private void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var dialog = new FormDialog(this, "Projectgegevens", 540);
        var title = dialog.Form.Text("Projectnaam", _project.Title);
        var drawing = dialog.Form.Text("Tekeningnummer", _project.DrawingNumber);
        var author = dialog.Form.Text("Getekend door", _project.Author);
        var customer = dialog.Form.Text("Klant / project", _project.Customer);
        var notes = dialog.Form.Text("Algemene montageaanwijzingen", _project.Notes, true);
        dialog.Form.Note("Deze gegevens worden gebruikt in het tekenkader, de lijsten en de export.");
        dialog.OnAccept = () => { RequireText(title.Text, "Projectnaam"); return true; };
        if (dialog.ShowDialog() != true) return;
        var before = (_project.Title, _project.DrawingNumber, _project.Author, _project.Customer, _project.Notes);
        var after = (title.Text.Trim(), drawing.Text.Trim(), author.Text.Trim(), customer.Text.Trim(), notes.Text);
        Execute(new DelegateCommand("Projectgegevens wijzigen",
            () => (_project.Title, _project.DrawingNumber, _project.Author, _project.Customer, _project.Notes) = after,
            () => (_project.Title, _project.DrawingNumber, _project.Author, _project.Customer, _project.Notes) = before));
    }

    private void Revision_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var dialog = new FormDialog(this, "Revisie vastleggen", 540);
        var revision = dialog.Form.Text("Nieuwe revisie", NextRevision(_project.Revision));
        var description = dialog.Form.Text("Wijzigingsomschrijving", "", true);
        var author = dialog.Form.Text("Door", _project.Author);
        var addTable = dialog.Form.Check("Revisietabel toevoegen als dit blad er nog geen heeft", true);
        dialog.Form.Note("De revisiegegevens veranderen mee in het tekenkader. Eerdere revisieregels blijven bewaard.");
        dialog.OnAccept = () =>
        {
            RequireText(revision.Text, "Revisie"); RequireText(description.Text, "Wijzigingsomschrijving");
            if (_project.Revisions.Any(r => r.Revision.Equals(revision.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Deze revisie staat al in de revisiehistorie. Kies een nieuwe revisie.");
            return true;
        };
        if (dialog.ShowDialog() != true) return;
        var entry = new RevisionEntry { Revision = revision.Text.Trim(), Description = description.Text.Trim(), Author = author.Text.Trim(), Date = DateTimeOffset.Now };
        var oldRevision = _project.Revision;
        var targetView = _view;
        var table = addTable.IsChecked == true && !_sheet.Views.SelectMany(v => v.Annotations).Any(a => a.Kind == AnnotationKind.RevisionTable)
            ? new Annotation { Kind = AnnotationKind.RevisionTable, Points = [new(10, 20)], WidthMm = 170, FontSizeMm = 2.5 } : null;
        Execute(new DelegateCommand("Revisie vastleggen",
            () => { _project.Revisions.Add(entry); _project.Revision = entry.Revision; if (table is not null) targetView.Annotations.Add(table); },
            () => { if (table is not null) targetView.Annotations.Remove(table); _project.Revisions.Remove(entry); _project.Revision = oldRevision; }));
    }

    private void AddSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var dialog = new FormDialog(this, "Tekenblad toevoegen", 480);
        var name = dialog.Form.Text("Bladnaam", $"Blad {_project.Sheets.Count + 1}");
        var format = dialog.Form.Choice("Papierformaat", PaperFormats, "A3 liggend");
        var copy = dialog.Form.Check("Opmaak en aanzichten van het actieve blad overnemen", false);
        dialog.Form.Note("Alle bladen horen bij dezelfde kabelboom. Extra aanzichten voegen geen onderdelen aan de stuklijst toe.");
        dialog.OnAccept = () => { RequireText(name.Text, "Bladnaam"); return true; };
        if (dialog.ShowDialog() != true) return;
        var sheet = copy.IsChecked == true ? EditData.Clone(_sheet) : new Sheet();
        sheet.Id = Ids.New(); sheet.Name = name.Text.Trim();
        (sheet.WidthMm, sheet.HeightMm) = PaperSize(PropertyForm.Value<string>(format));
        if (copy.IsChecked == true)
            foreach (var view in sheet.Views) AssignNewPlacementIds(view);
        var beforeSheet = _sheet; var beforeView = _view;
        Execute(new DelegateCommand("Tekenblad toevoegen",
            () => { _project.Sheets.Add(sheet); _sheet = sheet; _view = sheet.Views[0]; },
            () => { _project.Sheets.Remove(sheet); _sheet = beforeSheet; _view = beforeView; }));
        Canvas.ClearSelection(); Canvas.Fit();
    }

    private void AddView_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var dialog = new FormDialog(this, "Detailaanzicht toevoegen", 520);
        var name = dialog.Form.Text("Naam van het aanzicht", $"Detail {_sheet.Views.Count}");
        var x = dialog.Form.Number("Oorsprong X op het blad (mm)", 230);
        var y = dialog.Form.Number("Oorsprong Y op het blad (mm)", 35);
        var scale = dialog.Form.Number("Vergrotingsfactor van het aanzicht", 2);
        var free = dialog.Form.Check("Vrije opmaak", _view.Mode == LayoutMode.Free);
        var selectedOnly = dialog.Form.Check("Alleen geselecteerde onderdelen, routes en annotaties", Canvas.Selection.Count > 0);
        selectedOnly.IsEnabled = Canvas.Selection.Count > 0;
        dialog.Form.Note("Dit aanzicht gebruikt dezelfde fysieke onderdelen en aders. Plaatsing en labels kunnen onafhankelijk worden aangepast; materiaalaantallen blijven gelijk.");
        dialog.OnAccept = () =>
        {
            RequireText(name.Text, "Aanzichtnaam");
            EditData.Number(x.Text, "Oorsprong X"); EditData.Number(y.Text, "Oorsprong Y"); EditData.Number(scale.Text, "Vergrotingsfactor", .001);
            if (free.IsChecked != true) HarnessOperations.SetLayoutMode(_project, _view, LayoutMode.Scaled);
            return true;
        };
        if (dialog.ShowDialog() != true) return;
        var view = EditData.Clone(_view);
        if (selectedOnly.IsChecked == true)
        {
            var ids = Canvas.Selection.ToHashSet();
            var wires = _project.Assembly.Conductors.Where(w => ids.Contains(w.Id)).ToArray();
            foreach (var wire in wires)
            {
                ids.UnionWith(wire.BranchIds);
                if (wire.From.EntityId is { } from) ids.Add(from);
                if (wire.To.EntityId is { } to) ids.Add(to);
            }
            view.Components.RemoveAll(c => !ids.Contains(c.EntityId) && !ids.Contains(c.Id));
            view.Routes.RemoveAll(r => !ids.Contains(r.BranchId) && !ids.Contains(r.Id));
            view.Annotations.RemoveAll(a => !ids.Contains(a.Id));
        }
        AssignNewPlacementIds(view);
        view.Name = name.Text.Trim(); view.Origin = new(EditData.Number(x.Text, "X"), EditData.Number(y.Text, "Y"));
        view.Scale = EditData.Number(scale.Text, "Vergrotingsfactor", .001);
        view.Mode = free.IsChecked == true ? LayoutMode.Free : LayoutMode.Scaled;
        var targetSheet = _sheet; var beforeView = _view;
        Execute(new DelegateCommand("Detailaanzicht toevoegen",
            () => { targetSheet.Views.Add(view); _view = view; },
            () => { targetSheet.Views.Remove(view); _view = beforeView; }));
        Canvas.ClearSelection(); Canvas.Fit();
    }

    private void SheetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var sheet = _sheet; var view = _view;
        var dialog = new FormDialog(this, "Blad en actief aanzicht", 560);
        var name = dialog.Form.Text("Bladnaam", sheet.Name);
        var width = dialog.Form.Number("Bladbreedte (mm)", sheet.WidthMm);
        var height = dialog.Form.Number("Bladhoogte (mm)", sheet.HeightMm);
        var margin = dialog.Form.Number("Kaderafstand vanaf papierrand (mm)", sheet.MarginMm);
        var printScale = dialog.Form.Number("Afdrukschaal als factor (1 = 1:1, 0,5 = 1:2)", sheet.PrintScale);
        dialog.Form.Heading("Actief aanzicht");
        var viewName = dialog.Form.Text("Aanzichtnaam", view.Name);
        var originX = dialog.Form.Number("Oorsprong X op blad (mm)", view.Origin.X);
        var originY = dialog.Form.Number("Oorsprong Y op blad (mm)", view.Origin.Y);
        var scale = dialog.Form.Number("Vergrotingsfactor aanzicht", view.Scale);
        var free = dialog.Form.Check("Vrije opmaak", view.Mode == LayoutMode.Free);
        var logo = sheet.LogoAssetId;
        ProjectAsset? newLogo = null;
        dialog.Form.Heading("Logo");
        var logoLabel = new TextBlock { Text = _project.Assets.Find(a => a.Id == logo)?.FileName ?? "Geen logo", Margin = new(0, 0, 0, 5) };
        dialog.Form.Panel.Children.Add(logoLabel);
        dialog.Form.Action("Logo kiezen…", () =>
        {
            var picker = new OpenFileDialog { Title = "Logo in het tekenkader", Filter = "Afbeeldingen|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (picker.ShowDialog(dialog) != true) return;
            var file = new FileInfo(picker.FileName);
            if (file.Length > 25 * 1024 * 1024) throw new ArgumentException("Kies een logo kleiner dan 25 MB.");
            var data = File.ReadAllBytes(file.FullName);
            using var bitmap = SkiaSharp.SKBitmap.Decode(data);
            if (bitmap is null) throw new ArgumentException("Deze afbeelding kan niet worden gelezen.");
            newLogo = new ProjectAsset { FileName = file.Name, MediaType = ImageMediaType(file.Extension), Data = data };
            logo = newLogo.Id; logoLabel.Text = file.Name;
        });
        dialog.Form.Action("Logo verwijderen uit dit blad", () => { logo = null; newLogo = null; logoLabel.Text = "Geen logo"; });
        var deleteView = dialog.Form.Check("Actief aanzicht verwijderen", false); deleteView.IsEnabled = sheet.Views.Count > 1;
        var deleteSheet = dialog.Form.Check("Dit hele blad verwijderen", false); deleteSheet.IsEnabled = _project.Sheets.Count > 1;
        dialog.OnAccept = () =>
        {
            RequireText(name.Text, "Bladnaam"); RequireText(viewName.Text, "Aanzichtnaam");
            var w = EditData.Number(width.Text, "Breedte", 50); var h = EditData.Number(height.Text, "Hoogte", 50);
            var m = EditData.Number(margin.Text, "Kaderafstand", 0);
            if (w > 5000 || h > 5000 || m >= Math.Min(w, h) / 2 - 15) throw new ArgumentException("Bladformaat of kaderafstand is buiten het bruikbare bereik.");
            EditData.Number(printScale.Text, "Afdrukschaal", .001); EditData.Number(scale.Text, "Vergrotingsfactor", .001);
            EditData.Number(originX.Text, "Oorsprong X"); EditData.Number(originY.Text, "Oorsprong Y");
            if (free.IsChecked != (view.Mode == LayoutMode.Free) && free.IsChecked != true)
            {
                // Re-entering scaled mode is a physical operation; delegate to the tested domain command below.
                var testProject = EditData.Clone(_project);
                var testView = testProject.Sheets.First(s => s.Id == sheet.Id).Views.First(v => v.Id == view.Id);
                HarnessOperations.SetLayoutMode(testProject, testView, LayoutMode.Scaled).Execute();
            }
            return true;
        };
        if (dialog.ShowDialog() != true) return;
        if (deleteSheet.IsChecked == true)
        {
            var index = _project.Sheets.IndexOf(sheet);
            Execute(new DelegateCommand("Tekenblad verwijderen", () => _project.Sheets.Remove(sheet), () => _project.Sheets.Insert(index, sheet)));
            Canvas.ClearSelection(); Canvas.Fit(); return;
        }
        if (deleteView.IsChecked == true)
        {
            var index = sheet.Views.IndexOf(view);
            Execute(new DelegateCommand("Aanzicht verwijderen", () => sheet.Views.Remove(view), () => sheet.Views.Insert(index, view)));
            Canvas.ClearSelection(); Canvas.Fit(); return;
        }
        var beforeSheet = (sheet.Name, sheet.WidthMm, sheet.HeightMm, sheet.MarginMm, sheet.PrintScale, sheet.LogoAssetId);
        var afterSheet = (name.Text.Trim(), EditData.Number(width.Text, "Breedte"), EditData.Number(height.Text, "Hoogte"), EditData.Number(margin.Text, "Kaderafstand"),
            EditData.Number(printScale.Text, "Afdrukschaal"), logo);
        var beforeView = (view.Name, view.Origin, view.Scale);
        var afterView = (viewName.Text.Trim(), new Point2(EditData.Number(originX.Text, "X"), EditData.Number(originY.Text, "Y")), EditData.Number(scale.Text, "Vergrotingsfactor"));
        var changes = new List<IEditCommand>
        {
            new DelegateCommand("Blad en aanzicht instellen",
                () => { (sheet.Name, sheet.WidthMm, sheet.HeightMm, sheet.MarginMm, sheet.PrintScale, sheet.LogoAssetId) = afterSheet; (view.Name, view.Origin, view.Scale) = afterView; if (newLogo is not null) _project.Assets.Add(newLogo); },
                () => { (sheet.Name, sheet.WidthMm, sheet.HeightMm, sheet.MarginMm, sheet.PrintScale, sheet.LogoAssetId) = beforeSheet; (view.Name, view.Origin, view.Scale) = beforeView; if (newLogo is not null) _project.Assets.Remove(newLogo); })
        };
        var mode = free.IsChecked == true ? LayoutMode.Free : LayoutMode.Scaled;
        if (mode != view.Mode) changes.Add(HarnessOperations.SetLayoutMode(_project, view, mode));
        Execute(new CompositeCommand("Bladinstellingen wijzigen", [.. changes]));
        Canvas.Fit();
    }

    private void Layers_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var sheet = _sheet;
        var dialog = new FormDialog(this, "Lagen", 540);
        var values = new List<(DrawingLayer Layer, TextBox Name, CheckBox Visible, CheckBox Locked, CheckBox Print)>();
        foreach (var layer in sheet.Layers)
        {
            dialog.Form.Heading(layer.Name);
            var name = dialog.Form.Text("Laagnaam", layer.Name);
            var visible = dialog.Form.Check("Zichtbaar in de editor", layer.Visible);
            var locked = dialog.Form.Check("Vergrendeld voor selecteren en slepen", layer.Locked);
            var print = dialog.Form.Check("Meenemen in afdruk en export", layer.Print);
            values.Add((layer, name, visible, locked, print));
        }
        dialog.Form.Heading("Nieuwe laag");
        var extraName = dialog.Form.Text("Naam (leeg laten om geen laag toe te voegen)", "");
        dialog.Form.Note("De standaardlagen blijven bestaan. Objecten kunnen via hun eigenschappen aan een laag worden toegewezen.");
        dialog.OnAccept = () =>
        {
            foreach (var value in values) RequireText(value.Name.Text, "Laagnaam");
            var names = values.Select(v => v.Name.Text.Trim()).Concat(string.IsNullOrWhiteSpace(extraName.Text) ? [] : new[] { extraName.Text.Trim() }).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ArgumentException("Iedere laag moet een unieke naam hebben.");
            return true;
        };
        if (dialog.ShowDialog() != true) return;
        var before = values.Select(v => (v.Layer.Name, v.Layer.Visible, v.Layer.Locked, v.Layer.Print)).ToArray();
        var after = values.Select(v => (v.Name.Text.Trim(), v.Visible.IsChecked == true, v.Locked.IsChecked == true, v.Print.IsChecked == true)).ToArray();
        var extra = string.IsNullOrWhiteSpace(extraName.Text) ? null : new DrawingLayer { Name = extraName.Text.Trim() };
        Execute(new DelegateCommand("Lagen wijzigen",
            () => { for (var i = 0; i < values.Count; i++) (values[i].Layer.Name, values[i].Layer.Visible, values[i].Layer.Locked, values[i].Layer.Print) = after[i]; if (extra is not null) sheet.Layers.Add(extra); },
            () => { if (extra is not null) sheet.Layers.Remove(extra); for (var i = 0; i < values.Count; i++) (values[i].Layer.Name, values[i].Layer.Visible, values[i].Layer.Locked, values[i].Layer.Print) = before[i]; }));
        Canvas.ClearSelection();
    }

    private async void DraftExport_Click(object sender, RoutedEventArgs e) => await ExportDocumentsAsync(true);
    private async void ProductionExport_Click(object sender, RoutedEventArgs e) => await ExportDocumentsAsync(false);

    private async Task ExportDocumentsAsync(bool draft)
    {
        if (_busy is not null) return;
        var issues = ExportService.Validate(_project);
        var blockers = issues.Where(i => i.IsStructural || (!draft && i.Severity == Severity.Error)).ToArray();
        if (blockers.Length > 0)
        {
            Refresh(); TablesTab.SelectedItem = IssuesTab;
            MessageBox.Show(this, $"De export is geblokkeerd door {blockers.Length} {(blockers.Length == 1 ? "fout" : "fouten")}.\n\n" +
                string.Join("\n", blockers.Take(8).Select(i => "• " + i.Message)) + (blockers.Length > 8 ? "\n\nBekijk de controlelijst voor alle meldingen." : ""),
                draft ? "Projectstructuur controleren" : "Nog niet gereed voor productie", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var picker = new OpenFolderDialog { Title = draft ? "Map voor conceptexport" : "Map voor productie-export", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        await BusyAsync(draft ? "Concepttekeningen en lijsten exporteren…" : "Productietekeningen en lijsten exporteren…", async cancellationToken =>
        {
            var snapshotSource = _project; // BusyAsync prevents edits until the detached snapshot is captured.
            var result = await Task.Run(() => ExportService.ExportSetAsync(snapshotSource, picker.FolderName, draft, cancellationToken), cancellationToken);
            var outputDirectory = Path.GetDirectoryName(result.Files.First())!;
            Status($"{result.Files.Count} bestanden geëxporteerd naar {outputDirectory}");
            MessageBox.Show(this, $"{result.Files.Count} bestanden opgeslagen:\n\n{outputDirectory}\n\nPDF, SVG, PNG, draadlijst, kniplijst, pinlijst en stuklijst." +
                (draft ? "\n\nAlle uitvoer is als CONCEPT gemarkeerd." : "") +
                (result.Issues.Count > 0 ? $"\n{result.Issues.Count} aandachtspunten staan in de controlelijst." : ""),
                draft ? "Conceptexport gereed" : "Productie-export gereed", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null) return;
        var dialog = new FormDialog(this, "Afdrukvoorbeeld");
        dialog.Form.Note("Kabelaar maakt een vector-PDF van alle bladen en opent die in je PDF-lezer. Daar kies je de printer, het papierformaat en 100% / ware grootte.");
        var draft = dialog.Form.Check("Concept met watermerk", true);
        if (dialog.ShowDialog() != true) return;
        var isDraft = draft.IsChecked == true;
        var issues = ExportService.Validate(_project);
        if (issues.Any(i => i.IsStructural || (!isDraft && i.Severity == Severity.Error)))
        {
            Refresh(); TablesTab.SelectedItem = IssuesTab;
            MessageBox.Show(this, "Los eerst de fouten in de controlelijst op. Een concept is mogelijk zolang de projectstructuur geldig is.",
                "Afdrukvoorbeeld geblokkeerd", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await BusyAsync("Afdrukvoorbeeld maken…", async cancellationToken =>
        {
            var directory = Path.Combine(App.DataDirectory, "Afdrukvoorbeelden");
            var path = Path.Combine(directory, $"Kabelaar-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pdf");
            var snapshotSource = _project;
            var result = await Task.Run(() => ExportService.ExportPdfAsync(snapshotSource, path, isDraft, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo(result.Files[0]) { UseShellExecute = true });
            Status("Vector-PDF geopend. Kies bij het afdrukken 100% / ware grootte.");
        });
    }

    private static readonly string[] PaperFormats = ["A4 liggend", "A3 liggend", "A2 liggend", "A1 liggend", "A0 liggend", "A4 staand", "A3 staand"];
    private static (double Width, double Height) PaperSize(string name) => name switch
    {
        "A4 liggend" => (297, 210), "A2 liggend" => (594, 420), "A1 liggend" => (841, 594), "A0 liggend" => (1189, 841),
        "A4 staand" => (210, 297), "A3 staand" => (297, 420), _ => (420, 297)
    };
    private static void AssignNewPlacementIds(DrawingView view)
    {
        view.Id = Ids.New();
        foreach (var component in view.Components) component.Id = Ids.New();
        foreach (var route in view.Routes) route.Id = Ids.New();
        foreach (var annotation in view.Annotations) annotation.Id = Ids.New();
    }
    private static string RequireText(string value, string label) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"{label} mag niet leeg zijn.");
    private static string NextRevision(string revision) => revision.Length == 1 && revision[0] is >= 'A' and < 'Z'
        ? ((char)(revision[0] + 1)).ToString() : revision + ".1";
    private static string ImageMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/png"
    };
}
