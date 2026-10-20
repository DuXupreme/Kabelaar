namespace Kabelaar.Core;

public static partial class HarnessOperations
{
    public static IEditCommand SetRouteGeometry(HarnessProject project, DrawingView view, PlacedRoute route,
        RouteGeometry geometry, bool checkOtherScaledViews = true) =>
        SetRouteGeometryCore(project, view, route, geometry, checkOtherScaledViews, null);

    private static IEditCommand SetRouteGeometryCore(HarnessProject project, DrawingView view, PlacedRoute route,
        RouteGeometry geometry, bool checkOtherScaledViews, RouteBranch? knownBranch, double? measuredLength = null)
    {
        Geometry.ValidateRouteGeometry(geometry);
        var branch = knownBranch ?? project.Assembly.Branches.Single(b => b.Id == route.BranchId);
        var after = geometry.Copy(); var length = measuredLength ?? Geometry.Length(after);
        if (!double.IsFinite(length)) throw new ArgumentException("De routegeometrie is te groot om betrouwbaar te verwerken.");
        if (view.Mode == LayoutMode.Scaled)
        {
            if (branch.LengthLocked && Math.Abs(length - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
                throw new InvalidOperationException("De vastgezette taklengte zou veranderen. Ontgrendel de lengte of behoud de bestaande maat.");
            if (checkOtherScaledViews) ValidateOtherScaledViews(project, view, route, length);
        }
        var before = route.Geometry?.Copy(); var beforePoints = route.Points.ToList(); var beforeSmooth = route.Smooth;
        var branchBefore = branch.Geometry?.Copy(); var branchPoints = branch.Points.ToList();
        var branchSmooth = branch.Smooth; var branchLength = branch.PhysicalLengthMm; var scaled = view.Mode == LayoutMode.Scaled;
        return new DelegateCommand("Routegeometrie wijzigen", () =>
        {
            route.Geometry = after.Copy(); route.Points = after.Knots.Select(k => k.Position).ToList();
            route.Smooth = after.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
            if (scaled)
            {
                branch.Geometry = after.Copy(); branch.Points = [.. route.Points]; branch.Smooth = route.Smooth;
                if (!branch.LengthLocked) branch.PhysicalLengthMm = length;
            }
        }, () =>
        {
            route.Geometry = before?.Copy(); route.Points = [.. beforePoints]; route.Smooth = beforeSmooth;
            if (scaled)
            {
                branch.Geometry = branchBefore?.Copy(); branch.Points = [.. branchPoints];
                branch.Smooth = branchSmooth; branch.PhysicalLengthMm = branchLength;
            }
        });
    }

    public static IEditCommand SetWireRouteGeometry(HarnessProject project, DrawingView view, Conductor wire,
        PlacedRoute route, RouteGeometry geometry, bool wholeBundle = false)
    {
        if (!project.Assembly.Conductors.Contains(wire) || !wire.BranchIds.Contains(route.BranchId) || !view.Routes.Contains(route))
            throw new InvalidOperationException("Deze routetak hoort niet bij de geselecteerde ader.");
        Geometry.ValidateRouteGeometry(geometry);
        if (wholeBundle || view.Mode == LayoutMode.Scaled)
        {
            var changes = view.WirePresentations
                .SelectMany(p => p.Routes.Where(r => r.BranchId == route.BranchId).Select(r => (Presentation: p, Route: r, Index: p.Routes.IndexOf(r)))).ToArray();
            return new CompositeCommand("Gezamenlijke route wijzigen", SetRouteGeometry(project, view, route, geometry),
                new DelegateCommand("Afzonderlijke vorm terugzetten",
                    () => { foreach (var entry in changes) entry.Presentation.Routes.Remove(entry.Route); },
                    () => { foreach (var entry in changes) entry.Presentation.Routes.Insert(Math.Min(entry.Index, entry.Presentation.Routes.Count), entry.Route); }));
        }
        var after = geometry.Copy();
        var originalPresentation = view.WirePresentations.Find(p => p.ConductorId == wire.Id);
        var original = originalPresentation?.Routes.Find(r => r.BranchId == route.BranchId)?.Geometry.Copy();
        WirePresentation? created = null;
        return new DelegateCommand("Afzonderlijke ader vormgeven", () =>
        {
            var presentation = view.WirePresentations.Find(p => p.ConductorId == wire.Id);
            if (presentation is null) view.WirePresentations.Add(presentation = created = new() { ConductorId = wire.Id });
            var target = presentation.Routes.Find(r => r.BranchId == route.BranchId);
            if (target is null) presentation.Routes.Add(new() { BranchId = route.BranchId, Geometry = after.Copy() });
            else target.Geometry = after.Copy();
        }, () =>
        {
            var presentation = view.WirePresentations.Find(p => p.ConductorId == wire.Id);
            if (presentation is null) return;
            var target = presentation.Routes.Find(r => r.BranchId == route.BranchId);
            if (original is not null && target is not null) target.Geometry = original.Copy();
            else if (target is not null) presentation.Routes.Remove(target);
            if (presentation == created && presentation.Routes.Count == 0 && presentation.LineWidthMm is null) view.WirePresentations.Remove(presentation);
        });
    }

    public static IEditCommand SetWireLineWidth(DrawingView view, string conductorId, double? widthMm)
    {
        if (string.IsNullOrWhiteSpace(conductorId) || (widthMm.HasValue && !PresentationLimits.ValidLineWidth(widthMm.Value)))
            throw new ArgumentException("De lijndikte moet groter dan 0 en maximaal 1000 mm zijn.");
        var before = view.WirePresentations.Find(p => p.ConductorId == conductorId)?.LineWidthMm;
        WirePresentation? created = null;
        return new DelegateCommand("Aderlijndikte wijzigen", () =>
        {
            var presentation = view.WirePresentations.Find(p => p.ConductorId == conductorId);
            if (presentation is null) view.WirePresentations.Add(presentation = created = new() { ConductorId = conductorId });
            presentation.LineWidthMm = widthMm;
        }, () =>
        {
            var presentation = view.WirePresentations.Find(p => p.ConductorId == conductorId);
            if (presentation is null) return;
            presentation.LineWidthMm = before;
            if (presentation == created && presentation.Routes.Count == 0 && before is null) view.WirePresentations.Remove(presentation);
        });
    }

    public static IEditCommand ResetWireRouteGeometry(DrawingView view, string conductorId, string branchId)
    {
        var presentation = view.WirePresentations.Find(p => p.ConductorId == conductorId);
        var route = presentation?.Routes.Find(r => r.BranchId == branchId);
        if (presentation is null || route is null) return new CompositeCommand("Adervorm behouden");
        var index = presentation.Routes.IndexOf(route);
        return new DelegateCommand("Adervorm terugzetten", () => presentation.Routes.Remove(route),
            () => presentation.Routes.Insert(Math.Min(index, presentation.Routes.Count), route));
    }

    private static void ValidateOtherScaledViews(HarnessProject project, DrawingView view, PlacedRoute route, double newLength)
    {
        if (project.Sheets.SelectMany(s => s.Views).Where(v => v.Mode == LayoutMode.Scaled)
            .SelectMany(v => v.Routes).Any(r => r != route && r.BranchId == route.BranchId &&
                Math.Abs(Geometry.Length(r) - newLength) > Geometry.LengthToleranceMm))
            throw new InvalidOperationException("Deze wijziging past niet bij een ander aanzicht op schaal. Pas de betrokken aanzichten eerst expliciet aan of gebruik vrije opmaak.");
    }

    private static void ValidateScaledRouteBatch(HarnessProject project, IReadOnlyDictionary<PlacedRoute, double> changes)
    {
        if (changes.Count == 0) return;
        var desiredLengths = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (route, length) in changes)
        {
            if (desiredLengths.TryGetValue(route.BranchId, out var otherLength) &&
                Math.Abs(otherLength - length) > Geometry.LengthToleranceMm)
                throw new InvalidOperationException("De verplaatste aanzichten geven verschillende lengtes voor dezelfde fysieke tak.");
            desiredLengths[route.BranchId] = length;
        }
        foreach (var sheet in project.Sheets)
            foreach (var view in sheet.Views)
            {
                if (view.Mode != LayoutMode.Scaled) continue;
                foreach (var route in view.Routes)
                {
                    if (!desiredLengths.TryGetValue(route.BranchId, out var expected)) continue;
                    var actual = changes.TryGetValue(route, out var changedLength) ? changedLength : Geometry.Length(route);
                    if (Math.Abs(actual - expected) > Geometry.LengthToleranceMm)
                        throw new InvalidOperationException("Deze wijziging past niet bij een ander aanzicht op schaal. Pas de betrokken aanzichten eerst expliciet aan of gebruik vrije opmaak.");
                }
            }
    }
}
