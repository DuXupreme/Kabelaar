namespace Kabelaar.Core;

/// <summary>Physical route sections remain distinct from both conductors and electrical junctions.</summary>
public static class HarnessRouteOperations
{
    public static bool IsContinuous(HarnessProject project, IReadOnlyList<string> branchIds) =>
        IsContinuous(project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First()), branchIds);

    internal static bool IsContinuous(IReadOnlyDictionary<string, RouteBranch> branches, IReadOnlyList<string> branchIds)
    {
        if (branchIds.Count == 0) return false;
        if (branchIds.Distinct().Count() != branchIds.Count ||
            branchIds.Any(id => !branches.TryGetValue(id, out var branch) || branch.Points.Count < 2)) return false;
        bool Walk(bool reverse)
        {
            var first = branches[branchIds[0]];
            var end = reverse ? first.Points[0] : first.Points[^1];
            foreach (var id in branchIds.Skip(1))
            {
                var branch = branches[id];
                if (end.DistanceTo(branch.Points[0]) <= Geometry.LengthToleranceMm) end = branch.Points[^1];
                else if (end.DistanceTo(branch.Points[^1]) <= Geometry.LengthToleranceMm) end = branch.Points[0];
                else return false;
            }
            return true;
        }
        return Walk(false) || Walk(true);
    }

    public static IEditCommand SetConductorRoute(HarnessProject project, Conductor conductor, IReadOnlyList<string> orderedBranchIds)
    {
        if (!IsContinuous(project, orderedBranchIds))
            throw new InvalidOperationException("Kies een aaneengesloten reeks verschillende routetakken. De fysieke eindpunten van opeenvolgende takken moeten aansluiten.");
        var before = conductor.BranchIds.ToList();
        var after = orderedBranchIds.ToList();
        var staleShapes = project.Sheets.SelectMany(s => s.Views).SelectMany(v => v.WirePresentations)
            .Where(p => p.ConductorId == conductor.Id)
            .SelectMany(p => p.Routes.Where(r => !after.Contains(r.BranchId)).Select(r => (Presentation: p, Route: r, Index: p.Routes.IndexOf(r)))).ToArray();
        return new DelegateCommand("Aderroute wijzigen", () =>
        {
            conductor.BranchIds = [.. after];
            foreach (var shape in staleShapes) shape.Presentation.Routes.Remove(shape.Route);
        }, () =>
        {
            conductor.BranchIds = [.. before];
            foreach (var shape in staleShapes) shape.Presentation.Routes.Insert(Math.Min(shape.Index, shape.Presentation.Routes.Count), shape.Route);
        });
    }

    public static IEditCommand AddConductor(HarnessProject project, Conductor conductor)
    {
        if (project.Assembly.Conductors.Any(w => w.Id == conductor.Id)) throw new InvalidOperationException("Deze ader bestaat al.");
        if (!IsContinuous(project, conductor.BranchIds)) throw new InvalidOperationException("De nieuwe ader vereist een geldige aaneengesloten route.");
        var cavities = new List<(Cavity Cavity, CavityState Before)>();
        foreach (var endpoint in new[] { conductor.From, conductor.To })
        {
            if (!Enum.IsDefined(endpoint.Kind) || endpoint.Kind is EndpointKind.Splice or EndpointKind.ReferencePoint && endpoint.CavityId is not null)
                throw new InvalidOperationException("Het draadeinde heeft een ongeldige aansluitsoort of pinverwijzing.");
            if (endpoint.Kind == EndpointKind.Cavity)
            {
                var cavity = project.Assembly.Connectors.Find(c => c.Id == endpoint.EntityId)?.Cavities.Find(c => c.Id == endpoint.CavityId)
                    ?? throw new InvalidOperationException("Een aansluiting van de nieuwe ader ontbreekt.");
                if (cavities.All(c => c.Cavity != cavity)) cavities.Add((cavity, cavity.State));
            }
            else if (endpoint.Kind == EndpointKind.Splice && !project.Assembly.Splices.Any(s => s.Id == endpoint.EntityId))
                throw new InvalidOperationException("Een splice van de nieuwe ader ontbreekt.");
            else if (endpoint.Kind == EndpointKind.ReferencePoint && !project.Assembly.ReferencePoints.Any(s => s.Id == endpoint.EntityId))
                throw new InvalidOperationException("Een referentiepunt van de nieuwe ader ontbreekt.");
            else if (endpoint.Kind == EndpointKind.Free && (endpoint.EntityId is not null || endpoint.CavityId is not null))
                throw new InvalidOperationException("Een vrij uiteinde bevat nog een aansluitverwijzing.");
        }
        return new DelegateCommand("Ader op bestaande route toevoegen",
            () => { project.Assembly.Conductors.Add(conductor); foreach (var entry in cavities) entry.Cavity.State = CavityState.Occupied; },
            () => { project.Assembly.Conductors.Remove(conductor); foreach (var entry in cavities) entry.Cavity.State = entry.Before; });
    }

    public static IEditCommand SplitPhysicalBranch(HarnessProject project, DrawingView sourceView, PlacedRoute sourceRoute,
        int vertexIndex, double? firstPhysicalLengthMm = null)
    {
        if (vertexIndex <= 0 || vertexIndex >= sourceRoute.Points.Count - 1)
            throw new ArgumentOutOfRangeException(nameof(vertexIndex), "Kies een tussenpunt, geen begin- of eindpunt.");
        var branch = project.Assembly.Branches.Single(b => b.Id == sourceRoute.BranchId);
        if (project.Assembly.Materials.Concat(project.Assembly.Connectors.SelectMany(c => c.Accessories)).Any(m => m.BranchId == branch.Id) ||
            project.Sheets.SelectMany(s => s.Views).SelectMany(v => v.Annotations).Any(a => a.EntityId == branch.Id))
            throw new InvalidOperationException("Deze tak heeft gekoppeld materiaal of maatvoering. Ontkoppel of verdeel die koppelingen eerst expliciet; splitsen mag hun betekenis niet stilzwijgend veranderen.");
        var sourcePoints = Geometry.Flatten(sourceRoute).ToList();
        var splitIndex = 0;
        for (var control = 1; control <= vertexIndex; control++)
        {
            var found = sourcePoints.FindIndex(splitIndex + 1, p => p.DistanceTo(sourceRoute.Points[control]) < 1e-7);
            if (found < 0) throw new InvalidOperationException("Het gekozen punt ligt niet op de gemeten route.");
            splitIndex = found;
        }
        var leftSource = sourcePoints.Take(splitIndex + 1).ToList();
        var rightSource = sourcePoints.Skip(splitIndex).ToList();
        var sourceLength = Geometry.Length(sourcePoints);
        var prefixLength = Geometry.Length(leftSource);
        if (sourceLength <= 0 || prefixLength <= 0 || Geometry.Length(rightSource) <= 0)
            throw new InvalidOperationException("Beide nieuwe takdelen moeten een zichtbare lengte hebben.");
        var displayFraction = prefixLength / sourceLength;
        var firstLength = sourceView.Mode == LayoutMode.Scaled ? prefixLength :
            firstPhysicalLengthMm ?? throw new InvalidOperationException("Geef bij vrije opmaak de fysieke lengte van het eerste takdeel op.");
        if (!double.IsFinite(firstLength) || firstLength <= 0 || firstLength >= branch.PhysicalLengthMm)
            throw new InvalidOperationException("De eerste maat moet groter zijn dan nul en kleiner dan de oorspronkelijke taklengte.");
        if (sourceView.Mode == LayoutMode.Scaled && Math.Abs(sourceLength - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
            throw new InvalidOperationException("De route op schaal moet eerst overeenkomen met de fysieke maat.");
        var physicalFraction = firstLength / branch.PhysicalLengthMm;
        var canonical = SplitPolyline(Geometry.Flatten(branch),
            sourceView.Mode == LayoutMode.Scaled ? physicalFraction : displayFraction);
        var first = new RouteBranch { Name = branch.Name + " · 1", Points = canonical.Left, PhysicalLengthMm = firstLength, LengthLocked = branch.LengthLocked };
        var second = new RouteBranch { Name = branch.Name + " · 2", Points = canonical.Right, PhysicalLengthMm = branch.PhysicalLengthMm - firstLength, LengthLocked = branch.LengthLocked };
        if (branch.Geometry is { } branchGeometry)
        {
            var split = Geometry.SplitAtLengthFraction(branchGeometry, sourceView.Mode == LayoutMode.Scaled ? physicalFraction : displayFraction);
            first.Geometry = split.Left; first.Points = split.Left.Knots.Select(k => k.Position).ToList(); first.Smooth = split.Left.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
            second.Geometry = split.Right; second.Points = split.Right.Knots.Select(k => k.Position).ToList(); second.Smooth = split.Right.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
        }
        var commands = new List<IEditCommand> { ReplaceWithPair(project.Assembly.Branches, branch, first, second, "Fysieke tak splitsen") };
        foreach (var wire in project.Assembly.Conductors.Where(w => w.BranchIds.Contains(branch.Id)))
        {
            var before = wire.BranchIds.ToList();
            var reverse = TraversesBackward(project, sourceView, wire, branch.Id);
            var after = before.SelectMany(id => id == branch.Id ? (reverse ? new[] { second.Id, first.Id } : new[] { first.Id, second.Id }) : new[] { id }).ToList();
            commands.Add(new DelegateCommand("Aderroute behouden", () => wire.BranchIds = [.. after], () => wire.BranchIds = [.. before]));
        }
        foreach (var view in project.Sheets.SelectMany(s => s.Views))
            foreach (var route in view.Routes.Where(r => r.BranchId == branch.Id).ToArray())
            {
                var flattened = Geometry.Flatten(route);
                if (view.Mode == LayoutMode.Scaled && Math.Abs(Geometry.Length(flattened) - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
                    throw new InvalidOperationException("Een ander aanzicht op schaal wijkt af van de fysieke maat. Herstel dat aanzicht vóór het splitsen.");
                var split = route == sourceRoute ? (Left: leftSource, Right: rightSource) :
                    SplitPolyline(flattened, view.Mode == LayoutMode.Scaled ? physicalFraction : displayFraction);
                var firstRoute = new PlacedRoute { BranchId = first.Id, Points = split.Left, LayerId = route.LayerId };
                var secondRoute = new PlacedRoute { BranchId = second.Id, Points = split.Right, LayerId = route.LayerId };
                if (route.Geometry is { } routeGeometry)
                {
                    var curveSplit = route == sourceRoute ? Geometry.SplitAtKnot(routeGeometry, vertexIndex) :
                        Geometry.SplitAtLengthFraction(routeGeometry, view.Mode == LayoutMode.Scaled ? physicalFraction : displayFraction);
                    firstRoute.Geometry = curveSplit.Left; firstRoute.Points = curveSplit.Left.Knots.Select(k => k.Position).ToList(); firstRoute.Smooth = curveSplit.Left.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
                    secondRoute.Geometry = curveSplit.Right; secondRoute.Points = curveSplit.Right.Knots.Select(k => k.Position).ToList(); secondRoute.Smooth = curveSplit.Right.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic);
                }
                commands.Add(ReplaceWithPair(view.Routes, route, firstRoute, secondRoute, "Takweergave splitsen"));
            }
        foreach (var view in project.Sheets.SelectMany(s => s.Views))
            foreach (var presentation in view.WirePresentations)
            {
                var original = presentation.Routes.Find(r => r.BranchId == branch.Id);
                if (original is null) continue;
                var split = Geometry.SplitAtLengthFraction(original.Geometry, displayFraction);
                commands.Add(ReplaceWithPair(presentation.Routes, original,
                    new WireRouteGeometry { BranchId = first.Id, Geometry = split.Left },
                    new WireRouteGeometry { BranchId = second.Id, Geometry = split.Right }, "Afzonderlijke adervorm splitsen"));
            }
        return new CompositeCommand("Fysieke routetak splitsen", commands.ToArray());
    }

    private static bool TraversesBackward(HarnessProject project, DrawingView view, Conductor wire, string targetBranchId)
    {
        Point2? previous = Geometry.EndpointPosition(project, view, wire.From);
        foreach (var id in wire.BranchIds)
        {
            var points = view.Routes.Find(r => r.BranchId == id)?.Points ?? project.Assembly.Branches.Find(b => b.Id == id)?.Points;
            if (points is null || points.Count < 2) continue;
            var backwards = previous is { } point && point.DistanceTo(points[^1]) < point.DistanceTo(points[0]);
            if (id == targetBranchId) return backwards;
            previous = backwards ? points[0] : points[^1];
        }
        return false;
    }

    private static (List<Point2> Left, List<Point2> Right) SplitPolyline(IReadOnlyList<Point2> points, double fraction)
    {
        var target = Geometry.Length(points) * fraction;
        if (target <= 0 || points.Count < 2) throw new InvalidOperationException("De route bevat geen bruikbare splitsgeometrie.");
        var left = new List<Point2> { points[0] };
        double traversed = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var segment = points[i - 1].DistanceTo(points[i]);
            if (segment > 0 && traversed + segment >= target)
            {
                var cut = points[i - 1] + (points[i] - points[i - 1]) * ((target - traversed) / segment);
                if (left[^1].DistanceTo(cut) > 1e-9) left.Add(cut);
                var right = new List<Point2> { cut };
                right.AddRange(points.Skip(i).Where((p, index) => index != 0 || p.DistanceTo(cut) > 1e-9));
                if (left.Count < 2 || right.Count < 2) throw new InvalidOperationException("De splitsing moet twee niet-lege takdelen opleveren.");
                return (left, right);
            }
            left.Add(points[i]); traversed += segment;
        }
        throw new InvalidOperationException("Het splitspunt ligt buiten de route.");
    }

    private static IEditCommand ReplaceWithPair<T>(List<T> list, T original, T first, T second, string name)
    {
        var index = -1;
        return new DelegateCommand(name,
            () =>
            {
                index = list.IndexOf(original);
                if (index < 0) throw new InvalidOperationException("De te splitsen tak bestaat niet meer.");
                list.RemoveAt(index); list.Insert(index, second); list.Insert(index, first);
            },
            () => { list.Remove(first); list.Remove(second); list.Insert(index, original); });
    }
}
