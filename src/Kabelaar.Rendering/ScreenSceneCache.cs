using Kabelaar.Core;
using SkiaSharp;

namespace Kabelaar.Rendering;

/// <summary>A canvas owns this bounded cache. Scene changes redraw only changed regions; eviction frees native images.</summary>
public sealed class ScreenSceneCache : IDisposable
{
    internal readonly record struct Key(int Level, int X, int Y);
    private sealed class Entry(SKImage image, long bytes)
    {
        public SKImage Image = image;
        public long Bytes = bytes;
        public long LastUse;
        public SceneBounds? Dirty;
    }
    private const long MaxBytes = 64L * 1024 * 1024;
    private readonly Dictionary<Key, Entry> _images = [];
    private DrawingScene? _scene;
    private long _clock;
    public long CachedBytes { get; private set; }
    public int ImageCount => _images.Count;
    internal SceneBounds ContentBounds { get; private set; }

    internal void UseScene(DrawingScene scene)
    {
        if (ReferenceEquals(scene, _scene)) return;
        var bounds = scene.ContentBounds.Inflate(3);
        if (_scene is null || bounds != ContentBounds || _scene.Primitives.Count != scene.Primitives.Count)
            Clear();
        else
        {
            SceneBounds? dirty = null;
            for (var i = 0; i < scene.Primitives.Count; i++)
            {
                var before = _scene.Primitives[i]; var after = scene.Primitives[i];
                if (Equivalent(before, after)) continue;
                dirty = Union(dirty, before.Bounds.Inflate(1));
                dirty = Union(dirty, after.Bounds.Inflate(1));
            }
            if (dirty is { } changed)
                foreach (var entry in _images.Values) entry.Dirty = Union(entry.Dirty, changed);
        }
        _scene = scene;
        ContentBounds = bounds;
    }

    internal SKImage GetOrCreate(Key key, Func<SKImage> create, Func<SKImage, SceneBounds, SKImage> repaint)
    {
        if (_images.TryGetValue(key, out var cached))
        {
            cached.LastUse = ++_clock;
            if (cached.Dirty is { } dirty)
            {
                var old = cached.Image;
                cached.Image = repaint(old, dirty);
                if (!ReferenceEquals(old, cached.Image)) old.Dispose();
                cached.Dirty = null;
            }
            return cached.Image;
        }
        var image = create();
        var bytes = (long)image.Width * image.Height * 4;
        while (_images.Count > 0 && CachedBytes + bytes > MaxBytes)
        {
            var oldest = _images.MinBy(p => p.Value.LastUse);
            _images.Remove(oldest.Key);
            CachedBytes -= oldest.Value.Bytes;
            oldest.Value.Image.Dispose();
        }
        _images.Add(key, new(image, bytes) { LastUse = ++_clock });
        CachedBytes += bytes;
        return image;
    }

    private static bool Equivalent(ScenePrimitive before, ScenePrimitive after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before is ScenePath a && after is ScenePath b)
            return a.Points.SequenceEqual(b.Points) && a with { Points = b.Points } == b;
        return before == after;
    }

    private static SceneBounds Union(SceneBounds? a, SceneBounds b)
    {
        if (a is not { } bounds) return b;
        var left = Math.Min(bounds.Left, b.Left); var top = Math.Min(bounds.Top, b.Top);
        return new(left, top, Math.Max(bounds.Right, b.Right) - left, Math.Max(bounds.Bottom, b.Bottom) - top);
    }

    public void Clear()
    {
        foreach (var image in _images.Values) image.Image.Dispose();
        _images.Clear();
        _scene = null;
        CachedBytes = 0;
    }

    public void Dispose() => Clear();
}
