using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;
using Geometry = Kabelaar.Core.Geometry;

namespace Kabelaar.App;

public partial class MainWindow
{
    internal enum WireDragScope { WholeWire, Branch, Segment }
    internal WireDragScope _wireDragScope = WireDragScope.WholeWire;
    internal EndpointMoveMode _endpointMoveMode = EndpointMoveMode.KeepConnection;
    internal bool _editWholeBundle;
    private string? _editingRouteId;
    private int? _editingSegment;
    private double _bendOffsetMm = 10;
    private TangentMode _tangentMode = TangentMode.Aligned;

    private void BuildWireDrawingProperties(PropertyForm form, Conductor wire)
    {
        form.Heading("Draad tekenen");
        var routes = _view.Routes.Where(r => wire.BranchIds.Contains(r.BranchId)).ToArray();
        if (routes.Length == 0) { form.Note("Deze ader heeft geen route in dit aanzicht."); return; }
        var route = routes.FirstOrDefault(r => r.Id == _editingRouteId) ?? routes[0];
        if (route.Id != _editingRouteId) _editingSegment = null;
        _editingRouteId = route.Id;
        var routeChoice = form.Choice("Routetak voor vormbewerking", routes, route,
            r => _project.Assembly.Branches.Find(b => b.Id == r.BranchId)?.Name ?? r.BranchId);
        routeChoice.SelectionChanged += (_, _) =>
        {
            _editingRouteId = PropertyForm.Value<PlacedRoute>(routeChoice).Id;
            _editingSegment = null; ShowProperties();
        };
        var geometry = Geometry.EditableRouteGeometry(_view, route, wire.Id);
        var segments = Enumerable.Range(-1, geometry.Segments.Count).ToArray();
        var segment = form.Choice("Vorm toepassen op", segments, _editingSegment ?? -1,
            i => i < 0 ? "Hele routetak" : $"Segment {i + 1}");
        segment.SelectionChanged += (_, _) =>
        {
            var index = PropertyForm.Value<int>(segment);
            _editingSegment = index < 0 ? null : index;
        };
        var kinds = geometry.Segments.Select(s => s.Kind).Distinct().ToArray();
        var shape = form.Choice("Draadtype", new[] { "Recht", "Gebogen", "Gemengd" },
            kinds.Length > 1 ? "Gemengd" : kinds[0] == RouteSegmentKind.Cubic ? "Gebogen" : "Recht");
        var shapeDirty = false;
        shape.SelectionChanged += (_, _) => shapeDirty = true;
        var bend = form.Number("Bochtuitwijking (mm)", _bendOffsetMm);
        var bendDirty = false; bend.TextChanged += (_, _) => bendDirty = true;
        _tangentMode = geometry.Knots[0].TangentMode;
        var tangent = form.Choice("Gele handvatten", Enum.GetValues<TangentMode>(), _tangentMode,
            t => t switch { TangentMode.Independent => "Onafhankelijk", TangentMode.Mirrored => "Gespiegeld, even lang", _ => "In één richting, eigen lengte" });
        var tangentDirty = false;
        tangent.SelectionChanged += (_, _) => { _tangentMode = PropertyForm.Value<TangentMode>(tangent); tangentDirty = true; };
        var width = form.Text("Lijndikte ader (mm; leeg = aanzichtstijl)",
            _view.WirePresentations.Find(p => p.ConductorId == wire.Id)?.LineWidthMm is { } w ? EditData.Format(w) : "");
        form.Note("Lijndikte is opmaak; de fysieke aderdoorsnede staat bij Productiegegevens.");
        form.Action("Vorm toepassen", () =>
        {
            RequireEditableLayer(route.LayerId);
            var commands = new List<IEditCommand> { HarnessOperations.SetWireLineWidth(_view, wire.Id,
                string.IsNullOrWhiteSpace(width.Text) ? null : EditData.Number(width.Text, "Lijndikte", .01)) };
            _bendOffsetMm = EditData.Number(bend.Text, "Bochtuitwijking");
            if (shapeDirty || bendDirty || tangentDirty)
            {
                var next = Geometry.EditableRouteGeometry(_view, route, wire.Id);
                var chosen = PropertyForm.Value<string>(shape);
                if (shapeDirty && chosen != "Gemengd")
                    next = Geometry.WithCurveKind(next, chosen == "Recht" ? RouteSegmentKind.Line : RouteSegmentKind.Cubic, _bendOffsetMm, _editingSegment);
                if (bendDirty && chosen != "Recht") next = Geometry.WithBend(next, _bendOffsetMm, _editingSegment);
                if (tangentDirty)
                    foreach (var knot in next.Knots) knot.TangentMode = _tangentMode;
                commands.Add(HarnessOperations.SetWireRouteGeometry(_project, _view, wire, route, next, _editWholeBundle));
            }
            Execute(new CompositeCommand("Draadweergave wijzigen", commands.ToArray()));
        }, true);
        form.Action("Bocht omkeren", () => ApplyWireShape(wire, route, g => Geometry.FlipBend(g, _editingSegment)));
        form.Action("Handvatten herstellen", () =>
        {
            _bendOffsetMm = EditData.Number(bend.Text, "Bochtuitwijking");
            ApplyWireShape(wire, route, g => Geometry.ResetHandles(g, _bendOffsetMm, _editingSegment));
        });
        var scope = form.Choice("Verplaats draad", Enum.GetValues<WireDragScope>(), _wireDragScope,
            s => s switch { WireDragScope.Segment => "Aangeklikt segment", WireDragScope.Branch => "Aangeklikte routetak", _ => "Hele ader" });
        scope.SelectionChanged += (_, _) => _wireDragScope = PropertyForm.Value<WireDragScope>(scope);
        var endpoint = form.Choice("Aangesloten uiteinden", Enum.GetValues<EndpointMoveMode>(), _endpointMoveMode,
            m => m switch { EndpointMoveMode.MoveConnectedNode => "Aangesloten knooppunt meenemen", EndpointMoveMode.DetachThisEndpoint => "Verplaatst uiteinde losmaken", _ => "Verbinding behouden" });
        endpoint.SelectionChanged += (_, _) => _endpointMoveMode = PropertyForm.Value<EndpointMoveMode>(endpoint);
        var bundle = form.Check("Bewerk hele bundel", _editWholeBundle);
        bundle.Checked += (_, _) => _editWholeBundle = true;
        bundle.Unchecked += (_, _) => _editWholeBundle = false;
        form.Note(_view.Mode == LayoutMode.Free
            ? "Standaard wijzig je alleen deze ader. Gele bolletjes sturen de bocht; blauwe punten verplaatsen de route."
            : "Op schaal wijzig je de fysieke routetak. Vastgezette lengtes en andere aanzichten worden vooraf gecontroleerd.");
    }

