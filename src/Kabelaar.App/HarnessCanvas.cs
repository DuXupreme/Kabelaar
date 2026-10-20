using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Kabelaar.Core;
using Kabelaar.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Kabelaar.App;

public enum EditorTool { Select, Connector, Wire, Splice, Text, Dimension, Leader, Line, Arc, Image, WireTable, PinTable, BomTable, RevisionTable }
public sealed record CanvasClick(Point2 SheetPoint, int Clicks, ModifierKeys Modifiers);
public sealed record CanvasDrag(Point2 Start, Point2 End, IReadOnlySet<string> Selection);

public sealed class HarnessCanvas : SKElement
{
    private DrawingScene? _scene;
    private double _zoom = 2;
    private Point _offset = new(30, 25);
    private Point _lastPointer;
    private Point2? _dragStart;
    private Point2? _dragCurrent;
    private bool _pan;
    private bool _marquee;
    private int _vertexIndex = -1;
    private Point2 _mouse;
    private readonly List<double> _frames = [];
    private readonly ScreenSceneCache _screenCache = new();
    public IReadOnlyList<double> FrameMilliseconds => _frames;
    public EditorTool Tool { get; set; }
    public HashSet<string> Selection { get; } = [];
    public List<Point2> PendingPoints { get; } = [];
    public List<Point2> EditableVertices { get; } = [];
    public bool SnapEnabled { get; set; } = true;
    public double GridSize { get; set; } = 5;
    public Func<Point2, Point2>? SnapProvider { get; set; }
    public Point2? SnapPoint { get; private set; }
    public DrawingScene? Scene => _scene;
    public string? ActiveViewId { get; set; }
    public event Action? SelectionChanged;
    public event Action<string>? ViewActivated;
    public event Action<CanvasClick>? CanvasClicked;
    public event Action<CanvasDrag>? DragCompleted;
    public event Action<int, Point2>? VertexDragCompleted;
    public event Action<Point2>? PointerMoved;
    public event Action? FinishDrawing;

