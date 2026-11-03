using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private IEditCommand WirePresentationCommand(Conductor wire, WirePresentation next, string name)
    {
        var view = _view; var before = view.WirePresentations.Find(p => p.ConductorId == wire.Id);
        var index = before is null ? -1 : view.WirePresentations.IndexOf(before);
        return new DelegateCommand(name, () => { if (index < 0) view.WirePresentations.Add(next); else view.WirePresentations[index] = next; },
            () => { if (index < 0) view.WirePresentations.Remove(next); else view.WirePresentations[index] = before!; });
    }
    private WirePresentation WireStyle(Conductor wire) => _view.WirePresentations.Find(p => p.ConductorId == wire.Id)?.Copy() ?? new() { ConductorId = wire.Id };
    private void BuildWireLabelProperties(PropertyForm form, Conductor wire)
    {
        var original = WireStyle(wire);
        form.Heading("Draadlabel en kruisingen");
        var mode = form.Choice("Label", new[] { "Automatisch", "Eigen tekst", "Verborgen" },
            original.LabelText == "" || original.ShowLabel == false ? "Verborgen" : original.LabelText is null ? "Automatisch" : "Eigen tekst");
        var text = form.Text("Eigen labeltekst (leeg = verborgen)", original.LabelText ?? "");
        var position = form.Text("Labelpositie X; Y (leeg = automatisch)", original.LabelPosition is { } p ? FormatPropertyPoints([p]) : "");
        var angle = form.Number("Labelrotatie (graden)", original.LabelRotationDegrees);
        var bridges = form.Check("Oversteekboogjes bij kruisingen", original.ShowCrossingBridges);
        var radius = form.Number("Straal oversteekboog (mm)", original.CrossingRadiusMm);
        form.Action("Label en kruisingen toepassen", () =>
        {
            RequireEditableSelection();
            var next = WireStyle(wire); var choice = PropertyForm.Value<string>(mode);
            next.LabelText = choice == "Automatisch" ? null : choice == "Verborgen" ? "" : text.Text;
            next.ShowLabel = choice == "Verborgen" ? false : choice == "Eigen tekst" ? true : null;
            next.LabelPosition = string.IsNullOrWhiteSpace(position.Text) ? null : ParsePropertyPoints(position.Text, 1).Single();
            next.LabelRotationDegrees = EditData.Number(angle.Text, "Rotatie");
            next.ShowCrossingBridges = bridges.IsChecked == true;
            next.CrossingRadiusMm = EditData.Number(radius.Text, "Straal", .1);
            if (next.CrossingRadiusMm > 100) throw new ArgumentException("Gebruik een boogstraal van maximaal 100 mm.");
            Execute(WirePresentationCommand(wire, next, "Draadlabel en kruisingen"));
        });
        form.Note("Dit label hoort bij dit aanzicht. Draadnummer, signaal en fysieke verbindingen blijven behouden.");
    }

    private void BuildReferenceProperties(PropertyForm form, PhysicalReferencePoint reference, PlacedComponent? placement)
    {
        var name = form.Text("Naam", reference.Name); var description = form.Text("Omschrijving", reference.Description, true);
        var symbol = form.Choice("Symbool", Enum.GetValues<NodeSymbol>(), reference.Symbol, s => s == NodeSymbol.Ground ? "Massa" : "Algemeen");
        var max = form.Number("Maximaal aantal draadeinden (0 = onbeperkt)", reference.MaxWires);
        form.Note("Aansluitbaar referentiepunt; geen fysieke splice of automatisch geteld montagemateriaal. Leg ringterminals en andere gemonteerde delen afzonderlijk vast.");
        form.Action("Referentiepunt toepassen", () =>
        {
            var count = EditData.Number(max.Text, "Aantal", 0);
            if (count != Math.Truncate(count) || count > 1000) throw new ArgumentException("Gebruik een geheel aantal tot 1000.");
            var next = EditData.Clone(reference); next.Name = RequireText(name.Text, "Naam"); next.Description = description.Text; next.MaxWires = (int)count;
            next.Symbol = PropertyForm.Value<NodeSymbol>(symbol);
            Change("Referentiepunt wijzigen", _project.Assembly.ReferencePoints, reference, next);
        }, true);
    }

    private void BuildExtendedAnnotationProperties(PropertyForm form, Annotation annotation)
    {
        if (annotation.Kind == AnnotationKind.GraphicalNode)
            form.Note("Dit is alleen een tekensymbool. Een draad die het raakt krijgt daardoor geen elektrische verbinding.");
        if (IsTable(annotation)) form.Action(annotation.Kind == AnnotationKind.FreeTable ? "Tabelcellen en opmaak…" : "Tabelopmaak…", () => EditTable(annotation));
        if (annotation.Kind is AnnotationKind.Text or AnnotationKind.Leader or AnnotationKind.GraphicalNode)
        {
            var frame = form.Check("Tekstkader", annotation.TextFrame);
            var arrow = form.Number("Pijlgrootte (mm)", annotation.ArrowSizeMm);
            var rotation = form.Number("Tekstrotatie (graden)", annotation.RotationDegrees);
            var size = annotation.Kind == AnnotationKind.GraphicalNode ? form.Number("Symbooldiameter (mm)", annotation.WidthMm) : null;
            var symbol = annotation.Kind == AnnotationKind.GraphicalNode ? form.Choice("Symbool", Enum.GetValues<NodeSymbol>(), annotation.NodeSymbol, s => s == NodeSymbol.Ground ? "Massa" : "Algemeen") : null;
            form.Action("Detailopmaak toepassen", () =>
            {
                RequireEditableSelection(); var next = EditData.Clone(annotation);
                next.TextFrame = frame.IsChecked == true; next.ArrowSizeMm = EditData.Number(arrow.Text, "Pijlgrootte", .1);
                next.RotationDegrees = EditData.Number(rotation.Text, "Rotatie");
                if (size is not null) next.WidthMm = EditData.Number(size.Text, "Diameter", .1);
                if (symbol is not null) next.NodeSymbol = PropertyForm.Value<NodeSymbol>(symbol);
                Change("Detailopmaak wijzigen", _view.Annotations, annotation, next);
            });
        }
    }

    private void EditTable(Annotation annotation)
    {
        RequireEditableSelection();
        var next = EditData.Clone(annotation); var free = next.Kind == AnnotationKind.FreeTable;
        var dialog = new FormDialog(this, free ? "Vrije tabel" : "Gekoppelde tabel opmaken", 800);
        var form = dialog.Form;
        var width = form.Number("Totale breedte (mm)", next.WidthMm);
        var rowHeight = form.Number("Minimale rijhoogte (mm; 0 = automatisch)", next.RowHeightMm);
        var padding = form.Number("Celmarge (mm)", next.CellPaddingMm);
        var header = form.Check("Koprij weergeven", next.ShowTableHeader);
        var align = form.Choice("Standaard celuitlijning", Enum.GetValues<TableCellAlignment>(), next.CellAlignment);
        var widths = form.Text("Kolombreedtes in mm, gescheiden met puntkomma (leeg = gelijk)", string.Join("; ", next.ColumnWidthsMm.Select(EditData.Format)));
        var heads = free ? form.Text("Kolomkoppen: één kop per regel", string.Join(Environment.NewLine, next.Columns), true) : null;
        var columnAlignment = form.Text("Uitlijning per kolom: Left; Center; Right (leeg = standaard)", string.Join("; ", next.ColumnAlignments));
        DataGrid? grid = null;
        if (free)
        {
            if (next.Columns.Count == 0) next.Columns = ["Kolom 1"];
            grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false, CanUserDeleteRows = false,
                Height = 220, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader };
            void Commit()
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
                var titles = heads!.Text.Replace("\r", "").Split('\n').ToList();
                if (titles.Count != next.Columns.Count) throw new ArgumentException("Gebruik de kolomknoppen om het aantal kolommen te wijzigen.");
                next.Columns = titles;
            }
            void Rebuild()
            {
                grid.ItemsSource = null; grid.Columns.Clear();
                for (var i = 0; i < next.Columns.Count; i++)
                    grid.Columns.Add(new DataGridTextColumn { Header = next.Columns[i], Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                grid.ItemsSource = next.Rows.ToArray(); heads!.Text = string.Join(Environment.NewLine, next.Columns);
            }
            form.Panel.Children.Add(grid); Rebuild();
            form.Action("Rij toevoegen", () => { Commit(); next.Rows.Add(new TableRow { Cells = next.Columns.Select(_ => "").ToList() }); Rebuild(); });
            form.Action("Geselecteerde rij verwijderen", () =>
            { Commit(); if (next.Rows.Count <= 1) throw new ArgumentException("Behoud minstens één rij in de tabel."); if (grid.CurrentItem is TableRow row) next.Rows.Remove(row); Rebuild(); });
            form.Action("Kolom toevoegen", () =>
            {
                Commit(); next.Columns.Add($"Kolom {next.Columns.Count + 1}");
                foreach (var row in next.Rows) row.Cells.Add("");
                next.ColumnWidthsMm.Clear(); next.ColumnAlignments.Clear(); widths.Text = ""; columnAlignment.Text = ""; Rebuild();
            });
            form.Action("Geselecteerde kolom verwijderen", () =>
            {
                Commit(); var index = grid.CurrentColumn?.DisplayIndex ?? -1;
                if (index < 0 || next.Columns.Count <= 1) return;
                next.Columns.RemoveAt(index); foreach (var row in next.Rows) if (row.Cells.Count > index) row.Cells.RemoveAt(index);
                next.ColumnWidthsMm.Clear(); next.ColumnAlignments.Clear(); widths.Text = ""; columnAlignment.Text = ""; Rebuild();
            });
        }
        else form.Note("De celinhoud blijft gekoppeld aan de actuele productiegegevens.");
        dialog.OnAccept = () =>
        {
            grid?.CommitEdit(DataGridEditingUnit.Cell, true); grid?.CommitEdit(DataGridEditingUnit.Row, true);
            next.WidthMm = EditData.Number(width.Text, "Breedte", 1); next.RowHeightMm = EditData.Number(rowHeight.Text, "Rijhoogte", 0);
            next.CellPaddingMm = EditData.Number(padding.Text, "Celmarge", 0); next.ShowTableHeader = header.IsChecked == true;
            next.CellAlignment = PropertyForm.Value<TableCellAlignment>(align);
            next.ColumnWidthsMm = widths.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => EditData.Number(v, "Kolombreedte", .1)).ToList();
            next.ColumnAlignments = columnAlignment.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => Enum.TryParse<TableCellAlignment>(v, true, out var a) && Enum.IsDefined(a) ? a : throw new ArgumentException("Gebruik Left, Center of Right voor de uitlijning.")).ToList();
            if (free)
            {
                if (next.Rows.Count == 0) throw new ArgumentException("Een vrije tabel vereist minstens één rij.");
                var columns = heads!.Text.Replace("\r", "").Split('\n').ToList();
                if (columns.Count != next.Columns.Count) throw new ArgumentException("Gebruik de kolomknoppen om het aantal kolommen te wijzigen.");
                next.Columns = columns;
            }
            var columnCount = next.Columns.Count > 0 ? next.Columns.Count : Kabelaar.Rendering.SceneBuilder.TableData(_project, next.Kind).Columns.Count;
            if (next.ColumnWidthsMm.Count != 0 && next.ColumnWidthsMm.Count != columnCount) throw new ArgumentException("Geef voor elke kolom een breedte op.");
            if (next.ColumnAlignments.Count != 0 && next.ColumnAlignments.Count != columnCount) throw new ArgumentException("Geef voor elke kolom een uitlijning op.");
            Change("Tabel wijzigen", _view.Annotations, annotation, next); return true;
        };
        dialog.ShowDialog();
    }

    private void ExtendedTitleBlock_Click(object sender, RoutedEventArgs e)
    {
        var sheet = _sheet; var next = EditData.Clone(sheet.TitleBlock);
        var dialog = new FormDialog(this, "Titelblok en bladzones", 560); var form = dialog.Form;
        var enabled = form.Check("Uitgebreid titelblok gebruiken", next.Enabled);
        var drawn = form.Text("Getekend door", next.DrawnBy); var date = form.Text("Tekendatum", next.Date);
        var checkedBy = form.Text("Gecontroleerd door", next.CheckedBy); var checkedDate = form.Text("Controledatum", next.CheckedDate);
        var approved = form.Text("Goedgekeurd door", next.ApprovedBy); var approvedDate = form.Text("Goedkeuringsdatum", next.ApprovedDate);
        var material = form.Text("Materiaal", next.Material); var status = form.Text("Documentstatus", next.DocumentStatus);
        var tolerance = form.Text("Algemene toleranties", next.GeneralTolerances);
        var columns = form.Number("Zonekolommen (0 = uit)", sheet.ZoneColumns); var rows = form.Number("Zonerijen (0 = uit)", sheet.ZoneRows);
        form.Note("Het bestaande kader blijft behouden zolang het uitgebreide titelblok niet is ingeschakeld.");
        dialog.OnAccept = () =>
        {
            next.Enabled = enabled.IsChecked == true; next.DrawnBy = drawn.Text; next.Date = date.Text;
            next.CheckedBy = checkedBy.Text; next.CheckedDate = checkedDate.Text; next.ApprovedBy = approved.Text;
            next.ApprovedDate = approvedDate.Text; next.Material = material.Text; next.DocumentStatus = status.Text; next.GeneralTolerances = tolerance.Text;
            var x = EditData.Number(columns.Text, "Kolommen", 0); var y = EditData.Number(rows.Text, "Rijen", 0);
            if (x != Math.Truncate(x) || y != Math.Truncate(y) || x > 26 || y > 26) throw new ArgumentException("Gebruik gehele aantallen van 0 tot 26.");
            var previous = sheet.TitleBlock; var oldX = sheet.ZoneColumns; var oldY = sheet.ZoneRows;
            Execute(new DelegateCommand("Titelblok wijzigen", () => { sheet.TitleBlock = next; sheet.ZoneColumns = (int)x; sheet.ZoneRows = (int)y; },
                () => { sheet.TitleBlock = previous; sheet.ZoneColumns = oldX; sheet.ZoneRows = oldY; })); return true;
        };
        dialog.ShowDialog();
    }
}
