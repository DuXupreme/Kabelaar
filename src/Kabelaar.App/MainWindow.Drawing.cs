using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    private Endpoint _wireStart = new();
    private Annotation? _pendingImage;
    private DrawingScene? _endpointSnapScene;
    private DrawingView? _endpointSnapView;
    private SceneSpatialIndex? _endpointSnapIndex;
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag?.ToString();
        if (Enum.TryParse<EditorTool>(tag, out var tool)) SetTool(tool);
    }
    private void SetTool(EditorTool tool)
    {
        Canvas.Cancel();
        Canvas.Tool = tool;
        Canvas.SnapEnabled = SnapCheck.IsChecked == true;
        Canvas.EditableVertices.Clear();
        ToolHint.Text = tool switch
        {
            EditorTool.Select => "Slepen = verplaatsen · Ctrl+klik = meervoudige selectie",
            EditorTool.Connector => "Kies een connector uit de bibliotheek en klik op het blad.",
            EditorTool.Wire => "Klik beginpin, bochten en eindpin · Shift = recht · Enter = afronden",
            EditorTool.Dimension => "Klik twee meetpunten.",
            EditorTool.Leader => "Klik pijlpunt en tekstpositie.",
            EditorTool.Arc => "Klik middelpunt, beginpunt en eindpunt.",
            _ => "Klik op het blad om te plaatsen."
        };
        Status($"Gereedschap: {tool}"); Canvas.Focus(); Canvas.InvalidateVisual();
    }
    private Point2 SnapToEndpoint(Point2 sheetPoint)
    {
        EnsureEndpointSnapIndex();
        var closest = sheetPoint; var distance = 3.0;
        foreach (var pin in _endpointSnapIndex?.Query(new(sheetPoint.X - distance, sheetPoint.Y - distance, distance * 2, distance * 2))
            .OfType<SceneEllipse>() ?? [])
        {
            var d = pin.Center.DistanceTo(sheetPoint);
            if (d < distance) { closest = pin.Center; distance = d; }
        }
        return closest;
    }
    private void EnsureEndpointSnapIndex()
    {
        var scene = Canvas.Scene;
        if (ReferenceEquals(scene, _endpointSnapScene) && ReferenceEquals(_view, _endpointSnapView)) return;
        var endpoints = new List<ScenePrimitive>();
        var layers = _sheet.Layers.ToDictionary(l => l.Id);
        var connectors = _project.Assembly.Connectors.ToDictionary(c => c.Id);
        var splices = _project.Assembly.Splices.Select(s => s.Id).ToHashSet();
        var pinsByPart = _project.Parts.ToDictionary(p => p.Id, p => p.Pins.ToDictionary(pin => pin.Id));
        foreach (var placement in _view.Components)
        {
            if (layers.TryGetValue(placement.LayerId, out var layer) && !layer.Visible) continue;
            if (connectors.TryGetValue(placement.EntityId, out var connector) &&
                pinsByPart.TryGetValue(connector.PartId, out var pins))
            {
                foreach (var cavity in connector.Cavities)
                    if (pins.TryGetValue(cavity.PinDefinitionId, out var pin))
                        endpoints.Add(new SceneEllipse(cavity.Id, "snap-pin",
                            ToSheet(Geometry.ComponentWorld(placement, pin.Position)), 0, 0));
            }
            else if (splices.Contains(placement.EntityId))
                endpoints.Add(new SceneEllipse(placement.EntityId, "snap-splice", ToSheet(placement.Position), 0, 0));
        }
        // This index is interaction metadata, independent from visible/exported pin dots.
        // A new scene follows every model/style/placement change; pointer movement only queries it.
        _endpointSnapIndex = new SceneSpatialIndex(endpoints);
        _endpointSnapScene = scene;
        _endpointSnapView = _view;
    }
    private Endpoint FindEndpoint(Point2 modelPoint)
    {
        foreach (var placed in _view.Components)
        {
            if (_sheet.Layers.Find(l => l.Id == placed.LayerId)?.Visible == false) continue;
            var connector = _project.Assembly.Connectors.Find(c => c.Id == placed.EntityId);
            if (connector is not null)
                foreach (var cavity in connector.Cavities)
                {
                    var point = Geometry.PinWorld(_project, placed, cavity.Id);
                    if (point is not null && point.Value.DistanceTo(modelPoint) < .1)
                        return new() { Kind = EndpointKind.Cavity, EntityId = connector.Id, CavityId = cavity.Id, Description = "" };
                }
            else if (_project.Assembly.Splices.Any(s => s.Id == placed.EntityId) && placed.Position.DistanceTo(modelPoint) < .1)
                return new() { Kind = EndpointKind.Splice, EntityId = placed.EntityId, Description = "" };
        }
        return new() { Description = "Vrij uiteinde" };
    }
    private void CanvasClick(CanvasClick e)
    {
        if (_busy is not null) return;
        Canvas.SnapEnabled = SnapCheck.IsChecked == true;
        var model = ToModel(e.SheetPoint);
        switch (Canvas.Tool)
        {
            case EditorTool.Select:
                if (e.Clicks == 2 && SelectedRoute() is { } selected)
                {
                    if (!CanEditLayer(selected.LayerId)) return;
                    var closest = Enumerable.Range(0, selected.Points.Count - 1)
                        .MinBy(i => SceneSpatialIndex.DistanceToSegment(model, selected.Points[i], selected.Points[i + 1]));
                    try { Execute(HarnessOperations.InsertRoutePoint(_project, _view, selected, closest, model)); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Route niet gewijzigd"); }
                }
                break;
            case EditorTool.Connector:
                if (!CanEditLayer("main")) return;
                if (SelectedPart is not { Kind: PartKind.Housing or PartKind.RingTerminal } part)
                { Status("Kies eerst een connector of ringterminal in de bibliotheek."); return; }
                var connector = HarnessOperations.NewConnector(part, NextName("J", _project.Assembly.Connectors.Select(x => x.Name)));
                Execute(HarnessOperations.AddConnector(_project, _view, connector, model));
                Canvas.Select(connector.Id);
                break;
            case EditorTool.Splice:
                if (!CanEditLayer("main")) return;
                var splice = new Splice { Name = NextName("SP", _project.Assembly.Splices.Select(s => s.Name)),
                    PartId = SelectedPart?.Kind == PartKind.Splice ? SelectedPart.Id : null };
                var placement = new PlacedComponent { EntityId = splice.Id, Position = model };
                var spliceView = _view;
                Execute(new DelegateCommand("Splice plaatsen",
                    () => { _project.Assembly.Splices.Add(splice); spliceView.Components.Add(placement); },
                    () => { spliceView.Components.Remove(placement); _project.Assembly.Splices.Remove(splice); }));
                Canvas.Select(splice.Id);
                break;
            case EditorTool.Wire:
                if (Canvas.PendingPoints.Count == 0) _wireStart = FindEndpoint(model);
                AddPendingPoint(e);
                if (Canvas.PendingPoints.Count >= 2 && (e.Clicks == 2 || FindEndpoint(ToModel(Canvas.PendingPoints[^1])).Kind != EndpointKind.Free))
                    FinishDrawing();
                break;
            case EditorTool.Dimension:
            case EditorTool.Leader:
            case EditorTool.Line:
            case EditorTool.Arc:
                AddPendingPoint(e);
                if (Canvas.PendingPoints.Count >= (Canvas.Tool == EditorTool.Arc ? 3 : 2)) FinishDrawing();
                break;
            default:
                var kind = Enum.Parse<AnnotationKind>(Canvas.Tool.ToString());
                var annotation = _pendingImage is not null && kind == AnnotationKind.Image ? _pendingImage :
                    new Annotation { Kind = kind, Points = [model] };
                annotation.Points = [model];
                if (kind == AnnotationKind.Text)
                {
                    var text = FormDialog.Ask(this, "Tekst plaatsen", "Tekst");
                    if (text is null) return; annotation.Text = text;
                }
                if (kind == AnnotationKind.PinTable && SelectedId is { } connectorId) annotation.EntityId = connectorId;
                Add("Annotatie plaatsen", _view.Annotations, annotation);
                _pendingImage = null; SetTool(EditorTool.Select); Canvas.Select(annotation.Id);
                break;
        }
    }
    private void AddPendingPoint(CanvasClick e)
    {
        var point = e.SheetPoint;
        if (e.Modifiers.HasFlag(ModifierKeys.Shift) && Canvas.PendingPoints.Count > 0)
        {
            var before = Canvas.PendingPoints[^1]; var d = point - before;
            point = Math.Abs(d.X) > Math.Abs(d.Y) ? new(point.X, before.Y) : new(before.X, point.Y);
        }
        if (Canvas.PendingPoints.Count == 0 || point.DistanceTo(Canvas.PendingPoints[^1]) > .001)
            Canvas.PendingPoints.Add(point);
        Canvas.InvalidateVisual();
    }
    private void FinishDrawing()
    {
        if (Canvas.PendingPoints.Count < 2) { Canvas.PendingPoints.Clear(); return; }
        var points = Canvas.PendingPoints.Select(ToModel).ToList();
        if (Canvas.Tool == EditorTool.Wire)
        {
            if (!CanEditLayer("main")) return;
            var part = SelectedPart?.Kind == PartKind.Wire ? SelectedPart : _project.Parts.FirstOrDefault(p => p.Kind == PartKind.Wire);
            var branch = new RouteBranch
            {
                Points = points, PhysicalLengthMm = _view.Mode == LayoutMode.Scaled ? Geometry.Length(points) : 0,
                Name = $"Tak {_project.Assembly.Branches.Count + 1}"
            };
            var wire = new Conductor
            {
                Name = NextName("W", _project.Assembly.Conductors.Select(w => w.Name)), From = _wireStart,
                To = FindEndpoint(points[^1]), WirePartId = part?.Id, Color = part?.Color ?? "#243244",
                CrossSectionMm2 = part?.CrossSectionMm2 ?? .5, ColorCode = ColorCode(part?.Color ?? "#243244")
            };
            Execute(HarnessOperations.AddRoute(_project, _view, branch, wire));
            Canvas.PendingPoints.Clear(); Canvas.Select(wire.Id);
            Status(_view.Mode == LayoutMode.Free
                ? "Ader toegevoegd in vrije opmaak. Vul de fysieke taklengte in bij Eigenschappen; productie-export is tot die tijd geblokkeerd."
                : "Ader toegevoegd. Klik opnieuw voor de volgende ader, Esc om te selecteren.");
        }
        else
        {
            if (!Enum.TryParse<AnnotationKind>(Canvas.Tool.ToString(), out var kind)) return;
            var annotation = new Annotation { Kind = kind, Points = points };
            if (kind == AnnotationKind.Leader)
            {
                var text = FormDialog.Ask(this, "Aanwijzing plaatsen", "Tekst");
                if (text is null) { Canvas.PendingPoints.Clear(); return; }
                annotation.Text = text;
            }
            Add("Annotatie plaatsen", _view.Annotations, annotation);
            Canvas.PendingPoints.Clear(); Canvas.Select(annotation.Id); SetTool(EditorTool.Select);
        }
        Canvas.InvalidateVisual();
    }
    private PlacedRoute? SelectedRoute()
    {
        var id = SelectedId;
        var wire = _project.Assembly.Conductors.Find(w => w.Id == id);
        return _view.Routes.Find(r => r.BranchId == id || r.Id == id || wire?.BranchIds.Contains(r.BranchId) == true);
    }
    private void CanvasDrag(CanvasDrag drag)
    {
        try
        {
            var delta = (drag.End - drag.Start) * (1 / (_view.Scale * _sheet.PrintScale));
            var components = _view.Components.Where(c => drag.Selection.Contains(c.EntityId) || drag.Selection.Contains(c.Id)).ToArray();
            RequireEditableComponentGeometry(components);
            var commands = new List<IEditCommand>();
            if (components.Length > 0)
                commands.Add(HarnessOperations.MoveSelection(_project, _view, components.ToDictionary(c => c.Id, c => c.Position + delta)));
            var selectedBranches = _project.Assembly.Conductors.Where(w => drag.Selection.Contains(w.Id)).SelectMany(w => w.BranchIds)
                .Concat(drag.Selection).ToHashSet();
            foreach (var route in _view.Routes.Where(r => selectedBranches.Contains(r.BranchId) || selectedBranches.Contains(r.Id)))
            {
                if (components.Length > 0) continue;
                RequireEditableLayer(route.LayerId);
                var points = route.Points.Select(p => p + delta).ToList();
                foreach (var index in AttachedRouteEndpointIndices(route)) points[index] = route.Points[index];
                commands.Add(HarnessOperations.SetRoutePoints(_project, _view, route, points));
            }
            foreach (var annotation in _view.Annotations.Where(a => drag.Selection.Contains(a.Id)))
            {
                RequireEditableLayer(annotation.LayerId);
                if (annotation.Locked) throw new InvalidOperationException("De geselecteerde annotatie is vergrendeld.");
                var before = annotation.Points.ToList(); var after = before.Select(p => p + delta).ToList();
                commands.Add(new DelegateCommand("Annotatie verplaatsen", () => annotation.Points = after, () => annotation.Points = before));
            }
            if (commands.Count > 0) Execute(new CompositeCommand("Selectie verplaatsen", commands.ToArray()));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Verplaatsen niet toegepast", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void VertexDrag(int index, Point2 sheetPoint)
    {
        var route = SelectedRoute();
        if (route is null || index >= route.Points.Count) return;
        if (!CanEditLayer(route.LayerId)) return;
        var model = ToModel(sheetPoint);
        if (AttachedRouteEndpointIndices(route).Contains(index))
        { Status("Dit uiteinde zit vast aan een aansluiting. Wijzig de aansluiting bij Eigenschappen of verplaats de connector."); return; }
        var points = route.Points.ToList(); points[index] = model;
        try { Execute(HarnessOperations.SetRoutePoints(_project, _view, route, points)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Route niet gewijzigd"); }
    }
    private void LayoutMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || Canvas is null) return;
        var mode = LayoutModeBox.SelectedIndex == 0 ? LayoutMode.Scaled : LayoutMode.Free;
        if (mode == _view.Mode) return;
        try
        {
            var command = HarnessOperations.SetLayoutMode(_project, _view, mode);
            if (mode == LayoutMode.Scaled && MessageBox.Show(this,
                "De huidige routes komen overeen met de productiematen. In op-schaalmodus wijzigen routebewerkingen voortaan ook fysieke routelengtes.",
                "Op schaal werken", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) { Refresh(); return; }
            Execute(command);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Schaalmodus behouden"); Refresh(); }
    }
    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        var placement = _view.Components.Find(c => Canvas.Selection.Contains(c.EntityId));
        if (placement is null) return;
        try
        {
            RequireEditableComponentGeometry([placement]);
            Execute(HarnessOperations.MoveComponent(_project, _view, placement, placement.Position, placement.RotationDegrees + 90));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Roteren niet toegepast"); }
    }
    private void AlignHorizontal_Click(object sender, RoutedEventArgs e) => AlignSelection(true);
    private void AlignVertical_Click(object sender, RoutedEventArgs e) => AlignSelection(false);
    private void AlignSelection(bool horizontal)
    {
        var components = _view.Components.Where(c => Canvas.Selection.Contains(c.EntityId)).ToArray();
        if (components.Length < 2) { Status("Selecteer minstens twee onderdelen met Ctrl+klik."); return; }
        var mean = horizontal ? components.Average(c => c.Position.Y) : components.Average(c => c.Position.X);
        try
        {
            RequireEditableComponentGeometry(components);
            Execute(HarnessOperations.MoveSelection(_project, _view, components.ToDictionary(c => c.Id,
                c => horizontal ? new Point2(c.Position.X, mean) : new Point2(mean, c.Position.Y))));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Uitlijnen niet toegepast"); }
    }
    private bool CanEditLayer(string layerId)
    {
        if (_sheet.Layers.Find(l => l.Id == layerId)?.Locked != true) return true;
        Status("Deze laag is vergrendeld. Ontgrendel de laag voordat je de geometrie bewerkt.");
        return false;
    }
    private void RequireEditableLayer(string layerId)
    {
        if (_sheet.Layers.Find(l => l.Id == layerId)?.Locked == true)
            throw new InvalidOperationException("Een betrokken laag is vergrendeld. Ontgrendel de laag voordat je de geometrie bewerkt.");
    }
    private void RequireEditableComponentGeometry(IReadOnlyCollection<PlacedComponent> components)
    {
        foreach (var placement in components) RequireEditableLayer(placement.LayerId);
        if (components.Count == 0) return;
        var entities = components.Select(c => c.EntityId).ToHashSet();
        var affected = new HashSet<string>();
        foreach (var wire in _project.Assembly.Conductors.Where(w => w.BranchIds.Count > 0))
        {
            if (wire.From.EntityId is { } from && entities.Contains(from)) affected.Add(wire.BranchIds[0]);
            if (wire.To.EntityId is { } to && entities.Contains(to)) affected.Add(wire.BranchIds[^1]);
        }
        foreach (var route in _view.Routes.Where(r => affected.Contains(r.BranchId))) RequireEditableLayer(route.LayerId);
    }
    private HashSet<int> AttachedRouteEndpointIndices(PlacedRoute route)
    {
        var result = new HashSet<int>();
        foreach (var wire in _project.Assembly.Conductors.Where(w => w.BranchIds.Count > 0))
            foreach (var (endpoint, first) in new[] { (wire.From, true), (wire.To, false) })
            {
                if (endpoint.Kind == EndpointKind.Free || (first ? wire.BranchIds[0] : wire.BranchIds[^1]) != route.BranchId) continue;
                var position = Geometry.EndpointPosition(_project, _view, endpoint);
                if (position is { } point)
                    result.Add(route.Points[0].DistanceTo(point) <= route.Points[^1].DistanceTo(point) ? 0 : route.Points.Count - 1);
            }
        return result;
    }
    private static string NextName(string prefix, IEnumerable<string> used)
    {
        var names = used.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1; while (names.Contains(prefix + n)) n++;
        return prefix + n;
    }
    private static string ColorCode(string color) => color.ToUpperInvariant() switch
    {
        "#FF0000" or "#DC2626" => "RD", "#0000FF" or "#2563EB" => "BU", "#FFFF00" or "#EAB308" => "YE",
        "#00FF00" or "#16A34A" => "GN", "#FFFFFF" => "WH", "#F97316" => "OG", "#9333EA" => "VT",
        "#000000" or "#243244" => "BK", _ => ""
    };
}
