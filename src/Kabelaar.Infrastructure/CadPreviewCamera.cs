namespace Kabelaar.Infrastructure;

public sealed record CadCameraPose(Point3 Position, Point3 Target, Point3 Up, double NearPlane, double FarPlane);

/// <summary>Preview-only orbit/pan/zoom state. It has no access to a part's production projection.</summary>
public sealed class CadPreviewCamera
{
    public const double FieldOfViewDegrees = 40;
    private readonly Point3 center;
    private readonly double radius;
    private double yaw = -53.1301024, pitch = 28.0724869, distance;
    private Point3 target;
    public CadPreviewCamera(IEnumerable<Point3> vertices)
    {
        var points = vertices.ToArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)))
            throw new ArgumentException("De preview bevat ongeldige coördinaten.", nameof(vertices));
        if (points.Length == 0) { center = new(); radius = 1; }
        else
        {
            var min = new Point3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
            var max = new Point3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            center = Scale(Add(min, max), .5); radius = Math.Max(.01, Length(Subtract(max, min)) * .5);
        }
        target = center; Fit();
    }
    public void Fit(double aspect = 1)
    {
        if (!double.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        target = center;
        var halfAngle = Math.Atan(Math.Tan(FieldOfViewDegrees * Math.PI / 360) / Math.Max(1, aspect));
        distance = radius / Math.Sin(halfAngle) * 1.15;
    }
    public void Orbit(double horizontalDegrees, double verticalDegrees)
    {
        if (!double.IsFinite(horizontalDegrees) || !double.IsFinite(verticalDegrees)) throw new ArgumentOutOfRangeException();
        yaw = (yaw + horizontalDegrees) % 360; pitch = Math.Clamp(pitch + verticalDegrees, -89, 89);
    }
    public void Zoom(double wheelSteps)
    {
        if (!double.IsFinite(wheelSteps)) throw new ArgumentOutOfRangeException(nameof(wheelSteps));
        distance = Math.Clamp(distance * Math.Exp(-Math.Clamp(wheelSteps, -100, 100) * .15), radius * .03, radius * 10000);
    }
    public void Pan(double dxPixels, double dyPixels, double viewportWidth)
    {
        if (!double.IsFinite(dxPixels) || !double.IsFinite(dyPixels) || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
            throw new ArgumentOutOfRangeException();
        var right = new Point3(-Math.Sin(yaw * Math.PI / 180), Math.Cos(yaw * Math.PI / 180), 0);
        var outward = Direction();
        var up = Cross(outward, right);
        var scale = 2 * distance * Math.Tan(FieldOfViewDegrees * Math.PI / 360) / viewportWidth;
        target = Add(target, Add(Scale(right, -dxPixels * scale), Scale(up, dyPixels * scale)));
    }
    public CadCameraPose Pose => new(Add(target, Scale(Direction(), distance)), target, new(0, 0, 1),
        Math.Max(.0001, radius * .0001), Math.Max(radius * 20, distance + radius * 4));
    private Point3 Direction()
    {
        var y = yaw * Math.PI / 180; var p = pitch * Math.PI / 180;
        return new(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
    }
    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Subtract(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Point3 Scale(Point3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
    private static double Length(Point3 p) => Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
    private static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