    private void ApplyWireShape(Conductor wire, PlacedRoute route, Func<RouteGeometry, RouteGeometry> transform)
    {
        RequireEditableLayer(route.LayerId);
        Execute(HarnessOperations.SetWireRouteGeometry(_project, _view, wire, route,
            transform(Geometry.EditableRouteGeometry(_view, route, wire.Id)), _editWholeBundle));
    }

    internal sealed record DrawingPropertyPatch(
        bool SetColor = false, string? Color = null,
        bool SetWidth = false, double? Width = null,
        RouteSegmentKind? Shape = null, double? BendMm = null,
        string? LayerId = null, double? FontSizeMm = null);

    private void BuildMultipleProperties(PropertyForm form)
    {
        var wires = _project.Assembly.Conductors.Where(w => Canvas.Selection.Contains(w.Id)).ToArray();
        var annotations = _view.Annotations.Where(a => Canvas.Selection.Contains(a.Id)).ToArray();
        form.Note("Alleen gewijzigde velden worden op de hele selectie toegepast. Een ongeldige wijziging laat de volledige selectie ongewijzigd.");
        var onlyStyled = wires.Length + annotations.Length == Canvas.Selection.Count;
        TextBox? color = null, width = null, bend = null, font = null;
        ComboBox? shape = null;
        var colorDirty = false; var widthDirty = false; var bendDirty = false; var fontDirty = false;
        string Mixed(IEnumerable<string> values)
        {
            var distinct = values.Distinct().Take(2).ToArray();
            return distinct.Length == 1 ? distinct[0] : "Meerdere waarden";
        }
        if (onlyStyled)
        {
            color = form.Color("Tekenkleur (#RRGGBB)", Mixed(wires.Select(w => w.Color).Concat(annotations.Select(a => a.Color))));
            color.TextChanged += (_, _) => colorDirty = true;
            width = form.Text("Lijndikte (mm; leeg = aanzichtstijl)",
                Mixed(wires.Select(w => _view.WirePresentations.Find(p => p.ConductorId == w.Id)?.LineWidthMm is { } n ? EditData.Format(n) : "")
                    .Concat(annotations.Select(a => EditData.Format(a.LineWidthMm)))));
            width.TextChanged += (_, _) => widthDirty = true;
        }
        if (wires.Length == Canvas.Selection.Count)
        {
            shape = form.Choice("Draadtype", new[] { "Ongewijzigd", "Recht", "Gebogen" }, "Ongewijzigd");
            bend = form.Number("Bochtuitwijking (mm)", _bendOffsetMm); bend.TextChanged += (_, _) => bendDirty = true;
        }
        if (annotations.Length == Canvas.Selection.Count)
        {
            font = form.Text("Teksthoogte (mm)", Mixed(annotations.Select(a => EditData.Format(a.FontSizeMm))));
            font.TextChanged += (_, _) => fontDirty = true;
        }
        var layerOptions = new[] { new PropertyOption(null, "Ongewijzigd") }.Concat(_sheet.Layers.Select(l => new PropertyOption(l.Id, l.Name))).ToArray();
        var layer = form.Choice("Laag", layerOptions, layerOptions[0], o => o.Label);
        form.Action("Op selectie toepassen", () =>
        {
            var shapeValue = shape is null ? "Ongewijzigd" : PropertyForm.Value<string>(shape);
            ApplyDrawingPropertyPatch(new(colorDirty, colorDirty ? PropertyColor(color!.Text) : null,
                widthDirty, widthDirty && !string.IsNullOrWhiteSpace(width!.Text) ? EditData.Number(width.Text, "Lijndikte", .01) : null,
                shapeValue == "Ongewijzigd" ? null : shapeValue == "Recht" ? RouteSegmentKind.Line : RouteSegmentKind.Cubic,
                bendDirty ? EditData.Number(bend!.Text, "Bochtuitwijking") : null,
                PropertyForm.Value<PropertyOption>(layer).Id, fontDirty ? EditData.Number(font!.Text, "Teksthoogte", .1) : null));
        }, true);
    }

