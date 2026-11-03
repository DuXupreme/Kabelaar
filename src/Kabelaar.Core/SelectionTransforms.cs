namespace Kabelaar.Core;

public static partial class SelectionOperations
{
    public static IEditCommand SetDrawOrder(DrawingView view, IEnumerable<string> selectedIds, bool bringToFront)
        => SetDrawOrderCore(view, selectedIds, bringToFront, []);

    public static IEditCommand SetDrawOrder(HarnessProject project, DrawingView view, IEnumerable<string> selectedIds, bool bringToFront)
        => SetDrawOrderCore(view, selectedIds, bringToFront,
            project.Assembly.Conductors.Where(w => w.BranchIds.Any(id => view.Routes.Any(r => r.BranchId == id))).Select(w => w.Id));

    private static IEditCommand SetDrawOrderCore(DrawingView view, IEnumerable<string> selectedIds, bool bringToFront, IEnumerable<string> wireIds)
    {
        var selected = selectedIds.Distinct().ToList();
        var before = view.DrawOrder.ToList();
        // Complete the order before moving objects, so sending a previously unordered object back works.
        var defaults = view.Routes.Select(r => r.BranchId).Concat(wireIds).Concat(view.WirePresentations.Select(p => p.ConductorId))
            .Concat(view.Components.Select(p => p.EntityId)).Concat(view.Annotations.Select(a => a.Id));
        var remaining = before.Concat(defaults).Distinct().Where(id => !selected.Contains(id)).ToList();
        var after = bringToFront ? remaining.Concat(selected).ToList() : selected.Concat(remaining).ToList();
        return new DelegateCommand(bringToFront ? "Naar voren" : "Naar achteren", () => view.DrawOrder = [.. after], () => view.DrawOrder = [.. before]);
    }

