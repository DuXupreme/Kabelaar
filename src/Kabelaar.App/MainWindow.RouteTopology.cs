using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private void EditWireRoute(Conductor wire)
    {
        var dialog = new FormDialog(this, $"{wire.Name} · fysieke route", 570);
        var form = dialog.Form;
        var available = form.Choice("Beschikbare routetak", _project.Assembly.Branches.OrderBy(b => b.Name),
            _project.Assembly.Branches.FirstOrDefault(), b => $"{b.Name} · {EditData.Format(b.PhysicalLengthMm)} mm");
        var routeIds = wire.BranchIds.ToList();
        var list = new ListBox { Height = 180, Margin = new(0, 5, 0, 8), DisplayMemberPath = nameof(RouteBranch.Name) };
        form.Label("Van beginaansluiting naar eindaansluiting");
        form.Panel.Children.Add(list);
        var total = new TextBlock { Margin = new(0, 5, 0, 8), TextWrapping = TextWrapping.Wrap };
        form.Panel.Children.Add(total);
        void RefreshRoute(int selected = -1)
        {
            list.ItemsSource = routeIds.Select(id => _project.Assembly.Branches.Find(b => b.Id == id)).OfType<RouteBranch>().ToArray();
            list.SelectedIndex = Math.Min(selected, list.Items.Count - 1);
            total.Text = $"Fysieke routelengte: {EditData.Format(routeIds.Sum(id => _project.Assembly.Branches.Find(b => b.Id == id)?.PhysicalLengthMm ?? 0))} mm";
        }
        RefreshRoute();
        form.Action("Tak toevoegen", () =>
        {
            if (available.SelectedItem is null) throw new ArgumentException("Er zijn geen beschikbare takken.");
            var branch = PropertyForm.Value<RouteBranch>(available);
            if (routeIds.Contains(branch.Id)) throw new ArgumentException("Deze tak staat al in de route.");
            routeIds.Add(branch.Id); RefreshRoute(routeIds.Count - 1);
        });
        form.Action("Geselecteerde tak omhoog", () =>
        {
            var i = list.SelectedIndex;
            if (i <= 0) return;
            (routeIds[i], routeIds[i - 1]) = (routeIds[i - 1], routeIds[i]); RefreshRoute(i - 1);
        });
        form.Action("Geselecteerde tak omlaag", () =>
        {
            var i = list.SelectedIndex;
            if (i < 0 || i >= routeIds.Count - 1) return;
            (routeIds[i], routeIds[i + 1]) = (routeIds[i + 1], routeIds[i]); RefreshRoute(i + 1);
        });
        form.Action("Geselecteerde tak uit deze aderroute halen", () =>
        {
            var i = list.SelectedIndex; if (i < 0) return;
            routeIds.RemoveAt(i); RefreshRoute(i);
        });
        form.Note("De takken moeten fysiek aaneensluiten. Meerdere aders mogen dezelfde tak gebruiken. Routekeuze verandert geen elektrische aansluitingen; de kniplengte volgt de gekozen lengtebron.");
        dialog.OnAccept = () =>
        {
            Execute(HarnessRouteOperations.SetConductorRoute(_project, wire, routeIds));
            return true;
        };
        dialog.ShowDialog();
    }

    private void SplitRouteSection(PlacedRoute route)
    {
        if (route.Points.Count < 3) throw new ArgumentException("Voeg eerst met een dubbelklik een tussenpunt aan de route toe.");
        var branch = _project.Assembly.Branches.Single(b => b.Id == route.BranchId);
        var dialog = new FormDialog(this, "Fysieke routetak splitsen", 530);
        var form = dialog.Form;
        var vertices = Enumerable.Range(1, route.Points.Count - 2).ToArray();
        var point = form.Choice("Splitsen op routepunt", vertices, vertices[0],
            i => $"Punt {i + 1}: {EditData.Format(route.Points[i].X)}; {EditData.Format(route.Points[i].Y)} mm");
        var firstLength = form.Number("Fysieke lengte eerste takdeel (mm)", branch.PhysicalLengthMm / 2);
        firstLength.IsEnabled = _view.Mode == LayoutMode.Free;
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 12) };
        form.Panel.Children.Add(note);
        void Preview()
        {
            if (_view.Mode == LayoutMode.Scaled)
            {
                var selected = PropertyForm.Value<int>(point);
                var flattened = Kabelaar.Core.Geometry.Flatten(route.Points, route.Smooth).ToList();
                var end = 0;
                for (var index = 1; index <= selected; index++)
                    end = flattened.FindIndex(end + 1, p => p.DistanceTo(route.Points[index]) < 1e-7);
                firstLength.Text = EditData.Format(Kabelaar.Core.Geometry.Length(flattened.Take(end + 1).ToArray()));
            }
            note.Text = $"Totale fysieke maat blijft {EditData.Format(branch.PhysicalLengthMm)} mm.\nAlle aders en aanzichten worden bijgewerkt. Er ontstaan geen extra aders of elektrische verbindingen.";
        }
        point.SelectionChanged += (_, _) => Preview(); Preview();
        form.Note("Bij vrije opmaak geef je de eerste fysieke maat expliciet op. Materiaal en maatvoering die aan de oude tak hangen moeten vooraf expliciet worden ontkoppeld of verdeeld.");
        dialog.OnAccept = () =>
        {
            var index = _project.Assembly.Branches.IndexOf(branch);
            var command = HarnessRouteOperations.SplitPhysicalBranch(_project, _view, route, PropertyForm.Value<int>(point),
                _view.Mode == LayoutMode.Free ? EditData.Number(firstLength.Text, "Eerste taklengte", .001) : null);
            Execute(command);
            if (!_project.Assembly.Branches.Contains(branch)) Canvas.Select(_project.Assembly.Branches[index].Id);
            return true;
        };
        dialog.ShowDialog();
    }

    private void AddWireAlongExistingRoute(Conductor source)
    {
        var wire = EditData.Clone(source);
        wire.Id = Ids.New(); wire.Name = NextName("W", _project.Assembly.Conductors.Select(w => w.Name));
        // Empty descriptions keep these unfinished endpoints in the production-error list.
        wire.From = new() { Description = "" };
        wire.To = new() { Description = "" };
        Execute(HarnessRouteOperations.AddConductor(_project, wire));
        Canvas.Select(wire.Id);
        Status("Ader op de bestaande route toegevoegd. Kies de eigen begin- en eindaansluiting bij Eigenschappen.");
    }
}