    internal void ApplyDrawingPropertyPatch(DrawingPropertyPatch patch)
    {
        if (patch.SetWidth && patch.Width is { } width && !PresentationLimits.ValidLineWidth(width))
            throw new ArgumentException("De lijndikte moet groter dan 0 en maximaal 1000 mm zijn.");
        if (patch.FontSizeMm is { } font && !PresentationLimits.ValidFontSize(font))
            throw new ArgumentException("De teksthoogte moet groter dan 0 en maximaal 1000 mm zijn.");
        var commands = new List<IEditCommand>();
        var touchedRoutes = new HashSet<string>();
        foreach (var id in Canvas.Selection)
        {
            var wire = _project.Assembly.Conductors.Find(w => w.Id == id);
            if (wire is not null)
            {
                foreach (var route in _view.Routes.Where(r => wire.BranchIds.Contains(r.BranchId)))
                {
                    RequireEditableLayer(route.LayerId);
                    if (patch.Shape.HasValue || patch.BendMm.HasValue)
                    {
                        if ((_editWholeBundle || _view.Mode == LayoutMode.Scaled) && !touchedRoutes.Add(route.Id)) continue;
                        var geometry = Geometry.EditableRouteGeometry(_view, route, wire.Id);
                        if (patch.Shape is { } kind) geometry = Geometry.WithCurveKind(geometry, kind, patch.BendMm ?? _bendOffsetMm);
                        if (patch.BendMm is { } offset && patch.Shape != RouteSegmentKind.Line) geometry = Geometry.WithBend(geometry, offset);
                        commands.Add(HarnessOperations.SetWireRouteGeometry(_project, _view, wire, route, geometry, _editWholeBundle));
                    }
                    if (patch.LayerId is { } layer)
                    {
                        var before = route.LayerId;
                        commands.Add(new DelegateCommand("Routelaag", () => route.LayerId = layer, () => route.LayerId = before));
                    }
                }
                if (patch.SetColor)
                {
                    var color = PropertyColor(patch.Color ?? ""); var before = wire.Color;
                    commands.Add(new DelegateCommand("Draadkleur", () => wire.Color = color, () => wire.Color = before));
                }
                if (patch.SetWidth) commands.Add(HarnessOperations.SetWireLineWidth(_view, wire.Id, patch.Width));
                continue;
            }
            var annotation = _view.Annotations.Find(a => a.Id == id);
            if (annotation is not null)
            {
                RequireEditableLayer(annotation.LayerId);
                if (annotation.Locked) throw new InvalidOperationException("Ontgrendel eerst alle geselecteerde annotaties.");
                var next = EditData.Clone(annotation);
                if (patch.SetColor) next.Color = PropertyColor(patch.Color ?? "");
                if (patch.SetWidth) next.LineWidthMm = patch.Width ?? throw new ArgumentException("Een annotatie vereist een eigen lijndikte.");
                if (patch.FontSizeMm is { } height) next.FontSizeMm = height;
                if (patch.LayerId is { } layer) next.LayerId = layer;
                commands.Add(PropertyReplace(_view.Annotations, annotation, next, "Annotatiestijl"));
                continue;
            }
            var component = _view.Components.Find(c => c.Id == id || c.EntityId == id);
            if (component is not null && patch.LayerId is { } componentLayer)
            {
                RequireEditableLayer(component.LayerId);
                var before = component.LayerId;
                commands.Add(new DelegateCommand("Onderdeellaag", () => component.LayerId = componentLayer, () => component.LayerId = before));
            }
        }
        if (commands.Count > 0) Execute(new CompositeCommand("Eigenschappen van selectie wijzigen", commands.ToArray()));
    }
}
