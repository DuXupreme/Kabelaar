using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    /// <summary>Places optional detail labels on the body of a route, clear of connector artwork and other text.</summary>
    private sealed class WireLabelLayout(SceneBounds page)
    {
        private const double Cell = 25;
        private sealed record RouteAnchors(Point2[] Points);
        // Build only supplies private flattened/projected route snapshots. These are
        // immutable, like the sources used by WireProjectionCache, and most retain
        // their identity between edits. Cache geometry, never collision decisions.
        private static readonly ConditionalWeakTable<IReadOnlyList<Point2>, RouteAnchors> AnchorCache = new();
        private readonly Dictionary<(int X, int Y), List<SceneBounds>> cells = [];
        private readonly List<SceneBounds> large = [];

        private static int CellAt(double position) => (int)Math.Clamp(Math.Floor(position / Cell), -1_000_000, 1_000_000);
        private static (int X0, int Y0, int X1, int Y1) Range(SceneBounds b) =>
            (CellAt(b.Left), CellAt(b.Top), CellAt(b.Right), CellAt(b.Bottom));
        private static bool Finite(SceneBounds b) => double.IsFinite(b.Left) && double.IsFinite(b.Top) &&
            double.IsFinite(b.Right) && double.IsFinite(b.Bottom);

        public void Reserve(SceneBounds bounds)
        {
            var b = bounds.Inflate(.6);
            if (!Finite(b)) return;
            var (x0, y0, x1, y1) = Range(b);
            if (((long)x1 - x0 + 1) * ((long)y1 - y0 + 1) > 256) { large.Add(b); return; }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    if (!cells.TryGetValue((x, y), out var bucket)) cells[(x, y)] = bucket = [];
                    bucket.Add(b);
                }
        }

        private bool Clear(SceneBounds b)
        {
            if (!Finite(b) || b.Left < page.Left || b.Top < page.Top || b.Right > page.Right || b.Bottom > page.Bottom) return false;
            for (var i = 0; i < large.Count; i++)
                if (b.Intersects(large[i])) return false;
            var (x0, y0, x1, y1) = Range(b);
            if (((long)x1 - x0 + 1) * ((long)y1 - y0 + 1) > 256) return false;
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    if (cells.TryGetValue((x, y), out var bucket))
                        for (var i = 0; i < bucket.Count; i++)
                            if (b.Intersects(bucket[i])) return false;
            return true;
        }

        public (Point2 Position, Point2 Anchor) Place(IReadOnlyList<Point2> path, Point2 origin, double scale, string text, double height)
        {
            var anchors = AnchorCache.GetValue(path, CreateAnchors).Points;
            var width = Math.Max(height, text.Length * height);
            var centre = anchors[0] * scale + origin;
            (Point2 Position, Point2 Anchor) fallback = (new(
                Math.Clamp(centre.X, page.Left, Math.Max(page.Left, page.Right - width)),
                Math.Clamp(centre.Y - height, page.Top + height * 1.15, Math.Max(page.Top + height * 1.15, page.Bottom - height * .4))), centre);
            Span<double> offsets = stackalloc double[] { -height, height * 2.1, -height * 3, height * 4 };
            foreach (var point in anchors)
            {
                var anchor = point * scale + origin;
                foreach (var dy in offsets)
                {
                    var position = new Point2(anchor.X, anchor.Y + dy);
                    var bounds = SceneBounds.TextBounds(text, position, height, 0, null);
                    if (!Clear(bounds)) continue;
                    Reserve(bounds);
                    return (position, anchor);
                }
            }
            Reserve(SceneBounds.TextBounds(text, fallback.Position, height, 0, null));
            return fallback;
        }

        private static RouteAnchors CreateAnchors(IReadOnlyList<Point2> points)
        {
            var length = Geometry.Length(points);
            if (length <= 1e-9) return new([points[^1], points[^1], points[^1], points[^1], points[^1]]);
            var result = new Point2[5];
            ReadOnlySpan<double> fractions = [.2, .35, .5, .65, .8];
            ReadOnlySpan<int> destination = [3, 1, 0, 2, 4];
            var segment = 1;
            var traversed = 0d;
            var delta = points[segment] - points[segment - 1];
            var segmentLength = delta.Length;
            for (var i = 0; i < fractions.Length; i++)
            {
                var distance = length * fractions[i];
                while (segment < points.Count - 1 && traversed + segmentLength < distance)
                {
                    traversed += segmentLength;
                    segment++;
                    delta = points[segment] - points[segment - 1];
                    segmentLength = delta.Length;
                }
                result[destination[i]] = segmentLength <= 1e-9 ? points[segment] :
                    points[segment - 1] + delta * Math.Clamp((distance - traversed) / segmentLength, 0, 1);
            }
            return new(result);
        }
    }
}
