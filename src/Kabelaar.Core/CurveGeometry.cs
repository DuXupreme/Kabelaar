namespace Kabelaar.Core;

public static partial class Geometry
{
    public static RouteGeometry? EffectiveRouteGeometry(DrawingView view, PlacedRoute route, string? conductorId = null) =>
        (conductorId is null ? null : view.WirePresentations.Find(w => w.ConductorId == conductorId)?.Routes
            .Find(r => r.BranchId == route.BranchId)?.Geometry) ?? route.Geometry;

    public static RouteGeometry EditableRouteGeometry(DrawingView view, PlacedRoute route, string? conductorId = null) =>
        EffectiveRouteGeometry(view, route, conductorId)?.Copy() ?? FromLegacy(route.Points, route.Smooth);

    public static IReadOnlyList<Point2> Flatten(DrawingView view, PlacedRoute route, string? conductorId = null) =>
        EffectiveRouteGeometry(view, route, conductorId) is { } geometry ? Flatten(geometry) : Flatten(route.Points, route.Smooth);
    public static IReadOnlyList<Point2> Flatten(PlacedRoute route) =>
        route.Geometry is { } geometry ? Flatten(geometry) : Flatten(route.Points, route.Smooth);
    public static IReadOnlyList<Point2> Flatten(RouteBranch branch) =>
        branch.Geometry is { } geometry ? Flatten(geometry) : Flatten(branch.Points, branch.Smooth);
    public static double Length(PlacedRoute route) =>
        route.Geometry is { } geometry ? Length(geometry) : Length(route.Points, route.Smooth);
    public static double Length(RouteBranch branch) =>
        branch.Geometry is { } geometry ? Length(geometry) : Length(branch.Points, branch.Smooth);
    public static double Length(RouteGeometry geometry) => Length(Flatten(geometry));

    public static bool SamePath(IReadOnlyList<Point2> first, IReadOnlyList<Point2> second, double tolerance)
    {
        if (first.Count < 2 || second.Count < 2 || first[0].DistanceTo(second[0]) > tolerance || first[^1].DistanceTo(second[^1]) > tolerance)
            return false;
        static double Distance(Point2 point, IReadOnlyList<Point2> path)
        {
            var best = double.MaxValue;
            for (var i = 1; i < path.Count; i++)
            {
                var delta = path[i] - path[i - 1]; var squared = delta.X * delta.X + delta.Y * delta.Y;
                var t = squared == 0 ? 0 : Math.Clamp(((point.X - path[i - 1].X) * delta.X + (point.Y - path[i - 1].Y) * delta.Y) / squared, 0, 1);
                best = Math.Min(best, point.DistanceTo(path[i - 1] + delta * t));
            }
            return best;
        }
        return first.All(p => Distance(p, second) <= tolerance) && second.All(p => Distance(p, first) <= tolerance);
    }

    public static double ClosestParameter(RouteGeometry geometry, int segmentIndex, Point2 point)
    {
        // Coarse bracket followed by bounded local refinement. Does not require changing the curve.
        var best = 0d; var distance = double.MaxValue;
        for (var i = 0; i <= 64; i++)
        {
            var t = i / 64d; var d = Evaluate(geometry, segmentIndex, t).DistanceTo(point);
            if (d < distance) { distance = d; best = t; }
        }
        var left = Math.Max(0, best - 1d / 64); var right = Math.Min(1, best + 1d / 64);
        for (var i = 0; i < 32; i++)
        {
            var a = left + (right - left) / 3; var b = right - (right - left) / 3;
            if (Evaluate(geometry, segmentIndex, a).DistanceTo(point) <= Evaluate(geometry, segmentIndex, b).DistanceTo(point)) right = b;
            else left = a;
        }
        return (left + right) * .5;
    }

