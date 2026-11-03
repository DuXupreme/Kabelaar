namespace Kabelaar.Core;

public static partial class ProductionValidator
{
    private static void ValidateExtended(HarnessProject project, List<ValidationIssue> issues)
    {
        void Error(string code, string id, string text, bool structural = true) => issues.Add(new(Severity.Error, code, id, text, structural));
        foreach (var wire in project.Assembly.Conductors)
            if (wire.LengthNeedsConfirmation) Error("wire.lengthUnconfirmed", wire.Id, "Bevestig de kniplengte en nieuwe aansluittoeslagen na de fysieke splitsing.", false);
        foreach (var connector in project.Assembly.Connectors)
        {
            if (connector.MatingPartId is { } part && !project.Parts.Any(p => p.Id == part))
                Error("connector.matingPart", connector.Id, "Het tegenonderdeel bestaat niet.");
            if (connector.MatingConnectorId is { } other && (other == connector.Id || !project.Assembly.Connectors.Any(c => c.Id == other)))
                Error("connector.matingConnector", connector.Id, "De tegenconnector bestaat niet of verwijst naar zichzelf.");
        }
        foreach (var point in project.Assembly.ReferencePoints)
        {
            if (string.IsNullOrWhiteSpace(point.Name)) Error("referencePoint.name", point.Id, "Naam van het referentiepunt ontbreekt.", false);
            if (point.MaxWires < 0) Error("referencePoint.capacity", point.Id, "De capaciteit mag niet negatief zijn.");
            var count = project.Assembly.Conductors.Sum(w => new[] { w.From, w.To }.Count(e => e.Kind == EndpointKind.ReferencePoint && e.EntityId == point.Id));
            if (point.MaxWires > 0 && count > point.MaxWires) Error("referencePoint.overfull", point.Id, "Er zijn meer draadeinden aangesloten dan toegestaan.", false);
        }
        foreach (var group in project.Assembly.ReferencePoints.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var point in group) Error("referencePoint.duplicateName", point.Id, "De naam van het referentiepunt is niet uniek.", false);
        foreach (var sheet in project.Sheets)
        {
            if (sheet.ZoneColumns is < 0 or > 100 || sheet.ZoneRows is < 0 or > 100)
                Error("sheet.zones", sheet.Id, "Bladzones moeten tussen 0 en 100 liggen.");
            if (sheet.TitleBlock.Fields.Count > 100) Error("sheet.titleFields", sheet.Id, "Een titelblok mag maximaal 100 extra velden bevatten.");
            foreach (var view in sheet.Views)
            {
                if (view.DrawOrder.Distinct().Count() != view.DrawOrder.Count)
                    Error("view.drawOrder", view.Id, "Een object komt meermaals in de tekenvolgorde voor.");
                // Deleted identities may remain as ordering tombstones; they are ignored by rendering.
                foreach (var p in view.WirePresentations)
                {
                    if (!double.IsFinite(p.LabelRotationDegrees) || p.LabelPosition is { } position && !Geometry.IsFinite(position))
                        Error("wirePresentation.label", p.ConductorId, "De labelpositie of rotatie is ongeldig.");
                    if (!double.IsFinite(p.CrossingRadiusMm) || p.CrossingRadiusMm <= 0 || p.CrossingRadiusMm > 1000)
                        Error("wirePresentation.crossing", p.ConductorId, "Oversteekradius moet groter dan 0 en maximaal 1000 mm zijn.");
                }
                foreach (var a in view.Annotations)
                {
                    if (!double.IsFinite(a.RotationDegrees) || !double.IsFinite(a.ArrowSizeMm) || a.ArrowSizeMm < 0 || a.ArrowSizeMm > 1000)
                        Error("annotation.arrow", a.Id, "Annotatierotatie of pijlgrootte is ongeldig.");
                    if (!double.IsFinite(a.RowHeightMm) || a.RowHeightMm < 0 || !double.IsFinite(a.CellPaddingMm) || a.CellPaddingMm < 0 || a.CellPaddingMm > 1000)
                        Error("annotation.tableStyle", a.Id, "Rijhoogte of celmarge is ongeldig.");
                    if (a.ColumnWidthsMm.Any(w => !double.IsFinite(w) || w <= 0))
                        Error("annotation.columnWidths", a.Id, "Kolombreedten moeten positief en eindig zijn.");
                    if (a.Kind == AnnotationKind.FreeTable)
                    {
                        var count = a.Columns.Count > 0 ? a.Columns.Count : a.Rows.Select(r => r.Cells.Count).DefaultIfEmpty(0).Max();
                        if (count == 0 || a.Rows.Count == 0) Error("annotation.tableEmpty", a.Id, "Een vrije tabel vereist kolommen en minstens één rij.");
                        if (a.Rows.Any(r => r.Cells.Count != count)) Error("annotation.tableShape", a.Id, "Alle tabelrijen moeten hetzelfde aantal cellen hebben.");
                        if (a.ColumnWidthsMm.Count != 0 && a.ColumnWidthsMm.Count != count || a.ColumnAlignments.Count != 0 && a.ColumnAlignments.Count != count)
                            Error("annotation.tableColumns", a.Id, "Kolomopmaak moet bij het aantal kolommen passen.");
                    }
                    if (a.Kind == AnnotationKind.GraphicalNode && (a.WidthMm <= 0 || a.WidthMm > 1000))
                        Error("annotation.nodeSize", a.Id, "De diameter van een grafische knoop moet groter dan 0 en maximaal 1000 mm zijn.");
                }
            }
        }
    }
}
