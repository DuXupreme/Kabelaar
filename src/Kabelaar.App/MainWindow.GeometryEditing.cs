using Kabelaar.Core;
using Kabelaar.Rendering;
using Geometry = Kabelaar.Core.Geometry;

namespace Kabelaar.App;

public partial class MainWindow
{
    private sealed record EditRoute(Conductor? Wire, PlacedRoute Route, RouteGeometry Original, int? Segment);
    private sealed class GeometrySession
    {
        public required CanvasEditGesture Gesture;
        public required Guid State;
        public List<EditRoute> Routes = [];
        public List<Conductor> PreviewWires = [];
        public List<PlacedComponent> Components = [];
        public List<Annotation> Annotations = [];
        public IReadOnlyList<ScenePrimitive> SourcePrimitives = [];
        public IEditCommand? Command;
        public string? Error;
    }
    private GeometrySession? _geometrySession;
    private readonly Dictionary<(string? Wire, string Route), RouteGeometry> _handleGeometries = [];

    private void RefreshGeometryHandles(Conductor? wire, PlacedRoute selected)
    {
        _handleGeometries.Clear();
        var routes = wire is null ? new[] { selected } : _view.Routes.Where(r => wire.BranchIds.Contains(r.BranchId)).ToArray();
        foreach (var route in routes)
        {
            if (_sheet.Layers.Find(l => l.Id == route.LayerId)?.Locked == true) continue;
            var geometry = Geometry.EditableRouteGeometry(_view, route, wire?.Id);
            _handleGeometries[(wire?.Id, route.Id)] = geometry;
            Canvas.GeometryHandles.AddRange(Handles(wire?.Id, route, geometry));
        }
    }
    private IEnumerable<CanvasGeometryHandle> Handles(string? wireId, PlacedRoute route, RouteGeometry geometry)
    {
        foreach (var knot in geometry.Knots)
        {
            var point = ToSheet(knot.Position);
            yield return new(wireId, route.Id, knot.Id, GeometryHandleKind.Knot, point, point);
            if (geometry.Segments.Any(s => s.EndKnotId == knot.Id && s.Kind == RouteSegmentKind.Cubic))
                yield return new(wireId, route.Id, knot.Id, GeometryHandleKind.Incoming, ToSheet(knot.Position + knot.InHandle), point);
            if (geometry.Segments.Any(s => s.StartKnotId == knot.Id && s.Kind == RouteSegmentKind.Cubic))
                yield return new(wireId, route.Id, knot.Id, GeometryHandleKind.Outgoing, ToSheet(knot.Position + knot.OutHandle), point);
        }
    }

