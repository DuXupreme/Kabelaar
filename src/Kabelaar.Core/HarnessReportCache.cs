namespace Kabelaar.Core;

public sealed record HarnessReportSnapshot(
    IReadOnlyList<WireRow> Wires,
    IReadOnlyList<BomRow> Bom,
    IReadOnlyDictionary<string, string> Suppliers);

/// <summary>
/// UI-owned report cache. Exact input comparisons also detect direct edits and undo;
/// drawing geometry is deliberately absent because it is not production data.
/// </summary>
public sealed class HarnessReportCache
{
    private Inputs? _inputs;
    private HarnessReportSnapshot? _snapshot;
    private double[] _lengths = [];
    private Dictionary<string, int> _wireIndices = [];

    public HarnessReportSnapshot Get(HarnessProject project)
    {
        if (_snapshot is null || _inputs is null || !_inputs.Matches(project))
        {
            var inputs = Inputs.Capture(project);
            var capturedLengths = project.Assembly.Branches.Select(b => b.PhysicalLengthMm).ToArray();
            var wires = HarnessReports.Wires(project);
            var wireIndices = wires.Select((w, i) => (w.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);
            var snapshot = new HarnessReportSnapshot(wires, HarnessReports.Bom(project),
                project.Parts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Supplier));
            // Publish all cache state together only after every calculation succeeds. A failed
            // refresh must remain invalid on the next call, never match an older visible snapshot.
            _inputs = inputs;
            _lengths = capturedLengths;
            _wireIndices = wireIndices;
            return _snapshot = snapshot;
        }

        var lengthChanged = false;
        for (var i = 0; i < _lengths.Length; i++)
            if (!_lengths[i].Equals(project.Assembly.Branches[i].PhysicalLengthMm)) { lengthChanged = true; break; }
        if (!lengthChanged) return _snapshot;
        var nextLengths = project.Assembly.Branches.Select(b => b.PhysicalLengthMm).ToArray();
        var lengths = project.Assembly.Branches.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().PhysicalLengthMm);
        WireRow[]? changedWires = null;
        var looseWireCutChanged = false;
        foreach (var wire in project.Assembly.Conductors)
        {
            var index = _wireIndices[wire.Id];
            var previous = _snapshot.Wires[index];
            double routeLength = 0;
            foreach (var branchId in wire.BranchIds) routeLength += lengths.GetValueOrDefault(branchId);
            if (previous.RouteLengthMm.Equals(routeLength)) continue;
            var cut = wire.LengthSource == LengthSource.Manual ? wire.ManualCutLengthMm : routeLength + previous.AllowanceMm;
            changedWires ??= _snapshot.Wires.ToArray();
            changedWires[index] = previous with { RouteLengthMm = routeLength, CutLengthMm = cut };
            if (wire.CableId is null && !previous.CutLengthMm.Equals(cut)) looseWireCutChanged = true;
        }
        if (changedWires is null)
        {
            _lengths = nextLengths;
            return _snapshot;
        }
        IReadOnlyList<BomRow> bom = _snapshot.Bom;
        if (looseWireCutChanged)
        {
            var quantities = HarnessReports.BomQuantities(project);
            BomRow[]? changedBom = null;
            for (var i = 0; i < bom.Count; i++)
            {
                var row = bom[i];
                var quantity = quantities.GetValueOrDefault((row.PartId, row.Unit));
                if (row.Quantity.Equals(quantity)) continue;
                changedBom ??= bom.ToArray();
                changedBom[i] = row with { Quantity = quantity };
            }
            if (changedBom is not null) bom = changedBom;
        }
        var nextSnapshot = new HarnessReportSnapshot(changedWires, bom, _snapshot.Suppliers);
        _lengths = nextLengths;
        return _snapshot = nextSnapshot;
    }

    // Separate typed token streams avoid boxing, hashing, JSON and per-gesture snapshots.
    // Collection counts are tokens too, so additions, removals and reordering are unambiguous.
    private sealed record Inputs(string?[] Text, double[] Numbers)
    {
        public static Inputs Capture(HarnessProject project)
        {
            var cursor = new Cursor(null);
            Visit(project, ref cursor);
            return cursor.Captured();
        }
        public bool Matches(HarnessProject project)
        {
            var cursor = new Cursor(this);
            Visit(project, ref cursor);
            return cursor.Matches;
        }
    }
    private struct Cursor
    {
        private readonly Inputs? _expected;
        private readonly List<string?>? _text;
        private readonly List<double>? _numbers;
        private int _textIndex, _numberIndex;
        private bool _equal;
        public Cursor(Inputs? expected)
        {
            _expected = expected; _text = expected is null ? [] : null; _numbers = expected is null ? [] : null;
            _textIndex = _numberIndex = 0; _equal = true;
        }
        public void Add(string? value)
        {
            if (_expected is null) _text!.Add(value);
            else if (_textIndex >= _expected.Text.Length || !string.Equals(value, _expected.Text[_textIndex], StringComparison.Ordinal)) _equal = false;
            _textIndex++;
        }
        public void Add(double value)
        {
            if (_expected is null) _numbers!.Add(value);
            else if (_numberIndex >= _expected.Numbers.Length || !value.Equals(_expected.Numbers[_numberIndex])) _equal = false;
            _numberIndex++;
        }
        public bool Matches => _equal && _expected is not null && _textIndex == _expected.Text.Length && _numberIndex == _expected.Numbers.Length;
        public Inputs Captured() => new(_text!.ToArray(), _numbers!.ToArray());
    }
    private static void Visit(HarnessProject project, ref Cursor state)
    {
        state.Add(project.Parts.Count);
        foreach (var part in project.Parts)
        {
            state.Add(part.Id); state.Add(part.PartNumber); state.Add(part.Name); state.Add((int)part.Kind); state.Add(part.Supplier);
        }
        var assembly = project.Assembly;
        state.Add(assembly.Connectors.Count);
        foreach (var connector in assembly.Connectors)
        {
            state.Add(connector.Id); state.Add(connector.Name); state.Add(connector.PartId); state.Add(connector.IncludedInAssembly ? 1 : 0);
            state.Add(connector.Cavities.Count);
            foreach (var cavity in connector.Cavities)
            {
                state.Add(cavity.Id); state.Add(cavity.Label); state.Add(cavity.TerminalPartId); state.Add(cavity.SealPartId); state.Add(cavity.PlugPartId);
            }
            Materials(connector.Accessories, ref state);
        }
        state.Add(assembly.Splices.Count);
        foreach (var splice in assembly.Splices) { state.Add(splice.Id); state.Add(splice.Name); state.Add(splice.PartId); }
        state.Add(assembly.Cables.Count);
        foreach (var cable in assembly.Cables)
        {
            state.Add(cable.Id); state.Add(cable.Name); state.Add(cable.PartId); state.Add(cable.CutLengthMm);
        }
        Materials(assembly.Materials, ref state);
        state.Add(assembly.Branches.Count);
        foreach (var branch in assembly.Branches) state.Add(branch.Id);
        state.Add(assembly.Conductors.Count);
        foreach (var wire in assembly.Conductors)
        {
            state.Add(wire.Id); state.Add(wire.Name); state.Add(wire.WirePartId); state.Add(wire.ColorCode); state.Add(wire.CrossSectionMm2);
            state.Add(wire.Signal); state.Add(wire.CableId); state.Add((int)wire.LengthSource);
            state.Add(wire.ManualCutLengthMm); state.Add(wire.StartAllowanceMm); state.Add(wire.EndAllowanceMm); state.Add(wire.ProcessingAllowanceMm);
            Endpoint(wire.From, ref state); Endpoint(wire.To, ref state);
            state.Add(wire.BranchIds.Count);
            foreach (var id in wire.BranchIds) state.Add(id);
        }
    }
    private static void Endpoint(Endpoint endpoint, ref Cursor state)
    {
        state.Add((int)endpoint.Kind); state.Add(endpoint.EntityId); state.Add(endpoint.CavityId); state.Add(endpoint.Description);
    }
    private static void Materials(List<MaterialItem> materials, ref Cursor state)
    {
        state.Add(materials.Count);
        foreach (var material in materials)
        {
            state.Add(material.PartId); state.Add(material.Quantity); state.Add(material.Unit); state.Add(material.Notes);
        }
    }
}
