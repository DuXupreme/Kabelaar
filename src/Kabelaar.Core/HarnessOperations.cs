namespace Kabelaar.Core;

public static class HarnessOperations
{
    public static ConnectorInstance NewConnector(PartDefinition part, string name) => new()
    {
        Name = name, PartId = part.Id,
        Cavities = part.Pins.Select(pin => new Cavity
        {
            PinDefinitionId = pin.Id, Label = pin.Label, MaxWires = pin.MaxWires, State = CavityState.Unspecified
        }).ToList()
    };

    public static IEditCommand AddConnector(HarnessProject project, DrawingView view, ConnectorInstance connector, Point2 position)
    {
        var placement = new PlacedComponent { EntityId = connector.Id, Position = position };
        return new DelegateCommand("Connector toevoegen",
            () => { project.Assembly.Connectors.Add(connector); view.Components.Add(placement); },
            () => { view.Components.Remove(placement); project.Assembly.Connectors.Remove(connector); });
    }

    public static IEditCommand AddRoute(HarnessProject project, DrawingView view, RouteBranch branch, Conductor? conductor = null)
    {
        ValidatePoints(branch.Points);
        var placement = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points], Smooth = branch.Smooth };
        if (view.Mode == LayoutMode.Scaled) branch.PhysicalLengthMm = Geometry.Length(branch.Points, branch.Smooth);
        if (conductor is not null && !conductor.BranchIds.Contains(branch.Id)) conductor.BranchIds.Add(branch.Id);
        var cavityChanges = new List<(Cavity Cavity, CavityState Before)>();
        if (conductor is not null)
            foreach (var endpoint in new[] { conductor.From, conductor.To }.Where(e => e.Kind == EndpointKind.Cavity))
            {
                var cavity = project.Assembly.Connectors.Find(c => c.Id == endpoint.EntityId)?.Cavities.Find(c => c.Id == endpoint.CavityId);
                if (cavity is not null && cavityChanges.All(c => c.Cavity != cavity)) cavityChanges.Add((cavity, cavity.State));
            }
        return new DelegateCommand("Route en ader toevoegen",
            () =>
            {
                project.Assembly.Branches.Add(branch); view.Routes.Add(placement);
                if (conductor is not null) project.Assembly.Conductors.Add(conductor);
                foreach (var entry in cavityChanges) entry.Cavity.State = CavityState.Occupied;
            },
            () =>
            {
                if (conductor is not null) project.Assembly.Conductors.Remove(conductor);
                view.Routes.Remove(placement); project.Assembly.Branches.Remove(branch);
                foreach (var entry in cavityChanges) entry.Cavity.State = entry.Before;
            });
    }

    public static IEditCommand SetRoutePoints(HarnessProject project, DrawingView view, PlacedRoute route, IReadOnlyList<Point2> points, bool? smooth = null)
    {
        ValidatePoints(points);
        var branch = project.Assembly.Branches.Single(b => b.Id == route.BranchId);
        var afterSmooth = smooth ?? route.Smooth;
        var length = Geometry.Length(points, afterSmooth);
        if (view.Mode == LayoutMode.Scaled && branch.LengthLocked &&
            Math.Abs(length - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
            throw new InvalidOperationException("De vastgezette taklengte zou veranderen. Ontgrendel de lengte of behoud de bestaande maat.");
        var before = route.Points.ToList();
        var beforeSmooth = route.Smooth;
        var branchBefore = branch.Points.ToList();
        var branchSmooth = branch.Smooth;
        var physicalBefore = branch.PhysicalLengthMm;
        var after = points.ToList();
        var scaled = view.Mode == LayoutMode.Scaled;
        return new DelegateCommand("Route wijzigen",
            () =>
            {
                route.Points = [.. after]; route.Smooth = afterSmooth;
                if (scaled)
                {
                    branch.Points = [.. after]; branch.Smooth = afterSmooth;
                    if (!branch.LengthLocked) branch.PhysicalLengthMm = length;
                }
            },
            () =>
            {
                route.Points = [.. before]; route.Smooth = beforeSmooth;
                branch.Points = [.. branchBefore]; branch.Smooth = branchSmooth; branch.PhysicalLengthMm = physicalBefore;
            });
    }

    /// <summary>Insert a graphical bend; never creates another physical conductor or branch.</summary>
    public static IEditCommand InsertRoutePoint(HarnessProject project, DrawingView view, PlacedRoute route, int segmentIndex, Point2 point)
    {
        if (segmentIndex < 0 || segmentIndex >= route.Points.Count - 1) throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        var points = route.Points.ToList();
        points.Insert(segmentIndex + 1, point);
        return SetRoutePoints(project, view, route, points);
    }

    public static IEditCommand MoveComponent(HarnessProject project, DrawingView view, PlacedComponent placement, Point2 position,
        double? rotationDegrees = null, IReadOnlyDictionary<string, Point2>? previousPinPositions = null)
    {
        if (!Geometry.IsFinite(position) || !double.IsFinite(rotationDegrees ?? placement.RotationDegrees))
            throw new ArgumentException("Positie en hoek moeten eindig zijn.");
        return MovePlacements(project, view, new Dictionary<PlacedComponent, (Point2, double, double)> { [placement] = (position, rotationDegrees ?? placement.RotationDegrees, placement.DisplayScale) }, previousPinPositions);
    }

    public static IEditCommand MoveSelection(HarnessProject project, DrawingView view, IReadOnlyDictionary<string, Point2> positions)
    {
        var placements = new Dictionary<PlacedComponent, (Point2, double, double)>();
        foreach (var (id, position) in positions)
        {
            if (!Geometry.IsFinite(position)) throw new ArgumentException("Posities moeten eindig zijn.");
            var placement = view.Components.Single(p => p.Id == id);
            placements[placement] = (position, placement.RotationDegrees, placement.DisplayScale);
        }
        return MovePlacements(project, view, placements);
    }

    public static IEditCommand SetComponentPresentation(HarnessProject project, DrawingView view, PlacedComponent placement,
        double displayScale, bool showPins, bool showPinNumbers, bool showLabel, bool showPartNumber, double labelFontSizeMm,
        Point2? targetPosition = null, double? targetRotation = null)
    {
        if (!PresentationLimits.ValidDisplayScale(displayScale) || !PresentationLimits.ValidFontSize(labelFontSizeMm))
            throw new ArgumentOutOfRangeException(nameof(displayScale), "Symboolschaal en letterhoogte moeten groter dan 0 en maximaal 1000 zijn.");
        if (view.Mode == LayoutMode.Scaled && Math.Abs(displayScale - 1) > 1e-9)
            throw new InvalidOperationException("Een afzonderlijke symboolschaal kan alleen in vrije opmaak. Een aanzicht op schaal gebruikt de werkelijke onderdeelgeometrie.");
        var position = targetPosition ?? placement.Position;
        var rotation = targetRotation ?? placement.RotationDegrees;
        if (!Geometry.IsFinite(position) || !double.IsFinite(rotation))
            throw new ArgumentException("Positie en hoek moeten eindig zijn.");
        var geometry = position == placement.Position && rotation == placement.RotationDegrees && displayScale == placement.DisplayScale
            ? new CompositeCommand("Geometrie behouden")
            : MovePlacements(project, view,
                new Dictionary<PlacedComponent, (Point2, double, double)> { [placement] = (position, rotation, displayScale) });
        var before = (placement.ShowPins, placement.ShowPinNumbers, placement.ShowLabel, placement.ShowPartNumber, placement.LabelFontSizeMm);
        return new CompositeCommand("Onderdeelweergave wijzigen",
        [
            geometry,
            new DelegateCommand("Onderdeellabels wijzigen",
                () => (placement.ShowPins, placement.ShowPinNumbers, placement.ShowLabel, placement.ShowPartNumber, placement.LabelFontSizeMm) =
                    (showPins, showPinNumbers, showLabel, showPartNumber, labelFontSizeMm),
                () => (placement.ShowPins, placement.ShowPinNumbers, placement.ShowLabel, placement.ShowPartNumber, placement.LabelFontSizeMm) = before)
        ]);
    }

    public static IEditCommand SetDrawingStyle(DrawingView view, DrawingStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!PresentationLimits.ValidStyle(style))
            throw new ArgumentException("Lijndiktes en teksthoogte moeten groter dan 0 en maximaal 1000 mm zijn. Twistuitwijking moet 0–1000 mm zijn; getekende spoed 0–100000 mm.", nameof(style));
        var before = view.Style.Copy();
        var after = style.Copy();
        return new DelegateCommand("Tekeningstijl wijzigen", () => view.Style = after.Copy(), () => view.Style = before.Copy());
    }

    private static IEditCommand MovePlacements(HarnessProject project, DrawingView view, Dictionary<PlacedComponent, (Point2 Position, double Rotation, double Scale)> placements,
        IReadOnlyDictionary<string, Point2>? previousPinPositions = null)
    {
        var changes = new Dictionary<PlacedRoute, List<Point2>>();
        var wiresByBranch = project.Assembly.Conductors.SelectMany(w => w.BranchIds.Select(id => (id, wire: w))).ToLookup(x => x.id, x => x.wire);
        foreach (var wire in project.Assembly.Conductors)
        {
            foreach (var (endpoint, first) in new[] { (wire.From, true), (wire.To, false) })
            {
                var placement = placements.Keys.FirstOrDefault(p => p.EntityId == endpoint.EntityId);
                if (placement is null || endpoint.Kind == EndpointKind.Free || wire.BranchIds.Count == 0) continue;
                var target = placements[placement];
                var simulated = new PlacedComponent { EntityId = placement.EntityId, Position = target.Position, RotationDegrees = target.Rotation, DisplayScale = target.Scale };
                var branchId = first ? wire.BranchIds[0] : wire.BranchIds[^1];
                if (view.Mode == LayoutMode.Free && wiresByBranch[branchId].Take(2).Count() > 1) continue;
                var oldPin = endpoint.CavityId is { } cavityId && previousPinPositions?.TryGetValue(cavityId, out var previousPin) == true
                    ? previousPin : Geometry.PinWorld(project, placement, endpoint.CavityId);
                var newPin = Geometry.PinWorld(project, simulated, endpoint.CavityId);
                if (oldPin is null || newPin is null) continue;
                foreach (var route in view.Routes.Where(r => r.BranchId == branchId && r.Points.Count >= 2))
                {
                    if (!changes.TryGetValue(route, out var points)) changes[route] = points = [.. route.Points];
                    var index = route.Points[0].DistanceTo(oldPin.Value) <= route.Points[^1].DistanceTo(oldPin.Value) ? 0 : points.Count - 1;
                    points[index] = newPin.Value;
                }
            }
        }
        foreach (var (route, points) in changes)
        {
            var startDelta = points[0] - route.Points[0];
            var endDelta = points[^1] - route.Points[^1];
            if (startDelta.DistanceTo(endDelta) <= 1e-9)
                for (var i = 0; i < points.Count; i++) points[i] = route.Points[i] + startDelta;
            if (view.Mode == LayoutMode.Scaled && wiresByBranch[route.BranchId].Take(2).Count() > 1)
                foreach (var shared in wiresByBranch[route.BranchId])
                    foreach (var (endpoint, first) in new[] { (shared.From, true), (shared.To, false) })
                    {
                        if (endpoint.Kind == EndpointKind.Free || (first ? shared.BranchIds[0] : shared.BranchIds[^1]) != route.BranchId) continue;
                        var anchor = view.Components.Find(p => p.EntityId == endpoint.EntityId);
                        if (anchor is null) continue;
                        var oldPoint = Geometry.PinWorld(project, anchor, endpoint.CavityId);
                        var target = placements.TryGetValue(anchor, out var moved)
                            ? new PlacedComponent { EntityId = anchor.EntityId, Position = moved.Position, RotationDegrees = moved.Rotation, DisplayScale = moved.Scale } : anchor;
                        var expected = Geometry.PinWorld(project, target, endpoint.CavityId);
                        if (oldPoint is null || expected is null) continue;
                        var endIndex = route.Points[0].DistanceTo(oldPoint.Value) <= route.Points[^1].DistanceTo(oldPoint.Value) ? 0 : points.Count - 1;
                        if (points[endIndex].DistanceTo(expected.Value) > Geometry.LengthToleranceMm)
                            throw new InvalidOperationException("Een gedeelde tak kan niet voor slechts één aansluiting worden verplaatst. Gebruik een eigen aansluitsegment of verplaats alle betrokken aansluitingen samen.");
                    }
        }
        // Construct every route command before applying any; one failed lock leaves the whole edit untouched.
        var routeCommands = changes.Select(change => SetRoutePoints(project, view, change.Key, change.Value)).ToArray();
        var positionCommands = placements.Select(entry =>
        {
            var placement = entry.Key;
            var before = placement.Position; var beforeRotation = placement.RotationDegrees; var beforeScale = placement.DisplayScale;
            return new DelegateCommand("Positie",
                () => { placement.Position = entry.Value.Position; placement.RotationDegrees = entry.Value.Rotation; placement.DisplayScale = entry.Value.Scale; },
                () => { placement.Position = before; placement.RotationDegrees = beforeRotation; placement.DisplayScale = beforeScale; });
        }).ToArray();
        return new CompositeCommand("Onderdelen verplaatsen", [.. positionCommands, .. routeCommands]);
    }

    public static IEditCommand SetLayoutMode(HarnessProject project, DrawingView view, LayoutMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == LayoutMode.Scaled && view.Components.Any(p => Math.Abs(p.DisplayScale - 1) > 1e-9))
            throw new InvalidOperationException("Zet eerst alle afzonderlijke symboolschalen terug op 1 voordat dit aanzicht op schaal kan worden gezet.");
        if (mode == LayoutMode.Scaled)
            foreach (var route in view.Routes)
            {
                var branch = project.Assembly.Branches.Single(b => b.Id == route.BranchId);
                if (Math.Abs(Geometry.Length(route.Points, route.Smooth) - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
                    throw new InvalidOperationException("Dit vrije aanzicht komt niet overeen met de productiematen. Pas de geometrie eerst expliciet aan.");
            }
        var before = view.Mode;
        return new DelegateCommand("Schaalmodus wijzigen", () => view.Mode = mode, () => view.Mode = before);
    }

    public static IEditCommand SetPhysicalLength(HarnessProject project, RouteBranch branch, double lengthMm, bool locked)
    {
        if (!double.IsFinite(lengthMm) || lengthMm <= 0) throw new ArgumentOutOfRangeException(nameof(lengthMm));
        if (project.Sheets.SelectMany(s => s.Views).Where(v => v.Mode == LayoutMode.Scaled).SelectMany(v => v.Routes)
            .Any(r => r.BranchId == branch.Id && Math.Abs(Geometry.Length(r.Points, r.Smooth) - lengthMm) > Geometry.LengthToleranceMm))
            throw new InvalidOperationException("De opgegeven lengte past niet bij een aanzicht op schaal. Pas de route aan of gebruik vrije opmaak.");
        var before = branch.PhysicalLengthMm;
        var beforeLock = branch.LengthLocked;
        return new DelegateCommand("Productielengte wijzigen", () => { branch.PhysicalLengthMm = lengthMm; branch.LengthLocked = locked; },
            () => { branch.PhysicalLengthMm = before; branch.LengthLocked = beforeLock; });
    }

    public static IEditCommand SetConductorEndpoints(HarnessProject project, Conductor wire, Endpoint from, Endpoint to)
    {
        var beforeFrom = wire.From; var beforeTo = wire.To;
        ValidateEndpoint(project, from); ValidateEndpoint(project, to);
        var afterFrom = Clone(from); var afterTo = Clone(to);
        var changes = new List<(DrawingView View, PlacedRoute Route, List<Point2> Points)>();
        var sharedBranches = project.Assembly.Conductors.SelectMany(w => w.BranchIds).GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var view in project.Sheets.SelectMany(s => s.Views))
        {
            var viewChanges = new Dictionary<PlacedRoute, List<Point2>>();
            foreach (var (oldEnd, newEnd, first) in new[] { (wire.From, from, true), (wire.To, to, false) })
            {
                if (wire.BranchIds.Count == 0 || newEnd.Kind == EndpointKind.Free) continue;
                var oldPosition = Geometry.EndpointPosition(project, view, oldEnd);
                var newPosition = Geometry.EndpointPosition(project, view, newEnd);
                if (newPosition is null) continue;
                var branchId = first ? wire.BranchIds[0] : wire.BranchIds[^1];
                foreach (var route in view.Routes.Where(r => r.BranchId == branchId && r.Points.Count >= 2))
                {
                    var index = oldPosition is { } old
                        ? (route.Points[0].DistanceTo(old) <= route.Points[^1].DistanceTo(old) ? 0 : route.Points.Count - 1)
                        : (route.Points[0].DistanceTo(newPosition.Value) <= route.Points[^1].DistanceTo(newPosition.Value) ? 0 : route.Points.Count - 1);
                    if (sharedBranches.Contains(branchId))
                    {
                        if (view.Mode == LayoutMode.Scaled && route.Points[index].DistanceTo(newPosition.Value) > Geometry.LengthToleranceMm)
                            throw new InvalidOperationException("Deze ader gebruikt een gedeelde tak. Voeg voor deze andere aansluiting een eigen aansluitsegment toe; de gemeenschappelijke productiemaat blijft behouden.");
                        continue;
                    }
                    if (!viewChanges.TryGetValue(route, out var points)) viewChanges[route] = points = [.. route.Points];
                    points[index] = newPosition.Value;
                }
            }
            changes.AddRange(viewChanges.Select(change => (view, change.Key, change.Value)));
        }
        foreach (var group in changes.Where(c => c.View.Mode == LayoutMode.Scaled).GroupBy(c => c.Route.BranchId))
        {
            var lengths = group.Select(c => Geometry.Length(c.Points, c.Route.Smooth)).ToArray();
            if (lengths.Max() - lengths.Min() > Geometry.LengthToleranceMm)
                throw new InvalidOperationException("Deze aansluiting levert verschillende fysieke lengtes op in de aanzichten op schaal. Maak de betrokken aanzichten eerst consistent of kies vrije opmaak.");
        }
        var routeCommands = changes.Select(change => SetRoutePoints(project, change.View, change.Route, change.Points)).ToArray();
        var cavityChanges = new List<(Cavity Cavity, CavityState Before, CavityState After)>();
        foreach (var connector in project.Assembly.Connectors)
            foreach (var cavity in connector.Cavities)
            {
                bool Matches(Endpoint end) => end.Kind == EndpointKind.Cavity && end.EntityId == connector.Id && end.CavityId == cavity.Id;
                if (!new[] { beforeFrom, beforeTo, from, to }.Any(Matches)) continue;
                var occupied = project.Assembly.Conductors.Any(w => w != wire && (Matches(w.From) || Matches(w.To))) || Matches(from) || Matches(to);
                var state = occupied ? CavityState.Occupied : cavity.State == CavityState.Occupied ? CavityState.Unused : cavity.State;
                cavityChanges.Add((cavity, cavity.State, state));
            }
        return new CompositeCommand("Aansluitingen wijzigen",
            [new DelegateCommand("Aansluitgegevens",
                () =>
                {
                    wire.From = Clone(afterFrom); wire.To = Clone(afterTo);
                    foreach (var change in cavityChanges) change.Cavity.State = change.After;
                },
                () =>
                {
                    wire.From = beforeFrom; wire.To = beforeTo;
                    foreach (var change in cavityChanges) change.Cavity.State = change.Before;
                }), .. routeCommands]);
    }

    private static Endpoint Clone(Endpoint endpoint) => new() { Kind = endpoint.Kind, EntityId = endpoint.EntityId, CavityId = endpoint.CavityId, Description = endpoint.Description };
    private static void ValidateEndpoint(HarnessProject project, Endpoint endpoint)
    {
        if (!Enum.IsDefined(endpoint.Kind)) throw new InvalidOperationException("Onbekend aansluitsoort.");
        if (endpoint.Kind == EndpointKind.Cavity && !project.Assembly.Connectors.Any(c => c.Id == endpoint.EntityId && c.Cavities.Any(p => p.Id == endpoint.CavityId)) ||
            endpoint.Kind == EndpointKind.Splice && !project.Assembly.Splices.Any(s => s.Id == endpoint.EntityId))
            throw new InvalidOperationException("Onbekende aansluiting.");
        if (endpoint.Kind == EndpointKind.Free && (endpoint.EntityId is not null || endpoint.CavityId is not null))
            throw new InvalidOperationException("Een vrij uiteinde mag geen aansluitverwijzing bewaren.");
    }
    private static void ValidatePoints(IReadOnlyList<Point2> points)
    {
        if (points.Count < 2 || points.Any(p => !Geometry.IsFinite(p))) throw new ArgumentException("Een route vereist minstens twee eindige punten.");
    }
}
