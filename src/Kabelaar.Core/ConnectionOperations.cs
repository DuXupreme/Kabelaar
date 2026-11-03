namespace Kabelaar.Core;

public sealed record ConnectionProposal(IEditCommand Command, HarnessProject PreviewProject, IReadOnlyList<string> Summary);

/// <summary>Connectivity changes are prepared independently of graphical crossings and require explicit acceptance.</summary>
public static class ConnectionOperations
{
    public static ConnectionProposal PrepareConnect(HarnessProject project, DrawingView view, Conductor wire, bool from, Endpoint target)
    {
        if (!project.Assembly.Conductors.Contains(wire) || !project.Sheets.SelectMany(s => s.Views).Contains(view))
            throw new ArgumentException("De ader en het aanzicht moeten bij dit project horen.");
        if (target.Kind == EndpointKind.Free) throw new InvalidOperationException("Kies een fysieke aansluiting voor dit verbindingsvoorstel.");
        var after = GraphEdit.Clone(project); var nextWire = after.Assembly.Conductors.Single(w => w.Id == wire.Id);
        var end = GraphEdit.Clone(target);
        HarnessOperations.SetConductorEndpoints(after, nextWire, from ? end : nextWire.From, from ? nextWire.To : end).Execute();
        GraphEdit.RequireValid(after);
        RejectNewConnectionErrors(project, after, new[] { nextWire.Id, end.EntityId, end.CavityId }.OfType<string>());
        var beforeEnd = from ? wire.From : wire.To;
        var summary = new List<string>
        {
            $"{wire.Name}: {(from ? "begin" : "einde")} van {Describe(project, beforeEnd)} naar {Describe(after, end)}.",
            "Alleen dit expliciet gekozen draadeinde wordt aangesloten; grafische kruisingen blijven onverbonden."
        };
        var difference = HarnessReports.CutLength(after, nextWire) - HarnessReports.CutLength(project, wire);
        if (Math.Abs(difference) > Geometry.LengthToleranceMm) summary.Add($"De gemeten kniplengte verandert met {difference:0.###} mm door het aangepaste aansluitsegment.");
        return new(GraphEdit.Command("Verbindingsvoorstel toepassen", project, after), after, summary);
    }

