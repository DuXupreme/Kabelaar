using System.Collections;
using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    private sealed class ExplicitRouteProjection
    {
        public RouteGeometry? Snapshot;
        public IReadOnlyList<Point2> Flattened = [];
    }

    private static readonly ConditionalWeakTable<RouteGeometry, ExplicitRouteProjection> ExplicitRouteCache = new();

    private static IReadOnlyList<Point2> FlattenExplicitRoute(RouteGeometry geometry)
    {
        var cached = ExplicitRouteCache.GetOrCreateValue(geometry);
        lock (cached)
        {
            if (cached.Snapshot is null || !SameShape(cached.Snapshot, geometry))
            {
                cached.Snapshot = geometry.Copy();
                cached.Flattened = Geometry.Flatten(geometry);
            }
            return cached.Flattened;
        }
    }

    private static bool SameShape(RouteGeometry left, RouteGeometry right)
    {
        if (left.Knots.Count != right.Knots.Count || left.Segments.Count != right.Segments.Count) return false;
        for (var i = 0; i < left.Knots.Count; i++)
        {
            var a = left.Knots[i]; var b = right.Knots[i];
            if (a.Id != b.Id || a.Position != b.Position || a.InHandle != b.InHandle || a.OutHandle != b.OutHandle)
                return false;
        }
        for (var i = 0; i < left.Segments.Count; i++)
        {
            var a = left.Segments[i]; var b = right.Segments[i];
            if (a.StartKnotId != b.StartKnotId || a.EndKnotId != b.EndKnotId || a.Kind != b.Kind) return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Point2>> OverrideRoutes(
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> baseline, WirePresentation presentation) =>
        presentation.Routes.Count == 0 ? baseline : new PresentedRoutes(baseline, presentation);

    // A wire normally owns only one or two overrides. Do not copy the whole view's route dictionary
    // once per edited wire: the performance fixture contains a thousand independent physical routes.
    private sealed class PresentedRoutes(
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> baseline, WirePresentation presentation)
        : IReadOnlyDictionary<string, IReadOnlyList<Point2>>
    {
        public int Count => baseline.Count;
        public IEnumerable<string> Keys => baseline.Keys;
        public IEnumerable<IReadOnlyList<Point2>> Values => Keys.Select(key => this[key]);
        public IReadOnlyList<Point2> this[string key] => TryGetValue(key, out var points) ? points : throw new KeyNotFoundException(key);
        public bool ContainsKey(string key) => baseline.ContainsKey(key);
        public bool TryGetValue(string key, out IReadOnlyList<Point2> value)
        {
            if (!baseline.TryGetValue(key, out value!)) return false;
            var route = presentation.Routes.Find(r => r.BranchId == key);
            if (route is not null) value = FlattenExplicitRoute(route.Geometry);
            return true;
        }
        public IEnumerator<KeyValuePair<string, IReadOnlyList<Point2>>> GetEnumerator() =>
            Keys.Select(key => new KeyValuePair<string, IReadOnlyList<Point2>>(key, this[key])).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static double WireLineWidth(DrawingStyle style, Conductor wire, WirePresentation? presentation) =>
        presentation?.LineWidthMm ?? style.WireLineWidthMm * (style.UseWireSectionWidth
            ? Math.Clamp(Math.Sqrt(Math.Max(0, wire.CrossSectionMm2) / .5), 1, 2) : 1);
}
