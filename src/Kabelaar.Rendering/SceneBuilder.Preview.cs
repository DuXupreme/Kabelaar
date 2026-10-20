using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
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
            var freeEndpoints = endpointOverrides is not null && endpointOverrides.TryGetValue(wireId, out var overridden)
                ? overridden : (FromFree: wire.From.Kind == EndpointKind.Free, ToFree: wire.To.Kind == EndpointKind.Free);
            var appearance = new WireAppearance(view.Id, view.Origin, scale, layerId, placement?.Id, layer?.Locked ?? false,
                wire.Name, wire.ColorCode, wire.Color, wire.CrossSectionMm2, twist is not null,
                freeEndpoints.FromFree, freeEndpoints.ToFree,
                WireLineWidth(style, wire, presentation), style.ShowWireLabels, style.WireLabelFontSizeMm, position, anchor);
            result.AddRange(ProjectWire(localCache, wireId, points, appearance));
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
