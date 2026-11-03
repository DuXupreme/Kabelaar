using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

/// <summary>Builds the same sheet-space scene for the editor and every output format.</summary>
public static partial class SceneBuilder
{
    private const string Ink = "#243244";
    private const string Muted = "#6C7A86";
    private const string Accent = "#137F8B";
    private sealed class LegacyRouteGeometry
    {
        public Point2[] Original = [];
        public bool Smooth;
        public IReadOnlyList<Point2> Flattened = [];
    }
    private static readonly ConditionalWeakTable<PlacedRoute, LegacyRouteGeometry> RouteGeometryCache = new();
    private static IReadOnlyList<Point2> FlattenRoute(PlacedRoute route, RouteBranch? branch)
    {
        if ((route.Geometry ?? (route.Points.Count == 0 ? branch?.Geometry : null)) is { } geometry)
            return FlattenExplicitRoute(geometry);
        var points = route.Points.Count > 0 ? route.Points : branch?.Points ?? [];
        var smooth = route.Smooth || (branch?.Smooth ?? false);
        var cached = RouteGeometryCache.GetOrCreateValue(route);
        lock (cached)
        {
            if (cached.Smooth != smooth || cached.Original.Length != points.Count || !cached.Original.SequenceEqual(points))
            {
                cached.Original = points.ToArray();
                cached.Smooth = smooth;
                cached.Flattened = Geometry.Flatten(cached.Original, smooth);
            }
            return cached.Flattened;
        }
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static Dictionary<string, T> FirstById<T>(IEnumerable<T> values, Func<T, string> id)
    {
        var result = new Dictionary<string, T>();
        foreach (var value in values) result.TryAdd(id(value), value);
        return result;
    }
    private static PlacedRoute? ConductorPlacement(Conductor wire, IReadOnlyDictionary<string, PlacedRoute> routes)
    {
        foreach (var branchId in wire.BranchIds)
            if (routes.TryGetValue(branchId, out var route)) return route;
        return null;
    }

    public static DrawingScene Build(HarnessProject project, Sheet sheet, bool draft = false, bool forPrint = false,
        Action<string, double>? timing = null)
    {
        var stamp = timing is null ? 0 : Stopwatch.GetTimestamp();
        void Checkpoint(string name)
        {
            if (timing is null) return;
            timing(name, Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
            stamp = Stopwatch.GetTimestamp();
        }
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sheet);
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) || sheet.WidthMm <= 0 || sheet.HeightMm <= 0 ||
            !double.IsFinite(sheet.PrintScale) || sheet.PrintScale <= 0)
            throw new ArgumentException("Bladformaat en afdrukschaal moeten eindig en positief zijn.", nameof(sheet));
        var primitives = new List<ScenePrimitive>(SheetIndexCache.GetOrCreateValue(sheet).PrimitiveCount);
        var layers = FirstById(sheet.Layers, l => l.Id);
        var lookups = Lookups(project);
        var parts = lookups.Parts.ById;
        var branches = lookups.Branches.ById;
        var connectors = lookups.Connectors.ById;
        var visualParts = new Dictionary<string, PartVisual>();
        foreach (var connector in connectors.Values)
            if (!visualParts.ContainsKey(connector.PartId) && parts.TryGetValue(connector.PartId, out var part))
                visualParts.Add(connector.PartId, VisualPart(part));
        var splices = lookups.Splices.ById;
        var references = FirstById(project.Assembly.ReferencePoints, r => r.Id);
        var conductors = lookups.Wires.Items.ById;
        var wiresByBranch = lookups.Wires.ByBranch;
        var wireOffsets = lookups.Wires.Offsets;
        var materialsByBranch = lookups.Materials.ByBranch;
        var cablesById = lookups.Cables.ById;
        var twistByWire = lookups.Twists.ByWire;
        bool Visible(string layer) => !layers.TryGetValue(layer, out var definition) || (definition.Visible && (!forPrint || definition.Print));
        bool Locked(string layer) => layers.TryGetValue(layer, out var definition) && definition.Locked;
        AddFrame(project, sheet, primitives);
        if (draft)
            primitives.Add(new SceneText(sheet.Id, "draft-watermark", "CONCEPT — NIET VOOR PRODUCTIE",
                new(sheet.WidthMm * .18, sheet.HeightMm * .54), Math.Min(sheet.WidthMm / 35, 14), "#DEE3E6", true, -22) { Locked = true });
        Checkpoint("lookups-frame");
        foreach (var view in sheet.Views)
        {
            var viewStart = primitives.Count;
            var scale = view.Scale * sheet.PrintScale;
            var style = view.Style;
            if (!double.IsFinite(scale) || scale <= 0 || !Geometry.IsFinite(view.Origin))
                throw new ArgumentException("Aanzicht heeft een ongeldige oorsprong of schaal.", nameof(sheet));
            Point2 SheetPoint(Point2 point) => view.Origin + point * scale;
            void Add(ScenePrimitive primitive, string layer, string? placementId = null)
            {
                if (!Visible(layer)) return;
                primitives.Add(primitive with { ViewId = view.Id, LayerId = layer, Locked = primitive.Locked || Locked(layer), PlacementId = placementId });
            }
            var routePlacements = FirstById(view.Routes, r => r.BranchId);
            var wirePresentations = view.WirePresentations.Count == 0
                ? null : FirstById(view.WirePresentations, p => p.ConductorId);
            var flattenedRoutes = routePlacements.ToDictionary(pair => pair.Key, pair =>
            {
                var branch = branches.GetValueOrDefault(pair.Key); var route = pair.Value;
                return FlattenRoute(route, branch);
            });
            Checkpoint("flatten-routes");
            var endpointPositions = new Dictionary<(string Entity, string? Cavity), Point2>();
            var projectedComponents = new List<ComponentProjection>(view.Components.Count);
            foreach (var placed in view.Components)
            {
                var connector = connectors.GetValueOrDefault(placed.EntityId);
                var splice = splices.GetValueOrDefault(placed.EntityId);
                var reference = references.GetValueOrDefault(placed.EntityId);
                if (connector is null && splice is null && reference is null) continue;
                var appearance = new ComponentAppearance(view.Id, view.Origin, scale, placed.Id, placed.EntityId,
                    placed.LayerId, Locked(placed.LayerId), placed.Position, placed.RotationDegrees, placed.LabelOffset,
                    placed.DisplayScale, placed.ShowPins, placed.ShowPinNumbers, placed.ShowLabel, placed.ShowPartNumber,
                    placed.LabelFontSizeMm, style.ConnectorLineWidthMm, placed.MirrorX, placed.MirrorY, reference?.Symbol ?? NodeSymbol.General);
                var projection = ProjectComponent(placed, appearance, connector, splice,
                    connector is null ? null : visualParts.GetValueOrDefault(connector.PartId), reference);
                projectedComponents.Add(projection);
                foreach (var endpoint in projection.Endpoints) endpointPositions.TryAdd(endpoint.Key, endpoint.Position);
            }
            Point2? EndpointPoint(Endpoint endpoint) => endpoint.Kind != EndpointKind.Free && endpoint.EntityId is { } entity
                && endpointPositions.TryGetValue((entity, endpoint.Kind == EndpointKind.Cavity ? endpoint.CavityId : null), out var point) ? point : null;
            Checkpoint("endpoints");
            var wirePaths = new Dictionary<string, IReadOnlyList<Point2>>(conductors.Count);
            Dictionary<string, IReadOnlyList<Point2>>? baselineWirePaths = null;
            foreach (var wire in conductors.Values)
            {
                var start = EndpointPoint(wire.From); var end = EndpointPoint(wire.To);
                var baseline = ResolveConductorPoints(wire, start, end, flattenedRoutes);
                if (view.Mode == LayoutMode.Free && wirePresentations is not null &&
                    wirePresentations.TryGetValue(wire.Id, out var presentation) &&
                    HasRouteOverride(wire, presentation, routePlacements))
                {
                    (baselineWirePaths ??= new()).Add(wire.Id, baseline);
                    wirePaths.Add(wire.Id, ResolveConductorPoints(wire, start, end,
                        OverrideRoutes(flattenedRoutes, presentation)));
                }
                else wirePaths.Add(wire.Id, baseline);
            }
            var twistedPaths = new Dictionary<string, IReadOnlyList<Point2>>();
            foreach (var twist in project.Assembly.TwistGroups)
            {
                var paths = twist.ConductorIds.Where(wirePaths.ContainsKey)
                    .Select(id => baselineWirePaths?.GetValueOrDefault(id) ?? wirePaths[id]).Where(p => p.Count >= 2).ToArray();
                if (paths.Length != twist.ConductorIds.Count || paths.Length < 2) continue;
                for (var i = 0; i < twist.ConductorIds.Count; i++)
                    twistedPaths[twist.ConductorIds[i]] = TwistTogether(
                        baselineWirePaths?.ContainsKey(twist.ConductorIds[i]) == true
                            ? Enumerable.Repeat(wirePaths[twist.ConductorIds[i]], paths.Length).ToArray() : paths, i,
                        style.TwistDisplayPitchMm > 0 ? style.TwistDisplayPitchMm : twist.PitchMm,
                        style.TwistAmplitudeMm, style.TwistDisplayMode);
            }
            Checkpoint("resolve-wire-paths");

            // Jacket/sleeve outlines sit behind the individual conductors.
            foreach (var route in view.Routes.Where(r => Visible(r.LayerId)))
            {
                if (cablesById.Count == 0 && project.Assembly.Materials.Count == 0 && wiresByBranch.Contains(route.BranchId)) continue;
                var wires = wiresByBranch[route.BranchId].ToArray();
                var cables = wires.Where(w => w.CableId is not null).Select(w => cablesById.GetValueOrDefault(w.CableId!)).OfType<CableInstance>().DistinctBy(c => c.Id).ToArray();
                var materials = materialsByBranch[route.BranchId].Where(m => parts.GetValueOrDefault(m.PartId)?.Kind == PartKind.Sleeve).ToArray();
                if (wires.Length > 0 && cables.Length == 0 && materials.Length == 0) continue;
                var points = flattenedRoutes[route.BranchId].Select(SheetPoint).ToArray();
                if (points.Length < 2) continue;
                if (wires.Length == 0)
                    Add(new ScenePath(route.BranchId, "route", points, Muted, .35 * scale, Dashed: true), route.LayerId, route.Id);
                foreach (var cable in cables)
                {
                    Add(new ScenePath(cable.Id, "cable-jacket", points, "#AEB6BC", Math.Max(3, wires.Length * .7) * scale), route.LayerId, route.Id);
                    Add(new ScenePath(cable.Id, "cable-jacket", points, "#F0F2F4", Math.Max(2.4, wires.Length * .7 - .6) * scale), route.LayerId, route.Id);
                    Add(new SceneText(cable.Id, "cable-label", cable.Name, Midpoint(points) + new Point2(0, -3), 2.5), route.LayerId, route.Id);
                }
                foreach (var material in materials)
                {
                    var part = parts.GetValueOrDefault(material.PartId);
                    if (part?.Kind != PartKind.Sleeve) continue;
                    Add(new ScenePath(material.Id, "sleeve", points, "#748B8C", 4.5 * scale, Dashed: true), route.LayerId, route.Id);
                    Add(new SceneText(material.Id, "sleeve-label", $"{part.Name} · {part.PartNumber}", Midpoint(points) + new Point2(0, 6), 2.5), route.LayerId, route.Id);
                }
            }
            Checkpoint("jackets");
            var twistLabels = new List<(SceneText Text, string Layer)>();
            if (style.ShowTwistLabels)
                foreach (var twist in project.Assembly.TwistGroups)
                {
                    var wire = twist.ConductorIds.Select(id => conductors.GetValueOrDefault(id)).FirstOrDefault(w => w is not null);
                    if (wire is null || !wirePaths.TryGetValue(wire.Id, out var path) || path.Count == 0) continue;
                    var midpoints = twist.ConductorIds.Where(id => wirePaths.TryGetValue(id, out var p) && p.Count > 0)
                        .Select(id => Midpoint(wirePaths[id])).ToArray();
                    var centre = new Point2(midpoints.Average(p => p.X), midpoints.Average(p => p.Y));
                    var layer = ConductorPlacement(wire, routePlacements)?.LayerId ?? "main";
                    if (Visible(layer))
                        twistLabels.Add((new SceneText(twist.Id, "twist-label",
                            $"{twist.Name} · {twist.ConductorIds.Count} aders · spoed {Number(twist.PitchMm)} mm",
                            SheetPoint(centre) + new Point2(0, -6 * scale), 2.5 * scale, Muted), layer));
                }
            var annotationPrimitives = view.Annotations.Where(a => Visible(a.LayerId))
                .Select(a => (Annotation: a, Primitives: AnnotationPrimitives(project, view, a, scale).ToArray())).ToArray();
            WireLabelLayout? labels = null;
            if (style.ShowWireLabels || view.WirePresentations.Any(p => p.ShowLabel == true))
            {
                labels = new(new(sheet.MarginMm, sheet.MarginMm,
                    sheet.WidthMm - sheet.MarginMm * 2, sheet.HeightMm - sheet.MarginMm * 2));
                foreach (var text in primitives.OfType<SceneText>().Where(t => t.Category != "draft-watermark"))
                    labels.Reserve(text.Bounds);
                foreach (var (text, _) in twistLabels) labels.Reserve(text.Bounds);
                foreach (var component in projectedComponents.Where(c => Visible(c.Appearance.Layer)))
                {
                    SceneBounds? body = null;
                    foreach (var primitive in component.Primitives)
                    {
                        if (primitive.Category is "connector-symbol" or "splice" or "reference-point")
                            body = body is { } prior ? prior.Union(primitive.Bounds) : primitive.Bounds;
                        else if (primitive is SceneText) labels.Reserve(primitive.Bounds);
                    }
                    if (body is { } bounds) labels.Reserve(bounds);
                }
                foreach (var primitive in annotationPrimitives.SelectMany(a => a.Primitives).OfType<SceneText>())
                    labels.Reserve(primitive.Bounds);
            }
            var wireCache = WireProjectionCache.GetOrCreateValue(view);
            var order = view.DrawOrder.Select((id, rank) => (id, rank)).DistinctBy(v => v.id)
                .ToDictionary(v => v.id, v => v.rank + 1);
            var orderedWires = order.Count == 0 ? project.Assembly.Conductors.AsEnumerable() :
                project.Assembly.Conductors.OrderBy(w => order.GetValueOrDefault(w.Id));
            CrossingIndex? crossingIndex = view.WirePresentations.Any(p => p.ShowCrossingBridges) ? new() : null;
            lock (wireCache)
            {
                foreach (var stale in wireCache.Keys.Where(id => !conductors.ContainsKey(id)).ToArray()) wireCache.Remove(stale);
                foreach (var wire in orderedWires)
                {
                    var points = wirePaths[wire.Id];
                    if (points.Count < 2) continue;
                    var placement = ConductorPlacement(wire, routePlacements);
                    var layer = placement?.LayerId ?? "main";
                    if (!Visible(layer)) continue;
                    var twist = twistByWire.GetValueOrDefault(wire.Id);
                    if (twistedPaths.TryGetValue(wire.Id, out var twisted))
                        points = twisted;
                    else if (baselineWirePaths?.ContainsKey(wire.Id) != true && wireOffsets.TryGetValue(wire.Id, out var offset))
                        points = Offset(points, offset);
                    var presentation = wirePresentations?.GetValueOrDefault(wire.Id);
                    var showLabel = (presentation?.ShowLabel ?? style.ShowWireLabels) && presentation?.LabelText != "";
                    var labelText = showLabel ? presentation?.LabelText ?? WireLabel(wireCache, wire.Id, wire.Name, wire.ColorCode, wire.CrossSectionMm2) : "";
                    var defaultLabelAnchor = showLabel ? SheetPoint(WireLabelLayout.Centre(points)) : new Point2();
                    var label = showLabel ? presentation?.LabelPosition is { } labelPosition
                        ? (SheetPoint(labelPosition), defaultLabelAnchor)
                        : labels?.Place(points, view.Origin, scale, labelText, style.WireLabelFontSizeMm * scale) ?? (new Point2(), new Point2())
                        : (new Point2(), new Point2());
                    if (showLabel && presentation?.LabelPosition is not null)
                        labels?.Reserve(SceneBounds.TextBounds(labelText, label.Item1, style.WireLabelFontSizeMm * scale,
                            presentation.LabelRotationDegrees, null));
                    var lineWidth = WireLineWidth(style, wire, presentation);
                    var appearance = new WireAppearance(view.Id, view.Origin, scale, layer, placement?.Id, Locked(layer),
                        wire.Name, wire.ColorCode, wire.Color, wire.CrossSectionMm2, twist is not null,
                        wire.From.Kind == EndpointKind.Free, wire.To.Kind == EndpointKind.Free,
                        lineWidth, showLabel, style.WireLabelFontSizeMm, label.Item1, label.Item2,
                        presentation?.LabelText, presentation?.LabelRotationDegrees ?? 0, defaultLabelAnchor);
                    var displayed = points;
                    if (twist is null && crossingIndex is not null)
                    {
                        if (presentation?.ShowCrossingBridges == true)
                            displayed = crossingIndex.Decorate(points, presentation.CrossingRadiusMm);
                        crossingIndex.Add(points);
                    }
                    primitives.AddRange(ProjectWire(wireCache, wire.Id, displayed, appearance));
                }
            }
            foreach (var (text, layer) in twistLabels) Add(text, layer);
            Checkpoint("wires");
            foreach (var projection in projectedComponents)
                if (Visible(projection.Appearance.Layer)) primitives.AddRange(projection.Primitives);
            foreach (var (annotation, built) in annotationPrimitives)
            {
                foreach (var primitive in built)
                    Add(primitive with { Locked = annotation.Locked }, annotation.LayerId, annotation.Id);
            }
            Checkpoint("components-annotations");
            if (sheet.Views.Count > 1)
                Add(new SceneText(view.Id, "view-label", $"{view.Name}   {(view.Mode == LayoutMode.Free ? "VRIJE OPMAAK" : ScaleLabel(scale))}",
                    view.Origin + new Point2(0, -5), 3, Accent, true), "notes");
            if (order.Count > 0)
            {
                var sorted = primitives.Skip(viewStart).OrderBy(p => order.GetValueOrDefault(p.EntityId)).ToArray();
                primitives.RemoveRange(viewStart, primitives.Count - viewStart);
                primitives.AddRange(sorted);
            }
        }
        if (draft)
        {
            primitives.Add(new SceneText(sheet.Id, "draft-status", "CONCEPT", new(sheet.MarginMm + 2, sheet.HeightMm - sheet.MarginMm - 2), 3.5, "#B05B28", true) { Locked = true });
        }
        var scene = IndexedScene(sheet, draft, primitives);
        Checkpoint("index");
        return scene;
    }

    /// <summary>Returns display geometry in view mm, resolving real endpoints without altering domain data.</summary>
    public static IReadOnlyList<Point2> ConductorPoints(HarnessProject project, DrawingView view, Conductor wire,
        bool useIndividualPresentation = true)
    {
        var start = Geometry.EndpointPosition(project, view, wire.From);
        var end = Geometry.EndpointPosition(project, view, wire.To);
        var routes = view.Routes.GroupBy(r => r.BranchId).ToDictionary(g => g.Key, g =>
        {
            var route = g.First(); var branch = project.Assembly.Branches.Find(b => b.Id == g.Key);
            return FlattenRoute(route, branch);
        });
        var presentation = useIndividualPresentation && view.Mode == LayoutMode.Free
            ? view.WirePresentations.Find(p => p.ConductorId == wire.Id) : null;
        return ResolveConductorPoints(wire, start, end, presentation is null ? routes : OverrideRoutes(routes, presentation));
    }

    private static IReadOnlyList<Point2> ResolveConductorPoints(Conductor wire, Point2? start, Point2? end, IReadOnlyDictionary<string, IReadOnlyList<Point2>> routes)
    {
        if (wire.BranchIds.Count == 1 && routes.TryGetValue(wire.BranchIds[0], out var direct) && direct.Count >= 2 &&
            (start is null || start.Value == direct[0]) &&
            (end is null || end.Value == direct[^1]))
            return direct;
        var result = new List<Point2>();
        if (start is { } p) result.Add(p);
        foreach (var branchId in wire.BranchIds)
        {
            if (!routes.TryGetValue(branchId, out var route)) continue;
            var points = route.ToArray();
            if (points.Length == 0) continue;
            var reference = result.Count > 0 ? result[^1] : start;
            if (reference is { } previous && previous.DistanceTo(points[^1]) < previous.DistanceTo(points[0])) Array.Reverse(points);
            foreach (var point in points)
                if (result.Count == 0 || result[^1].DistanceTo(point) > 1e-8) result.Add(point);
        }
        // A conductor is shown only in views containing its route or both endpoints.
        if (result.Count <= 1 && (start is null || end is null)) return [];
        if (end is { } q)
        {
            if (result.Count == 0 || result[^1].DistanceTo(q) > 1e-8) result.Add(q);
            else result[^1] = q;
        }
        return result;
    }

    private static IReadOnlyList<Point2> Offset(IReadOnlyList<Point2> points, double distance)
    {
        if (points.Count < 2 || Math.Abs(distance) < 1e-9) return points;
        var result = new List<Point2>(points.Count + 2) { points[0] };
        for (var i = 0; i < points.Count; i++)
        {
            var tangent = points[Math.Min(i + 1, points.Count - 1)] - points[Math.Max(0, i - 1)];
            var length = tangent.Length;
            var normal = length < 1e-8 ? new Point2() : new Point2(-tangent.Y / length, tangent.X / length);
            result.Add(points[i] + normal * distance);
        }
        result.Add(points[^1]);
        return result;
    }

    private static IReadOnlyList<Point2> TwistTogether(IReadOnlyList<Point2>[] paths, int strand, double pitch,
        double amplitude, TwistDisplayMode mode)
    {
        // Electrical From/To direction is independent of the direction of a drawn bundle.
        // Align paths before averaging, then return each strand in its original direction.
        var first = paths[0];
        var reversed = paths.Select(path =>
            first[0].DistanceTo(path[^1]) + first[^1].DistanceTo(path[0]) <
            first[0].DistanceTo(path[0]) + first[^1].DistanceTo(path[^1])).ToArray();
        paths = paths.Select((path, i) => reversed[i] ? (IReadOnlyList<Point2>)path.Reverse().ToArray() : path).ToArray();
        var lengths = paths.Select(p => Geometry.Length(p)).ToArray();
        var length = lengths.Average();
        pitch = Math.Max(2, pitch);
        var samples = (int)Math.Clamp(Math.Ceiling(length / Math.Min(1, pitch / 12)), 2, 10000);
        var sampled = paths.Select((path, index) => Resample(path, lengths[index], samples)).ToArray();
        var centre = Enumerable.Range(0, samples + 1).Select(i =>
            new Point2(sampled.Average(path => path[i].X), sampled.Average(path => path[i].Y))).ToArray();
        var result = new List<Point2>(samples + 1);
        var markerCentres = length < 80 ? new[] { length * .5 } : new[] { length / 3, length * 2 / 3 };
        var markerHalfWidth = Math.Min(pitch * 1.4, length * .15);
        for (var i = 0; i <= samples; i++)
        {
            var along = length * i / samples;
            var lead = mode == TwistDisplayMode.Full ? 15 : Math.Max(1, Math.Min(8, length / 4));
            var fade = Math.Clamp(Math.Min(along, length - along) / lead, 0, 1);
            var delta = centre[Math.Min(samples, i + 1)] - centre[Math.Max(0, i - 1)];
            var normal = delta.Length < 1e-9 ? new Point2() : new Point2(-delta.Y / delta.Length, delta.X / delta.Length);
            var wave = Math.Sin(along / pitch * Math.PI * 2 + strand * Math.PI * 2 / paths.Length);
            var parallel = strand - (paths.Length - 1) / 2.0;
            double offset;
            if (mode == TwistDisplayMode.Parallel) offset = parallel;
            else if (mode == TwistDisplayMode.Markers)
            {
                // A short twist indication leaves most of a long harness route calm and parallel.
                var distance = markerCentres.Min(c => Math.Abs(c - along));
                var blend = markerHalfWidth <= 0 ? 0 : Math.Clamp((markerHalfWidth - distance) / Math.Max(.1, pitch * .4), 0, 1);
                blend = blend * blend * (3 - 2 * blend);
                offset = parallel * (1 - blend) + wave * blend;
            }
            else offset = wave;
            result.Add(sampled[strand][i] * (1 - fade) + centre[i] * fade + normal * (amplitude * offset * fade));
        }
        result[0] = paths[strand][0];
        result[^1] = paths[strand][^1];
        if (reversed[strand]) result.Reverse();
        return result;
    }

    private static Point2[] Resample(IReadOnlyList<Point2> path, double length, int count)
    {
        var result = new Point2[count + 1];
        var segment = 1; double startDistance = 0;
        for (var i = 0; i <= count; i++)
        {
            var distance = length * i / count;
            while (segment < path.Count - 1 && startDistance + path[segment - 1].DistanceTo(path[segment]) < distance)
            { startDistance += path[segment - 1].DistanceTo(path[segment]); segment++; }
            var delta = path[segment] - path[segment - 1];
            result[i] = delta.Length < 1e-9 ? path[segment] : path[segment - 1] + delta * Math.Clamp((distance - startDistance) / delta.Length, 0, 1);
        }
        result[0] = path[0]; result[^1] = path[^1];
        return result;
    }

    private static Point2 Midpoint(IReadOnlyList<Point2> points)
    {
        if (points.Count == 0) return new();
        var half = Geometry.Length(points) / 2;
        for (var i = 1; i < points.Count; i++)
        {
            var length = points[i - 1].DistanceTo(points[i]);
            if (length > half && length > 1e-9) return points[i - 1] + (points[i] - points[i - 1]) * (half / length);
            half -= length;
        }
        return points[^1];
    }

    private static string ScaleLabel(double scale) => scale >= 1 ? $"{Number(scale)}:1" : $"1:{Number(1 / Math.Max(.0001, scale))}";

    private static void AddFrame(HarnessProject project, Sheet sheet, List<ScenePrimitive> items)
    {
        var m = sheet.MarginMm; var right = sheet.WidthMm - m; var bottom = sheet.HeightMm - m;
        void Path(Point2[] p, double width = .25) => items.Add(new ScenePath(sheet.Id, "sheet-frame", p, Ink, width) { Locked = true });
        void Text(string value, Point2 p, double height, bool bold = false) => items.Add(new SceneText(sheet.Id, "title-block", value, p, height, Ink, bold) { Locked = true });
        Path([new(m, m), new(right, m), new(right, bottom), new(m, bottom), new(m, m)], .5);
        AddZones(sheet, items);
        if (sheet.TitleBlock.Enabled)
        {
            AddExtendedTitleBlock(project, sheet, items);
            return;
        }
        var titleWidth = Math.Min(190, right - m); var left = right - titleWidth; var top = bottom - 29;
        Path([new(left, bottom), new(left, top), new(right, top)]);
        Path([new(left, top + 12), new(right, top + 12)]);
        Path([new(left, top + 21), new(right, top + 21)]);
        Path([new(right - 28, top + 12), new(right - 28, bottom)]);
        Text(project.Title, new(left + 3, top + 5), 3.5, true);
        Text(project.Customer, new(left + 3, top + 9), 2.4);
        Text(project.DrawingNumber, new(left + 3, top + 18), 4, true);
        Text($"REV {project.Revision}", new(right - 25, top + 18), 3, true);
        Text($"{project.Author}  ·  {sheet.Name}  ·  {project.Sheets.IndexOf(sheet) + 1}/{project.Sheets.Count}", new(left + 3, bottom - 3), 2.3);
        Text(sheet.Views.Any(v => v.Mode == LayoutMode.Free) ? "VRIJ" : ScaleLabel(sheet.PrintScale), new(right - 25, bottom - 3), 2.5);
        var asset = project.Assets.Find(a => a.Id == sheet.LogoAssetId);
        if (asset is { Data.Length: > 0 })
            items.Add(new SceneImage(asset.Id, "logo", asset.Data, new(left + titleWidth - 35, top + 1, 32, 10)) { Locked = true });
        if (!string.IsNullOrWhiteSpace(project.Notes))
            Text(project.Notes, new(m + 3, bottom - 34), 2.5);
    }

    private static IEnumerable<ScenePrimitive> AnnotationPrimitives(HarnessProject project, DrawingView view, Annotation annotation, double scale)
    {
        Point2 P(Point2 p) => view.Origin + p * scale;
        var points = annotation.Points.Select(P).ToArray();
        if (points.Length == 0) points = [view.Origin];
        var a = points[0]; var h = annotation.FontSizeMm * scale; var width = annotation.LineWidthMm * scale;
        switch (annotation.Kind)
        {
            case AnnotationKind.Text:
                var lines = annotation.Text.Replace("\r", "").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var position = a + Geometry.Transform(new(0, i * h * 1.35), annotation.RotationDegrees, new());
                    var text = new SceneText(annotation.Id, "annotation-text", lines[i], position, h, annotation.Color,
                        RotationDegrees: annotation.RotationDegrees);
                    if (annotation.TextFrame) yield return TextFrame(annotation, text, scale);
                    yield return text;
                }
                break;
            case AnnotationKind.Line:
                yield return new ScenePath(annotation.Id, "annotation-line", points, annotation.Color, width);
                break;
            case AnnotationKind.Leader:
                yield return new ScenePath(annotation.Id, "leader", points, annotation.Color, width);
                if (points.Length > 1)
                    yield return Arrow(annotation.Id, "leader", points[0], points[1], annotation.Color, annotation.ArrowSizeMm * scale, width);
                var leaderText = new SceneText(annotation.Id, "leader-text", annotation.Text, points[^1] + new Point2(scale, -scale),
                    h, annotation.Color, RotationDegrees: annotation.RotationDegrees);
                if (annotation.TextFrame) yield return TextFrame(annotation, leaderText, scale);
                yield return leaderText;
                break;
            case AnnotationKind.Arc:
                // Three points are centre, radius/start and sweep-end, in that order.
                if (points.Length >= 3)
                {
                    var start = Math.Atan2(points[1].Y - a.Y, points[1].X - a.X) * 180 / Math.PI;
                    var end = Math.Atan2(points[2].Y - a.Y, points[2].X - a.X) * 180 / Math.PI;
                    yield return new SceneArc(annotation.Id, "arc", a, a.DistanceTo(points[1]), start, (end - start + 360) % 360, annotation.Color, width);
                }
                break;
            case AnnotationKind.Dimension:
                if (points.Length >= 2)
                    foreach (var primitive in Dimension(project, view, annotation, points[0], points[1], scale)) yield return primitive;
                break;
            case AnnotationKind.Image:
                var asset = project.Assets.Find(asset => asset.Id == annotation.AssetId);
                if (asset is { Data.Length: > 0 })
                    yield return new SceneImage(annotation.Id, "image", asset.Data, new(a.X, a.Y, annotation.WidthMm * scale, annotation.HeightMm * scale), annotation.RotationDegrees);
                else
                {
                    yield return new ScenePath(annotation.Id, "missing-image", [a, a + new Point2(annotation.WidthMm, 0), a + new Point2(annotation.WidthMm, annotation.HeightMm), a + new Point2(0, annotation.HeightMm)], "#B55435", .3, Closed: true);
                    yield return new SceneText(annotation.Id, "missing-image", "Afbeelding ontbreekt", a + new Point2(2, 5), h, "#B55435");
                }
                break;
            case AnnotationKind.WireTable:
            case AnnotationKind.PinTable:
            case AnnotationKind.BomTable:
            case AnnotationKind.RevisionTable:
            case AnnotationKind.FreeTable:
                foreach (var primitive in Table(project, annotation, a, h, scale))
                    yield return RotateTablePrimitive(primitive, a, annotation.RotationDegrees);
                break;
            case AnnotationKind.GraphicalNode:
                var radius = annotation.WidthMm * scale / 2;
                if (annotation.NodeSymbol == NodeSymbol.Ground)
                {
                    foreach (var path in GroundPaths(annotation.Id, "graphical-node",
                        p => Geometry.Transform(p * scale, annotation.RotationDegrees, a), annotation.WidthMm / 2, annotation.Color, width)) yield return path;
                }
                else
                    yield return new SceneEllipse(annotation.Id, "graphical-node", a, radius, radius, annotation.Color,
                        width, annotation.FillColor);
                if (!string.IsNullOrEmpty(annotation.Text))
                {
                    var nodeLabel = new SceneText(annotation.Id, "graphical-node-label", annotation.Text,
                        a + new Point2(radius + scale, 0), h, annotation.Color, RotationDegrees: annotation.RotationDegrees);
                    if (annotation.TextFrame) yield return TextFrame(annotation, nodeLabel, scale);
                    yield return nodeLabel;
                }
                break;
        }
    }

    private static IEnumerable<ScenePrimitive> Dimension(HarnessProject project, DrawingView view, Annotation annotation, Point2 a, Point2 b, double scale)
    {
        var offset = annotation.OffsetMm * scale; var d = b - a; var length = d.Length;
        var normal = length < 1e-9 ? new Point2(0, 1) : new Point2(-d.Y / length, d.X / length);
        Point2 da; Point2 db; double measurement;
        switch (annotation.DimensionDirection)
        {
            case DimensionDirection.Horizontal:
                da = new(a.X, Math.Max(a.Y, b.Y) + offset); db = new(b.X, da.Y); measurement = Math.Abs(d.X / scale); break;
            case DimensionDirection.Vertical:
                da = new(Math.Max(a.X, b.X) + offset, a.Y); db = new(da.X, b.Y); measurement = Math.Abs(d.Y / scale); break;
            default:
                da = a + normal * offset; db = b + normal * offset; measurement = length / scale; break;
        }
        if (annotation.EntityId is { } id && project.Assembly.Branches.Find(branch => branch.Id == id) is { } branch)
            measurement = branch.PhysicalLengthMm;
        measurement = annotation.DimensionOverrideMm ?? measurement;
        var value = string.IsNullOrWhiteSpace(annotation.Text) ? Number(measurement) : annotation.Text;
        if (!string.IsNullOrWhiteSpace(annotation.Tolerance)) value += " " + annotation.Tolerance;
        var color = annotation.Color;
        var lineWidth = annotation.LineWidthMm * scale;
        yield return new ScenePath(annotation.Id, "dimension", [a, da, db, b], color, lineWidth);
        yield return Arrow(annotation.Id, "dimension", da, db, color, 2 * scale, lineWidth);
        yield return Arrow(annotation.Id, "dimension", db, da, color, 2 * scale, lineWidth);
        yield return new SceneText(annotation.Id, "dimension-text", value, (da + db) * .5 + new Point2(0, -1.5), annotation.FontSizeMm * scale, color);
    }

    private static ScenePath Arrow(string id, string category, Point2 tip, Point2 towards, string color, double size, double lineWidth = .1)
    {
        var delta = towards - tip; var length = delta.Length;
        var direction = length < 1e-9 ? new Point2(1, 0) : delta * (1 / length);
        var normal = new Point2(-direction.Y, direction.X);
        return new(id, category, [tip, tip + direction * size + normal * (size * .3), tip + direction * size - normal * (size * .3)],
            color, lineWidth, color, true);
    }

    private static IEnumerable<ScenePrimitive> Table(HarnessProject project, Annotation annotation, Point2 origin, double fontSize, double scale)
    {
        var (defaults, rows) = TableData(project, annotation.Kind, annotation.EntityId);
        var columns = annotation.Columns.Count > 0 ? annotation.Columns : defaults;
        if (annotation.Kind == AnnotationKind.FreeTable && columns.Count == 0)
            columns = Enumerable.Range(1, annotation.Rows.Select(r => r.Cells.Count).DefaultIfEmpty(0).Max()).Select(i => $"Kolom {i}").ToList();
        if (columns.Count == 0) yield break;
        var width = Math.Max(1, annotation.WidthMm * scale);
        var lineWidth = annotation.LineWidthMm * scale;
        var color = annotation.Color;
        var widths = TableWidths(annotation, columns).Select(w => w * scale).ToArray();
        width = widths.Sum();
        var padding = annotation.CellPaddingMm * scale;
        var offsets = new double[columns.Count + 1];
        for (var column = 0; column < columns.Count; column++) offsets[column + 1] = offsets[column] + widths[column];
        double totalHeight = 0;
        var count = annotation.Kind == AnnotationKind.FreeTable ? annotation.Rows.Count : rows.Count;
        for (var row = annotation.ShowTableHeader ? -1 : 0; row < count; row++)
        {
            var header = row < 0;
            string Cell(string key, int column) => header
                ? annotation.Kind == AnnotationKind.FreeTable ? key : TableColumnTitle(key)
                : annotation.Kind == AnnotationKind.FreeTable ? annotation.Rows[row].Cells.ElementAtOrDefault(column) ?? ""
                : rows[row].GetValueOrDefault(key, "");
            var cells = columns.Select((key, column) => WrapTableText(Cell(key, column),
                Math.Max(.1, widths[column] - padding * 2), fontSize)).ToArray();
            var minimumHeight = Math.Max(fontSize * 1.35, cells.Max(c => c.Count) * fontSize * 1.35 + padding * 2);
            var rowHeight = Math.Max(annotation.RowHeightMm > 0 ? annotation.RowHeightMm * scale : 5 * scale, minimumHeight);
            var y = origin.Y + totalHeight;
            if (header)
                yield return new ScenePath(annotation.Id, "table-header",
                    [new(origin.X, y), new(origin.X + width, y), new(origin.X + width, y + rowHeight), new(origin.X, y + rowHeight)],
                    color, lineWidth, annotation.TableHeaderColor, true);
            yield return new ScenePath(annotation.Id, "table-grid", [new(origin.X, y), new(origin.X + width, y)], color, lineWidth);
            for (var column = 0; column < columns.Count; column++)
            {
                for (var line = 0; line < cells[column].Count; line++)
                    yield return new SceneText(annotation.Id, "table-cell", cells[column][line],
                        new(origin.X + offsets[column] + padding, y + padding + fontSize * (line * 1.35 + 1)),
                        fontSize, color, header, MaxWidthMm: Math.Max(.1, widths[column] - padding * 2),
                        Alignment: column < annotation.ColumnAlignments.Count ? annotation.ColumnAlignments[column] : annotation.CellAlignment);
            }
            totalHeight += rowHeight;
        }
        yield return new ScenePath(annotation.Id, "table-grid", [new(origin.X, origin.Y + totalHeight), new(origin.X + width, origin.Y + totalHeight)], color, lineWidth);
        for (var column = 0; column <= columns.Count; column++)
        {
            var x = origin.X + offsets[column];
            yield return new ScenePath(annotation.Id, "table-grid", [new(x, origin.Y), new(x, origin.Y + totalHeight)], color, lineWidth);
        }
    }

    private static IReadOnlyList<string> WrapTableText(string text, double width, double height)
    {
        var maxCharacters = Math.Max(2, (int)(width / Math.Max(.1, height * .6)));
        var output = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var remaining = paragraph;
            while (remaining.Length > maxCharacters)
            {
                var split = remaining.LastIndexOf(' ', maxCharacters - 1, maxCharacters);
                if (split < maxCharacters / 3) split = maxCharacters;
                output.Add(remaining[..split]);
                remaining = remaining[split..].TrimStart();
            }
            output.Add(remaining);
        }
        return output;
    }

    public static (IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, string>> Rows) TableData(
        HarnessProject project, AnnotationKind kind, string? connectorId = null)
    {
        Dictionary<string, string> Row(params (string Key, object? Value)[] values) =>
            values.ToDictionary(x => x.Key, x => x.Value is double number ? number.ToString("0.######", CultureInfo.InvariantCulture) : Convert.ToString(x.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.OrdinalIgnoreCase);
        IEnumerable<WireRow> WireRows()
        {
            var rows = HarnessReports.Wires(project);
            if (connectorId is null) return rows;
            var associated = project.Assembly.Conductors
                .Where(w => w.From.EntityId == connectorId || w.To.EntityId == connectorId)
                .ToDictionary(w => w.Id, w => w.From.EntityId != connectorId);
            return rows.Where(w => associated.ContainsKey(w.Id))
                .Select(w => associated[w.Id] ? w with { From = w.To, To = w.From, FromNote = w.ToNote, ToNote = w.FromNote } : w);
        }
        return kind switch
        {
            AnnotationKind.WireTable => (["Name", "From", "To", "Color", "CrossSectionMm2", "CutLengthMm"],
                WireRows().Select(w => Row(("Name", w.Name), ("From", w.From), ("To", w.To), ("PartNumber", w.PartNumber), ("Color", w.Color),
                    ("CrossSectionMm2", w.CrossSectionMm2), ("RouteLengthMm", w.RouteLengthMm), ("AllowanceMm", w.AllowanceMm), ("CutLengthMm", w.CutLengthMm),
                    ("LengthSource", w.LengthSource), ("Signal", w.Signal), ("Cable", w.Cable),
                    ("NetName", w.NetName), ("Shielding", w.Shielding), ("FromNote", w.FromNote), ("ToNote", w.ToNote))).ToArray()),
            AnnotationKind.PinTable => (["Connector", "Pin", "Wires", "Connections", "Terminal", "Seal"],
                HarnessReports.Pins(project).Where(p => connectorId is null || p.ConnectorId == connectorId)
                    .Select(p => Row(("Connector", p.Connector), ("Pin", p.Pin), ("State", p.State), ("Terminal", p.Terminal),
                    ("Seal", p.Seal), ("Plug", p.Plug), ("Wires", p.Wires), ("Connections", p.Connections),
                    ("Function", p.Function), ("Notes", p.Notes), ("Counterpart", p.Counterpart))).ToArray()),
            AnnotationKind.BomTable => (["PartNumber", "Description", "Quantity", "Unit"],
                HarnessReports.Bom(project).Select(b => Row(("PartNumber", b.PartNumber), ("Description", b.Description), ("Kind", b.Kind),
                    ("Quantity", b.Quantity), ("Unit", b.Unit), ("References", b.References))).ToArray()),
            AnnotationKind.RevisionTable => (["Revision", "Description", "Author", "Date"],
                project.Revisions.Select(r => Row(("Revision", r.Revision), ("Description", r.Description), ("Author", r.Author),
                    ("Date", r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))).ToArray()),
            _ => ([], [])
        };
    }

    public static string TableColumnTitle(string key) => key switch
    {
        "Name" => "Draad", "From" => "Van", "To" => "Naar", "PartNumber" => "Artikelnummer", "Color" => "Kleur",
        "CrossSectionMm2" => "mm²", "RouteLengthMm" => "Route mm", "AllowanceMm" => "Toeslag mm", "CutLengthMm" => "Knip mm",
        "LengthSource" => "Lengtebron", "Signal" => "Signaal", "Cable" => "Kabel", "Pin" => "Pin", "State" => "Status", "Terminal" => "Terminal",
        "Seal" => "Afdichting", "Plug" => "Plug", "Wires" => "Draden", "Connections" => "Aansluitingen", "Description" => "Omschrijving",
        "Kind" => "Type", "Quantity" => "Aantal", "Unit" => "Eenheid", "References" => "Referenties", "Revision" => "Revisie",
        "Author" => "Auteur", "Date" => "Datum", "NetName" => "Netnaam", "Shielding" => "Afscherming",
        "FromNote" => "Aansluitnotitie van", "ToNote" => "Aansluitnotitie naar", "Function" => "Pinfunctie",
        "Notes" => "Notities", "Counterpart" => "Tegenonderdeel", _ => key
    };
}
