using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    private readonly record struct PinVisual(string Id, string Label, Point2 Position);
    private readonly record struct LabelVisual(string Text, Point2 Position, double Height);
    private readonly record struct CavityVisual(string Id, string PinId, string Label, CavityState State);
    private sealed record SymbolVisual(Point2[] Source, IReadOnlyList<Point2> Flattened, bool Closed, bool Smooth, bool Hidden);
    private sealed record PartVisual(string PartNumber, double Width, double Height, PinVisual[] Pins,
        LabelVisual[] Labels, SymbolVisual[] Symbol, Dictionary<string, PinVisual> PinsById);
    private sealed class PartMemo { public PartVisual? Value; }
    private static readonly ConditionalWeakTable<PartDefinition, PartMemo> PartVisualCache = new();

    private static PartVisual VisualPart(PartDefinition part)
    {
        var memo = PartVisualCache.GetOrCreateValue(part);
        lock (memo)
        {
            var old = memo.Value;
            if (old is not null && old.PartNumber == part.PartNumber && old.Width == part.WidthMm && old.Height == part.HeightMm &&
                old.Pins.Length == part.Pins.Count && old.Labels.Length == part.Labels.Count && old.Symbol.Length == part.Symbol.Count)
            {
                var same = true;
                for (var i = 0; same && i < old.Pins.Length; i++)
                    same = old.Pins[i] == new PinVisual(part.Pins[i].Id, part.Pins[i].Label, part.Pins[i].Position);
                for (var i = 0; same && i < old.Labels.Length; i++)
                    same = old.Labels[i] == new LabelVisual(part.Labels[i].Text, part.Labels[i].Position, part.Labels[i].HeightMm);
                for (var i = 0; same && i < old.Symbol.Length; i++)
                {
                    var a = old.Symbol[i]; var b = part.Symbol[i];
                    same = a.Closed == b.Closed && a.Smooth == b.Smooth && a.Hidden == b.Hidden && a.Source.SequenceEqual(b.Points);
                }
                if (same) return old;
            }
            var pins = part.Pins.Select(p => new PinVisual(p.Id, p.Label, p.Position)).ToArray();
            var symbols = part.Symbol.Select(p =>
            {
                var source = p.Points.ToArray();
                return new SymbolVisual(source, Geometry.Flatten(source, p.Smooth), p.Closed, p.Smooth, p.Hidden);
            }).ToArray();
            return memo.Value = new(part.PartNumber, part.WidthMm, part.HeightMm, pins,
                part.Labels.Select(l => new LabelVisual(l.Text, l.Position, l.HeightMm)).ToArray(),
                symbols, FirstById(pins, p => p.Id));
        }
    }

    private readonly record struct ComponentAppearance(string ViewId, Point2 Origin, double Scale, string PlacementId,
        string EntityId, string Layer, bool Locked, Point2 Position, double Rotation, Point2 LabelOffset,
        double DisplayScale, bool ShowPins, bool ShowPinNumbers, bool ShowLabel, bool ShowPartNumber,
        double LabelFontSize, double LineWidth);
    private sealed record ComponentProjection(ComponentAppearance Appearance, PartVisual? Part, bool IsConnector,
        string Name, CavityVisual[] Cavities, ScenePrimitive[] Primitives,
        ((string Entity, string? Cavity) Key, Point2 Position)[] Endpoints);
    private sealed class ComponentMemo { public ComponentProjection? Value; }
    private static readonly ConditionalWeakTable<PlacedComponent, ComponentMemo> ComponentProjectionCache = new();

    private static ComponentProjection ProjectComponent(PlacedComponent placed, ComponentAppearance appearance,
        ConnectorInstance? connector, Splice? splice, PartVisual? part)
    {
        var memo = ComponentProjectionCache.GetOrCreateValue(placed);
        lock (memo)
        {
            var name = connector?.Name ?? splice?.Name ?? "";
            var cavities = connector?.Cavities;
            var count = cavities?.Count ?? 0;
            var old = memo.Value;
            if (old is not null && old.Appearance == appearance && ReferenceEquals(old.Part, part) &&
                old.IsConnector == (connector is not null) && old.Name == name && old.Cavities.Length == count)
            {
                var same = true;
                for (var i = 0; same && i < count; i++)
                {
                    var cavity = cavities![i];
                    same = old.Cavities[i] == new CavityVisual(cavity.Id, cavity.PinDefinitionId, cavity.Label, cavity.State);
                }
                if (same) return old;
            }
            var snapshot = cavities?.Select(c => new CavityVisual(c.Id, c.PinDefinitionId, c.Label, c.State)).ToArray() ?? [];
            var primitives = new List<ScenePrimitive>();
            var endpoints = new List<((string Entity, string? Cavity) Key, Point2 Position)>(count);
            var (sin, cos) = Math.SinCos(appearance.Rotation * Math.PI / 180);
            Point2 World(Point2 p)
            {
                p *= appearance.DisplayScale;
                return new(p.X * cos - p.Y * sin + appearance.Position.X, p.X * sin + p.Y * cos + appearance.Position.Y);
            }
            Point2 SheetPoint(Point2 p) => appearance.Origin + p * appearance.Scale;
            Point2 ComponentPoint(Point2 p) => SheetPoint(World(p));
            void Add(ScenePrimitive primitive) => primitives.Add(primitive with
            {
                ViewId = appearance.ViewId, LayerId = appearance.Layer, PlacementId = appearance.PlacementId, Locked = appearance.Locked
            });
            var position = SheetPoint(appearance.Position);
            var scale = appearance.Scale;
            var id = appearance.EntityId;
            if (connector is not null)
            {
                if (part is not null && part.Symbol.Length > 0)
                    foreach (var path in part.Symbol)
                        Add(new ScenePath(id, "connector-symbol", path.Flattened.Select(ComponentPoint).ToArray(),
                            path.Hidden ? Muted : Ink, appearance.LineWidth * scale, Closed: path.Closed, Dashed: path.Hidden));
                else
                {
                    var w = part?.Width ?? 20; var h = part?.Height ?? 12;
                    Add(new ScenePath(id, "connector-symbol",
                        new[] { new Point2(-w / 2, -h / 2), new Point2(w / 2, -h / 2), new Point2(w / 2, h / 2), new Point2(-w / 2, h / 2) }.Select(ComponentPoint).ToArray(),
                        Ink, appearance.LineWidth * scale, "#FFFFFF", true));
                }
                if (part is not null)
                {
                    foreach (var label in part.Labels)
                        Add(new SceneText(id, "symbol-label", label.Text, ComponentPoint(label.Position),
                            label.Height * scale * appearance.DisplayScale, RotationDegrees: appearance.Rotation));
                    var cavityByPin = FirstById(snapshot, c => c.PinId);
                    foreach (var pin in part.Pins)
                    {
                        var found = cavityByPin.TryGetValue(pin.Id, out var cavity);
                        var point = ComponentPoint(pin.Position);
                        var fill = found ? cavity.State switch { CavityState.Occupied => "#137F8B", CavityState.Plugged => "#6C7A86", _ => "#FFFFFF" } : "#FFFFFF";
                        if (appearance.ShowPins)
                            Add(new SceneEllipse(id, "pin", point, .7 * scale, .7 * scale, Ink, .2 * scale, fill));
                        if (appearance.ShowPinNumbers)
                            Add(new SceneText(id, "pin-label", found ? cavity.Label : pin.Label, point + new Point2(1.2 * scale, -.7 * scale), 2 * scale));
                    }
                    foreach (var cavity in snapshot)
                        if (part.PinsById.TryGetValue(cavity.PinId, out var pin))
                            endpoints.Add(((id, cavity.Id), World(pin.Position)));
                }
                if (appearance.ShowLabel)
                    Add(new SceneText(id, "connector-label", name, position + appearance.LabelOffset * scale,
                        appearance.LabelFontSize * scale, Bold: true));
                if (appearance.ShowPartNumber)
                    Add(new SceneText(id, "part-number", part?.PartNumber ?? "Onderdeel ontbreekt",
                        position + appearance.LabelOffset * scale + new Point2(0, appearance.LabelFontSize * 1.2 * scale),
                        appearance.LabelFontSize * .77 * scale, Muted));
            }
            else
            {
                var radius = 1.3 * scale * appearance.DisplayScale;
                Add(new SceneEllipse(id, "splice", position, radius, radius, Ink, appearance.LineWidth * scale, Ink));
                if (appearance.ShowLabel)
                    Add(new SceneText(id, "splice-label", name, position + appearance.LabelOffset * scale,
                        appearance.LabelFontSize * scale, Bold: true));
                endpoints.Add(((id, null), appearance.Position));
            }
            return memo.Value = new(appearance, part, connector is not null, name, snapshot, primitives.ToArray(), endpoints.ToArray());
        }
    }
}
