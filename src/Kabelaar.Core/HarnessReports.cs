namespace Kabelaar.Core;

public sealed record WireRow(string Id, string Name, string From, string To, string PartNumber, string Color,
    double CrossSectionMm2, double RouteLengthMm, double AllowanceMm, double CutLengthMm, string LengthSource, string Signal, string? Cable,
    string NetName = "", string Shielding = "", string FromNote = "", string ToNote = "");
public sealed record BomRow(string PartId, string PartNumber, string Description, string Kind, double Quantity, string Unit, string References);
public sealed record PinRow(string ConnectorId, string Connector, string CavityId, string Pin, string State,
    string Terminal, string Seal, string Plug, string Wires, string Connections, string Function = "", string Notes = "", string Counterpart = "");

public static class HarnessReports
{
    public static double RouteLength(HarnessProject project, Conductor conductor)
    {
        var lengths = project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().PhysicalLengthMm);
        return conductor.BranchIds.Sum(id => lengths.GetValueOrDefault(id));
    }

    public static double CutLength(HarnessProject project, Conductor conductor) => conductor.LengthSource == LengthSource.Manual
        ? conductor.ManualCutLengthMm
        : RouteLength(project, conductor) + conductor.StartAllowanceMm + conductor.EndAllowanceMm + conductor.ProcessingAllowanceMm;

    public static string EndpointLabel(HarnessProject project, Endpoint endpoint)
    {
        if (endpoint.Kind == EndpointKind.Free) return string.IsNullOrWhiteSpace(endpoint.Description) ? "Vrij uiteinde" : endpoint.Description;
        if (endpoint.Kind == EndpointKind.Splice) return project.Assembly.Splices.Find(s => s.Id == endpoint.EntityId)?.Name ?? "? splice";
        if (endpoint.Kind == EndpointKind.ReferencePoint) return project.Assembly.ReferencePoints.Find(p => p.Id == endpoint.EntityId)?.Name ?? "? referentiepunt";
        var connector = project.Assembly.Connectors.Find(c => c.Id == endpoint.EntityId);
        var cavity = connector?.Cavities.Find(c => c.Id == endpoint.CavityId);
        return connector is null || cavity is null ? "? aansluiting" : $"{connector.Name}:{cavity.Label}";
    }

    public static IReadOnlyList<WireRow> Wires(HarnessProject project)
    {
        var lengths = project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().PhysicalLengthMm);
        var parts = project.Parts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var endpointLabels = CreateEndpointLabels(project);
        var cables = project.Assembly.Cables.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        return project.Assembly.Conductors.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Id)
            .Select(w =>
            {
                var route = w.BranchIds.Sum(id => lengths.GetValueOrDefault(id));
                var allowance = w.StartAllowanceMm + w.EndAllowanceMm + w.ProcessingAllowanceMm;
                return new WireRow(w.Id, w.Name, IndexedEndpointLabel(endpointLabels, w.From), IndexedEndpointLabel(endpointLabels, w.To),
                    w.WirePartId is { } partId && parts.TryGetValue(partId, out var part) ? part.PartNumber : "",
                    w.ColorCode, w.CrossSectionMm2, route, allowance,
                    w.LengthSource == LengthSource.Manual ? w.ManualCutLengthMm : route + allowance,
                    w.LengthSource == LengthSource.Manual ? "Handmatig" : "Route + toeslagen", w.Signal,
                    w.CableId is { } cableId ? cables.GetValueOrDefault(cableId) : null, w.NetName, w.Shielding, w.From.Notes, w.To.Notes);
            }).ToArray();
    }

    public static IReadOnlyList<BomRow> Bom(HarnessProject project)
    {
        var entries = new List<(string PartId, double Qty, string Unit, string Ref)>();
        void Add(string? id, double quantity, string unit, string reference)
        {
            if (!string.IsNullOrWhiteSpace(id)) entries.Add((id, quantity, unit, reference));
        }
        foreach (var connector in project.Assembly.Connectors.Where(c => c.IncludedInAssembly))
        {
            Add(connector.PartId, 1, "st", connector.Name);
            foreach (var cavity in connector.Cavities)
            {
                Add(cavity.TerminalPartId, 1, "st", $"{connector.Name}:{cavity.Label}");
                Add(cavity.SealPartId, 1, "st", $"{connector.Name}:{cavity.Label}");
                Add(cavity.PlugPartId, 1, "st", $"{connector.Name}:{cavity.Label}");
            }
            foreach (var item in connector.Accessories) Add(item.PartId, item.Quantity, item.Unit, connector.Name);
        }
        foreach (var splice in project.Assembly.Splices) Add(splice.PartId, 1, "st", splice.Name);
        foreach (var cable in project.Assembly.Cables) Add(cable.PartId, cable.CutLengthMm / 1000, "m", cable.Name);
        var branchLengths = project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().PhysicalLengthMm);
        foreach (var wire in project.Assembly.Conductors.Where(w => w.CableId is null))
        {
            var length = wire.LengthSource == LengthSource.Manual ? wire.ManualCutLengthMm :
                wire.BranchIds.Sum(id => branchLengths.GetValueOrDefault(id)) + wire.StartAllowanceMm + wire.EndAllowanceMm + wire.ProcessingAllowanceMm;
            Add(wire.WirePartId, length / 1000, "m", wire.Name);
        }
        foreach (var item in project.Assembly.Materials) Add(item.PartId, item.Quantity, item.Unit, item.Notes);
        var parts = project.Parts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        return entries.GroupBy(e => (e.PartId, e.Unit))
            .Select(group =>
            {
                parts.TryGetValue(group.Key.PartId, out var part);
                return new BomRow(group.Key.PartId, part?.PartNumber ?? "?", part?.Name ?? "Onbekend onderdeel",
                    part?.Kind.ToString() ?? "?", group.Sum(e => e.Qty), group.Key.Unit,
                    string.Join(", ", group.Select(e => e.Ref).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Order()));
            }).OrderBy(r => r.Kind).ThenBy(r => r.PartNumber).ToArray();
    }

    // Same contribution order as Bom, without rebuilding labels, groups or reference text.
    internal static Dictionary<(string PartId, string Unit), double> BomQuantities(HarnessProject project,
        IReadOnlyDictionary<string, double>? knownBranchLengths = null)
    {
        var totals = new Dictionary<(string PartId, string Unit), double>();
        void Add(string? id, double quantity, string unit)
        {
            if (!string.IsNullOrWhiteSpace(id)) totals[(id, unit)] = totals.GetValueOrDefault((id, unit)) + quantity;
        }
        foreach (var connector in project.Assembly.Connectors.Where(c => c.IncludedInAssembly))
        {
            Add(connector.PartId, 1, "st");
            foreach (var cavity in connector.Cavities)
            {
                Add(cavity.TerminalPartId, 1, "st"); Add(cavity.SealPartId, 1, "st"); Add(cavity.PlugPartId, 1, "st");
            }
            foreach (var item in connector.Accessories) Add(item.PartId, item.Quantity, item.Unit);
        }
        foreach (var splice in project.Assembly.Splices) Add(splice.PartId, 1, "st");
        foreach (var cable in project.Assembly.Cables) Add(cable.PartId, cable.CutLengthMm / 1000, "m");
        var lengths = knownBranchLengths ??
            project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().PhysicalLengthMm);
        foreach (var wire in project.Assembly.Conductors)
        {
            if (wire.CableId is not null) continue;
            var length = wire.ManualCutLengthMm;
            if (wire.LengthSource != LengthSource.Manual)
            {
                double route = 0;
                foreach (var id in wire.BranchIds) route += lengths.GetValueOrDefault(id);
                length = route + wire.StartAllowanceMm + wire.EndAllowanceMm + wire.ProcessingAllowanceMm;
            }
            Add(wire.WirePartId, length / 1000, "m");
        }
        foreach (var material in project.Assembly.Materials) Add(material.PartId, material.Quantity, material.Unit);
        return totals;
    }

    public static IReadOnlyList<PinRow> Pins(HarnessProject project)
    {
        var partNumbers = project.Parts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().PartNumber);
        var endpointLabels = CreateEndpointLabels(project);
        string Number(string? id) => id is not null ? partNumbers.GetValueOrDefault(id, "?") : "";
        var endpointRows = project.Assembly.Conductors.SelectMany(w => new[] { (Wire: w, End: w.From, Other: w.To), (Wire: w, End: w.To, Other: w.From) })
            .Where(e => e.End.Kind == EndpointKind.Cavity)
            .ToLookup(e => (e.End.EntityId, e.End.CavityId));
        return project.Assembly.Connectors.OrderBy(c => c.Name).SelectMany(c => c.Cavities.Select(pin =>
        {
            var ends = endpointRows[(c.Id, pin.Id)].OrderBy(e => e.Wire.Name).ToArray();
            return new PinRow(c.Id, c.Name, pin.Id, pin.Label, pin.State.ToString(), Number(pin.TerminalPartId),
                Number(pin.SealPartId), Number(pin.PlugPartId), string.Join(", ", ends.Select(e => e.Wire.Name)),
                string.Join(", ", ends.Select(e => IndexedEndpointLabel(endpointLabels, e.Other))),
                pin.Function.Length > 0 ? pin.Function : project.Parts.Find(p => p.Id == c.PartId)?.Pins.Find(p => p.Id == pin.PinDefinitionId)?.Function ?? "",
                pin.Notes, c.MatingConnectorId is { } other ? project.Assembly.Connectors.Find(x => x.Id == other)?.Name ?? "" :
                    c.MatingPartId is { } matingPart ? Number(matingPart) : "");
        })).ToArray();
    }

    private static Dictionary<(string?, string?), string> CreateEndpointLabels(HarnessProject project)
    {
        var labels = new Dictionary<(string?, string?), string>();
        foreach (var connector in project.Assembly.Connectors)
            foreach (var cavity in connector.Cavities)
                labels[(connector.Id, cavity.Id)] = $"{connector.Name}:{cavity.Label}";
        foreach (var splice in project.Assembly.Splices) labels[(splice.Id, null)] = splice.Name;
        foreach (var point in project.Assembly.ReferencePoints) labels[(point.Id, null)] = point.Name;
        return labels;
    }
    private static string IndexedEndpointLabel(Dictionary<(string?, string?), string> labels, Endpoint endpoint) =>
        endpoint.Kind == EndpointKind.Free
            ? string.IsNullOrWhiteSpace(endpoint.Description) ? "Vrij uiteinde" : endpoint.Description
            : labels.GetValueOrDefault((endpoint.EntityId, endpoint.CavityId), "? aansluiting");
}
