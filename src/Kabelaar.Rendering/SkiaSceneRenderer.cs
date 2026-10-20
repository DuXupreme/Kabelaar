using System.Runtime.CompilerServices;
using Kabelaar.Core;
using SkiaSharp;

namespace Kabelaar.Rendering;

public static class SkiaSceneRenderer
{
    private sealed class Pictures
    {
        public SKPicture? Text;
        public SKPicture? OutlinedText;
    }
    private static readonly ConditionalWeakTable<DrawingScene, Pictures> Cache = new();
    private static readonly Lazy<SKTypeface> Regular = new(() => SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal) ?? SKTypeface.Default);
    private static readonly Lazy<SKTypeface> Bold = new(() => SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default);

    /// <summary>Caller supplies the mm-to-screen/page transform; this renderer never changes model data.</summary>
    public static void Render(SKCanvas canvas, DrawingScene scene, SceneBounds? visibleBounds = null,
        ISet<string>? selectedIds = null, bool textAsPaths = false)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var pictures = Cache.GetOrCreateValue(scene);
        SKPicture picture;
        lock (pictures)
        {
            if (textAsPaths) picture = pictures.OutlinedText ??= Record(scene, true);
            else picture = pictures.Text ??= Record(scene, false);
        }
        canvas.Save();
        if (visibleBounds is { } b) canvas.ClipRect(Rect(b));
        canvas.DrawPicture(picture);
        DrawSelection(canvas, scene, selectedIds);
        canvas.Restore();
    }

    /// <summary>Bounded, screen-only raster cache. Vector exports always use Render.</summary>
    public static void RenderScreen(SKCanvas canvas, DrawingScene scene, SceneBounds visibleBounds,
        ScreenSceneCache cache, ISet<string>? selectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(cache);
        cache.UseScene(scene);
        var transform = canvas.TotalMatrix;
        var pixelsPerMm = Math.Sqrt(transform.ScaleX * transform.ScaleX + transform.SkewY * transform.SkewY);
        if (!double.IsFinite(pixelsPerMm) || pixelsPerMm <= 0) return;
        // Small zoom steps reuse a slightly oversampled image; text stays sharp without rerendering every frame.
        var level = (int)Math.Ceiling(Math.Log2(Math.Clamp(pixelsPerMm * 1.15, 1.0 / 64, 256)) * 2);
        var density = Math.Pow(2, level / 2.0);
        var extent = cache.ContentBounds;
        var left = Math.Floor(extent.Left * density) / density;
        var top = Math.Floor(extent.Top * density) / density;
        var width = Math.Ceiling((extent.Right - left) * density);
        var height = Math.Ceiling((extent.Bottom - top) * density);
        canvas.Save();
        canvas.ClipRect(Rect(visibleBounds));
        if (width <= 8192 && height <= 8192 && width * height <= 8_000_000)
        {
            var bounds = new SceneBounds(left, top, width / density, height / density);
            var raster = cache.GetOrCreate(new(level, int.MinValue, 0), () => Rasterize(scene, bounds, density, false),
                (old, dirty) => PatchRaster(scene, old, bounds, density, dirty));
            canvas.DrawImage(raster, Rect(bounds), new SKSamplingOptions(SKFilterMode.Linear));
        }
        else
        {
            // At close zoom only visible tiles are rasterized. Querying the scene index avoids replaying
            // thousands of offscreen paths for each new tile. One-pixel gutters prevent tile-edge seams.
            const int tilePixels = 512;
            var tileMm = tilePixels / density;
            var minX = (int)Math.Floor(Math.Max(visibleBounds.Left, extent.Left) / tileMm);
            var minY = (int)Math.Floor(Math.Max(visibleBounds.Top, extent.Top) / tileMm);
            var maxX = (int)Math.Floor(Math.Min(visibleBounds.Right, extent.Right) / tileMm);
            var maxY = (int)Math.Floor(Math.Min(visibleBounds.Bottom, extent.Bottom) / tileMm);
            var source = new SKRect(1, 1, tilePixels + 1, tilePixels + 1);
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var bounds = new SceneBounds(x * tileMm, y * tileMm, tileMm, tileMm);
                    var padded = bounds.Inflate(1 / density);
                    var raster = cache.GetOrCreate(new(level, x, y), () => Rasterize(scene, padded, density, true),
                        (old, dirty) => PatchRaster(scene, old, padded, density, dirty));
                    canvas.DrawImage(raster, source, Rect(bounds), new SKSamplingOptions(SKFilterMode.Linear));
                }
        }
        DrawSelection(canvas, scene, selectedIds);
        canvas.Restore();
    }

    private static SKImage Rasterize(DrawingScene scene, SceneBounds bounds, double density, bool indexed)
    {
        var width = Math.Max(1, (int)Math.Round(bounds.Width * density));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * density));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Onvoldoende geheugen voor de tekenweergave.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)density);
        canvas.Translate((float)-bounds.Left, (float)-bounds.Top);
        if (indexed)
        {
            foreach (var primitive in scene.Index.Query(bounds.Inflate(2 / density))) DrawPrimitive(canvas, primitive, false);
        }
        else Render(canvas, scene);
        return surface.Snapshot();
    }

    private static SKImage PatchRaster(DrawingScene scene, SKImage previous, SceneBounds bounds, double density, SceneBounds dirty)
    {
        if (!bounds.Intersects(dirty)) return previous;
        using var surface = SKSurface.Create(new SKImageInfo(previous.Width, previous.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Onvoldoende geheugen voor de tekenweergave.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(previous, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Scale((float)density);
        canvas.Translate((float)-bounds.Left, (float)-bounds.Top);
        // Pixel-align the cleared region so that repainting cannot leave a one-pixel antialias fringe.
        var left = Math.Floor(dirty.Left * density - 1) / density;
        var top = Math.Floor(dirty.Top * density - 1) / density;
        var right = Math.Ceiling(dirty.Right * density + 1) / density;
        var bottom = Math.Ceiling(dirty.Bottom * density + 1) / density;
        var clip = new SceneBounds(left, top, right - left, bottom - top);
        canvas.ClipRect(Rect(clip), SKClipOperation.Intersect, false);
        canvas.DrawColor(SKColors.Transparent, SKBlendMode.Clear);
        foreach (var primitive in scene.Index.Query(clip.Inflate(2 / density))) DrawPrimitive(canvas, primitive, false);
        return surface.Snapshot();
    }

    private static void DrawSelection(SKCanvas canvas, DrawingScene scene, ISet<string>? selectedIds)
    {
        if (selectedIds is { Count: > 0 })
        {
            using var paint = new SKPaint { Color = new SKColor(15, 146, 160), Style = SKPaintStyle.Stroke, StrokeWidth = .35f, IsAntialias = true };
            foreach (var primitive in scene.Primitives.Where(p => selectedIds.Contains(p.EntityId) || (p.PlacementId is { } id && selectedIds.Contains(id))))
                canvas.DrawRect(Rect(primitive.Bounds.Inflate(.7)), paint);
        }
    }

    private static SKPicture Record(DrawingScene scene, bool textAsPaths)
    {
        using var recorder = new SKPictureRecorder();
        var bounds = scene.ContentBounds.Inflate(30);
        using var canvas = recorder.BeginRecording(Rect(bounds));
        foreach (var primitive in scene.Primitives) DrawPrimitive(canvas, primitive, textAsPaths);
        return recorder.EndRecording();
    }

    private static void DrawPrimitive(SKCanvas canvas, ScenePrimitive primitive, bool textAsPaths)
    {
        switch (primitive)
        {
            case ScenePath line:
                if (line.Points.Count == 0) return;
                using (var builder = new SKPathBuilder())
                {
                    var points = line.Smooth ? Geometry.Flatten(line.Points, true) : line.Points;
                    builder.MoveTo(Point(points[0]));
                    for (var i = 1; i < points.Count; i++) builder.LineTo(Point(points[i]));
                    if (line.Closed) builder.Close();
                    using var path = builder.Detach();
                    if (line.Fill is not null)
                    {
                        using var fill = Paint(line.Fill, 0, true);
                        canvas.DrawPath(path, fill);
                    }
                    if (line.StrokeWidth > 0)
                    {
                        using var stroke = Paint(line.Stroke, line.StrokeWidth);
                        using var dash = line.Dashed ? SKPathEffect.CreateDash([2f, 1.3f], 0) : null;
                        stroke.PathEffect = dash;
                        canvas.DrawPath(path, stroke);
                    }
                }
                break;
            case SceneText text:
                using (var font = new SKFont(text.Bold ? Bold.Value : Regular.Value, (float)text.HeightMm))
                using (var paint = Paint(text.Color, 0, true))
                {
                    font.Edging = SKFontEdging.Antialias;
                    font.Subpixel = true;
                    var measuredWidth = font.MeasureText(text.Text);
                    canvas.Save();
                    canvas.Translate((float)text.Position.X, (float)text.Position.Y);
                    if (Math.Abs(text.RotationDegrees) > .001) canvas.RotateDegrees((float)text.RotationDegrees);
                    if (text.MaxWidthMm is { } maxWidth && measuredWidth > maxWidth && measuredWidth > 0)
                        canvas.Scale((float)(maxWidth / measuredWidth), 1);
                    if (textAsPaths)
                    {
                        using var path = font.GetTextPath(text.Text, new SKPoint(0, 0));
                        canvas.DrawPath(path, paint);
                    }
                    else canvas.DrawText(text.Text, 0, 0, SKTextAlign.Left, font, paint);
                    canvas.Restore();
                }
                break;
            case SceneEllipse ellipse:
                if (ellipse.Fill is not null)
                {
                    using var fill = Paint(ellipse.Fill, 0, true);
                    canvas.DrawOval(Rect(ellipse.Bounds), fill);
                }
                using (var stroke = Paint(ellipse.Stroke, ellipse.StrokeWidth))
                    canvas.DrawOval(Rect(ellipse.Bounds), stroke);
                break;
            case SceneArc arc:
                using (var stroke = Paint(arc.Stroke, arc.StrokeWidth))
                    canvas.DrawArc(Rect(arc.Bounds), (float)arc.StartDegrees, (float)arc.SweepDegrees, false, stroke);
                break;
            case SceneImage image:
                using (var bitmap = DecodeImage(image.Data))
                {
                    if (bitmap is not null)
                    {
                        using var paint = new SKPaint { IsAntialias = true };
                        using var skImage = SKImage.FromBitmap(bitmap);
                        canvas.DrawImage(skImage, Rect(image.Bounds), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                    }
                    else
                    {
                        using var error = Paint("#B55435", .3);
                        canvas.DrawRect(Rect(image.Bounds), error);
                        canvas.DrawLine((float)image.Bounds.Left, (float)image.Bounds.Top, (float)image.Bounds.Right, (float)image.Bounds.Bottom, error);
                    }
                }
                break;
        }
    }

    internal static SKBitmap? DecodeImage(byte[] data)
    {
        using var encoded = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000)
            return null;
        return SKBitmap.Decode(codec);
    }

    private static SKPaint Paint(string color, double width, bool fill = false) => new()
    {
        Color = SKColor.TryParse(color, out var parsed) ? parsed : SKColors.Black,
        StrokeWidth = (float)width,
        Style = fill ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
        StrokeJoin = SKStrokeJoin.Round,
        StrokeCap = SKStrokeCap.Round,
        IsAntialias = true
    };
    private static SKPoint Point(Point2 point) => new((float)point.X, (float)point.Y);
    private static SKRect Rect(SceneBounds b) => new((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom);
}