    public static IEditCommand TransformSelection(HarnessProject project, DrawingView view, IEnumerable<string> selectedIds,
        Point2 pivot, double rotationDegrees = 0, bool mirrorX = false, bool mirrorY = false)
    {
        if (!Geometry.IsFinite(pivot) || !double.IsFinite(rotationDegrees) || !project.Sheets.SelectMany(s => s.Views).Contains(view))
            throw new ArgumentException("Ongeldig aanzicht, draaipunt of hoek.");
        var ids = selectedIds.ToHashSet();
        foreach (var p in view.Components.Where(p => ids.Contains(p.Id))) ids.Add(p.EntityId);
        var sheet = project.Sheets.Single(s => s.Views.Contains(view));
        void Editable(string layer) { if (sheet.Layers.Find(l => l.Id == layer)?.Locked == true) throw new InvalidOperationException("Een betrokken laag is vergrendeld."); }
        var after = GraphEdit.Clone(project); var next = GraphEdit.View(after, view.Id);
        var reflected = mirrorX ^ mirrorY;
        Point2 Vector(Point2 p) => Geometry.Transform(new(mirrorX ? -p.X : p.X, mirrorY ? -p.Y : p.Y), rotationDegrees, new());
        Point2 Point(Point2 p) => pivot + Vector(p - pivot);
        foreach (var p in next.Components.Where(p => ids.Contains(p.EntityId)))
        {
            Editable(p.LayerId); p.Position = Point(p.Position); p.LabelOffset = Vector(p.LabelOffset);
            p.RotationDegrees = rotationDegrees + (reflected ? -p.RotationDegrees : p.RotationDegrees);
            p.MirrorX ^= mirrorX; p.MirrorY ^= mirrorY;
        }
        var explicitBranches = view.Routes.Where(r => ids.Contains(r.Id) || ids.Contains(r.BranchId)).Select(r => r.BranchId).ToHashSet();
        var changedShared = new HashSet<string>();
        var users = project.Assembly.Conductors.SelectMany(w => w.BranchIds.Select(b => (b, w))).ToLookup(x => x.b, x => x.w);
        foreach (var originalWire in project.Assembly.Conductors)
        {
            var selected = ids.Contains(originalWire.Id);
            var wire = after.Assembly.Conductors.Single(w => w.Id == originalWire.Id);
            foreach (var branchId in wire.BranchIds)
            {
                var originalRoute = view.Routes.Find(r => r.BranchId == branchId);
                if (originalRoute is null) continue;
                var route = next.Routes.Single(r => r.Id == originalRoute.Id);
                var moveWhole = selected || explicitBranches.Contains(branchId);
                var endpoints = new List<(int Index, Point2 Position)>();
                var originalGeometry = Geometry.EditableRouteGeometry(view, originalRoute, wire.Id);
                foreach (var (end, first) in new[] { (originalWire.From, true), (originalWire.To, false) })
                {
                    if (branchId != (first ? wire.BranchIds[0] : wire.BranchIds[^1]) || end.Kind == EndpointKind.Free) continue;
                    var oldPosition = Geometry.EndpointPosition(project, view, end);
                    var newPosition = Geometry.EndpointPosition(after, next, end);
                    if (oldPosition is null || newPosition is null || !moveWhole && oldPosition == newPosition) continue;
                    var index = originalGeometry.Knots[0].Position.DistanceTo(oldPosition.Value) <= originalGeometry.Knots[^1].Position.DistanceTo(oldPosition.Value)
                        ? 0 : originalGeometry.Knots.Count - 1;
                    endpoints.Add((index, newPosition.Value));
                }
                if (!moveWhole && endpoints.Count == 0) continue;
                Editable(route.LayerId);
                var shared = users[branchId].Count() > 1;
                var allSelected = explicitBranches.Contains(branchId) || users[branchId].All(w => ids.Contains(w.Id));
                if (next.Mode == LayoutMode.Scaled && shared && !allSelected)
                    throw new InvalidOperationException("Selecteer alle aders van de gedeelde routetak of werk in vrije opmaak.");
                var individual = next.Mode == LayoutMode.Free && (shared && !allSelected || view.WirePresentations.Any(p => p.ConductorId == wire.Id && p.Routes.Any(r => r.BranchId == branchId)));
                var geometry = originalGeometry.Copy();
                if (moveWhole) TransformGeometry(geometry, Point, Vector);
                foreach (var endpoint in endpoints) geometry.Knots[endpoint.Index].Position = endpoint.Position;
                if (individual)
                {
                    var presentation = next.WirePresentations.Find(p => p.ConductorId == wire.Id);
                    if (presentation is null) next.WirePresentations.Add(presentation = new() { ConductorId = wire.Id });
                    var custom = presentation.Routes.Find(r => r.BranchId == branchId);
                    if (custom is null) presentation.Routes.Add(new() { BranchId = branchId, Geometry = geometry });
                    else custom.Geometry = geometry;
                }
                else
                {
                    if (!changedShared.Add(branchId))
                    {
                        if (!Geometry.SamePath(Geometry.Flatten(route), Geometry.Flatten(geometry), Geometry.LengthToleranceMm))
                            throw new InvalidOperationException("De geselecteerde aders vereisen verschillende vormen voor een gedeelde tak. Kies vrije individuele opmaak.");
                        continue;
                    }
                    route.Geometry = geometry; route.Points = geometry.Knots.Select(k => k.Position).ToList(); route.Smooth = geometry.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
                    if (next.Mode == LayoutMode.Scaled)
                    {
                        var branch = after.Assembly.Branches.Single(b => b.Id == branchId);
                        if (Math.Abs(Geometry.Length(geometry) - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
                            throw new InvalidOperationException("Deze transformatie zou een fysieke lengte wijzigen. Selecteer ook de aangesloten onderdelen of gebruik vrije opmaak.");
                        branch.Geometry = geometry.Copy(); branch.Points = [.. route.Points]; branch.Smooth = route.Smooth;
                    }
                }
            }
        }
        foreach (var route in next.Routes.Where(r => explicitBranches.Contains(r.BranchId) && !changedShared.Contains(r.BranchId)))
        {
            Editable(route.LayerId);
            var geometry = Geometry.EditableRouteGeometry(next, route);
            TransformGeometry(geometry, Point, Vector); route.Geometry = geometry; route.Points = geometry.Knots.Select(k => k.Position).ToList();
            if (next.Mode == LayoutMode.Scaled)
            { var branch = after.Assembly.Branches.Single(b => b.Id == route.BranchId); branch.Geometry = geometry.Copy(); branch.Points = [.. route.Points]; }
        }
        foreach (var p in next.WirePresentations.Where(p => ids.Contains(p.ConductorId)))
        { if (p.LabelPosition is { } label) p.LabelPosition = Point(label); p.LabelRotationDegrees = rotationDegrees + (reflected ? -p.LabelRotationDegrees : p.LabelRotationDegrees); }
        foreach (var a in next.Annotations.Where(a => ids.Contains(a.Id)))
        {
            Editable(a.LayerId); if (a.Locked) throw new InvalidOperationException("Een geselecteerde annotatie is vergrendeld.");
            a.Points = a.Points.Select(Point).ToList(); a.RotationDegrees = rotationDegrees + (reflected ? -a.RotationDegrees : a.RotationDegrees);
            if (a.Kind == AnnotationKind.Dimension && Math.Abs(rotationDegrees % 180) > 1e-9) a.DimensionDirection = DimensionDirection.Aligned;
        }
        GraphEdit.RequireValid(after);
        var oldContinuity = ProductionValidator.Validate(project).Where(i => i.Code == "wire.routeContinuity").Select(i => i.EntityId).ToHashSet();
        if (ProductionValidator.Validate(after).Any(i => i.Code == "wire.routeContinuity" && !oldContinuity.Contains(i.EntityId)))
            throw new InvalidOperationException("Deze transformatie zou de gemeten routetakken onderbreken. Selecteer de volledige route.");
        return GraphEdit.Command("Selectie transformeren", project, after);
    }
}