    public HarnessCanvas()
    {
        Focusable = true;
        IgnorePixelScaling = false;
        PaintSurface += Paint;
        MouseDown += PointerDown;
        MouseMove += PointerMove;
        MouseUp += PointerUp;
        MouseWheel += Wheel;
        SizeChanged += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => { if (!IsMouseCaptured) { SnapPoint = null; InvalidateVisual(); } };
        LostMouseCapture += (_, _) => { _pan = false; _dragStart = null; _dragCurrent = null; };
        Unloaded += (_, _) => _screenCache.Clear();
    }
    public void SetScene(DrawingScene scene) { _scene = scene; InvalidateVisual(); }
    public Point2 ToSheet(Point p) => new((p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom);
    public Point ToScreen(Point2 p) => new(p.X * _zoom + _offset.X, p.Y * _zoom + _offset.Y);
    public void Fit()
    {
        if (_scene is null || ActualWidth < 1 || ActualHeight < 1) return;
        _zoom = Math.Max(.05, Math.Min((ActualWidth - 64) / _scene.WidthMm, (ActualHeight - 64) / _scene.HeightMm));
        _offset = new((ActualWidth - _scene.WidthMm * _zoom) / 2, (ActualHeight - _scene.HeightMm * _zoom) / 2);
        InvalidateVisual();
    }
    public void ZoomBy(double factor) => ZoomAt(new(ActualWidth / 2, ActualHeight / 2), factor);
    public void PanBy(double dx, double dy)
    {
        _offset = new(_offset.X + dx, _offset.Y + dy);
        InvalidateVisual();
    }
    public void Select(string id, bool append = false)
    {
        if (!append) Selection.Clear();
        Selection.Add(id);
        SelectionChanged?.Invoke(); InvalidateVisual();
    }
    public void ClearSelection()
    {
        Selection.Clear(); SelectionChanged?.Invoke(); InvalidateVisual();
    }
    public void Cancel()
    {
        PendingPoints.Clear(); _dragStart = null; _dragCurrent = null;
        Tool = EditorTool.Select; ReleaseMouseCapture(); InvalidateVisual();
    }
    public Point2 Snap(Point2 p)
    {
        var pin = SnapProvider?.Invoke(p) ?? p;
        if (pin.DistanceTo(p) > 1e-7) return pin;
        return SnapEnabled && GridSize > 0
            ? new(Math.Round(p.X / GridSize) * GridSize, Math.Round(p.Y / GridSize) * GridSize) : p;
    }
    private void ZoomAt(Point location, double factor)
    {
        var world = ToSheet(location);
        _zoom = Math.Clamp(_zoom * factor, .05, 50);
        _offset = new(location.X - world.X * _zoom, location.Y - world.Y * _zoom);
        InvalidateVisual();
    }
    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true;
    }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        { _pan = true; _lastPointer = p; CaptureMouse(); return; }
        if (e.ChangedButton == MouseButton.Right)
        {
            if (Tool != EditorTool.Select) FinishDrawing?.Invoke();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var world = ToSheet(p);
        if (Tool != EditorTool.Select)
        { CanvasClicked?.Invoke(new(Snap(world), e.ClickCount, Keyboard.Modifiers)); e.Handled = true; return; }
        _vertexIndex = EditableVertices.FindIndex(v => v.DistanceTo(world) * _zoom <= 7);
        if (_vertexIndex >= 0)
        {
            _dragStart = EditableVertices[_vertexIndex]; _dragCurrent = _dragStart;
            _marquee = false; CaptureMouse(); return;
        }
        var hit = _scene?.Index.HitTest(world, 5 / _zoom)
            .FirstOrDefault(x => x.Category is not ("frame" or "title-block" or "watermark" or "grid") && !string.IsNullOrEmpty(x.EntityId));
        if (hit is not null)
        {
            if (hit.ViewId is { Length: > 0 } viewId && viewId != ActiveViewId)
                ViewActivated?.Invoke(viewId);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (!Selection.Add(hit.EntityId)) Selection.Remove(hit.EntityId);
            }
            else if (!Selection.Contains(hit.EntityId)) { Selection.Clear(); Selection.Add(hit.EntityId); }
            _marquee = false;
        }
        else
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Selection.Clear();
            _marquee = true;
        }
        _dragStart = world; _dragCurrent = world; CaptureMouse();
        SelectionChanged?.Invoke();
        if (e.ClickCount == 2) CanvasClicked?.Invoke(new(world, 2, Keyboard.Modifiers));
        InvalidateVisual();
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_pan)
        {
            _offset = new(_offset.X + p.X - _lastPointer.X, _offset.Y + p.Y - _lastPointer.Y);
            _lastPointer = p; InvalidateVisual(); return;
        }
        _mouse = ToSheet(p);
        PointerMoved?.Invoke(_mouse);
        SnapPoint = Tool == EditorTool.Select ? null : Snap(_mouse);
        if (_dragStart.HasValue) _dragCurrent = _marquee ? _mouse : _dragStart.Value + (Snap(_mouse) - Snap(_dragStart.Value));
        if (Tool != EditorTool.Select || _dragStart.HasValue) InvalidateVisual();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_pan) { _pan = false; ReleaseMouseCapture(); return; }
        if (e.ChangedButton != MouseButton.Left || !_dragStart.HasValue) return;
        var start = _dragStart.Value; var end = _dragCurrent ?? start;
        var vertex = _vertexIndex; _vertexIndex = -1;
        var marquee = _marquee; _dragStart = null; _dragCurrent = null; ReleaseMouseCapture();
        if (vertex >= 0 && start.DistanceTo(end) * _zoom > 2)
            VertexDragCompleted?.Invoke(vertex, Snap(end));
        else if (marquee && start.DistanceTo(end) * _zoom > 4)
        {
            var b = SceneBounds.Around([start, end]);
            if (_scene is not null)
                foreach (var item in _scene.Index.Query(b).Where(x => !x.Locked && x.ViewId == ActiveViewId &&
                    x.Category is not ("frame" or "title-block" or "watermark" or "grid")))
                    if (!string.IsNullOrWhiteSpace(item.EntityId)) Selection.Add(item.EntityId);
            SelectionChanged?.Invoke();
        }
        else if (!marquee && start.DistanceTo(end) * _zoom > 3)
            DragCompleted?.Invoke(new(start, end, new HashSet<string>(Selection)));
        InvalidateVisual();
    }
    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var watch = Stopwatch.StartNew();
        var c = e.Surface.Canvas;
        c.Clear(SKColor.Parse("#DCE4EB"));
        if (_scene is null || ActualWidth <= 0) return;
        var dpi = e.Info.Width / ActualWidth;
        c.Scale((float)dpi);
        c.Translate((float)_offset.X, (float)_offset.Y);
        c.Scale((float)_zoom);
        using var paper = new SKPaint { Color = SKColors.White, IsAntialias = false };
        using var shadow = new SKPaint { Color = new(75, 91, 106, 35) };
        c.DrawRect(2, 3, (float)_scene.WidthMm, (float)_scene.HeightMm, shadow);
        c.DrawRect(0, 0, (float)_scene.WidthMm, (float)_scene.HeightMm, paper);
        var viewport = new SceneBounds(-_offset.X / _zoom, -_offset.Y / _zoom, ActualWidth / _zoom, ActualHeight / _zoom);
        if (SnapEnabled && GridSize * _zoom > 8)
        {
            using var grid = new SKPaint { Color = SKColor.Parse("#DFE6EC"), StrokeWidth = (float)(1 / _zoom) };
            var l = Math.Max(GridSize, Math.Ceiling(viewport.Left / GridSize) * GridSize);
            var t = Math.Max(GridSize, Math.Ceiling(viewport.Top / GridSize) * GridSize);
            for (var x = l; x < Math.Min(_scene.WidthMm, viewport.Right); x += GridSize)
                for (var y = t; y < Math.Min(_scene.HeightMm, viewport.Bottom); y += GridSize)
                    c.DrawPoint((float)x, (float)y, grid);
        }
        SkiaSceneRenderer.RenderScreen(c, _scene, viewport, _screenCache, Selection);
        using var accent = new SKPaint { Color = SKColor.Parse("#087F8C"), StrokeWidth = (float)(1.5 / _zoom), IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var vertex in EditableVertices)
            c.DrawRect((float)(vertex.X - 3 / _zoom), (float)(vertex.Y - 3 / _zoom), (float)(6 / _zoom), (float)(6 / _zoom), accent);
        if (_dragStart.HasValue && _dragCurrent.HasValue)
        {
            if (_marquee)
            {
                var b = SceneBounds.Around([_dragStart.Value, _dragCurrent.Value]);
                c.DrawRect((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height, accent);
            }
            else
            {
                var delta = _dragCurrent.Value - _dragStart.Value;
                foreach (var id in Selection)
                {
                    var primitives = _scene.Primitives.Where(x => x.EntityId == id).ToArray();
                    if (primitives.Length == 0) continue;
                    var b = SceneBounds.Around(primitives.SelectMany(x => new[] { new Point2(x.Bounds.Left, x.Bounds.Top), new Point2(x.Bounds.Right, x.Bounds.Bottom) }));
                    c.DrawRect((float)(b.X + delta.X), (float)(b.Y + delta.Y), (float)b.Width, (float)b.Height, accent);
                }
            }
        }
        if (PendingPoints.Count > 0)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo((float)PendingPoints[0].X, (float)PendingPoints[0].Y);
            foreach (var p in PendingPoints.Skip(1)) builder.LineTo((float)p.X, (float)p.Y);
            var next = SnapPoint ?? _mouse;
            builder.LineTo((float)next.X, (float)next.Y);
            using var path = builder.Detach();
            c.DrawPath(path, accent);
        }
        if (SnapPoint is { } snap)
            c.DrawRect((float)(snap.X - 3 / _zoom), (float)(snap.Y - 3 / _zoom), (float)(6 / _zoom), (float)(6 / _zoom), accent);
        watch.Stop();
        if (_frames.Count >= 3000) _frames.RemoveRange(0, 1000);
        _frames.Add(watch.Elapsed.TotalMilliseconds);
    }
}
