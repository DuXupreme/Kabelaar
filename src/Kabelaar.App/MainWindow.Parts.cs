using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Microsoft.Win32;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async void AddPart_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PartEditorWindow(this, new());
        if (dialog.ShowDialog() != true) return;
        Add("Onderdeel definiëren", _project.Parts, dialog.Result);
        LibraryList.SelectedItem = dialog.Result; await PersistLibraryAsync();
    }
    private async void EditPart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null || _saving) return;
        var original = SelectedPart;
        if (original is null) { Status("Selecteer een bibliotheekonderdeel."); return; }
        CadImportResult? cad = null;
        var source = _project.Assets.Find(a => a.Id == original.SourceAssetId);
        if (source is not null && source.MediaType.Equals("model/step", StringComparison.OrdinalIgnoreCase))
        {
            await BusyAsync("Ingesloten STEP-model voorbereiden…", async token =>
            {
                var imported = await Task.Run(() => ImportEmbeddedStepPreviewAsync(source, token), token);
                token.ThrowIfCancellationRequested();
                cad = imported;
            });
            if (cad is null) return;
        }
        var proposed = EditData.Clone(original);
        proposed.Revision = int.TryParse(original.Revision, out var rev) ? (rev + 1).ToString() : original.Revision + ".1";
        // The CAD result supplies optional projections and the mesh only. The editor starts
        // with this saved symbol and pin mapping, replacing geometry upon a projection choice.
        var dialog = new PartEditorWindow(this, proposed, cad);
        if (dialog.ShowDialog() != true) return;
        var next = dialog.Result;
        var changes = PartRevisionDifferences(original, next);
        var pinsMap = next.Pins.ToDictionary(p => p.Id, _ => Ids.New());
        next.Id = Ids.New();
        foreach (var pin in next.Pins) pin.Id = pinsMap[pin.Id];
        var review = new FormDialog(this, "Onderdeelrevisie — verschillen", 630);
        review.Form.Note(string.Join("\n", changes));
        review.Form.Note("Deze revisie wordt als nieuwe definitie bewaard. Bestaande projecten houden hun eigen onderdeelkopie.");
        var update = review.Form.Check("Alle toepassingen van dit onderdeel in het huidige project bijwerken", false);
        if (review.ShowDialog() != true) return;
        try
        {
            var command = update.IsChecked == true ? UpdatePartRevision(original, next, pinsMap) :
                new DelegateCommand("Onderdeelrevisie bewaren", () => _project.Parts.Add(next), () => _project.Parts.Remove(next));
            Execute(command); LibraryList.SelectedItem = next; await PersistLibraryAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Revisie niet toegepast"); }
    }
    private static async Task<CadImportResult> ImportEmbeddedStepPreviewAsync(ProjectAsset source, CancellationToken token)
    {
        if (source.Data.Length > 256L * 1024 * 1024) throw new IOException("Het ingesloten STEP-model is groter dan 256 MB.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kabelaar", "PartPreview"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var input = Path.Combine(directory, "embedded.step");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(input, source.Data, token);
            return await new CadImportService().ImportAsync(input, token);
        }
        finally
        {
            // Only this newly created file and its now-empty directory are removed.
            try { File.Delete(input); }
            catch (IOException ex) { App.Log(ex); }
            catch (UnauthorizedAccessException ex) { App.Log(ex); }
            try { Directory.Delete(directory, false); }
            catch (IOException ex) { App.Log(ex); }
            catch (UnauthorizedAccessException ex) { App.Log(ex); }
        }
    }
    private IReadOnlyList<string> PartRevisionDifferences(PartDefinition before, PartDefinition after)
    {
        var changes = new List<string>();
        static string Text(string? value) => string.IsNullOrEmpty(value) ? "(niet ingesteld)" : value;
        void Difference<T>(string label, T oldValue, T newValue, Func<T, string>? display = null)
        {
            if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
            var oldText = display is null ? Text(oldValue is null ? null : oldValue.ToString()) : display(oldValue);
            var newText = display is null ? Text(newValue is null ? null : newValue.ToString()) : display(newValue);
            changes.Add($"{label}: {oldText} → {newText}");
        }
        Difference("Revisie", before.Revision, after.Revision);
        Difference("Naam", before.Name, after.Name);
        Difference("Artikelnummer", before.PartNumber, after.PartNumber);
        Difference("Leverancier", before.Supplier, after.Supplier);
        Difference("Soort", before.Kind, after.Kind);
        Difference("Omschrijving", before.Description, after.Description);
        Difference("Kleur", before.Color, after.Color);
        Difference("Draaddoorsnede", before.CrossSectionMm2, after.CrossSectionMm2, value => $"{value:g} mm²");
        Difference("Breedte", before.WidthMm, after.WidthMm, value => $"{value:g} mm");
        Difference("Hoogte", before.HeightMm, after.HeightMm, value => $"{value:g} mm");
        Difference("Terminal verplicht", before.RequiresTerminal, after.RequiresTerminal, value => value ? "ja" : "nee");
        Difference("Seal verplicht", before.RequiresSeal, after.RequiresSeal, value => value ? "ja" : "nee");
        Difference("Minimale doorsnede", before.MinCrossSectionMm2, after.MinCrossSectionMm2,
            value => value.HasValue ? $"{value:g} mm²" : "(niet ingesteld)");
        Difference("Maximale doorsnede", before.MaxCrossSectionMm2, after.MaxCrossSectionMm2,
            value => value.HasValue ? $"{value:g} mm²" : "(niet ingesteld)");
        string Source(string? id)
        {
            if (id is null) return "(geen bron)";
            var asset = _project.Assets.Find(a => a.Id == id);
            return asset is null ? $"Onbekende bron [{id}]" : $"{asset.FileName} [{asset.Id}]";
        }
        Difference("Bronmodel", before.SourceAssetId, after.SourceAssetId, Source);
        Difference("Projectie", before.Projection, after.Projection);
        var symbolChanged = before.Symbol.Count != after.Symbol.Count ||
            before.Symbol.Where((path, index) => index < after.Symbol.Count &&
                (path.Closed != after.Symbol[index].Closed || path.Hidden != after.Symbol[index].Hidden ||
                 path.Smooth != after.Symbol[index].Smooth || !path.Points.SequenceEqual(after.Symbol[index].Points))).Any();
        if (symbolChanged)
            changes.Add($"2D-symbool gewijzigd: {before.Symbol.Count} paden / {before.Symbol.Sum(p => p.Points.Count)} punten → " +
                $"{after.Symbol.Count} paden / {after.Symbol.Sum(p => p.Points.Count)} punten (geometrie of lijnstijl).");
        for (var i = 0; i < Math.Max(before.Labels.Count, after.Labels.Count); i++)
        {
            static string Label(SymbolLabel label) => $"\"{label.Text}\" op ({label.Position.X:g}, {label.Position.Y:g}) mm, hoogte {label.HeightMm:g} mm";
            if (i >= before.Labels.Count) changes.Add($"Symbooltekst toegevoegd: {Label(after.Labels[i])}");
            else if (i >= after.Labels.Count) changes.Add($"Symbooltekst verwijderd: {Label(before.Labels[i])}");
            else
            {
                var oldLabel = before.Labels[i]; var newLabel = after.Labels[i];
                if (oldLabel.Text != newLabel.Text || oldLabel.Position != newLabel.Position || oldLabel.HeightMm != newLabel.HeightMm)
                    changes.Add($"Symbooltekst: {Label(oldLabel)} → {Label(newLabel)}");
            }
        }
        Difference("Aantal pinnen", before.Pins.Count, after.Pins.Count);
        var previousPinIds = before.Pins.Select(p => p.Id).ToHashSet();
        foreach (var pin in before.Pins)
        {
            var changed = after.Pins.Find(p => p.Id == pin.Id);
            if (changed is null) changes.Add($"Pin {pin.Label}: verwijderd");
            else if (pin.Position != changed.Position || pin.Label != changed.Label || pin.MaxWires != changed.MaxWires)
                changes.Add($"Pin {pin.Label} → {changed.Label}: ({pin.Position.X:g}, {pin.Position.Y:g}) → ({changed.Position.X:g}, {changed.Position.Y:g}) mm, capaciteit {pin.MaxWires} → {changed.MaxWires}");
        }
        foreach (var pin in after.Pins.Where(p => !previousPinIds.Contains(p.Id)))
            changes.Add($"Pin {pin.Label}: toegevoegd op ({pin.Position.X:g}, {pin.Position.Y:g}) mm, capaciteit {pin.MaxWires}");
        if (changes.Count == 0) changes.Add("Geen inhoudelijke wijzigingen.");
        return changes;
    }
    private IEditCommand UpdatePartRevision(PartDefinition old, PartDefinition next, Dictionary<string, string> pinMap)
    {
        if (old.Kind != next.Kind) throw new ArgumentException("Een toegepast onderdeel kan niet naar een andere soort worden omgezet. Bewaar de revisie afzonderlijk.");
        var references = new List<(Action<string> Set, string Before)>();
        void Ref(string? id, Action<string> set) { if (id == old.Id) references.Add((set, old.Id)); }
        var connectors = _project.Assembly.Connectors.Where(c => c.PartId == old.Id).ToArray();
        foreach (var connector in _project.Assembly.Connectors)
        {
            Ref(connector.PartId, v => connector.PartId = v);
            foreach (var cavity in connector.Cavities)
            {
                Ref(cavity.TerminalPartId, v => cavity.TerminalPartId = v);
                Ref(cavity.SealPartId, v => cavity.SealPartId = v); Ref(cavity.PlugPartId, v => cavity.PlugPartId = v);
            }
            foreach (var a in connector.Accessories) Ref(a.PartId, v => a.PartId = v);
        }
        foreach (var wire in _project.Assembly.Conductors) Ref(wire.WirePartId, v => wire.WirePartId = v);
        foreach (var cable in _project.Assembly.Cables) Ref(cable.PartId, v => cable.PartId = v);
        foreach (var material in _project.Assembly.Materials) Ref(material.PartId, v => material.PartId = v);
        foreach (var splice in _project.Assembly.Splices) Ref(splice.PartId, v => splice.PartId = v);
        var cavityChanges = new List<(ConnectorInstance Connector, List<Cavity> Before, List<Cavity> After)>();
        foreach (var c in connectors)
        {
            foreach (var cavity in c.Cavities.Where(p => !pinMap.ContainsKey(p.PinDefinitionId)))
                if (_project.Assembly.Conductors.Any(w => w.From.CavityId == cavity.Id || w.To.CavityId == cavity.Id) ||
                    cavity.TerminalPartId is not null || cavity.SealPartId is not null || cavity.PlugPartId is not null)
                    throw new ArgumentException($"Pin {c.Name}:{cavity.Label} bevat een aansluiting of materiaal. Maak die eerst expliciet leeg.");
            var after = new List<Cavity>();
            foreach (var pin in next.Pins)
            {
                var prior = c.Cavities.Find(x => pinMap.GetValueOrDefault(x.PinDefinitionId) == pin.Id);
                var cavity = prior is null ? new Cavity() : EditData.Clone(prior);
                cavity.PinDefinitionId = pin.Id; cavity.Label = pin.Label; cavity.MaxWires = Math.Min(cavity.MaxWires, pin.MaxWires);
                after.Add(cavity);
            }
            cavityChanges.Add((c, c.Cavities, after));
        }
        var model = new DelegateCommand("Onderdeelrevisie toepassen",
            () => { _project.Parts.Add(next); foreach (var r in references) r.Set(next.Id); foreach (var c in cavityChanges) c.Connector.Cavities = c.After; },
            () => { foreach (var c in cavityChanges) c.Connector.Cavities = c.Before; foreach (var r in references) r.Set(r.Before); _project.Parts.Remove(next); });
        var geometryCommands = new List<IEditCommand>();
        var previousPins = _project.Sheets.SelectMany(s => s.Views).SelectMany(v => v.Components)
            .Where(p => connectors.Any(c => c.Id == p.EntityId))
            .ToDictionary(p => p.Id, p => connectors.First(c => c.Id == p.EntityId).Cavities
                .Select(c => (c.Id, Point: Geometry.PinWorld(_project, p, c.Id)))
                .Where(x => x.Point.HasValue).ToDictionary(x => x.Id, x => x.Point!.Value));
        return new DelegateCommand("Onderdeel bijwerken met pinmapping", () =>
        {
            model.Execute(); geometryCommands.Clear();
            try
            {
                foreach (var view in _project.Sheets.SelectMany(s => s.Views))
                    foreach (var placement in view.Components.Where(p => connectors.Any(c => c.Id == p.EntityId)))
                    {
                        var command = HarnessOperations.MoveComponent(_project, view, placement, placement.Position,
                            previousPinPositions: previousPins[placement.Id]);
                        command.Execute(); geometryCommands.Add(command);
                    }
            }
            catch { for (var i = geometryCommands.Count - 1; i >= 0; i--) geometryCommands[i].Undo(); model.Undo(); throw; }
        }, () => { for (var i = geometryCommands.Count - 1; i >= 0; i--) geometryCommands[i].Undo(); model.Undo(); });
    }
    private async void ImportStep_Click(object sender, RoutedEventArgs e)
    {
        var chooser = new OpenFileDialog { Filter = "STEP-model (*.step;*.stp)|*.step;*.stp", CheckFileExists = true };
        if (chooser.ShowDialog(this) != true) return;
        CadImportResult? result = null; byte[]? data = null;
        await BusyAsync("STEP-geometrie lezen en projecteren…", async token =>
        {
            result = await Task.Run(() => new CadImportService().ImportAsync(chooser.FileName, token), token);
            data = await File.ReadAllBytesAsync(chooser.FileName, token);
        });
        if (result is null || data is null) return;
        var projection = result.Views.First(v => v.Name == "Front");
        var asset = new ProjectAsset { FileName = Path.GetFileName(chooser.FileName), MediaType = "model/step", Data = data };
        var part = new PartDefinition { Name = Path.GetFileNameWithoutExtension(chooser.FileName), SourceAssetId = asset.Id,
            Symbol = EditData.Clone(projection.Paths), WidthMm = Math.Max(.1, projection.WidthMm), HeightMm = Math.Max(.1, projection.HeightMm) };
        var dialog = new PartEditorWindow(this, part, result);
        if (dialog.ShowDialog() != true) return;
        var added = dialog.Result;
        Execute(new DelegateCommand("STEP-onderdeel importeren", () => { _project.Assets.Add(asset); _project.Parts.Add(added); },
            () => { _project.Parts.Remove(added); _project.Assets.Remove(asset); }));
        LibraryList.SelectedItem = added; await PersistLibraryAsync();
    }
    private void Twist_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _project.Assembly.Conductors.Where(w => Canvas.Selection.Contains(w.Id)).ToArray();
        var dialog = new FormDialog(this, "Twisted pair / triad", 540);
        var name = dialog.Form.Text("Groepsnaam", "Twisted pair");
        var pitch = dialog.Form.Number("Getekende twistafstand (mm)", 20);
        dialog.Form.Note("Selecteer precies twee of drie afzonderlijke aders. De twistweergave wijzigt geen kniplengtes.");
        var list = new ListBox { ItemsSource = _project.Assembly.Conductors, DisplayMemberPath = "Name", SelectionMode = SelectionMode.Multiple, Height = 180 };
        foreach (var wire in chosen) list.SelectedItems.Add(wire);
        dialog.Form.Panel.Children.Add(list);
        TwistGroup? group = null;
        dialog.OnAccept = () =>
        {
            var ids = list.SelectedItems.Cast<Conductor>().Select(w => w.Id).ToList();
            if (ids.Count is < 2 or > 3) throw new ArgumentException("Selecteer twee of drie aders.");
            if (_project.Assembly.TwistGroups.Any(g => g.ConductorIds.Intersect(ids).Any())) throw new ArgumentException("Een geselecteerde ader behoort al tot een twistgroep.");
            group = new() { Name = name.Text, PitchMm = EditData.Number(pitch.Text, "Twistafstand", .1), ConductorIds = ids }; return true;
        };
        if (dialog.ShowDialog() == true && group is not null) Add("Twistgroep toevoegen", _project.Assembly.TwistGroups, group);
    }
    private void Cable_Click(object sender, RoutedEventArgs e)
    {
        var parts = _project.Parts.Where(p => p.Kind == PartKind.Cable).ToArray();
        if (parts.Length == 0) { MessageBox.Show(this, "Definieer eerst een kabelartikel in de onderdelenbibliotheek.", "Kabel"); return; }
        var dialog = new FormDialog(this, "Kabel definiëren");
        var name = dialog.Form.Text("Kabelnaam", NextName("C", _project.Assembly.Cables.Select(c => c.Name)));
        var part = dialog.Form.Choice("Kabelartikel", parts, parts[0], p => $"{p.PartNumber} · {p.Name}");
        var length = dialog.Form.Number("Kabelkniplengte (mm)", 1000);
        var wires = new ListBox { ItemsSource = _project.Assembly.Conductors.Where(w => w.CableId is null).ToArray(),
            DisplayMemberPath = "Name", SelectionMode = SelectionMode.Multiple, Height = 150 };
        dialog.Form.Label("Aders in deze kabel"); dialog.Form.Panel.Children.Add(wires);
        CableInstance? cable = null;
        dialog.OnAccept = () => { cable = new() { Name = name.Text, PartId = PropertyForm.Value<PartDefinition>(part).Id,
            CutLengthMm = EditData.Number(length.Text, "Kniplengte", .1) }; return true; };
        if (dialog.ShowDialog() != true || cable is null) return;
        var selected = wires.SelectedItems.Cast<Conductor>().ToArray();
        Execute(new DelegateCommand("Kabel toevoegen", () => { _project.Assembly.Cables.Add(cable); foreach (var w in selected) w.CableId = cable.Id; },
            () => { foreach (var w in selected) w.CableId = null; _project.Assembly.Cables.Remove(cable); }));
    }
    private void Material_Click(object sender, RoutedEventArgs e)
    {
        var parts = _project.Parts.Where(p => p.Kind is PartKind.Sleeve or PartKind.Accessory).ToArray();
        if (parts.Length == 0) { MessageBox.Show(this, "Definieer eerst een kous- of accessoireartikel.", "Materiaal"); return; }
        var dialog = new FormDialog(this, "Bescherming / materiaal toevoegen");
        var part = dialog.Form.Choice("Artikel", parts, parts[0], p => $"{p.PartNumber} · {p.Name}");
        var quantity = dialog.Form.Number("Hoeveelheid", 1);
        var unit = dialog.Form.Choice("Eenheid", new[] { "st", "mm", "m" }, "st");
        var branches = new RouteBranch?[] { null }.Concat(_project.Assembly.Branches);
        var branch = dialog.Form.Choice("Weergeven op tak (optioneel)", branches, _project.Assembly.Branches.Find(b => b.Id == SelectedRoute()?.BranchId), b => b?.Name ?? "Geen routekoppeling");
        var notes = dialog.Form.Text("Notitie", "");
        MaterialItem? item = null;
        dialog.OnAccept = () => { item = new() { PartId = PropertyForm.Value<PartDefinition>(part).Id, Quantity = EditData.Number(quantity.Text, "Hoeveelheid", .001),
            Unit = PropertyForm.Value<string>(unit), BranchId = PropertyForm.Value<RouteBranch?>(branch)?.Id, Notes = notes.Text }; return true; };
        if (dialog.ShowDialog() == true && item is not null) Add("Materiaal toevoegen", _project.Assembly.Materials, item);
    }
}