    internal void BeginGeometryEdit(CanvasEditGesture gesture)
    {
        CancelGeometryEdit();
        if (_busy is not null || _saving || gesture.Selection.Count == 0) return;
        var session = new GeometrySession { Gesture = gesture, State = _history.StateToken };
        try
        {
            session.Components = _view.Components.Where(c => gesture.Selection.Contains(c.Id) || gesture.Selection.Contains(c.EntityId)).ToList();
            RequireEditableComponentGeometry(session.Components);
            session.Annotations = _view.Annotations.Where(a => gesture.Selection.Contains(a.Id)).ToList();
            foreach (var annotation in session.Annotations)
            {
                RequireEditableLayer(annotation.LayerId);
                if (annotation.Locked) throw new InvalidOperationException("De geselecteerde annotatie is vergrendeld.");
            }
            var selectedWires = _project.Assembly.Conductors.Where(w => gesture.Selection.Contains(w.Id)).ToArray();
            var directRoutes = _view.Routes.Where(r => gesture.Selection.Contains(r.Id) || gesture.Selection.Contains(r.BranchId)).ToArray();
            var seen = new HashSet<(string?, string)>();
            foreach (var wire in selectedWires)
            {
                var routes = _view.Routes.Where(r => wire.BranchIds.Contains(r.BranchId)).ToArray();
                if (gesture.Handle is { } handle) routes = routes.Where(r => r.Id == handle.RouteId).ToArray();
                else if (_wireDragScope != WireDragScope.WholeWire && routes.Length > 0)
                    routes = [routes.MinBy(r => DistanceToRoute(wire.Id, r, ToModel(gesture.Start)))!];
                foreach (var route in routes) AddRoute(wire, route);
            }
            foreach (var route in directRoutes) AddRoute(null, route);
            void AddRoute(Conductor? wire, PlacedRoute route)
            {
                RequireEditableLayer(route.LayerId);
                var shared = wire is null || _editWholeBundle || _view.Mode == LayoutMode.Scaled;
                var key = (shared ? null : wire!.Id, route.Id);
                if (!seen.Add(key)) return;
                var g = _handleGeometries.TryGetValue((wire?.Id, route.Id), out var known) ? known.Copy() : Geometry.EditableRouteGeometry(_view, route, wire?.Id);
                var segment = gesture.Handle is null && _wireDragScope == WireDragScope.Segment ? NearestSegment(g, ToModel(gesture.Start)) : (int?)null;
                session.Routes.Add(new(shared ? null : wire, route, g, segment));
            }
            var branchIds = session.Routes.Select(r => r.Route.BranchId).ToHashSet();
            var componentIds = session.Components.Select(c => c.EntityId).ToHashSet();
            if (_endpointMoveMode == EndpointMoveMode.MoveConnectedNode)
                foreach (var wire in selectedWires)
                    foreach (var endpoint in new[] { wire.From, wire.To })
                        if (endpoint.Kind != EndpointKind.Free && endpoint.EntityId is not null) componentIds.Add(endpoint.EntityId);
            session.PreviewWires = _project.Assembly.Conductors.Where(w => selectedWires.Contains(w) ||
                session.Routes.Any(r => r.Wire is null && w.BranchIds.Contains(r.Route.BranchId)) ||
                componentIds.Contains(w.From.EntityId ?? "") || componentIds.Contains(w.To.EntityId ?? "")).ToList();
            var hidden = session.PreviewWires.Select(w => w.Id).Concat(componentIds)
                .Concat(session.Annotations.Select(a => a.Id)).Concat(directRoutes.Select(r => r.BranchId)).ToHashSet();
            foreach (var group in _project.Assembly.TwistGroups.Where(g => g.ConductorIds.Any(hidden.Contains))) hidden.Add(group.Id);
            session.SourcePrimitives = Canvas.Scene?.Primitives.Where(p => p.ViewId == _view.Id && hidden.Contains(p.EntityId)).ToArray() ?? [];
            if (session.Routes.Count == 0 && session.Components.Count == 0 && session.Annotations.Count == 0) return;
            _geometrySession = session;
            Canvas.BeginPreview(hidden);
            UpdateGeometryEdit(gesture);
        }
        catch (Exception ex) { Status(ex.Message); _geometrySession = null; Canvas.EndPreview(); }
    }

    private double DistanceToRoute(string? wireId, PlacedRoute route, Point2 point)
    {
        var points = Geometry.Flatten(_view, route, wireId);
        return Enumerable.Range(0, Math.Max(0, points.Count - 1)).Select(i => SceneSpatialIndex.DistanceToSegment(point, points[i], points[i + 1])).DefaultIfEmpty(double.MaxValue).Min();
    }
    private static int NearestSegment(RouteGeometry geometry, Point2 point) =>
        Enumerable.Range(0, geometry.Segments.Count).MinBy(index =>
            Enumerable.Range(0, 24).Min(i => SceneSpatialIndex.DistanceToSegment(point,
                Geometry.Evaluate(geometry, index, i / 24d), Geometry.Evaluate(geometry, index, (i + 1) / 24d))));

