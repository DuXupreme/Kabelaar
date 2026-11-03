using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    // Only ordinary, already drawn conductors enter this index. Bridge geometry is a
    // display path; physical branches and lengths are never changed by intersections.
    private sealed class CrossingIndex
    {
        private const double CellSize = 25;
        private readonly record struct Segment(Point2 A, Point2 B);
        private readonly List<Segment> segments = [];
        private readonly Dictionary<(int X, int Y), List<int>> cells = [];
        private readonly List<int> large = [];

        private static (int X0, int Y0, int X1, int Y1) Range(Point2 a, Point2 b) => (
            (int)Math.Clamp(Math.Floor(Math.Min(a.X, b.X) / CellSize), -1_000_000, 1_000_000),
            (int)Math.Clamp(Math.Floor(Math.Min(a.Y, b.Y) / CellSize), -1_000_000, 1_000_000),
            (int)Math.Clamp(Math.Floor(Math.Max(a.X, b.X) / CellSize), -1_000_000, 1_000_000),
            (int)Math.Clamp(Math.Floor(Math.Max(a.Y, b.Y) / CellSize), -1_000_000, 1_000_000));

        public void Add(IReadOnlyList<Point2> points)
        {
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i - 1].DistanceTo(points[i]) < 1e-8) continue;
                var index = segments.Count; segments.Add(new(points[i - 1], points[i]));
                var (x0, y0, x1, y1) = Range(points[i - 1], points[i]);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1024) { large.Add(index); continue; }
                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                    {
                        if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = [];
                        list.Add(index);
                    }
            }
        }

        private IEnumerable<Segment> Candidates(Point2 a, Point2 b)
        {
            var (x0, y0, x1, y1) = Range(a, b);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1024) return segments;
            var found = new HashSet<int>(large);
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    if (cells.TryGetValue((x, y), out var list)) found.UnionWith(list);
            return found.Select(i => segments[i]);
        }

        public IReadOnlyList<Point2> Decorate(IReadOnlyList<Point2> points, double radius)
        {
            if (points.Count < 2 || !double.IsFinite(radius) || radius <= 0 || segments.Count == 0) return points;
            var distances = new double[points.Count];
            var crossings = new List<double>();
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var direction = points[i] - a; var length = direction.Length;
                distances[i] = distances[i - 1] + length;
                if (length < 1e-8) continue;
                foreach (var other in Candidates(a, points[i]))
                {
                    var delta = other.B - other.A; var denominator = Cross(direction, delta);
                    if (Math.Abs(denominator) < 1e-9) continue; // Collinear/shared route, never a crossing.
                    var relative = other.A - a;
                    var t = Cross(relative, delta) / denominator; var u = Cross(relative, direction) / denominator;
                    if (t < -1e-8 || t > 1 + 1e-8 || u < -1e-8 || u > 1 + 1e-8) continue;
                    crossings.Add(distances[i - 1] + Math.Clamp(t, 0, 1) * length);
                }
            }
            var total = distances[^1];
            // Endpoint connections (including cavity/splice joins) never receive a bridge.
            var accepted = new List<double>();
            foreach (var distance in crossings.Order())
                if (distance > radius && distance < total - radius &&
                    (accepted.Count == 0 || distance - accepted[^1] > radius * 2 + .01)) accepted.Add(distance);
            if (accepted.Count == 0) return points;
            Point2 At(double distance)
            {
                var next = Array.BinarySearch(distances, distance);
                if (next >= 0) return points[next];
                next = Math.Clamp(~next, 1, points.Count - 1);
                var length = distances[next] - distances[next - 1];
                return length < 1e-8 ? points[next] : points[next - 1] +
                    (points[next] - points[next - 1]) * ((distance - distances[next - 1]) / length);
            }
            var output = new List<Point2>(); var cursor = 0;
            foreach (var distance in accepted)
            {
                while (cursor < points.Count && distances[cursor] < distance - radius) output.Add(points[cursor++]);
                var start = At(distance - radius); var end = At(distance + radius);
                var chord = end - start; var half = chord.Length / 2;
                if (half < 1e-8) continue;
                var normal = new Point2(chord.Y / chord.Length, -chord.X / chord.Length);
                for (var sample = 0; sample <= 12; sample++)
                {
                    var angle = sample * Math.PI / 12;
                    output.Add(start + chord * ((1 - Math.Cos(angle)) / 2) + normal * (Math.Sin(angle) * half));
                }
                while (cursor < points.Count && distances[cursor] <= distance + radius) cursor++;
            }
            while (cursor < points.Count) output.Add(points[cursor++]);
            return output;
        }

        private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    }
}