    /// <summary>Exact Catmull-Rom to cubic conversion. Existing files keep null geometry until edited.</summary>
    public static RouteGeometry FromLegacy(IReadOnlyList<Point2> points, bool smooth)
    {
        var geometry = new RouteGeometry { Knots = points.Select(p => new RouteKnot { Position = p }).ToList() };
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = geometry.Knots[i]; var b = geometry.Knots[i + 1];
            var cubic = smooth && points.Count >= 3;
            a.OutHandle = cubic ? (b.Position - points[Math.Max(0, i - 1)]) * (1d / 6) : (b.Position - a.Position) * (1d / 3);
            b.InHandle = cubic ? (a.Position - points[Math.Min(points.Count - 1, i + 2)]) * (1d / 6) : (a.Position - b.Position) * (1d / 3);
            geometry.Segments.Add(new() { StartKnotId = a.Id, EndKnotId = b.Id, Kind = cubic ? RouteSegmentKind.Cubic : RouteSegmentKind.Line, HasCurveHandles = cubic });
        }
        return geometry;
    }

    public static void ValidateRouteGeometry(RouteGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Knots is null || geometry.Segments is null || geometry.Knots.Count < 2 ||
            geometry.Segments.Count != geometry.Knots.Count - 1 ||
            geometry.Knots.Any(k => k is null || string.IsNullOrWhiteSpace(k.Id) || !IsFinite(k.Position) ||
                !IsFinite(k.InHandle) || !IsFinite(k.OutHandle) || !Enum.IsDefined(k.TangentMode)) ||
            geometry.Knots.Select(k => k.Id).Distinct().Count() != geometry.Knots.Count ||
            geometry.Segments.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || !Enum.IsDefined(s.Kind)) ||
            geometry.Segments.Select(s => s.Id).Distinct().Count() != geometry.Segments.Count)
            throw new ArgumentException("Een route vereist unieke, eindige knopen en aaneengesloten segmenten.");
        for (var i = 0; i < geometry.Segments.Count; i++)
            if (geometry.Segments[i].StartKnotId != geometry.Knots[i].Id || geometry.Segments[i].EndKnotId != geometry.Knots[i + 1].Id)
                throw new ArgumentException("Routsegmenten verwijzen niet in volgorde naar hun knopen.");
    }

    public static Point2 Evaluate(RouteGeometry geometry, int segmentIndex, double t)
    {
        if (segmentIndex < 0 || segmentIndex >= geometry.Segments.Count || !double.IsFinite(t) || t < 0 || t > 1)
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        var a = geometry.Knots[segmentIndex]; var b = geometry.Knots[segmentIndex + 1];
        if (geometry.Segments[segmentIndex].Kind == RouteSegmentKind.Line) return a.Position + (b.Position - a.Position) * t;
        var s = 1 - t;
        return a.Position * (s * s * s) + (a.Position + a.OutHandle) * (3 * s * s * t) +
            (b.Position + b.InHandle) * (3 * s * t * t) + b.Position * (t * t * t);
    }

    public static IReadOnlyList<Point2> Flatten(RouteGeometry geometry)
    {
        ValidateRouteGeometry(geometry);
        var result = new List<Point2> { geometry.Knots[0].Position };
        for (var i = 0; i < geometry.Segments.Count; i++)
        {
            if (geometry.Segments[i].Kind == RouteSegmentKind.Line) result.Add(geometry.Knots[i + 1].Position);
            else FlattenCubic(geometry.Knots[i].Position, geometry.Knots[i].Position + geometry.Knots[i].OutHandle,
                geometry.Knots[i + 1].Position + geometry.Knots[i + 1].InHandle, geometry.Knots[i + 1].Position, result, 0);
        }
        return result;
    }

    private static void FlattenCubic(Point2 a, Point2 b, Point2 c, Point2 d, List<Point2> result, int depth)
    {
        // Bounded adaptive tessellation, independent of zoom, shared by display, measuring and exports.
        var polygon = a.DistanceTo(b) + b.DistanceTo(c) + c.DistanceTo(d);
        if (depth >= 14 || polygon - a.DistanceTo(d) <= .00001)
        {
            result.Add(d); return;
        }
        var ab = (a + b) * .5; var bc = (b + c) * .5; var cd = (c + d) * .5;
        var abc = (ab + bc) * .5; var bcd = (bc + cd) * .5; var middle = (abc + bcd) * .5;
        FlattenCubic(a, ab, abc, middle, result, depth + 1);
        FlattenCubic(middle, bcd, cd, d, result, depth + 1);
    }

    /// <summary>Insert a knot without changing the mathematical curve or any physical entity.</summary>
    public static RouteGeometry Split(RouteGeometry geometry, int segmentIndex, double t)
    {
        ValidateRouteGeometry(geometry);
        if (segmentIndex < 0 || segmentIndex >= geometry.Segments.Count || !double.IsFinite(t) || t <= 0 || t >= 1)
            throw new ArgumentOutOfRangeException(nameof(t));
        var copy = geometry.Copy(); var segment = copy.Segments[segmentIndex];
        var a = copy.Knots[segmentIndex]; var d = copy.Knots[segmentIndex + 1];
        var middle = new RouteKnot { Position = Evaluate(copy, segmentIndex, t) };
        if (segment.Kind == RouteSegmentKind.Cubic)
        {
            Point2 Lerp(Point2 x, Point2 y) => x + (y - x) * t;
            var ab = Lerp(a.Position, a.Position + a.OutHandle);
            var bc = Lerp(a.Position + a.OutHandle, d.Position + d.InHandle);
            var cd = Lerp(d.Position + d.InHandle, d.Position);
            var abc = Lerp(ab, bc); var bcd = Lerp(bc, cd);
            a.OutHandle = ab - a.Position; d.InHandle = cd - d.Position;
            middle.InHandle = abc - middle.Position; middle.OutHandle = bcd - middle.Position;
        }
        copy.Knots.Insert(segmentIndex + 1, middle);
        segment.EndKnotId = middle.Id;
        copy.Segments.Insert(segmentIndex + 1, new() { StartKnotId = middle.Id, EndKnotId = d.Id, Kind = segment.Kind, HasCurveHandles = segment.HasCurveHandles });
        return copy;
    }

    public static (RouteGeometry Left, RouteGeometry Right) SplitAtKnot(RouteGeometry geometry, int knotIndex)
    {
        ValidateRouteGeometry(geometry);
        if (knotIndex <= 0 || knotIndex >= geometry.Knots.Count - 1) throw new ArgumentOutOfRangeException(nameof(knotIndex));
        return (new() { Knots = geometry.Knots.Take(knotIndex + 1).Select(k => k.Copy()).ToList(),
                Segments = geometry.Segments.Take(knotIndex).Select(s => s.Copy()).ToList() },
            new() { Knots = geometry.Knots.Skip(knotIndex).Select(k => k.Copy()).ToList(),
                Segments = geometry.Segments.Skip(knotIndex).Select(s => s.Copy()).ToList() });
    }

    public static (RouteGeometry Left, RouteGeometry Right) SplitAtLengthFraction(RouteGeometry geometry, double fraction)
    {
        ValidateRouteGeometry(geometry);
        if (!double.IsFinite(fraction) || fraction <= 0 || fraction >= 1) throw new ArgumentOutOfRangeException(nameof(fraction));
        var segments = geometry.Segments.Select((segment, i) => new RouteGeometry
        {
            Knots = [geometry.Knots[i].Copy(), geometry.Knots[i + 1].Copy()], Segments = [segment.Copy()]
        }).ToArray();
        var lengths = segments.Select(Length).ToArray(); var target = lengths.Sum() * fraction;
        var traversed = 0d;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (i > 0 && Math.Abs(target - traversed) <= 1e-8) return SplitAtKnot(geometry, i);
            if (target < traversed + lengths[i] || i == lengths.Length - 1)
            {
                var localTarget = target - traversed;
                var low = .000000001; var high = .999999999;
                for (var step = 0; step < 36; step++)
                {
                    var t = (low + high) * .5;
                    var candidate = SplitAtKnot(Split(segments[i], 0, t), 1).Left;
                    if (Length(candidate) < localTarget) low = t; else high = t;
                }
                return SplitAtKnot(Split(geometry, i, (low + high) * .5), i + 1);
            }
            traversed += lengths[i];
        }
        throw new InvalidOperationException("De curve bevat geen bruikbare lengte.");
    }

    public static RouteGeometry WithPoints(RouteGeometry geometry, IReadOnlyList<Point2> points)
    {
        if (geometry.Knots.Count != points.Count) throw new ArgumentException("Het aantal knopen is gewijzigd; gebruik een expliciete segmentbewerking.");
        var copy = geometry.Copy();
        for (var i = 0; i < points.Count; i++) copy.Knots[i].Position = points[i];
        ValidateRouteGeometry(copy);
        return copy;
    }

    public static RouteGeometry WithCurveKind(RouteGeometry geometry, RouteSegmentKind kind, double bendMm = 10, int? segmentIndex = null)
    {
        if (!Enum.IsDefined(kind) || !double.IsFinite(bendMm)) throw new ArgumentException("Ongeldige bochtinstelling.");
        var copy = geometry.Copy();
        foreach (var i in SegmentIndices(copy, segmentIndex))
        {
            var segment = copy.Segments[i]; var wasLine = segment.Kind == RouteSegmentKind.Line;
            if (!wasLine) segment.HasCurveHandles = true;
            segment.Kind = kind;
            // Retain edited handles when toggling straight/curved. Bend a previously collinear pair.
            if (kind == RouteSegmentKind.Cubic)
            {
                if (wasLine && !segment.HasCurveHandles && IsCollinear(copy, i)) SetBend(copy, i, bendMm);
                segment.HasCurveHandles = true;
            }
        }
        return copy;
    }

    public static RouteGeometry WithBend(RouteGeometry geometry, double bendMm, int? segmentIndex = null)
    {
        if (!double.IsFinite(bendMm)) throw new ArgumentOutOfRangeException(nameof(bendMm));
        var copy = geometry.Copy();
        foreach (var i in SegmentIndices(copy, segmentIndex))
        { copy.Segments[i].Kind = RouteSegmentKind.Cubic; copy.Segments[i].HasCurveHandles = true; SetBend(copy, i, bendMm); }
        return copy;
    }

    public static RouteGeometry FlipBend(RouteGeometry geometry, int? segmentIndex = null)
    {
        var copy = geometry.Copy();
        foreach (var i in SegmentIndices(copy, segmentIndex))
        {
            var a = copy.Knots[i]; var b = copy.Knots[i + 1]; var chord = b.Position - a.Position;
            var unit = chord.Length > 1e-12 ? chord * (1 / chord.Length) : new Point2(1, 0);
            Point2 Reflect(Point2 handle) => unit * (2 * (handle.X * unit.X + handle.Y * unit.Y)) - handle;
            a.OutHandle = Reflect(a.OutHandle); b.InHandle = Reflect(b.InHandle);
        }
        return copy;
    }

    public static RouteGeometry ResetHandles(RouteGeometry geometry, double bendMm = 10, int? segmentIndex = null) => WithBend(geometry, bendMm, segmentIndex);

    public static RouteGeometry MoveHandle(RouteGeometry geometry, string knotId, bool outgoing, Point2 offset, TangentMode? mode = null)
    {
        if (!IsFinite(offset) || (mode.HasValue && !Enum.IsDefined(mode.Value))) throw new ArgumentException("Ongeldig bochthandvat.");
        var copy = geometry.Copy(); var knot = copy.Knots.Single(k => k.Id == knotId);
        foreach (var segment in copy.Segments.Where(s => outgoing ? s.StartKnotId == knotId : s.EndKnotId == knotId)) segment.HasCurveHandles = true;
        knot.TangentMode = mode ?? knot.TangentMode;
        var other = outgoing ? knot.InHandle : knot.OutHandle;
        if (outgoing) knot.OutHandle = offset; else knot.InHandle = offset;
        if (knot.TangentMode != TangentMode.Independent && offset.Length > 1e-12)
        {
            var opposite = offset * (-(knot.TangentMode == TangentMode.Mirrored ? offset.Length : other.Length) / offset.Length);
            if (outgoing) knot.InHandle = opposite; else knot.OutHandle = opposite;
        }
        return copy;
    }

    private static IEnumerable<int> SegmentIndices(RouteGeometry geometry, int? index)
    {
        if (index is { } selected)
        {
            if (selected < 0 || selected >= geometry.Segments.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return [selected];
        }
        return Enumerable.Range(0, geometry.Segments.Count);
    }
    private static bool IsCollinear(RouteGeometry geometry, int i)
    {
        var a = geometry.Knots[i]; var b = geometry.Knots[i + 1]; var chord = b.Position - a.Position;
        return Math.Abs(chord.X * a.OutHandle.Y - chord.Y * a.OutHandle.X) < 1e-9 &&
            Math.Abs(chord.X * b.InHandle.Y - chord.Y * b.InHandle.X) < 1e-9;
    }
    private static void SetBend(RouteGeometry geometry, int i, double bend)
    {
        var a = geometry.Knots[i]; var b = geometry.Knots[i + 1]; var chord = b.Position - a.Position;
        var normal = chord.Length > 1e-12 ? new Point2(-chord.Y, chord.X) * (bend / chord.Length) : new Point2(0, bend);
        a.OutHandle = chord * (1d / 3) + normal; b.InHandle = chord * (-1d / 3) + normal;
    }
}