    internal void UpdateGeometryEdit(CanvasEditGesture gesture)
    {
        var session = _geometrySession;
        if (session is null) return;
        if (_history.StateToken != session.State) { CancelGeometryEdit(); Status("De tekening veranderde tijdens het slepen. Begin de bewerking opnieuw."); return; }
        var delta = ToModel(gesture.Current) - ToModel(session.Gesture.Start);
        var edited = new Dictionary<EditRoute, RouteGeometry>();
        var componentPositions = session.Components.ToDictionary(c => c.Id, c => c.Position + delta);
        var detached = new Dictionary<Conductor, (Endpoint From, Endpoint To)>();
        var error = (string?)null;
        var commands = new List<IEditCommand>();
        try
        {
            foreach (var entry in session.Routes)
            {
                var g = entry.Original.Copy();
                var handle = session.Gesture.Handle;
                var movedIndices = new HashSet<int>();
                if (handle is not null)
                {
                    var index = g.Knots.FindIndex(k => k.Id == handle.KnotId);
                    if (index < 0) throw new InvalidOperationException("Het bewerkingspunt is gewijzigd; selecteer de ader opnieuw.");
                    if (handle.Kind == GeometryHandleKind.Knot) { g.Knots[index].Position += delta; movedIndices.Add(index); }
                    else g = Geometry.MoveHandle(g, handle.KnotId, handle.Kind == GeometryHandleKind.Outgoing,
                        ToModel(gesture.Current) - g.Knots[index].Position, _tangentMode);
                }
                else
                {
                    if (entry.Segment is { } index) { movedIndices.Add(index); movedIndices.Add(index + 1); }
                    else for (var i = 0; i < g.Knots.Count; i++) movedIndices.Add(i);
                    foreach (var i in movedIndices) g.Knots[i].Position += delta;
                }
                var wires = entry.Wire is null
                    ? _project.Assembly.Conductors.Where(w => w.BranchIds.Contains(entry.Route.BranchId))
                    : new[] { entry.Wire };
                foreach (var wire in wires)
                    foreach (var (endpoint, first) in new[] { (wire.From, true), (wire.To, false) })
                    {
                        if (endpoint.Kind == EndpointKind.Free || wire.BranchIds.Count == 0 ||
                            (first ? wire.BranchIds[0] : wire.BranchIds[^1]) != entry.Route.BranchId) continue;
                        var point = Geometry.EndpointPosition(_project, _view, endpoint);
                        if (point is null) continue;
                        var index = entry.Original.Knots[0].Position.DistanceTo(point.Value) <= entry.Original.Knots[^1].Position.DistanceTo(point.Value) ? 0 : g.Knots.Count - 1;
                        if (!movedIndices.Contains(index) || g.Knots[index].Position.DistanceTo(entry.Original.Knots[index].Position) < 1e-9) continue;
                        if (_endpointMoveMode == EndpointMoveMode.KeepConnection) g.Knots[index].Position = entry.Original.Knots[index].Position;
                        else if (_endpointMoveMode == EndpointMoveMode.MoveConnectedNode)
                        {
                            var component = _view.Components.Find(c => c.EntityId == endpoint.EntityId)
                                ?? throw new InvalidOperationException("De aangesloten component staat niet in dit aanzicht.");
                            RequireEditableComponentGeometry([component]);
                            var target = component.Position + (g.Knots[index].Position - entry.Original.Knots[index].Position);
                            if (componentPositions.TryGetValue(component.Id, out var existing) && existing.DistanceTo(target) > .00001)
                                throw new InvalidOperationException("De selectie vraagt verschillende bewegingen van hetzelfde aangesloten knooppunt.");
                            componentPositions[component.Id] = target;
                        }
                        else
                        {
                            if (entry.Wire is null) throw new InvalidOperationException("Maak een uiteinde los bij één geselecteerde ader, met ‘Bewerk hele bundel’ uit.");
                            var ends = detached.GetValueOrDefault(wire, (wire.From, wire.To));
                            var free = new Endpoint { Description = "Losgemaakt uiteinde" };
                            detached[wire] = first ? (free, ends.Item2) : (ends.Item1, free);
                        }
                    }
                edited[entry] = g;
            }
            // The final explicit route edit must include endpoint motion performed by
            // MoveSelection, otherwise it would overwrite that command with stale anchors.
            foreach (var (entry, g) in edited)
            {
                var wires = entry.Wire is null ? _project.Assembly.Conductors.Where(w => w.BranchIds.Contains(entry.Route.BranchId)) : new[] { entry.Wire };
                foreach (var wire in wires)
                    foreach (var (endpoint, first) in new[] { (wire.From, true), (wire.To, false) })
                    {
                        if (wire.BranchIds.Count == 0 || (first ? wire.BranchIds[0] : wire.BranchIds[^1]) != entry.Route.BranchId) continue;
                        var component = _view.Components.Find(c => c.EntityId == endpoint.EntityId);
                        if (component is null || !componentPositions.TryGetValue(component.Id, out var position)) continue;
                        var oldPoint = Geometry.EndpointPosition(_project, _view, endpoint);
                        if (oldPoint is null) continue;
                        var index = entry.Original.Knots[0].Position.DistanceTo(oldPoint.Value) <= entry.Original.Knots[^1].Position.DistanceTo(oldPoint.Value) ? 0 : g.Knots.Count - 1;
                        g.Knots[index].Position = oldPoint.Value + position - component.Position;
                    }
            }
            foreach (var key in componentPositions.Keys.ToArray())
                if (_view.Components.Single(c => c.Id == key).Position.DistanceTo(componentPositions[key]) < 1e-9)
                    componentPositions.Remove(key);
            foreach (var entry in edited.Keys.ToArray())
                if (UnchangedGeometry(entry.Original, edited[entry])) edited.Remove(entry);
            if (componentPositions.Count > 0)
                commands.Add(HarnessOperations.MoveSelection(_project, _view, componentPositions));
            if (detached.Count > 0)
                commands.Add(HarnessOperations.SetConductorEndpointsBatch(_project, detached));
            foreach (var (entry, g) in edited)
            {
                var representative = entry.Wire ?? _project.Assembly.Conductors.FirstOrDefault(w => w.BranchIds.Contains(entry.Route.BranchId));
                commands.Add(representative is null ? HarnessOperations.SetRouteGeometry(_project, _view, entry.Route, g)
                    : HarnessOperations.SetWireRouteGeometry(_project, _view, representative, entry.Route, g, entry.Wire is null));
            }
            foreach (var annotation in session.Annotations)
            {
                if (delta.Length < 1e-9) continue;
                var before = annotation.Points.ToList(); var after = before.Select(p => p + delta).ToList();
                commands.Add(new DelegateCommand("Annotatie verplaatsen", () => annotation.Points = after, () => annotation.Points = before));
            }
        }
        catch (Exception ex) { error = ex.Message; }
        var wirePoints = new Dictionary<string, IReadOnlyList<Point2>>();
        foreach (var wire in session.PreviewWires)
        {
            var start = PreviewEndpoint(wire.From); var end = PreviewEndpoint(wire.To);
            if (detached.TryGetValue(wire, out var ends))
            {
                if (ends.From.Kind == EndpointKind.Free) start = null;
                if (ends.To.Kind == EndpointKind.Free) end = null;
            }
            var points = new List<Point2>();
            if (start is { } first) points.Add(first);
            foreach (var branchId in wire.BranchIds)
            {
                var route = _view.Routes.Find(r => r.BranchId == branchId);
                if (route is null) continue;
                var change = edited.FirstOrDefault(e => e.Key.Route == route && (e.Key.Wire is null || e.Key.Wire == wire));
                var g = change.Key is null ? Geometry.EditableRouteGeometry(_view, route, wire.Id) : change.Value.Copy();
                var beforeComponentMotion = g.Copy();
                var hasIndividual = _view.WirePresentations.Find(p => p.ConductorId == wire.Id)?.Routes.Any(r => r.BranchId == branchId) == true;
                var sharedFreeWithoutOverride = change.Key is null && _view.Mode == LayoutMode.Free && !hasIndividual &&
                    _project.Assembly.Conductors.Count(w => w.BranchIds.Contains(branchId)) > 1;
                // Match the component command: attached endpoints follow their real connection.
                foreach (var (endpoint, isFirst) in new[] { (wire.From, true), (wire.To, false) })
                {
                    if (sharedFreeWithoutOverride) continue;
                    if ((isFirst ? wire.BranchIds[0] : wire.BranchIds[^1]) != branchId || endpoint.EntityId is null) continue;
                    var component = _view.Components.Find(c => c.EntityId == endpoint.EntityId);
                    if (component is null || !componentPositions.ContainsKey(component.Id)) continue;
                    var oldPosition = Geometry.EndpointPosition(_project, _view, endpoint);
                    var newPosition = PreviewEndpoint(endpoint);
                    if (oldPosition is null || newPosition is null) continue;
                    var i = g.Knots[0].Position.DistanceTo(oldPosition.Value) <= g.Knots[^1].Position.DistanceTo(oldPosition.Value) ? 0 : g.Knots.Count - 1;
                    g.Knots[i].Position = newPosition.Value;
                }
                if (change.Key is null && !sharedFreeWithoutOverride)
                {
                    var firstDelta = g.Knots[0].Position - beforeComponentMotion.Knots[0].Position;
                    var lastDelta = g.Knots[^1].Position - beforeComponentMotion.Knots[^1].Position;
                    if (firstDelta.DistanceTo(lastDelta) <= 1e-9)
                        for (var i = 0; i < g.Knots.Count; i++) g.Knots[i].Position = beforeComponentMotion.Knots[i].Position + firstDelta;
                    if (route.Geometry is null && !hasIndividual)
                        g = Geometry.FromLegacy(g.Knots.Select(k => k.Position).ToArray(), route.Smooth);
                }
                var path = Geometry.Flatten(g).ToArray();
                if (points.Count > 0 && points[^1].DistanceTo(path[^1]) < points[^1].DistanceTo(path[0])) Array.Reverse(path);
                foreach (var p in path) if (points.Count == 0 || points[^1].DistanceTo(p) > 1e-8) points.Add(p);
            }
            if (end is { } last && (points.Count == 0 || points[^1].DistanceTo(last) > 1e-8)) points.Add(last);
            wirePoints[wire.Id] = points;
        }
        Point2? PreviewEndpoint(Endpoint endpoint)
        {
            var p = Geometry.EndpointPosition(_project, _view, endpoint);
            var component = _view.Components.Find(c => c.EntityId == endpoint.EntityId);
            return p is not null && component is not null && componentPositions.TryGetValue(component.Id, out var target) ? p.Value + target - component.Position : p;
        }
        var sharedBranches = edited.Keys.Where(r => r.Wire is null).Select(r => r.Route.BranchId).ToHashSet();
        var individualIds = _view.WirePresentations.Where(p => p.Routes.Any(r => !sharedBranches.Contains(r.BranchId))).Select(p => p.ConductorId)
            .Concat(edited.Keys.Where(r => r.Wire is not null).Select(r => r.Wire!.Id)).ToHashSet();
        var preview = SceneBuilder.WirePreview(_project, _sheet, _view, wirePoints,
            _view.Mode == LayoutMode.Free && !_editWholeBundle && session.Routes.Count > 0 && session.Routes.All(r => r.Wire is not null),
            individualIds, detached.ToDictionary(e => e.Key.Id, e => (e.Value.From.Kind == EndpointKind.Free, e.Value.To.Kind == EndpointKind.Free))).ToList();
        foreach (var primitive in session.SourcePrimitives)
        {
            if (session.PreviewWires.Any(w => w.Id == primitive.EntityId) || _project.Assembly.TwistGroups.Any(g => g.Id == primitive.EntityId)) continue;
            var routeChange = edited.FirstOrDefault(e => e.Key.Route.BranchId == primitive.EntityId);
            if (routeChange.Key is not null && primitive is ScenePath routePath)
            {
                preview.Add(new ScenePath(routePath.EntityId, routePath.Category, Geometry.Flatten(routeChange.Value).Select(ToSheet).ToArray(),
                    routePath.Stroke, routePath.StrokeWidth, routePath.Fill, routePath.Closed, false, routePath.Dashed)
                    { ViewId = routePath.ViewId, PlacementId = routePath.PlacementId, LayerId = routePath.LayerId, Locked = routePath.Locked });
                continue;
            }
            var component = _view.Components.Find(c => c.EntityId == primitive.EntityId);
            var displacement = component is not null && componentPositions.TryGetValue(component.Id, out var p) ? p - component.Position :
                session.Annotations.Any(a => a.Id == primitive.EntityId) ? delta : new Point2();
            preview.Add(TranslatePrimitive(primitive, displacement * (_view.Scale * _sheet.PrintScale)));
        }
        Canvas.PreviewHandles = edited.Count == 0 ? Canvas.GeometryHandles.ToArray() :
            edited.SelectMany(e => Handles(e.Key.Wire?.Id ?? session.Gesture.Handle?.WireId, e.Key.Route, e.Value)).ToArray();
        Canvas.UpdatePreview(preview, error is not null);
        session.Command = error is null && commands.Count > 0 ? new CompositeCommand("Geometrie wijzigen", commands.ToArray()) : null;
        session.Error = error;
        if (error is not null) Status("Niet toepasbaar: " + error);
    }

