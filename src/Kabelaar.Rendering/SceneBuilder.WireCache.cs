using System.Globalization;
using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    private readonly record struct WireAppearance(string ViewId, Point2 Origin, double Scale, string Layer,
        string? PlacementId, bool Locked, string Name, string ColorCode, string Color, double CrossSection,
        bool Twisted, bool FreeStart, bool FreeEnd);
    private sealed record WireProjection(IReadOnlyList<Point2> Source, WireAppearance Appearance, ScenePrimitive[] Primitives);
    private static readonly ConditionalWeakTable<DrawingView, Dictionary<string, WireProjection>> WireProjectionCache = new();

    private static ScenePrimitive[] ProjectWire(Dictionary<string, WireProjection> cache, string id,
        IReadOnlyList<Point2> points, WireAppearance appearance)
    {
        // Sources are private flattened/projected geometry, never mutable domain point lists. A content
        // comparison also handles rebuilt twist paths, bundles and endpoint-adjusted routes.
        if (cache.TryGetValue(id, out var previous) && previous.Appearance == appearance &&
            (ReferenceEquals(previous.Source, points) || previous.Source.SequenceEqual(points)))
            return previous.Primitives;
        var sheetPoints = new Point2[points.Count];
        for (var i = 0; i < points.Count; i++) sheetPoints[i] = appearance.Origin + points[i] * appearance.Scale;
        ScenePrimitive Tag(ScenePrimitive primitive) => primitive with
        {
            ViewId = appearance.ViewId, LayerId = appearance.Layer, PlacementId = appearance.PlacementId, Locked = appearance.Locked
        };
        var result = new ScenePrimitive[2 + (appearance.FreeStart ? 1 : 0) + (appearance.FreeEnd ? 1 : 0)];
        result[0] = Tag(new ScenePath(id, appearance.Twisted ? "twisted-wire" : "wire", sheetPoints, appearance.Color, .45 * appearance.Scale));
        var label = $"{appearance.Name}  {appearance.ColorCode}  {appearance.CrossSection.ToString("0.######", CultureInfo.InvariantCulture)} mm²";
        result[1] = Tag(new SceneText(id, "wire-label", label, sheetPoints[Math.Min(1, sheetPoints.Length - 1)] + new Point2(1, -2 * appearance.Scale), 2.2 * appearance.Scale));
        var next = 2;
        if (appearance.FreeStart) result[next++] = Tag(new SceneEllipse(id, "free-end", sheetPoints[0], .7 * appearance.Scale, .7 * appearance.Scale, appearance.Color, .25));
        if (appearance.FreeEnd) result[next] = Tag(new SceneEllipse(id, "free-end", sheetPoints[^1], .7 * appearance.Scale, .7 * appearance.Scale, appearance.Color, .25));
        cache[id] = new(points, appearance, result);
        return result;
    }
}
