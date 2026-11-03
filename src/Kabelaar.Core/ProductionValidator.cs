using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Kabelaar.Core;

public enum Severity { Error, Warning }
public sealed record ValidationIssue(Severity Severity, string Code, string EntityId, string Message, bool IsStructural = false);

public static class StructuralValidator
{
    public static IReadOnlyList<string> Validate(HarnessProject project) => ProductionValidator.Validate(project)
        .Where(i => i.IsStructural).Select(i => $"{i.Code}: {i.Message}").ToArray();
}

public static partial class ProductionValidator
{
    private sealed record ShapeProperty(Func<object, object?> Get, bool Required);
    private sealed record ShapeType(Func<object, object?>? Id, ShapeProperty[] Properties);
    private static readonly ConcurrentDictionary<Type, ShapeType> ShapeTypes = new();
    public static IReadOnlyList<ValidationIssue> Validate(HarnessProject project)
    {
        var issues = new List<ValidationIssue>();
        void Error(string code, string id, string text, bool structural = false) => issues.Add(new(Severity.Error, code, id, text, structural));
        void Warn(string code, string id, string text) => issues.Add(new(Severity.Warning, code, id, text));
        if (project is null) return [new(Severity.Error, "project.missing", "", "Project ontbreekt.", true)];
        ValidateShape(project, issues);
        if (issues.Any(i => i.Code is "data.null" or "data.depth")) return issues;
        if (project.SchemaVersion != HarnessProject.CurrentSchemaVersion)
            Error("schema.version", project.Id, "Deze projectversie wordt niet ondersteund.", true);

        var assembly = project.Assembly;
        var parts = project.Parts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var connectors = assembly.Connectors.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var branches = assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var cables = assembly.Cables.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var wireIds = assembly.Conductors.Select(w => w.Id).ToHashSet();
        var spliceIds = assembly.Splices.Select(s => s.Id).ToHashSet();
        var referenceIds = assembly.ReferencePoints.Select(s => s.Id).ToHashSet();
        var assetIds = project.Assets.Select(a => a.Id).ToHashSet();
        var usedParts = new HashSet<string>();

        PartDefinition? Part(string? partId, string owner, PartKind? expected = null, bool required = false)
        {
            if (string.IsNullOrWhiteSpace(partId))
            {
                if (required) Error("part.required", owner, "Een vereist onderdeel is nog niet gekozen.");
                return null;
            }
            usedParts.Add(partId);
            if (!parts.TryGetValue(partId, out var part)) { Error("part.unknown", owner, "Verwijzing naar een onbekend onderdeel.", true); return null; }
            if (expected.HasValue && part.Kind != expected) Error("part.kind", owner, $"Onderdeel {part.PartNumber} heeft niet het vereiste type {expected}.", true);
            return part;
        }
        void Material(MaterialItem item)
        {
            Part(item.PartId, item.Id, required: true);
            if (item.Quantity <= 0 || string.IsNullOrWhiteSpace(item.Unit)) Error("material.quantity", item.Id, "Materiaal vereist een positieve hoeveelheid en een eenheid.");
            if (item.BranchId is { } id && !branches.ContainsKey(id)) Error("material.branch", item.Id, "Materiaal verwijst naar een onbekende tak.", true);
        }
        if (string.IsNullOrWhiteSpace(project.Title)) Error("project.title", project.Id, "Een projecttitel ontbreekt.");
        if (string.IsNullOrWhiteSpace(project.DrawingNumber)) Error("project.number", project.Id, "Een tekeningnummer ontbreekt.");
        if (string.IsNullOrWhiteSpace(project.Revision)) Error("project.revision", project.Id, "Een revisie ontbreekt.");
        if (project.Sheets.Count == 0) Error("sheet.required", project.Id, "Het project heeft geen tekenblad.", true);
        if (assembly.Conductors.Count == 0) Warn("assembly.empty", project.Id, "Er zijn nog geen aders vastgelegd.");

        var cavityUsage = new Dictionary<(string, string), List<Conductor>>();
        void Endpoint(Conductor wire, Endpoint end)
        {
            switch (end.Kind)
            {
                case EndpointKind.Free:
                    if (end.EntityId is not null || end.CavityId is not null) Error("endpoint.freeReference", wire.Id, "Een vrij uiteinde bevat nog een aansluitverwijzing.", true);
                    if (string.IsNullOrWhiteSpace(end.Description)) Error("endpoint.description", wire.Id, "Beschrijf het vrije draadeinde.");
                    break;
                case EndpointKind.Splice:
                    if (end.EntityId is null || !spliceIds.Contains(end.EntityId)) Error("endpoint.splice", wire.Id, "Draad verwijst naar een onbekende splice.", true);
                    if (end.CavityId is not null) Error("endpoint.spliceCavity", wire.Id, "Een splice-uiteinde heeft een ongeldige pinverwijzing.", true);
                    break;
                case EndpointKind.ReferencePoint:
                    if (end.EntityId is null || !referenceIds.Contains(end.EntityId)) Error("endpoint.referencePoint", wire.Id, "Draad verwijst naar een onbekend referentiepunt.", true);
                    if (end.CavityId is not null) Error("endpoint.referenceCavity", wire.Id, "Een referentiepunt heeft geen connectorpin.", true);
                    break;
                case EndpointKind.Cavity:
                    if (end.EntityId is null || !connectors.TryGetValue(end.EntityId, out var connector) ||
                        end.CavityId is null || !connector.Cavities.Any(c => c.Id == end.CavityId))
                        Error("endpoint.cavity", wire.Id, "Draad verwijst naar een onbekende connectorpin.", true);
                    else
                    {
                        var key = (end.EntityId, end.CavityId);
                        if (!cavityUsage.TryGetValue(key, out var uses)) cavityUsage[key] = uses = [];
                        uses.Add(wire);
                    }
                    break;
            }
        }

        foreach (var wire in assembly.Conductors)
        {
            Endpoint(wire, wire.From); Endpoint(wire, wire.To);
            if (wire.From.Kind != EndpointKind.Free && wire.From.Kind == wire.To.Kind &&
                wire.From.EntityId == wire.To.EntityId && wire.From.CavityId == wire.To.CavityId)
                Error("wire.sameEndpoint", wire.Id, "Een draad begint en eindigt op dezelfde aansluiting.");
            if (string.IsNullOrWhiteSpace(wire.Name)) Error("wire.name", wire.Id, "Draadnummer ontbreekt.");
            if (string.IsNullOrWhiteSpace(wire.ColorCode)) Error("wire.color", wire.Id, "Fysieke draadkleur ontbreekt.");
            if (wire.CrossSectionMm2 <= 0) Error("wire.section", wire.Id, "Draaddoorsnede moet groter zijn dan nul.");
            if (wire.StartAllowanceMm < 0 || wire.EndAllowanceMm < 0 || wire.ProcessingAllowanceMm < 0)
                Error("wire.allowance", wire.Id, "Aansluit- en verwerkingstoeslagen mogen niet negatief zijn.");
            if (wire.CableId is { } cableId && !cables.ContainsKey(cableId)) Error("wire.cable", wire.Id, "Draad verwijst naar een onbekende kabel.", true);
            Part(wire.WirePartId, wire.Id, PartKind.Wire, wire.CableId is null);
            if (wire.BranchIds.Count != wire.BranchIds.Distinct().Count()) Error("wire.repeatedBranch", wire.Id, "Een route bevat dezelfde tak meermaals.", true);
            foreach (var branchId in wire.BranchIds)
                if (!branches.ContainsKey(branchId)) Error("wire.branch", wire.Id, "Draad verwijst naar een onbekende tak.", true);
            if (wire.BranchIds.Count > 1 && wire.BranchIds.All(branches.ContainsKey) && !HarnessRouteOperations.IsContinuous(branches, wire.BranchIds))
                Error("wire.routeContinuity", wire.Id, "De fysieke routetakken van de ader sluiten niet in de opgegeven volgorde op elkaar aan.");
            if (wire.LengthSource == LengthSource.RouteAndAllowances && wire.BranchIds.Count == 0)
                Error("wire.routeRequired", wire.Id, "Een berekende kniplengte vereist een route.");
            var cut = wire.LengthSource == LengthSource.Manual ? wire.ManualCutLengthMm :
                wire.BranchIds.Sum(id => branches.GetValueOrDefault(id)?.PhysicalLengthMm ?? 0) +
                wire.StartAllowanceMm + wire.EndAllowanceMm + wire.ProcessingAllowanceMm;
            if (cut <= 0) Error("wire.length", wire.Id, "Kniplengte moet groter zijn dan nul.");
        }
        foreach (var duplicate in assembly.Conductors.GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var wire in duplicate) Error("wire.duplicateName", wire.Id, $"Draadnummer {wire.Name} is niet uniek.");
        foreach (var duplicate in assembly.Connectors.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var connector in duplicate) Error("connector.duplicateName", connector.Id, $"Connectornaam {connector.Name} is niet uniek.");

        foreach (var connector in assembly.Connectors)
        {
            var housing = Part(connector.PartId, connector.Id, required: true);
            if (housing is not null && housing.Kind is not (PartKind.Housing or PartKind.RingTerminal))
                Error("part.kind", connector.Id, "Een connectorpositie vereist een connectorhuis of ringterminal.", true);
            if (string.IsNullOrWhiteSpace(connector.Name)) Error("connector.name", connector.Id, "Connectornaam ontbreekt.");
            foreach (var cavity in connector.Cavities)
            {
                var pin = housing?.Pins.Find(p => p.Id == cavity.PinDefinitionId);
                if (housing is not null && pin is null) Error("cavity.definition", cavity.Id, "Connectorpin ontbreekt in de onderdeeldefinitie.", true);
                cavityUsage.TryGetValue((connector.Id, cavity.Id), out var wires);
                var count = wires?.Count ?? 0;
                if (cavity.MaxWires < 1 || (pin is not null && cavity.MaxWires > pin.MaxWires))
                    Error("cavity.capacity", cavity.Id, "Het ingestelde aantal draden past niet bij de pin.");
                if (count > cavity.MaxWires) Error("cavity.overfull", cavity.Id, $"Pin {connector.Name}:{cavity.Label} bevat te veel draadeinden.");
                if (count > 0 && cavity.State != CavityState.Occupied) Error("cavity.state", cavity.Id, "Een aangesloten pin moet als bezet zijn vastgelegd.");
                if (cavity.State == CavityState.Occupied && count == 0) Error("cavity.emptyOccupied", cavity.Id, "Een als bezet gemarkeerde pin heeft geen aangesloten ader.");
                if (connector.IncludedInAssembly && cavity.State == CavityState.Unspecified) Error("cavity.unspecified", cavity.Id, "Leg vast of de pin bezet, ongebruikt of afgedicht is.");
                var terminal = Part(cavity.TerminalPartId, cavity.Id, PartKind.Terminal,
                    connector.IncludedInAssembly && count > 0 && housing?.Kind != PartKind.RingTerminal && (housing?.RequiresTerminal ?? false));
                Part(cavity.SealPartId, cavity.Id, PartKind.Seal,
                    connector.IncludedInAssembly && count > 0 && (housing?.RequiresSeal ?? false));
                Part(cavity.PlugPartId, cavity.Id, PartKind.Plug, connector.IncludedInAssembly && cavity.State == CavityState.Plugged);
                if (cavity.PlugPartId is not null && cavity.State != CavityState.Plugged) Error("cavity.plugState", cavity.Id, "Een afdichtplug hoort bij een als afgedicht vastgelegde pin.");
                if (cavity.PlugPartId is not null && (count > 0 || cavity.TerminalPartId is not null || cavity.SealPartId is not null))
                    Error("cavity.plugOccupied", cavity.Id, "Een afdichtplug kan niet samen met een draad, terminal of seal worden geplaatst.");
                if (terminal is not null && wires is not null)
                {
                    var section = wires.Sum(w => w.CrossSectionMm2);
                    if ((terminal.MinCrossSectionMm2 is { } min && section < min) || (terminal.MaxCrossSectionMm2 is { } max && section > max))
                        Error("terminal.section", cavity.Id, "Totale draaddoorsnede valt buiten het terminalbereik.");
                }
            }
            foreach (var accessory in connector.Accessories) Material(accessory);
        }
        foreach (var splice in assembly.Splices) Part(splice.PartId, splice.Id, PartKind.Splice, true);
        foreach (var item in assembly.Materials) Material(item);
        foreach (var cable in assembly.Cables)
        {
            Part(cable.PartId, cable.Id, PartKind.Cable, true);
            if (cable.CutLengthMm <= 0) Error("cable.length", cable.Id, "Kabelkniplengte moet groter zijn dan nul.");
        }
        foreach (var branch in assembly.Branches)
        {
            if (branch.Points.Count < 2) Error("branch.geometry", branch.Id, "Een fysieke route vereist minstens twee punten.", true);
            ValidateCurve(branch.Geometry, branch.Points, branch.Id);
            if (branch.PhysicalLengthMm <= 0) Error("branch.length", branch.Id, "Fysieke taklengte moet groter zijn dan nul.");
        }
        foreach (var group in assembly.TwistGroups)
        {
            if (group.ConductorIds.Count is not (2 or 3) || group.ConductorIds.Distinct().Count() != group.ConductorIds.Count)
                Error("twist.members", group.Id, "Een twistgroep vereist twee of drie verschillende aders.");
            foreach (var id in group.ConductorIds)
                if (!wireIds.Contains(id)) Error("twist.unknown", group.Id, "Twistgroep verwijst naar een onbekende ader.", true);
            if (group.PitchMm <= 0) Error("twist.pitch", group.Id, "De getekende twistspoed moet positief zijn.");
        }
        foreach (var id in usedParts)
            if (parts.TryGetValue(id, out var part) && string.IsNullOrWhiteSpace(part.PartNumber))
                Error("part.number", id, $"Artikelnummer ontbreekt voor {part.Name}.");
        foreach (var part in project.Parts)
        {
            if (part.SourceAssetId is { } assetId && !assetIds.Contains(assetId)) Error("part.asset", part.Id, "Onderdeel verwijst naar een onbekende bronbijlage.", true);
            if (part.WidthMm <= 0 || part.HeightMm <= 0) Error("part.size", part.Id, "Symboolafmetingen moeten positief zijn.", true);
            foreach (var pin in part.Pins)
                if (pin.MaxWires < 1) Error("pin.capacity", pin.Id, "Pincapaciteit moet minstens één zijn.", true);
        }
        foreach (var sheet in project.Sheets)
        {
            if (sheet.Views.Count == 0) Error("view.required", sheet.Id, "Een tekenblad vereist minstens één aanzicht.", true);
            if (sheet.WidthMm <= 0 || sheet.HeightMm <= 0 || sheet.PrintScale <= 0 || sheet.MarginMm < 0)
                Error("sheet.size", sheet.Id, "Bladformaat, schaal of marge is ongeldig.", true);
            if (sheet.LogoAssetId is { } logo && !assetIds.Contains(logo)) Error("sheet.logo", sheet.Id, "Blad verwijst naar een onbekend logo.", true);
            var layerIds = sheet.Layers.Select(l => l.Id).ToHashSet();
            if (layerIds.Count != sheet.Layers.Count) Error("layer.duplicate", sheet.Id, "Laag-ID is niet uniek binnen het blad.", true);
            foreach (var view in sheet.Views)
            {
                if (view.Scale <= 0) Error("view.scale", view.Id, "Aanzichtschaal moet positief zijn.", true);
                if (!PresentationLimits.ValidStyle(view.Style))
                    Error("view.style", view.Id, "De tekenstijl bevat een ongeldige lijndikte, letterhoogte of twistweergave.", true);
                if (view.WirePresentations.Select(p => p.ConductorId).Distinct().Count() != view.WirePresentations.Count)
                    Error("wirePresentation.duplicate", view.Id, "Een ader heeft meerdere weergave-instellingen in hetzelfde aanzicht.", true);
                foreach (var presentation in view.WirePresentations)
                {
                    var wire = assembly.Conductors.Find(w => w.Id == presentation.ConductorId);
                    if (wire is null) Error("wirePresentation.conductor", view.Id, "Aderweergave verwijst naar een onbekende ader.", true);
                    if (presentation.LineWidthMm is { } width && !PresentationLimits.ValidLineWidth(width))
                        Error("wirePresentation.width", presentation.ConductorId, "Aderlijndikte moet groter dan 0 en maximaal 1000 mm zijn.", true);
                    if (view.Mode == LayoutMode.Scaled && presentation.Routes.Count != 0)
                        Error("wirePresentation.scaled", presentation.ConductorId, "Afzonderlijke adervormen vereisen vrije opmaak.", true);
                    if (presentation.Routes.Select(r => r.BranchId).Distinct().Count() != presentation.Routes.Count)
                        Error("wirePresentation.duplicateRoute", presentation.ConductorId, "Een adervorm bevat een routetak meermaals.", true);
                    foreach (var geometry in presentation.Routes)
                    {
                        if (wire is null || !wire.BranchIds.Contains(geometry.BranchId) || !view.Routes.Any(r => r.BranchId == geometry.BranchId))
                            Error("wirePresentation.route", presentation.ConductorId, "Adervorm verwijst naar een onbekende of niet gebruikte routetak.", true);
                        ValidateCurve(geometry.Geometry, null, presentation.ConductorId);
                    }
                }
                foreach (var placement in view.Components)
                {
                    if (!PresentationLimits.ValidDisplayScale(placement.DisplayScale) || !PresentationLimits.ValidFontSize(placement.LabelFontSizeMm))
                        Error("placement.style", placement.Id, "Symboolschaal en letterhoogte moeten groter dan 0 en maximaal 1000 zijn.", true);
                    if (view.Mode == LayoutMode.Scaled && Math.Abs(placement.DisplayScale - 1) > 1e-9)
                        Error("placement.scaledSymbol", placement.Id, "Een aanzicht op schaal vereist werkelijke onderdeelgeometrie met symboolschaal 1.", true);
                    if (!connectors.ContainsKey(placement.EntityId) && !spliceIds.Contains(placement.EntityId) && !referenceIds.Contains(placement.EntityId))
                        Error("placement.entity", placement.Id, "Aanzicht verwijst naar een onbekend onderdeel.", true);
                    if (!layerIds.Contains(placement.LayerId)) Error("placement.layer", placement.Id, "Onderdeel staat op een onbekende laag.", true);
                }
                foreach (var route in view.Routes)
                {
                    if (!branches.TryGetValue(route.BranchId, out var branch)) Error("route.branch", route.Id, "Aanzicht verwijst naar een onbekende tak.", true);
                    else if (view.Mode == LayoutMode.Scaled && !HasInvalidCurve(route.Geometry) && Math.Abs(Geometry.Length(route) - branch.PhysicalLengthMm) > Geometry.LengthToleranceMm)
                        Error("route.scaleMismatch", route.Id, "De getekende routelengte komt niet overeen met de fysieke maat.");
                    if (route.Points.Count < 2) Error("route.geometry", route.Id, "Een getekende route vereist minstens twee punten.", true);
                    ValidateCurve(route.Geometry, route.Points, route.Id);
                    if (!layerIds.Contains(route.LayerId)) Error("route.layer", route.Id, "Route staat op een onbekende laag.", true);
                }
                if (view.Mode == LayoutMode.Scaled)
                {
                    var routeMap = view.Routes.GroupBy(r => r.BranchId).ToDictionary(g => g.Key, g => g.First());
                    var pins = project.Parts.SelectMany(p => p.Pins).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
                    var positions = new Dictionary<(string?, string?), Point2>();
                    foreach (var placement in view.Components)
                    {
                        if (connectors.TryGetValue(placement.EntityId, out var connector))
                            foreach (var cavity in connector.Cavities)
                                if (pins.TryGetValue(cavity.PinDefinitionId, out var pin))
                                    positions[(connector.Id, cavity.Id)] = Geometry.ComponentWorld(placement, pin.Position);
                        if (spliceIds.Contains(placement.EntityId) || referenceIds.Contains(placement.EntityId)) positions[(placement.EntityId, null)] = placement.Position;
                    }
                    foreach (var wire in assembly.Conductors.Where(w => w.BranchIds.Count > 0 && w.BranchIds.All(routeMap.ContainsKey)))
                    {
                        Point2? previous = positions.TryGetValue((wire.From.EntityId, wire.From.CavityId), out var fromPoint) ? fromPoint : null;
                        var disconnected = false;
                        foreach (var branchId in wire.BranchIds)
                        {
                            var points = routeMap[branchId].Points;
                            if (points.Count < 2) { disconnected = true; break; }
                            var reverse = previous is { } p && p.DistanceTo(points[^1]) < p.DistanceTo(points[0]);
                            var start = reverse ? points[^1] : points[0];
                            if (previous is { } prior && prior.DistanceTo(start) > Geometry.LengthToleranceMm) disconnected = true;
                            previous = reverse ? points[0] : points[^1];
                        }
                        if (previous is { } last && positions.TryGetValue((wire.To.EntityId, wire.To.CavityId), out var toPoint) &&
                            last.DistanceTo(toPoint) > Geometry.LengthToleranceMm) disconnected = true;
                        if (disconnected) Error("wire.endpointGeometry", wire.Id,
                            "Een route op schaal bevat een niet-gemeten aansluitstuk of onderbreking. Voeg een eigen aansluitsegment toe of herstel de route.");
                    }
                }
                foreach (var annotation in view.Annotations)
                {
                    if (annotation.FontSizeMm <= 0 || annotation.LineWidthMm < 0)
                        Error("annotation.style", annotation.Id, "Teksthoogte moet positief zijn; lijndikte mag niet negatief zijn.", true);
                    var requiredPoints = annotation.Kind switch
                    {
                        AnnotationKind.Line or AnnotationKind.Leader or AnnotationKind.Dimension => 2,
                        AnnotationKind.Arc => 3, _ => 1
                    };
                    if (annotation.Points.Count < requiredPoints)
                        Error("annotation.geometry", annotation.Id, $"Deze annotatie vereist minstens {requiredPoints} punt(en).", true);
                    if (annotation.Kind == AnnotationKind.Image && (annotation.WidthMm <= 0 || annotation.HeightMm <= 0))
                        Error("annotation.size", annotation.Id, "Afbeeldingsbreedte en -hoogte moeten positief zijn.", true);
                    if (!layerIds.Contains(annotation.LayerId)) Error("annotation.layer", annotation.Id, "Aantekening staat op een onbekende laag.", true);
                    if (annotation.AssetId is { } asset && !assetIds.Contains(asset)) Error("annotation.asset", annotation.Id, "Afbeelding verwijst naar een onbekende bijlage.", true);
                    if (annotation.EntityId is { } entity && !branches.ContainsKey(entity) && !wireIds.Contains(entity) && !connectors.ContainsKey(entity) && !spliceIds.Contains(entity) && !referenceIds.Contains(entity))
                        Error("annotation.entity", annotation.Id, "Aantekening verwijst naar een onbekend object.", true);
                    if (annotation.Kind is AnnotationKind.WireTable or AnnotationKind.PinTable &&
                        annotation.EntityId is { } filter && !connectors.ContainsKey(filter))
                        Error("annotation.connectorFilter", annotation.Id, "Het aansluitingenfilter van deze tabel moet naar een connector verwijzen.", true);
                    if (annotation.DimensionOverrideMm.HasValue) Warn("dimension.override", annotation.Id, "Deze maattekst is handmatig ingevuld en wordt niet uit de productiemaat afgeleid.");
                }
            }
        }
        ValidateExtended(project, issues);
        return issues;

        void ValidateCurve(RouteGeometry? geometry, IReadOnlyList<Point2>? points, string owner)
        {
            if (geometry is null) return;
            if (HasInvalidCurve(geometry)) { Error("route.curve", owner, "De curve bevat ongeldige knopen, handvatten of segmentverwijzingen.", true); return; }
            if (points is not null && (geometry.Knots.Count != points.Count || geometry.Knots.Where((k, i) => k.Position != points[i]).Any()))
                Error("route.curvePoints", owner, "Curveknopen en routepunten komen niet overeen.", true);
        }
    }

