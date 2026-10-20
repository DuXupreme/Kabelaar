using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private void BuildAssemblyOverview(PropertyForm form)
    {
        form.Heading("Twistgroepen");
        if (_project.Assembly.TwistGroups.Count == 0) form.Note("Nog geen twistgroepen. Selecteer aders en maak een pair of triad via het menu.");
        foreach (var group in _project.Assembly.TwistGroups)
            form.Action($"{group.Name} · {group.ConductorIds.Count} aders", () => EditAssemblyTwist(group));
        form.Heading("Kabels");
        if (_project.Assembly.Cables.Count == 0) form.Note("Nog geen samengestelde kabels.");
        foreach (var cable in _project.Assembly.Cables)
            form.Action($"{cable.Name} · {EditData.Format(cable.CutLengthMm)} mm", () => EditAssemblyCable(cable));
        form.Heading("Overig materiaal");
        foreach (var material in _project.Assembly.Materials)
        {
            var part = _project.Parts.Find(p => p.Id == material.PartId);
            form.Action($"{part?.PartNumber ?? "?"} · {EditData.Format(material.Quantity)} {material.Unit}",
                () => EditAssemblyMaterial(_project.Assembly.Materials, material, "Assemblagemateriaal"));
        }
        form.Action("Materiaal toevoegen", () => EditAssemblyMaterial(_project.Assembly.Materials, null, "Assemblagemateriaal"));
    }

    private void EditAssemblyTwist(TwistGroup group)
    {
        var dialog = new FormDialog(this, "Twistgroep bewerken");
        var form = dialog.Form;
        var name = form.Text("Naam", group.Name);
        var pitch = form.Number("Getekende twistspoed (mm)", group.PitchMm);
        var notes = form.Text("Productieaanwijzing", group.Notes, true);
        form.Note("Selecteer twee of drie aders. De getekende twistspoed wijzigt geen kniplengtes of verwerkingstoeslagen.");
        var checks = _project.Assembly.Conductors.OrderBy(w => w.Name).Select(w =>
            (Wire: w, Check: form.Check($"{w.Name} · {w.ColorCode}", group.ConductorIds.Contains(w.Id)))).ToArray();
        form.Action("Twistgroep verwijderen", () =>
        {
            RemoveAssemblyItem(_project.Assembly.TwistGroups, group, "Twistgroep verwijderen");
            dialog.Close();
        });
        dialog.OnAccept = () =>
        {
            var next = EditData.Clone(group);
            next.Name = name.Text.Trim(); next.PitchMm = EditData.Number(pitch.Text, "Twistspoed", .1);
            next.Notes = notes.Text;
            next.ConductorIds = checks.Where(c => c.Check.IsChecked == true).Select(c => c.Wire.Id).ToList();
            if (next.ConductorIds.Count is not (2 or 3)) throw new ArgumentException("Een pair heeft twee aders; een triad heeft drie aders.");
            var other = _project.Assembly.TwistGroups.FirstOrDefault(g => g.Id != group.Id && g.ConductorIds.Intersect(next.ConductorIds).Any());
            if (other is not null) throw new ArgumentException($"Een gekozen ader zit al in twistgroep {other.Name}. Verwijder die toewijzing eerst.");
            Change("Twistgroep wijzigen", _project.Assembly.TwistGroups, group, next);
            return true;
        };
        dialog.ShowDialog();
    }

    private void EditAssemblyCable(CableInstance cable)
    {
        var dialog = new FormDialog(this, "Samengestelde kabel bewerken");
        var form = dialog.Form;
        var name = form.Text("Kabelnaam", cable.Name);
        var part = PartChoice(form, "Kabelartikel", cable.PartId, PartKind.Cable);
        var cut = form.Number("Kniplengte kabel (mm)", cable.CutLengthMm);
        form.Heading("Aders in deze kabel");
        form.Note("Deze aders blijven afzonderlijk in de aansluitlijst staan. Alleen het kabelartikel wordt als lengtemateriaal ingekocht. Een geselecteerde ader krijgt deze kabeltoewijzing.");
        var checks = _project.Assembly.Conductors.OrderBy(w => w.Name).Select(w =>
        {
            var currentCable = _project.Assembly.Cables.Find(c => c.Id == w.CableId);
            return (Wire: w, Check: form.Check($"{w.Name} · {w.ColorCode}{(currentCable is not null && currentCable.Id != cable.Id ? $" · nu {currentCable.Name}" : "")}", w.CableId == cable.Id));
        }).ToArray();
        form.Action("Kabel verwijderen; aders behouden", () =>
        {
            var members = _project.Assembly.Conductors.Where(w => w.CableId == cable.Id).ToArray();
            var index = _project.Assembly.Cables.IndexOf(cable);
            if (index < 0) return;
            Execute(new DelegateCommand("Kabel verwijderen",
                () => { foreach (var wire in members) wire.CableId = null; _project.Assembly.Cables.RemoveAt(index); },
                () => { _project.Assembly.Cables.Insert(index, cable); foreach (var wire in members) wire.CableId = cable.Id; }));
            dialog.Close();
        });
        dialog.OnAccept = () =>
        {
            var next = EditData.Clone(cable);
            next.Name = name.Text.Trim(); next.PartId = PropertyForm.Value<PropertyOption>(part).Id ?? "";
            next.CutLengthMm = EditData.Number(cut.Text, "Kabelkniplengte", .001);
            var assignments = checks.Where(c => c.Check.IsChecked == true || c.Wire.CableId == cable.Id)
                .Select(c => (c.Wire, Before: c.Wire.CableId, After: c.Check.IsChecked == true ? cable.Id : null)).ToArray();
            Execute(new CompositeCommand("Kabel wijzigen",
                PropertyReplace(_project.Assembly.Cables, cable, next, "Kabelgegevens"),
                new DelegateCommand("Kabeladers",
                    () => { foreach (var change in assignments) change.Wire.CableId = change.After; },
                    () => { foreach (var change in assignments) change.Wire.CableId = change.Before; })));
            return true;
        };
        dialog.ShowDialog();
    }

    private void EditAssemblyMaterial(List<MaterialItem> owner, MaterialItem? material, string title)
    {
        var dialog = new FormDialog(this, title);
        var form = dialog.Form;
        var source = material ?? new MaterialItem();
        var options = new[] { new PropertyOption(null, "Nog geen onderdeel gekozen") }.Concat(_project.Parts.OrderBy(p => p.Kind).ThenBy(p => p.PartNumber)
            .Select(p => new PropertyOption(p.Id, $"{p.PartNumber} · {p.Name}"))).ToArray();
        var part = form.Choice("Materiaalartikel", options, options.FirstOrDefault(o => o.Id == source.PartId), o => o.Label);
        var quantity = form.Number("Hoeveelheid", source.Quantity);
        var unit = form.Text("Eenheid (bijvoorbeeld st of m)", source.Unit);
        var branches = new[] { new PropertyOption(null, "Geen specifieke routetak") }.Concat(_project.Assembly.Branches.Select(b => new PropertyOption(b.Id, b.Name))).ToArray();
        var branch = form.Choice("Gekoppelde routetak", branches, branches.FirstOrDefault(o => o.Id == source.BranchId), o => o.Label);
        var notes = form.Text("Montage / toepassing", source.Notes, true);
        form.Note("De hoeveelheid is expliciet opgegeven en verandert niet automatisch als de routetak wordt verlengd.");
        if (material is not null)
            form.Action("Materiaalregel verwijderen", () => { RemoveAssemblyItem(owner, material, "Materiaal verwijderen"); dialog.Close(); });
        dialog.OnAccept = () =>
        {
            var next = EditData.Clone(source);
            next.PartId = PropertyForm.Value<PropertyOption>(part).Id ?? "";
            next.Quantity = EditData.Number(quantity.Text, "Hoeveelheid", .000001);
            next.Unit = unit.Text.Trim();
            if (string.IsNullOrWhiteSpace(next.Unit)) throw new ArgumentException("Vul een eenheid in.");
            next.BranchId = PropertyForm.Value<PropertyOption>(branch).Id;
            next.Notes = notes.Text;
            if (material is null) Add("Materiaal toevoegen", owner, next);
            else Change("Materiaal wijzigen", owner, material, next);
            return true;
        };
        dialog.ShowDialog();
    }

    private void RemoveAssemblyItem<T>(List<T> owner, T item, string name)
    {
        var index = owner.IndexOf(item);
        if (index < 0) return;
        Execute(new DelegateCommand(name, () => owner.RemoveAt(index), () => owner.Insert(index, item)));
    }
}
