using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    private static IEnumerable<ScenePath> GroundPaths(string id, string category, Func<Point2, Point2> project,
        double radius, string color, double lineWidth)
    {
        yield return new(id, category, [project(new()), project(new(0, radius * 2))], color, lineWidth);
        for (var row = 0; row < 3; row++)
        {
            var half = radius * (1 - row * .3); var y = radius * (2 + row * .6);
            yield return new(id, category, [project(new(-half, y)), project(new(half, y))], color, lineWidth);
        }
    }

    /// <summary>Actual unrotated table height in view millimetres, including wrapped rows.</summary>
    public static double TableHeight(HarnessProject project, Annotation annotation) =>
        Table(project, annotation, new(), annotation.FontSizeMm, 1).OfType<ScenePath>()
            .Where(p => p.Category == "table-grid").SelectMany(p => p.Points).Select(p => p.Y).DefaultIfEmpty(0).Max();

    /// <summary>Column widths in view millimetres; the total WidthMm controls outer resizing.</summary>
    public static IReadOnlyList<double> TableColumnWidths(HarnessProject project, Annotation annotation)
    {
        var columns = annotation.Columns.Count > 0 ? annotation.Columns : TableData(project, annotation.Kind, annotation.EntityId).Columns;
        if (annotation.Kind == AnnotationKind.FreeTable && columns.Count == 0)
            columns = Enumerable.Range(1, annotation.Rows.Select(r => r.Cells.Count).DefaultIfEmpty(0).Max()).Select(i => $"Kolom {i}").ToList();
        return TableWidths(annotation, columns);
    }

    private static double[] TableWidths(Annotation annotation, IReadOnlyList<string> columns)
    {
        static double Weight(string key) => key switch
        {
            "Description" or "Connections" or "References" => 3,
            "PartNumber" or "LengthSource" or "Terminal" or "Seal" or "Plug" => 2,
            "Quantity" or "Unit" or "Pin" or "CrossSectionMm2" => .8,
            _ => 1.3
        };
        if (columns.Count == 0) return [];
        var width = Math.Max(1, annotation.WidthMm);
        var weights = columns.Select(Weight).ToArray(); var totalWeight = weights.Sum();
        var widths = columns.Select((_, i) => i < annotation.ColumnWidthsMm.Count && annotation.ColumnWidthsMm[i] > 0
            ? annotation.ColumnWidthsMm[i] : width * weights[i] / totalWeight).ToArray();
        var total = widths.Sum();
        return widths.Select(w => width * w / total).ToArray();
    }

    private static ScenePath TextFrame(Annotation annotation, SceneText text, double scale)
    {
        var padding = Math.Max(.5, annotation.CellPaddingMm) * scale;
        var width = Math.Max(text.HeightMm, text.Text.Length * text.HeightMm);
        var local = new[] { new Point2(-padding, -text.HeightMm * 1.15 - padding),
            new Point2(width + padding, -text.HeightMm * 1.15 - padding),
            new Point2(width + padding, text.HeightMm * .4 + padding), new Point2(-padding, text.HeightMm * .4 + padding) };
        return new(annotation.Id, "text-frame", local.Select(p => Geometry.Transform(p, text.RotationDegrees, text.Position)).ToArray(),
            annotation.Color, annotation.LineWidthMm * scale, annotation.FillColor, true);
    }

    private static ScenePrimitive RotateTablePrimitive(ScenePrimitive primitive, Point2 origin, double degrees)
    {
        if (Math.Abs(degrees) < 1e-12) return primitive;
        Point2 Rotate(Point2 p) => Geometry.Transform(p - origin, degrees, origin);
        return primitive switch
        {
            ScenePath p => new ScenePath(p.EntityId, p.Category, p.Points.Select(Rotate).ToArray(), p.Stroke,
                p.StrokeWidth, p.Fill, p.Closed, p.Smooth, p.Dashed),
            SceneText t => new SceneText(t.EntityId, t.Category, t.Text, Rotate(t.Position), t.HeightMm,
                t.Color, t.Bold, t.RotationDegrees + degrees, t.MaxWidthMm, t.Alignment),
            _ => primitive
        };
    }

    private static void AddZones(Sheet sheet, List<ScenePrimitive> items)
    {
        var m = sheet.MarginMm; var right = sheet.WidthMm - m; var bottom = sheet.HeightMm - m;
        var labelHeight = Math.Min(2.5, Math.Max(1, m * .35));
        void Line(Point2 a, Point2 b) => items.Add(new ScenePath(sheet.Id, "sheet-zone", [a, b], Ink, .2) { Locked = true });
        void Text(string value, Point2 p) => items.Add(new SceneText(sheet.Id, "sheet-zone-label", value, p, labelHeight) { Locked = true });
        if (sheet.ZoneColumns > 0)
        {
            var size = (right - m) / sheet.ZoneColumns;
            for (var column = 0; column < sheet.ZoneColumns; column++)
            {
                var x = m + (column + .5) * size;
                Text((column + 1).ToString(), new(x, m - labelHeight));
                Text((column + 1).ToString(), new(x, bottom + labelHeight * 1.7));
                if (column > 0) { Line(new(m + column * size, m), new(m + column * size, m - 2)); Line(new(m + column * size, bottom), new(m + column * size, bottom + 2)); }
            }
        }
        if (sheet.ZoneRows > 0)
        {
            var size = (bottom - m) / sheet.ZoneRows;
            for (var row = 0; row < sheet.ZoneRows; row++)
            {
                var y = m + (row + .5) * size;
                var label = ZoneLetters(row);
                Text(label, new(Math.Max(.5, m - labelHeight * 1.8), y)); Text(label, new(right + labelHeight * .6, y));
                if (row > 0) { Line(new(m, m + row * size), new(m - 2, m + row * size)); Line(new(right, m + row * size), new(right + 2, m + row * size)); }
            }
        }
    }

    private static string ZoneLetters(int zeroBased)
    {
        var result = "";
        do { result = (char)('A' + zeroBased % 26) + result; zeroBased = zeroBased / 26 - 1; } while (zeroBased >= 0);
        return result;
    }

    private static void AddExtendedTitleBlock(HarnessProject project, Sheet sheet, List<ScenePrimitive> items)
    {
        var block = sheet.TitleBlock; var right = sheet.WidthMm - sheet.MarginMm; var bottom = sheet.HeightMm - sheet.MarginMm;
        var width = Math.Min(190, right - sheet.MarginMm); var left = right - width;
        var extra = block.Fields.Count; var top = bottom - 53 - extra * 6;
        void Path(Point2[] points, double stroke = .25) => items.Add(new ScenePath(sheet.Id, "sheet-frame", points, Ink, stroke) { Locked = true });
        void Text(string value, double x, double y, double height = 2.5, bool bold = false, double? maxWidth = null) =>
            items.Add(new SceneText(sheet.Id, "title-block", value, new(x, y), height, Ink, bold, MaxWidthMm: maxWidth) { Locked = true });
        Path([new(left, bottom), new(left, top), new(right, top)]);
        Text(project.Title, left + 3, top + 5, 3.5, true, width - 40);
        Text(project.Customer, left + 3, top + 10, 2.4, maxWidth: width - 40);
        Path([new(left, top + 13), new(right, top + 13)]);
        Text(project.DrawingNumber, left + 3, top + 19, 4, true, width - 40);
        Text($"REV {project.Revision}", right - 32, top + 19, 3, true);
        Path([new(left, top + 22), new(right, top + 22)]);
        var third = width / 3;
        var names = new[] { "Getekend", "Gecontroleerd", "Goedgekeurd" };
        var people = new[] { string.IsNullOrEmpty(block.DrawnBy) ? project.Author : block.DrawnBy, block.CheckedBy, block.ApprovedBy };
        var dates = new[] { block.Date, block.CheckedDate, block.ApprovedDate };
        for (var column = 0; column < 3; column++)
        {
            var x = left + column * third;
            if (column > 0) Path([new(x, top + 22), new(x, top + 36)]);
            Text(names[column], x + 2, top + 26, 2);
            Text(people[column], x + 2, top + 30, 2.5, maxWidth: third - 4);
            Text(dates[column], x + 2, top + 34, 2.2, maxWidth: third - 4);
        }
        Path([new(left, top + 36), new(right, top + 36)]);
        Text($"Algemene toleranties: {block.GeneralTolerances}", left + 3, top + 40, 2.4, maxWidth: width - 6);
        Text($"Materiaal: {block.Material}   Status: {block.DocumentStatus}", left + 3, top + 45, 2.4, maxWidth: width - 6);
        for (var i = 0; i < extra; i++)
            Text($"{block.Fields[i].Name}: {block.Fields[i].Value}", left + 3, top + 50 + i * 6, 2.4, maxWidth: width - 6);
        Path([new(left, bottom - 6), new(right, bottom - 6)]);
        Text($"{sheet.Name}  ·  {project.Sheets.IndexOf(sheet) + 1}/{project.Sheets.Count}", left + 3, bottom - 2, 2.3, maxWidth: width - 38);
        Text(sheet.Views.Any(v => v.Mode == LayoutMode.Free) ? "VRIJ" : ScaleLabel(sheet.PrintScale), right - 30, bottom - 2);
        var asset = project.Assets.Find(a => a.Id == sheet.LogoAssetId);
        if (asset is { Data.Length: > 0 })
            items.Add(new SceneImage(asset.Id, "logo", asset.Data, new(right - 35, top + 1, 32, 11)) { Locked = true });
        if (!string.IsNullOrWhiteSpace(project.Notes)) Text(project.Notes, sheet.MarginMm + 3, top - 5, 2.5, maxWidth: sheet.WidthMm - sheet.MarginMm * 2 - 6);
    }
}