    private static bool HasInvalidCurve(RouteGeometry? geometry)
    {
        if (geometry is null) return false;
        try { Geometry.ValidateRouteGeometry(geometry); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void ValidateShape(HarnessProject project, List<ValidationIssue> issues)
    {
        var ids = new HashSet<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Required(string? text, string owner)
        {
            if (text is null) issues.Add(new(Severity.Error, "data.null", owner, "Een vereist tekstveld is null.", true));
        }
        void Identity(string? id, string owner)
        {
            Required(id, owner);
            if (string.IsNullOrWhiteSpace(id)) issues.Add(new(Severity.Error, "data.id", owner, "Een object-ID ontbreekt.", true));
            else if (!ids.Add(id)) issues.Add(new(Severity.Error, "data.duplicateId", id, "Een object-ID komt meermaals voor.", true));
        }
        void Number(double number, string owner)
        {
            if (!double.IsFinite(number)) issues.Add(new(Severity.Error, "data.nonfinite", owner, "Een getal is niet eindig.", true));
        }
        void Points(List<Point2>? points, string owner)
        {
            if (points is null) { issues.Add(new(Severity.Error, "data.null", owner, "Een vereiste puntenlijst is null.", true)); return; }
            foreach (var point in points)
                if (!Geometry.IsFinite(point)) issues.Add(new(Severity.Error, "data.nonfinite", owner, "Een coördinaat is niet eindig.", true));
        }
        void EnumValue<T>(T value, string owner) where T : struct, Enum
        {
            if (!Enum.IsDefined(value)) issues.Add(new(Severity.Error, "data.enum", owner, "Onbekende keuzewaarde in projectgegevens.", true));
        }
        void Walk(object? value, string owner, int depth)
        {
            if (value is null) { issues.Add(new(Severity.Error, "data.null", owner, "Een vereist object of lijstitem is null.", true)); return; }
            if (depth > 30) { issues.Add(new(Severity.Error, "data.depth", owner, "Projectgegevens zijn te diep genest.", true)); return; }
            if (value is Point2 point)
            {
                if (!Geometry.IsFinite(point)) issues.Add(new(Severity.Error, "data.nonfinite", owner, "Een coördinaat is niet eindig.", true));
                return;
            }
            var type = value.GetType();
            if (value is double number)
            {
                if (!double.IsFinite(number)) issues.Add(new(Severity.Error, "data.nonfinite", owner, "Een getal is niet eindig.", true));
                return;
            }
            if (type.IsEnum)
            {
                if (!Enum.IsDefined(type, value)) issues.Add(new(Severity.Error, "data.enum", owner, "Onbekende keuzewaarde in projectgegevens.", true));
                return;
            }
            if (value is string || type.IsPrimitive || value is decimal || value is DateTimeOffset || value is DateTime || value is byte[]) return;
            if (!type.IsValueType && !visited.Add(value)) return;
            if (value is IEnumerable list)
            {
                foreach (var item in list) Walk(item, owner, depth + 1);
                return;
            }
            // The large CAD collections use typed checks; rare metadata keeps the generic cached schema walker.
            switch (value)
            {
                case Conductor wire:
                    Identity(wire.Id, owner);
                    Required(wire.Name, wire.Id); Required(wire.Color, wire.Id); Required(wire.ColorCode, wire.Id);
                    Required(wire.Signal, wire.Id); Required(wire.Notes, wire.Id); Required(wire.NetName, wire.Id); Required(wire.Shielding, wire.Id);
                    Walk(wire.From, wire.Id, depth + 1); Walk(wire.To, wire.Id, depth + 1);
                    if (wire.BranchIds is null) Walk(null, wire.Id, depth + 1);
                    else foreach (var branchReference in wire.BranchIds) Required(branchReference, wire.Id);
                    EnumValue(wire.LengthSource, wire.Id);
                    Number(wire.CrossSectionMm2, wire.Id); Number(wire.ManualCutLengthMm, wire.Id);
                    Number(wire.StartAllowanceMm, wire.Id); Number(wire.EndAllowanceMm, wire.Id); Number(wire.ProcessingAllowanceMm, wire.Id);
                    return;
                case Endpoint endpoint:
                    Required(endpoint.Description, owner); Required(endpoint.Notes, owner); EnumValue(endpoint.Kind, owner);
                    return;
                case Cavity cavity:
                    Identity(cavity.Id, owner); Required(cavity.Label, cavity.Id); Required(cavity.PinDefinitionId, cavity.Id);
                    Required(cavity.Function, cavity.Id); Required(cavity.Notes, cavity.Id);
                    EnumValue(cavity.State, cavity.Id);
                    return;
                case ConnectorInstance connector:
                    Identity(connector.Id, owner); Required(connector.Name, connector.Id); Required(connector.PartId, connector.Id); Required(connector.Notes, connector.Id);
                    Walk(connector.Cavities, connector.Id, depth + 1); Walk(connector.Accessories, connector.Id, depth + 1);
                    return;
                case RouteBranch branch:
                    Identity(branch.Id, owner); Required(branch.Name, branch.Id); Points(branch.Points, branch.Id); Number(branch.PhysicalLengthMm, branch.Id);
                    if (branch.Geometry is not null) Walk(branch.Geometry, branch.Id, depth + 1);
                    return;
                case PlacedRoute route:
                    Identity(route.Id, owner); Required(route.BranchId, route.Id); Required(route.LayerId, route.Id); Points(route.Points, route.Id);
                    if (route.Geometry is not null) Walk(route.Geometry, route.Id, depth + 1);
                    return;
                case RouteGeometry geometry:
                    // Knot/segment identities are local to a route, and intentionally copied into its physical and view representations.
                    if (geometry.Knots is null || geometry.Segments is null) Walk(null, owner, depth + 1);
                    else if (geometry.Knots.Any(k => k is null) || geometry.Segments.Any(s => s is null)) Walk(null, owner, depth + 1);
                    else
                    {
                        foreach (var knot in geometry.Knots)
                        {
                            Required(knot.Id, owner); EnumValue(knot.TangentMode, owner);
                            if (!Geometry.IsFinite(knot.Position) || !Geometry.IsFinite(knot.InHandle) || !Geometry.IsFinite(knot.OutHandle))
                                issues.Add(new(Severity.Error, "data.nonfinite", owner, "Een bochthandvat is niet eindig.", true));
                        }
                        foreach (var segment in geometry.Segments)
                        { Required(segment.Id, owner); Required(segment.StartKnotId, owner); Required(segment.EndKnotId, owner); EnumValue(segment.Kind, owner); }
                    }
                    return;
                case PlacedComponent component:
                    Identity(component.Id, owner); Required(component.EntityId, component.Id); Required(component.LayerId, component.Id);
                    Number(component.RotationDegrees, component.Id);
                    Number(component.DisplayScale, component.Id); Number(component.LabelFontSizeMm, component.Id);
                    if (!Geometry.IsFinite(component.Position) || !Geometry.IsFinite(component.LabelOffset))
                        issues.Add(new(Severity.Error, "data.nonfinite", component.Id, "Een coördinaat is niet eindig.", true));
                    return;
                case PinDefinition pin:
                    Identity(pin.Id, owner); Required(pin.Label, pin.Id); Required(pin.Function, pin.Id);
                    if (!Geometry.IsFinite(pin.Position)) issues.Add(new(Severity.Error, "data.nonfinite", pin.Id, "Een coördinaat is niet eindig.", true));
                    return;
            }
            if (type.Namespace != typeof(HarnessProject).Namespace) return;
            var shape = ShapeTypes.GetOrAdd(type, CreateShapeType);
            var id = shape.Id?.Invoke(value) as string ?? owner;
            if (shape.Id is not null)
            {
                if (string.IsNullOrWhiteSpace(id)) issues.Add(new(Severity.Error, "data.id", owner, "Een object-ID ontbreekt.", true));
                else if (value is not DrawingLayer && !ids.Add(id)) issues.Add(new(Severity.Error, "data.duplicateId", id, "Een object-ID komt meermaals voor.", true));
            }
            foreach (var property in shape.Properties)
            {
                var child = property.Get(value);
                if (child is null)
                {
                    if (property.Required)
                        Walk(null, id, depth + 1);
                }
                else Walk(child, id, depth + 1);
            }
        }
        Walk(project, project.Id, 0);
    }

    private static ShapeType CreateShapeType(Type type)
    {
        var nullability = new NullabilityInfoContext();
        Func<object, object?> Getter(PropertyInfo property)
        {
            var argument = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, object?>>(
                Expression.Convert(Expression.Property(Expression.Convert(argument, type), property), typeof(object)), argument).Compile();
        }
        var id = type.GetProperty("Id");
        return new(id is null ? null : Getter(id),
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)
                .Select(p => new ShapeProperty(Getter(p), nullability.Create(p).ReadState == NullabilityState.NotNull)).ToArray());
    }
}
