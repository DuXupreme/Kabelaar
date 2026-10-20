namespace Kabelaar.Core;

public static partial class Geometry
{
    public const double LengthToleranceMm = 0.01;

    public static bool IsFinite(Point2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    /// <summary>One deterministic tessellation shared by display, measurements and export.</summary>
    public static IReadOnlyList<Point2> Flatten(IReadOnlyList<Point2> points, bool smooth)
    {
        if (!smooth || points.Count < 3) return points.ToArray();
        var result = new List<Point2> { points[0] };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var segment = Segment(points, i);
            for (var step = 1; step <= segment.Samples; step++)
                result.Add(segment.Evaluate(step));
        }
        return result;
    }

    public static double Length(IReadOnlyList<Point2> points, bool smooth = false)
    {
        double result = 0;
        if (!smooth || points.Count < 3)
        {
            for (var i = 1; i < points.Count; i++) result += points[i - 1].DistanceTo(points[i]);
            return result;
        }
        // Measure the very same samples as Flatten without allocating a render polyline.
        var previous = points[0];
        for (var i = 0; i < points.Count - 1; i++)
        {
            var segment = Segment(points, i);
            for (var step = 1; step <= segment.Samples; step++)
            {
                var current = segment.Evaluate(step);
                result += previous.DistanceTo(current);
                previous = current;
            }
        }
        return result;
    }

    private static CurveSegment Segment(IReadOnlyList<Point2> points, int i) =>
        new(points[Math.Max(0, i - 1)], points[i], points[i + 1], points[Math.Min(points.Count - 1, i + 2)]);

    private readonly struct CurveSegment(Point2 p0, Point2 p1, Point2 p2, Point2 p3)
    {
        // Bound work for malformed or unusually large imported coordinates.
        public int Samples { get; } = SampleCount(p1.DistanceTo(p2));
        private static int SampleCount(double distance) =>
            double.IsFinite(distance) ? (int)Math.Clamp(Math.Ceiling(distance / 2), 16, 256) : 16;
        public Point2 Evaluate(int step)
        {
            var t = (double)step / Samples;
            var t2 = t * t;
            var t3 = t2 * t;
            return new(
                .5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                .5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3));
        }
    }

    public static Point2 Transform(Point2 point, double rotationDegrees, Point2 offset)
    {
        var r = rotationDegrees * Math.PI / 180;
        return new(point.X * Math.Cos(r) - point.Y * Math.Sin(r) + offset.X,
                   point.X * Math.Sin(r) + point.Y * Math.Cos(r) + offset.Y);
    }

    public static Point2 ComponentWorld(PlacedComponent placement, Point2 local) =>
        Transform(local * placement.DisplayScale, placement.RotationDegrees, placement.Position);

    public static Point2? PinWorld(HarnessProject project, PlacedComponent placement, string? cavityId)
    {
        var connector = project.Assembly.Connectors.Find(c => c.Id == placement.EntityId);
        if (connector is null) return project.Assembly.Splices.Any(s => s.Id == placement.EntityId) ? placement.Position : null;
        var cavity = connector.Cavities.Find(c => c.Id == cavityId);
        var part = project.Parts.Find(p => p.Id == connector.PartId);
        var pin = part?.Pins.Find(p => p.Id == cavity?.PinDefinitionId);
        return pin is null ? null : ComponentWorld(placement, pin.Position);
    }

    public static Point2? EndpointPosition(HarnessProject project, DrawingView view, Endpoint endpoint)
    {
        if (endpoint.Kind == EndpointKind.Free) return null;
        var placement = view.Components.Find(p => p.EntityId == endpoint.EntityId);
        return placement is null ? null : PinWorld(project, placement, endpoint.CavityId);
    }
}