    public static ConnectionProposal PrepareJunction(HarnessProject project, DrawingView view, Conductor wire, PlacedRoute route,
        double fraction, string spliceName, Conductor? incomingWire = null, bool incomingFrom = false, double? firstPhysicalLengthMm = null)
    {
        if (!project.Assembly.Conductors.Contains(wire) || !view.Routes.Contains(route) || !wire.BranchIds.Contains(route.BranchId) ||
            !project.Sheets.SelectMany(s => s.Views).Contains(view) || !double.IsFinite(fraction) || fraction <= 0 || fraction >= 1)
            throw new ArgumentException("Kies een binnenpunt op een routetak van de geselecteerde ader.");
        if (incomingWire is not null && (!project.Assembly.Conductors.Contains(incomingWire) || incomingWire == wire))
            throw new ArgumentException("De aftakkende ader moet een andere bestaande ader zijn.");
        if (string.IsNullOrWhiteSpace(spliceName) || project.Assembly.Splices.Any(s => s.Name.Equals(spliceName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Geef een unieke splicenaam.");
        if (wire.CableId is not null || project.Assembly.TwistGroups.Any(g => g.ConductorIds.Contains(wire.Id)))
            throw new InvalidOperationException("Deze ader hoort bij een kabel of twistgroep. Verdeel die groepering eerst expliciet voordat je de fysieke ader splitst.");
        if (project.Assembly.Materials.Concat(project.Assembly.Connectors.SelectMany(c => c.Accessories)).Any(m => m.BranchId == route.BranchId) ||
            project.Sheets.SelectMany(s => s.Views).SelectMany(v => v.Annotations).Any(a => a.EntityId == route.BranchId || a.EntityId == wire.Id))
            throw new InvalidOperationException("Deze ader of tak heeft gekoppelde maatvoering of materiaal. Verdeel of ontkoppel die eerst expliciet.");
        foreach (var sheet in project.Sheets)
            foreach (var placement in sheet.Views.SelectMany(v => v.Routes).Where(r => r.BranchId == route.BranchId))
                if (sheet.Layers.Find(l => l.Id == placement.LayerId)?.Locked == true)
                    throw new InvalidOperationException("Een betrokken routelaag is vergrendeld.");
        var after = GraphEdit.Clone(project); var next = GraphEdit.View(after, view.Id);
        var branch = after.Assembly.Branches.Single(b => b.Id == route.BranchId);
        var sourceGeometry = Geometry.EditableRouteGeometry(view, route, wire.Id);
        if (view.Mode == LayoutMode.Scaled && Math.Abs(Geometry.Length(sourceGeometry) - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
            throw new InvalidOperationException("De route op schaal moet eerst met de fysieke taklengte overeenkomen.");
        var split = Geometry.SplitAtLengthFraction(sourceGeometry, fraction);
        var firstLength = view.Mode == LayoutMode.Scaled ? Geometry.Length(split.Left) : firstPhysicalLengthMm;
        if (firstLength.HasValue && (!double.IsFinite(firstLength.Value) || firstLength <= 0 || firstLength >= branch.PhysicalLengthMm))
            throw new ArgumentException("De eerste fysieke taklengte moet groter dan nul en kleiner dan de oorspronkelijke taklengte zijn.");
        var physicalFraction = firstLength.HasValue && branch.PhysicalLengthMm > 0 ? firstLength.Value / branch.PhysicalLengthMm : fraction;
        var canonical = Geometry.SplitAtLengthFraction(branch.Geometry ?? Geometry.FromLegacy(branch.Points, branch.Smooth),
            view.Mode == LayoutMode.Scaled ? physicalFraction : fraction);
        var left = NewBranch(branch, canonical.Left, firstLength ?? 0, "1");
        var right = NewBranch(branch, canonical.Right, firstLength.HasValue ? branch.PhysicalLengthMm - firstLength.Value : 0, "2");
        var splice = new Splice { Name = spliceName, Notes = "Nieuw aftakpunt; kies spliceartikel en bevestig de nieuwe aansluittoeslagen." };
        after.Assembly.Splices.Add(splice); after.Assembly.Branches.Add(left); after.Assembly.Branches.Add(right);
        var targetWire = after.Assembly.Conductors.Single(w => w.Id == wire.Id);
        var branchIndex = targetWire.BranchIds.IndexOf(branch.Id);
        var reverse = TraversesBackward(project, view, wire, route.BranchId);
        var firstBranch = reverse ? right : left; var secondBranch = reverse ? left : right;
        var firstWire = GraphEdit.Clone(targetWire); var secondWire = GraphEdit.Clone(targetWire);
        firstWire.Name = SelectionOperations.UniqueName(wire.Name + "_1", after.Assembly.Conductors.Select(w => w.Name));
        secondWire.Id = Ids.New(); secondWire.Name = SelectionOperations.UniqueName(wire.Name + "_2", after.Assembly.Conductors.Select(w => w.Name).Append(firstWire.Name));
        firstWire.To = new() { Kind = EndpointKind.Splice, EntityId = splice.Id, Description = "", Notes = "Nieuwe aansluiting; toeslag bevestigen" };
        secondWire.From = GraphEdit.Clone(firstWire.To);
        firstWire.BranchIds = targetWire.BranchIds.Take(branchIndex).Append(firstBranch.Id).ToList();
        secondWire.BranchIds = new[] { secondBranch.Id }.Concat(targetWire.BranchIds.Skip(branchIndex + 1)).ToList();
        firstWire.EndAllowanceMm = 0; secondWire.StartAllowanceMm = 0;
        firstWire.ProcessingAllowanceMm = secondWire.ProcessingAllowanceMm = 0;
        firstWire.ManualCutLengthMm = secondWire.ManualCutLengthMm = 0;
        firstWire.LengthNeedsConfirmation = secondWire.LengthNeedsConfirmation = true;
        var index = after.Assembly.Conductors.IndexOf(targetWire);
        after.Assembly.Conductors.RemoveAt(index); after.Assembly.Conductors.InsertRange(index, [firstWire, secondWire]);
        var shared = after.Assembly.Conductors.Any(w => w.Id != firstWire.Id && w.Id != secondWire.Id && w.BranchIds.Contains(branch.Id));
        foreach (var destinationView in after.Sheets.SelectMany(s => s.Views))
        {
            var originalPlacement = destinationView.Routes.Find(r => r.BranchId == branch.Id);
            if (originalPlacement is null) continue;
            var g = Geometry.EditableRouteGeometry(destinationView, originalPlacement, wire.Id);
            var parts = Geometry.SplitAtLengthFraction(g, destinationView.Id == view.Id ? fraction :
                destinationView.Mode == LayoutMode.Scaled ? physicalFraction : fraction);
            var firstPlacement = NewRoute(originalPlacement, left.Id, parts.Left);
            var secondPlacement = NewRoute(originalPlacement, right.Id, parts.Right);
            destinationView.Routes.Add(firstPlacement); destinationView.Routes.Add(secondPlacement);
            destinationView.Components.Add(new() { EntityId = splice.Id, Position = parts.Left.Knots[^1].Position, LayerId = originalPlacement.LayerId, LabelFontSizeMm = 2.5 });
            var presentation = destinationView.WirePresentations.Find(p => p.ConductorId == wire.Id);
            if (presentation is not null)
            {
                var secondPresentation = presentation.Copy(); secondPresentation.ConductorId = secondWire.Id;
                presentation.Routes.RemoveAll(r => !firstWire.BranchIds.Contains(r.BranchId));
                secondPresentation.Routes.RemoveAll(r => !secondWire.BranchIds.Contains(r.BranchId));
                // New displayed branch geometry already contains the original individual curve.
                presentation.LabelText = null; secondPresentation.LabelText = null;
                secondPresentation.LabelPosition = null;
                destinationView.WirePresentations.Add(secondPresentation);
            }
            if (!shared) destinationView.Routes.Remove(originalPlacement);
            destinationView.DrawOrder.Add(secondWire.Id);
        }
        if (!shared) after.Assembly.Branches.Remove(branch);
        if (incomingWire is not null)
        {
            var incoming = after.Assembly.Conductors.Single(w => w.Id == incomingWire.Id);
            var endpoint = new Endpoint { Kind = EndpointKind.Splice, EntityId = splice.Id, Description = "" };
            HarnessOperations.SetConductorEndpoints(after, incoming, incomingFrom ? endpoint : incoming.From, incomingFrom ? incoming.To : endpoint).Execute();
        }
        GraphEdit.RequireValid(after); RejectNewConnectionErrors(project, after, new[] { firstWire.Id, secondWire.Id, incomingWire?.Id }.OfType<string>());
        var summary = new List<string>
        {
            $"Fysieke ader {wire.Name} wordt {firstWire.Name} en {secondWire.Name}, verbonden via nieuwe splice {spliceName}.",
            "Nieuwe splice en twee fysieke aderdelen; geen uitsluitend grafische knoop.",
            "Beide kniplengtes/aansluittoeslagen vereisen bevestiging. Een bestaande handmatige kniplengte is niet naar beide delen gekopieerd.",
            firstLength.HasValue ? $"Fysieke takverdeling: {left.PhysicalLengthMm:0.###} + {right.PhysicalLengthMm:0.###} mm." : "Fysieke takverdeling onbekend: beide nieuwe taklengtes moeten worden ingevuld."
        };
        if (incomingWire is not null)
        {
            summary.Add($"{incomingWire.Name} wordt met het gekozen uiteinde aangesloten op {spliceName}.");
            var incomingAfter = after.Assembly.Conductors.Single(w => w.Id == incomingWire.Id);
            var difference = HarnessReports.CutLength(after, incomingAfter) - HarnessReports.CutLength(project, incomingWire);
            if (Math.Abs(difference) > Geometry.LengthToleranceMm)
                summary.Add($"De gemeten kniplengte van {incomingWire.Name} verandert met {difference:0.###} mm door het nieuwe aansluitsegment.");
        }
        if (shared) summary.Add("De bestaande gedeelde tak en overige aders blijven behouden.");
        return new(GraphEdit.Command("Aftakkingsvoorstel toepassen", project, after), after, summary);
    }

    private static RouteBranch NewBranch(RouteBranch original, RouteGeometry geometry, double length, string suffix) => new()
    { Name = original.Name + " · " + suffix, Geometry = geometry, Points = geometry.Knots.Select(k => k.Position).ToList(),
        Smooth = geometry.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic), PhysicalLengthMm = length, LengthLocked = original.LengthLocked };
    private static PlacedRoute NewRoute(PlacedRoute original, string id, RouteGeometry geometry) => new()
    { BranchId = id, Geometry = geometry, Points = geometry.Knots.Select(k => k.Position).ToList(),
        Smooth = geometry.Segments.Any(s => s.Kind == RouteSegmentKind.Cubic), LayerId = original.LayerId };
    private static bool TraversesBackward(HarnessProject project, DrawingView view, Conductor wire, string branchId)
    {
        Point2? previous = Geometry.EndpointPosition(project, view, wire.From);
        foreach (var id in wire.BranchIds)
        {
            var route = view.Routes.Find(r => r.BranchId == id);
            if (route is null) continue;
            var points = Geometry.Flatten(view, route, wire.Id);
            var reverse = previous is { } p && p.DistanceTo(points[^1]) < p.DistanceTo(points[0]);
            if (id == branchId) return reverse;
            previous = reverse ? points[0] : points[^1];
        }
        return false;
    }
    private static void RejectNewConnectionErrors(HarnessProject before, HarnessProject after, IEnumerable<string> affectedIds)
    {
        var relevant = new HashSet<string> { "cavity.overfull", "cavity.plugOccupied", "wire.sameEndpoint", "referencePoint.overfull" };
        var previous = ProductionValidator.Validate(before).Where(i => relevant.Contains(i.Code)).Select(i => (i.Code, i.EntityId)).ToHashSet();
        var affected = affectedIds.ToHashSet();
        var added = ProductionValidator.Validate(after).Where(i => relevant.Contains(i.Code) &&
            (affected.Contains(i.EntityId) || !previous.Contains((i.Code, i.EntityId)))).ToArray();
        if (added.Length > 0) throw new InvalidOperationException(string.Join("\n", added.Select(i => i.Message)));
    }
    private static string Describe(HarnessProject p, Endpoint e) => e.Kind switch
    {
        EndpointKind.Cavity => p.Assembly.Connectors.Find(c => c.Id == e.EntityId)?.Name + ":" + p.Assembly.Connectors.Find(c => c.Id == e.EntityId)?.Cavities.Find(c => c.Id == e.CavityId)?.Label,
        EndpointKind.Splice => p.Assembly.Splices.Find(s => s.Id == e.EntityId)?.Name ?? "? splice",
        EndpointKind.ReferencePoint => p.Assembly.ReferencePoints.Find(r => r.Id == e.EntityId)?.Name ?? "? referentiepunt",
        _ => string.IsNullOrWhiteSpace(e.Description) ? "vrij uiteinde" : e.Description
    };
}
