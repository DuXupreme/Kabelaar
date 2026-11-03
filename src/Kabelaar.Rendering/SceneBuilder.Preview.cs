using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    /// <summary>Projects one detached annotation for a drag preview without touching project data or scene caches.</summary>
    public static IReadOnlyList<ScenePrimitive> AnnotationPreview(HarnessProject project, Sheet sheet,
        DrawingView view, Annotation annotation)
    {
        var layer = sheet.Layers.Find(l => l.Id == annotation.LayerId);
        if (layer is { Visible: false }) return [];
        return AnnotationPrimitives(project, view, annotation, view.Scale * sheet.PrintScale)
            .Select(p => p with { ViewId = view.Id, LayerId = annotation.LayerId,
                PlacementId = annotation.Id, Locked = annotation.Locked || (layer?.Locked ?? false) }).ToArray();
    }

    /// <summary>Reprojects only one cached wire's label, including its changing leader, during label dragging.</summary>
    public static IReadOnlyList<ScenePrimitive> WireLabelPreview(HarnessProject project, Sheet sheet, DrawingView view,
        Conductor wire, WirePresentation presentation)
    {
        var committed = WireProjectionCache.GetOrCreateValue(view);
        WireProjection? prior;
        lock (committed) committed.TryGetValue(wire.Id, out prior);
        if (prior is null) return [];
        var scale = view.Scale * sheet.PrintScale;
        var appearance = prior.Appearance with
        {
            ShowLabel = (presentation.ShowLabel ?? view.Style.ShowWireLabels) && presentation.LabelText != "",
            LabelText = presentation.LabelText, LabelRotation = presentation.LabelRotationDegrees,
            LabelPosition = presentation.LabelPosition is { } position ? view.Origin + position * scale : prior.Appearance.LabelPosition,
            LabelAnchor = prior.Appearance.DefaultLabelAnchor
        };
        var local = new Dictionary<string, WireProjection> { [wire.Id] = prior };
        return ProjectWire(local, wire.Id, prior.Source, appearance);
    }

    /// <summary>
    /// Builds only the changing wires and their labels from temporary view-mm geometry.
    /// It never changes project data or the committed scene cache. The caller removes these wire
    /// IDs, and the affected twist-group label IDs, from its background scene once per gesture.
    /// </summary>
    public static IReadOnlyList<ScenePrimitive> WirePreview(HarnessProject project, Sheet sheet, DrawingView view,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> wireModelPoints, bool individualGeometry = true,
        IReadOnlySet<string>? individualWireIds = null,
        IReadOnlyDictionary<string, (bool FromFree, bool ToFree)>? endpointOverrides = null)
    {
        var result = new List<ScenePrimitive>();
        var scale = view.Scale * sheet.PrintScale;
        var style = view.Style;
        var lookups = Lookups(project);
        var routePlacements = FirstById(view.Routes, r => r.BranchId);
        var presentations = FirstById(view.WirePresentations, p => p.ConductorId);
        var localCache = new Dictionary<string, WireProjection>();
        var committedCache = WireProjectionCache.GetOrCreateValue(view);
        var affectedTwists = new Dictionary<string, TwistGroup>();
        var twistBaselines = new Dictionary<string, IReadOnlyList<Point2>[]>();
        bool Individual(string id) => individualWireIds?.Contains(id) ??
            (individualGeometry || view.Mode == LayoutMode.Free &&
                presentations.TryGetValue(id, out var p) && p.Routes.Count > 0);
        Dictionary<string, IReadOnlyList<Point2>>? bridges = null;
        if (wireModelPoints.Keys.Any(id => presentations.GetValueOrDefault(id)?.ShowCrossingBridges == true))
        {
            bridges = new();
            var crossingIndex = new CrossingIndex();
            var ranks = view.DrawOrder.Select((id, rank) => (id, rank)).DistinctBy(p => p.id).ToDictionary(p => p.id, p => p.rank + 1);
            foreach (var candidate in project.Assembly.Conductors.OrderBy(w => ranks.GetValueOrDefault(w.Id)))
            {
                if (lookups.Twists.ByWire.ContainsKey(candidate.Id)) continue;
                var route = candidate.BranchIds.Select(routePlacements.GetValueOrDefault).FirstOrDefault(r => r is not null);
                if (sheet.Layers.Find(l => l.Id == (route?.LayerId ?? "main")) is { Visible: false }) continue;
                var changed = wireModelPoints.TryGetValue(candidate.Id, out var candidatePoints);
                candidatePoints ??= ConductorPoints(project, view, candidate);
                var individual = changed ? Individual(candidate.Id) : view.Mode == LayoutMode.Free &&
                    presentations.GetValueOrDefault(candidate.Id)?.Routes.Count > 0;
                if (!individual && lookups.Wires.Offsets.TryGetValue(candidate.Id, out var candidateOffset))
                    candidatePoints = Offset(candidatePoints, candidateOffset);
                var candidatePresentation = presentations.GetValueOrDefault(candidate.Id);
                if (changed && candidatePresentation?.ShowCrossingBridges == true)
                    bridges[candidate.Id] = crossingIndex.Decorate(candidatePoints, candidatePresentation.CrossingRadiusMm);
                crossingIndex.Add(candidatePoints);
            }
        }
        foreach (var (wireId, source) in wireModelPoints)
        {
            if (source.Count < 2) continue;
            var wire = lookups.Wires.Items.ById.GetValueOrDefault(wireId);
            if (wire is null) continue;
            var placement = wire.BranchIds.Select(routePlacements.GetValueOrDefault)
                .FirstOrDefault(r => r is not null);
            var layerId = placement?.LayerId ?? "main";
            var layer = sheet.Layers.Find(l => l.Id == layerId);
            if (layer is { Visible: false }) continue;
            var twist = lookups.Twists.ByWire.GetValueOrDefault(wireId);
            IReadOnlyList<Point2> points = source;
            if (twist is not null)
            {
                affectedTwists.TryAdd(twist.Id, twist);
                var index = twist.ConductorIds.IndexOf(wireId);
                if (!twistBaselines.TryGetValue(twist.Id, out var baseline))
                {
                    baseline = twist.ConductorIds.Select(id =>
                        wireModelPoints.TryGetValue(id, out var changed) && !Individual(id) ? changed :
                        lookups.Wires.Items.ById.GetValueOrDefault(id) is { } member
                            ? ConductorPoints(project, view, member, false) : []).ToArray();
                    twistBaselines.Add(twist.Id, baseline);
                }
                var paths = Individual(wireId) ? Enumerable.Repeat(source, twist.ConductorIds.Count).ToArray() : baseline;
                if (paths.All(p => p.Count >= 2))
                    points = TwistTogether(paths, index,
                    style.TwistDisplayPitchMm > 0 ? style.TwistDisplayPitchMm : twist.PitchMm,
                    style.TwistAmplitudeMm, style.TwistDisplayMode);
            }
            else if (!Individual(wireId))
            {
                if (lookups.Wires.Offsets.TryGetValue(wireId, out var offset)) points = Offset(points, offset);
            }
            var anchor = view.Origin + Midpoint(points) * scale;
            var midpointAnchor = anchor;
            var position = anchor + new Point2(0, -3 * scale);
            lock (committedCache)
            {
                if (committedCache.TryGetValue(wireId, out var prior))
                {
                    // Retain the label's existing local offset while dragging; the normal collision
                    // layout runs once when the edit is committed.
                    var movement = (Midpoint(points) - Midpoint(prior.Source)) * scale;
                    position = prior.Appearance.LabelPosition + movement;
                    anchor = prior.Appearance.LabelAnchor + movement;
                    localCache[wireId] = prior;
                }
            }
            var presentation = presentations.GetValueOrDefault(wireId);
            if (presentation?.LabelPosition is { } fixedLabel)
            {
                position = view.Origin + fixedLabel * scale;
                anchor = midpointAnchor;
            }
            var freeEndpoints = endpointOverrides is not null && endpointOverrides.TryGetValue(wireId, out var overridden)
                ? overridden : (FromFree: wire.From.Kind == EndpointKind.Free, ToFree: wire.To.Kind == EndpointKind.Free);
            var appearance = new WireAppearance(view.Id, view.Origin, scale, layerId, placement?.Id, layer?.Locked ?? false,
                wire.Name, wire.ColorCode, wire.Color, wire.CrossSectionMm2, twist is not null,
                freeEndpoints.FromFree, freeEndpoints.ToFree,
                WireLineWidth(style, wire, presentation), (presentation?.ShowLabel ?? style.ShowWireLabels) && presentation?.LabelText != "",
                style.WireLabelFontSizeMm, position, anchor, presentation?.LabelText, presentation?.LabelRotationDegrees ?? 0, midpointAnchor);
            result.AddRange(ProjectWire(localCache, wireId, bridges?.GetValueOrDefault(wireId) ?? points, appearance));
        }
        if (style.ShowTwistLabels)
            foreach (var twist in affectedTwists.Values)
            {
                var members = twist.ConductorIds.Select(id => lookups.Wires.Items.ById.GetValueOrDefault(id))
                    .OfType<Conductor>().ToArray();
                var midpoints = members.Select(w => wireModelPoints.TryGetValue(w.Id, out var path)
                    ? path : ConductorPoints(project, view, w)).Where(p => p.Count > 0).Select(Midpoint).ToArray();
                if (midpoints.Length == 0) continue;
                var centre = new Point2(midpoints.Average(p => p.X), midpoints.Average(p => p.Y));
                var layerId = members.SelectMany(w => w.BranchIds).Select(routePlacements.GetValueOrDefault)
                    .FirstOrDefault(r => r is not null)?.LayerId ?? "main";
                var layer = sheet.Layers.Find(l => l.Id == layerId);
                if (layer is { Visible: false }) continue;
                result.Add(new SceneText(twist.Id, "twist-label",
                    $"{twist.Name} · {twist.ConductorIds.Count} aders · spoed {Number(twist.PitchMm)} mm",
                    view.Origin + centre * scale + new Point2(0, -6 * scale), 2.5 * scale, Muted)
                { ViewId = view.Id, LayerId = layerId, Locked = layer?.Locked ?? false });
            }
        return result;
    }
}
