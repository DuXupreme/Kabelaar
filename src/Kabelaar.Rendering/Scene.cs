using Kabelaar.Core;

namespace Kabelaar.Rendering;

/// <summary>Axis aligned bounds in sheet millimetres. Independent of any graphics backend.</summary>
public readonly record struct SceneBounds(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(Point2 point) => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
    public bool Intersects(SceneBounds other) => Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
    public SceneBounds Inflate(double amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);
    public SceneBounds Union(SceneBounds other)
    {
        var left = Math.Min(Left, other.Left); var top = Math.Min(Top, other.Top);
        return new(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }
    public static SceneBounds Around(IEnumerable<Point2> points, double padding = 0)
    {
        var left = double.PositiveInfinity; var top = double.PositiveInfinity;
        var right = double.NegativeInfinity; var bottom = double.NegativeInfinity;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X); top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
        }
        return double.IsPositiveInfinity(left) ? new() : new(left - padding, top - padding, right - left + 2 * padding, bottom - top + 2 * padding);
    }
    public static SceneBounds TextBounds(string text, Point2 baseline, double height, double angle, double? maximumWidth)
    {
        // Conservative glyph bounds keep hit testing/culling independent from the platform font rasterizer.
        var width = maximumWidth ?? Math.Max(height, text.Length * height);
        if (Math.Abs(angle) < 1e-12) return new(baseline.X, baseline.Y - height * 1.15, width, height * 1.55);
        var corners = new[] { new Point2(0, -height * 1.15), new Point2(width, -height * 1.15), new Point2(width, height * .4), new Point2(0, height * .4) };
        return Around(corners.Select(p => Geometry.Transform(p, angle, baseline)));
    }
}

public abstract record ScenePrimitive(string EntityId, string Category, SceneBounds Bounds)
{
    public string? PlacementId { get; init; }
    public string? ViewId { get; init; }
    public string LayerId { get; init; } = "main";
    public bool Locked { get; init; }
}

public sealed record ScenePath(
    string EntityId, string Category, IReadOnlyList<Point2> Points,
    string Stroke = "#243244", double StrokeWidth = 0.25, string? Fill = null,
    bool Closed = false, bool Smooth = false, bool Dashed = false)
    : ScenePrimitive(EntityId, Category, SceneBounds.Around(Points, StrokeWidth / 2));

/// <summary>Text baseline is Position; height and rotation are expressed in mm and degrees.</summary>
public sealed record SceneText(
    string EntityId, string Category, string Text, Point2 Position,
    double HeightMm = 3, string Color = "#243244", bool Bold = false,
    double RotationDegrees = 0, double? MaxWidthMm = null)
    : ScenePrimitive(EntityId, Category, SceneBounds.TextBounds(Text, Position, HeightMm, RotationDegrees, MaxWidthMm));

public sealed record SceneEllipse(
    string EntityId, string Category, Point2 Center, double RadiusX, double RadiusY,
    string Stroke = "#243244", double StrokeWidth = 0.25, string? Fill = null)
    : ScenePrimitive(EntityId, Category, new(Center.X - RadiusX, Center.Y - RadiusY, RadiusX * 2, RadiusY * 2));

public sealed record SceneArc(
    string EntityId, string Category, Point2 Center, double Radius,
    double StartDegrees, double SweepDegrees, string Stroke = "#243244", double StrokeWidth = 0.25)
    : ScenePrimitive(EntityId, Category, new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2));

public sealed record SceneImage(string EntityId, string Category, byte[] Data, SceneBounds ImageBounds)
    : ScenePrimitive(EntityId, Category, ImageBounds);

public sealed class DrawingScene
{
    public string SheetId { get; }
    public double WidthMm { get; }
    public double HeightMm { get; }
    public bool IsDraft { get; }
    public IReadOnlyList<ScenePrimitive> Primitives { get; }
    public SceneSpatialIndex Index { get; }
    public SceneBounds Bounds => new(0, 0, WidthMm, HeightMm);
    public SceneBounds ContentBounds { get; }

    public DrawingScene(string sheetId, double widthMm, double heightMm, bool isDraft, IEnumerable<ScenePrimitive> primitives)
        : this(sheetId, widthMm, heightMm, isDraft, primitives, null) { }

    internal DrawingScene(string sheetId, double widthMm, double heightMm, bool isDraft,
        IEnumerable<ScenePrimitive> primitives, SceneSpatialIndex? previousIndex)
    {
        SheetId = sheetId; WidthMm = widthMm; HeightMm = heightMm; IsDraft = isDraft;
        Primitives = primitives.ToArray();
        var bounds = Bounds;
        foreach (var primitive in Primitives) bounds = bounds.Union(primitive.Bounds);
        ContentBounds = bounds;
        Index = new SceneSpatialIndex(Primitives, previousIndex);
    }
}

/// <summary>A uniform grid keeps queries local while retaining stable scene/z order.</summary>
public sealed class SceneSpatialIndex
{
    private readonly IReadOnlyList<ScenePrimitive> _primitives;
    // These base collections are immutable after construction and may be shared by later indexes.
    private readonly Dictionary<(int X, int Y), List<int>> _cells;
    private readonly List<int> _large;
    private readonly SceneBounds[] _baseBounds;
    private readonly int[] _changed;
    private const double CellSize = 25;

    public SceneSpatialIndex(IReadOnlyList<ScenePrimitive> primitives)
        : this(primitives, null) { }

