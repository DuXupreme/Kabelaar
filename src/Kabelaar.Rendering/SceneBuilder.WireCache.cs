using System.Globalization;
using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    private readonly record struct WireAppearance(string ViewId, Point2 Origin, double Scale, string Layer,
        string? PlacementId, bool Locked, string Name, string ColorCode, string Color, double CrossSection,
        bool Twisted, bool FreeStart, bool FreeEnd, double LineWidth, bool ShowLabel, double LabelFontSize,
        Point2 LabelPosition, Point2 LabelAnchor);
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
        var reusablePath = previous is not null &&
            previous.Appearance with { LabelPosition = appearance.LabelPosition, LabelAnchor = appearance.LabelAnchor } == appearance &&
            (ReferenceEquals(previous.Source, points) || previous.Source.SequenceEqual(points))
            ? previous.Primitives.FirstOrDefault(p => p is ScenePath && p.Category is "wire" or "twisted-wire") : null;
        var result = new List<ScenePrimitive>(5)
        {
            reusablePath ?? Tag(new ScenePath(id, appearance.Twisted ? "twisted-wire" : "wire", sheetPoints, appearance.Color, appearance.LineWidth * appearance.Scale))
        };
        if (appearance.ShowLabel)
        {
            var label = WireLabel(appearance.Name, appearance.ColorCode, appearance.CrossSection);
            result.Add(Tag(new SceneText(id, "wire-label", label, appearance.LabelPosition,
                appearance.LabelFontSize * appearance.Scale)));
            if (appearance.LabelPosition.DistanceTo(appearance.LabelAnchor) > appearance.LabelFontSize * appearance.Scale * 2.2)
                result.Add(Tag(new ScenePath(id, "wire-label-leader",
                    [appearance.LabelAnchor, appearance.LabelPosition + new Point2(0, .5 * appearance.Scale)],
                    Muted, .15 * appearance.Scale)));
        }
        if (appearance.FreeStart) result.Add(Tag(new SceneEllipse(id, "free-end", sheetPoints[0], .7 * appearance.Scale, .7 * appearance.Scale, appearance.Color, .25)));
        if (appearance.FreeEnd) result.Add(Tag(new SceneEllipse(id, "free-end", sheetPoints[^1], .7 * appearance.Scale, .7 * appearance.Scale, appearance.Color, .25)));
        var primitives = result.ToArray();
        cache[id] = new(points, appearance, primitives);
        return primitives;
    }

    private static string WireLabel(string name, string color, double section) =>
        $"{name}  {color}  {section.ToString("0.######", CultureInfo.InvariantCulture)} mm²";
}
