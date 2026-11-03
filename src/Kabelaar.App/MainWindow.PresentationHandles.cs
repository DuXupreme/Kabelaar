using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    private sealed class PresentationDrag
    {
        public required CanvasGeometryHandle Handle;
        public required Guid State;
        public required Point2 Start;
        public required IReadOnlyList<ScenePrimitive> Source;
        public Annotation? Annotation;
        public PlacedComponent? Component;
        public Conductor? Wire;
        public IEditCommand? Command;
        public string? Error;
    }
    private PresentationDrag? _presentationDrag;

    private void RefreshPresentationHandles()
    {
        if (Canvas.Tool != EditorTool.Select || Canvas.Selection.Count != 1 || Canvas.Scene is null) return;
        var id = SelectedId!;
        var sources = Canvas.Scene.Primitives.Where(p => p.EntityId == id && p.ViewId == _view.Id).ToArray();
        if (sources.Any(p => p.Locked)) return;
        var label = sources.OfType<SceneText>().FirstOrDefault(p => p.Category is "connector-label" or "splice-label" or "reference-label" or "wire-label");
        if (label is not null) Canvas.GeometryHandles.Add(new(id, "presentation", "label", GeometryHandleKind.Label, label.Position, label.Position));
        var annotation = _view.Annotations.Find(a => a.Id == id);
        if (annotation is null || annotation.Locked || _sheet.Layers.Find(l => l.Id == annotation.LayerId)?.Locked == true) return;
        if (annotation.Kind is AnnotationKind.Leader or AnnotationKind.Line or AnnotationKind.Dimension or AnnotationKind.Arc)
            for (var i = 0; i < annotation.Points.Count; i++)
                Canvas.GeometryHandles.Add(new(id, "presentation", i.ToString(), GeometryHandleKind.AnnotationPoint, ToSheet(annotation.Points[i]), ToSheet(annotation.Points[i])));
        if (IsTable(annotation) && annotation.Points.Count > 0)
        {
            var rows = annotation.Kind == AnnotationKind.FreeTable ? annotation.Rows.Count : SceneBuilder.TableData(_project, annotation.Kind, annotation.EntityId).Rows.Count;
            var columns = annotation.Columns.Count > 0 ? annotation.Columns.Count : SceneBuilder.TableData(_project, annotation.Kind, annotation.EntityId).Columns.Count;
            var height = SceneBuilder.TableHeight(_project, annotation);
            Point2 At(Point2 p) => ToSheet(Geometry.Transform(p, annotation.RotationDegrees, annotation.Points[0]));
            var corner = At(new(annotation.WidthMm, height));
            Canvas.GeometryHandles.Add(new(id, "presentation", "corner", GeometryHandleKind.TableCorner, corner, corner));
            var widths = SceneBuilder.TableColumnWidths(_project, annotation); var x = 0.0;
            for (var i = 0; i < columns - 1; i++)
            {
                x += widths[i]; var at = At(new(x, height / 2));
                Canvas.GeometryHandles.Add(new(id, "presentation", i.ToString(), GeometryHandleKind.TableColumn, at, at));
            }
        }
    }
    private static double[] TableWidths(Annotation annotation, int count)
    {
        if (count <= 0) return [];
        if (annotation.ColumnWidthsMm.Count != count) return Enumerable.Repeat(annotation.WidthMm / count, count).ToArray();
        var sum = annotation.ColumnWidthsMm.Sum();
        return annotation.ColumnWidthsMm.Select(w => w * annotation.WidthMm / sum).ToArray();
    }

    private bool TryBeginPresentationEdit(CanvasEditGesture gesture)
    {
        if (gesture.Handle is not { RouteId: "presentation" } handle) return false;
        RequireEditableSelection(); var id = handle.WireId!;
        var session = new PresentationDrag { Handle = handle, State = _history.StateToken, Start = gesture.Start,
            Annotation = _view.Annotations.Find(a => a.Id == id),
            Component = _view.Components.Find(p => p.EntityId == id), Wire = _project.Assembly.Conductors.Find(w => w.Id == id),
            Source = Canvas.Scene?.Primitives.Where(p => p.ViewId == _view.Id && p.EntityId == id).ToArray() ?? [] };
        _presentationDrag = session; Canvas.BeginPreview(new HashSet<string> { id });
        UpdatePresentationEdit(gesture); return true;
    }

    private void UpdatePresentationEdit(CanvasEditGesture gesture)
    {
        var session = _presentationDrag; if (session is null) return;
        if (session.State != _history.StateToken) { CancelGeometryEdit(); Status("De tekening veranderde tijdens slepen."); return; }
        var delta = ToModel(gesture.Current) - ToModel(session.Start); var sheetDelta = gesture.Current - session.Start;
        session.Error = null; session.Command = null;
        try
        {
            IReadOnlyList<ScenePrimitive> preview;
            if (session.Handle.Kind == GeometryHandleKind.Label)
            {
                if (session.Component is { } placement)
                {
                    var before = placement.LabelOffset; var after = before + delta;
                    session.Command = new DelegateCommand("Label verplaatsen", () => placement.LabelOffset = after, () => placement.LabelOffset = before);
                }
                else if (session.Wire is { } wire)
                {
                    var style = WireStyle(wire); style.LabelPosition = ToModel(session.Handle.Position) + delta;
                    session.Command = WirePresentationCommand(wire, style, "Draadlabel verplaatsen");
                }
                var previewStyle = session.Wire is null ? null : WireStyle(session.Wire);
                if (previewStyle is not null) previewStyle.LabelPosition = ToModel(session.Handle.Position) + delta;
                preview = session.Wire is { } previewWire ? SceneBuilder.WireLabelPreview(_project, _sheet, _view, previewWire,
                    previewStyle!) : session.Source.Select(p => p is SceneText && p.Category is "connector-label" or "part-number" or "splice-label" or "reference-label" or "wire-label"
                    ? TranslatePrimitive(p, sheetDelta) : p is ScenePath { Category: "wire-label-leader" } leader
                    ? new ScenePath(leader.EntityId, leader.Category, [leader.Points[0], leader.Points[^1] + sheetDelta], leader.Stroke, leader.StrokeWidth)
                        { ViewId = leader.ViewId, PlacementId = leader.PlacementId, LayerId = leader.LayerId, Locked = leader.Locked } : p).ToArray();
            }
            else if (session.Annotation is { } annotation)
            {
                var next = EditData.Clone(annotation);
                if (session.Handle.Kind == GeometryHandleKind.AnnotationPoint)
                    next.Points[int.Parse(session.Handle.KnotId)] += delta;
                else
                {
                    var local = Geometry.Transform(ToModel(gesture.Current) - annotation.Points[0], -annotation.RotationDegrees, new());
                    if (session.Handle.Kind == GeometryHandleKind.TableCorner)
                    {
                        var rows = annotation.Kind == AnnotationKind.FreeTable ? annotation.Rows.Count : SceneBuilder.TableData(_project, annotation.Kind, annotation.EntityId).Rows.Count;
                        next.WidthMm = Math.Max(1, local.X);
                        next.RowHeightMm = Math.Max(.5, local.Y / Math.Max(1, rows + (annotation.ShowTableHeader ? 1 : 0)));
                    }
                    else
                    {
                        var count = annotation.Columns.Count > 0 ? annotation.Columns.Count : SceneBuilder.TableData(_project, annotation.Kind, annotation.EntityId).Columns.Count;
                        var widths = SceneBuilder.TableColumnWidths(_project, annotation).ToArray(); var index = int.Parse(session.Handle.KnotId);
                        var start = widths.Take(index).Sum(); var total = widths[index] + widths[index + 1];
                        var minimum = Math.Min(.1, total / 2);
                        widths[index] = Math.Clamp(local.X - start, minimum, total - minimum); widths[index + 1] = total - widths[index];
                        next.ColumnWidthsMm = widths.ToList();
                    }
                }
                session.Command = PropertyReplace(_view.Annotations, annotation, next, "Annotatiehandvat verplaatsen");
                preview = SceneBuilder.AnnotationPreview(_project, _sheet, _view, next);
            }
            else return;
            Canvas.UpdatePreview(preview, false);
            Canvas.PreviewHandles = [session.Handle with { Position = gesture.Current }];
        }
        catch (Exception ex) { session.Error = ex.Message; Canvas.UpdatePreview(session.Source, true); }
    }

    private void CommitPresentationEdit(CanvasEditGesture gesture)
    {
        UpdatePresentationEdit(gesture); var command = _presentationDrag?.Command; var error = _presentationDrag?.Error;
        CancelGeometryEdit(); if (error is not null) Status(error); else if (command is not null) Execute(command);
    }
}