    /// <summary>Reuses an immutable grid when only a bounded number of primitive bounds changed.</summary>
    /// <remarks>The primitive list and its contents must remain unchanged for the lifetime of this index.</remarks>
    public SceneSpatialIndex(IReadOnlyList<ScenePrimitive> primitives, SceneSpatialIndex? previousIndex)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        _primitives = primitives;
        if (previousIndex is not null && previousIndex._baseBounds.Length == primitives.Count)
        {
            var limit = Math.Min(256, Math.Max(8, primitives.Count / 4));
            List<int>? changed = null;
            for (var i = 0; i < primitives.Count; i++)
            {
                // Compare to the original grid, not the preceding scene: earlier moves still need
                // to be queried even when this particular edit did not move them again.
                if (primitives[i].Bounds == previousIndex._baseBounds[i]) continue;
                (changed ??= []).Add(i);
                if (changed.Count > limit) break;
            }
            if (changed is null || changed.Count <= limit)
            {
                _cells = previousIndex._cells;
                _large = previousIndex._large;
                _baseBounds = previousIndex._baseBounds;
                _changed = changed?.ToArray() ?? [];
                return;
            }
        }

        // A fresh base prevents unbounded overlays. No reference to a previous index is retained.
        _cells = [];
        _large = [];
        _baseBounds = new SceneBounds[primitives.Count];
        _changed = [];
        for (var i = 0; i < primitives.Count; i++)
        {
            var b = primitives[i].Bounds;
            _baseBounds[i] = b;
            var (x0, y0, x1, y1) = Cells(b);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1024) { _large.Add(i); continue; }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out var indices)) _cells[(x, y)] = indices = [];
                    indices.Add(i);
                }
        }
    }

    public IReadOnlyList<ScenePrimitive> Query(SceneBounds bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width < 0 || bounds.Height < 0) return [];
        var (x0, y0, x1, y1) = Cells(bounds);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 10000)
            return _primitives.Where(p => p.Bounds.Intersects(bounds)).ToArray();
        var indices = new HashSet<int>(_large);
        indices.UnionWith(_changed);
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                if (_cells.TryGetValue((x, y), out var cell)) indices.UnionWith(cell);
        return indices.Order().Select(i => _primitives[i]).Where(p => p.Bounds.Intersects(bounds)).ToArray();
    }

    public IReadOnlyList<ScenePrimitive> HitTest(Point2 point, double toleranceMm = 1.5, bool includeLocked = false)
    {
        return Query(new(point.X - toleranceMm, point.Y - toleranceMm, toleranceMm * 2, toleranceMm * 2))
            .Where(p => (includeLocked || !p.Locked) && Hits(p, point, toleranceMm)).Reverse().ToArray();
    }

    private static bool Hits(ScenePrimitive primitive, Point2 point, double tolerance)
    {
        if (primitive is ScenePath path && path.Points.Count > 1)
        {
            var points = Geometry.Flatten(path.Points, path.Smooth);
            if ((path.Fill is not null || path.Category == "connector-symbol") && path.Closed && InsidePolygon(points, point)) return true;
            for (var i = 1; i < points.Count; i++)
                if (DistanceToSegment(point, points[i - 1], points[i]) <= tolerance + path.StrokeWidth / 2) return true;
            return path.Closed && DistanceToSegment(point, points[^1], points[0]) <= tolerance + path.StrokeWidth / 2;
        }
        if (primitive is SceneEllipse ellipse)
        {
            if (ellipse.RadiusX <= 0 || ellipse.RadiusY <= 0) return false;
            var normalized = Math.Sqrt(Math.Pow((point.X - ellipse.Center.X) / ellipse.RadiusX, 2) + Math.Pow((point.Y - ellipse.Center.Y) / ellipse.RadiusY, 2));
            var radialTolerance = (tolerance + ellipse.StrokeWidth / 2) / Math.Min(ellipse.RadiusX, ellipse.RadiusY);
            return ellipse.Fill is not null ? normalized <= 1 + radialTolerance : Math.Abs(normalized - 1) <= radialTolerance;
        }
        if (primitive is SceneArc arc)
        {
            var delta = point - arc.Center;
            if (Math.Abs(delta.Length - arc.Radius) > tolerance + arc.StrokeWidth / 2) return false;
            var relative = (Math.Atan2(delta.Y, delta.X) * 180 / Math.PI - arc.StartDegrees + 720) % 360;
            return arc.SweepDegrees >= 0 ? relative <= arc.SweepDegrees : relative >= 360 + arc.SweepDegrees;
        }
        return primitive.Bounds.Inflate(tolerance).Contains(point);
    }

    private static bool InsidePolygon(IReadOnlyList<Point2> points, Point2 point)
    {
        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i]; var b = points[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceToSegment(Point2 point, Point2 a, Point2 b)
    {
        var v = b - a; var lengthSquared = v.X * v.X + v.Y * v.Y;
        if (lengthSquared < 1e-12) return point.DistanceTo(a);
        var w = point - a; var t = Math.Clamp((w.X * v.X + w.Y * v.Y) / lengthSquared, 0, 1);
        return point.DistanceTo(a + v * t);
    }

    private static (int X0, int Y0, int X1, int Y1) Cells(SceneBounds b) =>
        (Cell(b.Left), Cell(b.Top), Cell(b.Right), Cell(b.Bottom));
    private static int Cell(double value) => (int)Math.Clamp(Math.Floor(value / CellSize), -1_000_000_000, 1_000_000_000);
}