    internal void CommitGeometryEdit(CanvasEditGesture gesture)
    {
        if (_geometrySession is null) return;
        UpdateGeometryEdit(gesture);
        var command = _geometrySession?.Command; var error = _geometrySession?.Error;
        CancelGeometryEdit();
        if (error is not null) { Status("Bewerking afgewezen: " + error); return; }
        if (command is not null) Execute(command);
    }
    internal void CancelGeometryEdit()
    {
        _geometrySession = null;
        Canvas.EndPreview();
    }
    private static bool UnchangedGeometry(RouteGeometry a, RouteGeometry b) =>
        a.Knots.Count == b.Knots.Count && a.Segments.Count == b.Segments.Count &&
        a.Knots.Zip(b.Knots).All(k => k.First.Position == k.Second.Position && k.First.InHandle == k.Second.InHandle &&
            k.First.OutHandle == k.Second.OutHandle && k.First.TangentMode == k.Second.TangentMode) &&
        a.Segments.Zip(b.Segments).All(s => s.First.Kind == s.Second.Kind);
    private static ScenePrimitive TranslatePrimitive(ScenePrimitive p, Point2 delta) => p switch
    {
        ScenePath path => new ScenePath(path.EntityId, path.Category, path.Points.Select(x => x + delta).ToArray(), path.Stroke, path.StrokeWidth, path.Fill, path.Closed, path.Smooth, path.Dashed)
            { ViewId = path.ViewId, PlacementId = path.PlacementId, LayerId = path.LayerId, Locked = path.Locked },
        SceneText text => new SceneText(text.EntityId, text.Category, text.Text, text.Position + delta, text.HeightMm, text.Color, text.Bold, text.RotationDegrees, text.MaxWidthMm)
            { ViewId = text.ViewId, PlacementId = text.PlacementId, LayerId = text.LayerId, Locked = text.Locked },
        SceneEllipse ellipse => new SceneEllipse(ellipse.EntityId, ellipse.Category, ellipse.Center + delta, ellipse.RadiusX, ellipse.RadiusY, ellipse.Stroke, ellipse.StrokeWidth, ellipse.Fill)
            { ViewId = ellipse.ViewId, PlacementId = ellipse.PlacementId, LayerId = ellipse.LayerId, Locked = ellipse.Locked },
        SceneArc arc => new SceneArc(arc.EntityId, arc.Category, arc.Center + delta, arc.Radius, arc.StartDegrees, arc.SweepDegrees, arc.Stroke, arc.StrokeWidth)
            { ViewId = arc.ViewId, PlacementId = arc.PlacementId, LayerId = arc.LayerId, Locked = arc.Locked },
        SceneImage image => new SceneImage(image.EntityId, image.Category, image.Data, new(image.ImageBounds.X + delta.X, image.ImageBounds.Y + delta.Y, image.ImageBounds.Width, image.ImageBounds.Height))
            { ViewId = image.ViewId, PlacementId = image.PlacementId, LayerId = image.LayerId, Locked = image.Locked },
        _ => p
    };
}
