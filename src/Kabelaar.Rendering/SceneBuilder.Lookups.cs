using System.Runtime.CompilerServices;
using Kabelaar.Core;

namespace Kabelaar.Rendering;

public static partial class SceneBuilder
{
    // Model lists are deliberately mutable. Reference identity alone is not a valid cache key:
    // retain the IDs/memberships used to build each lookup and compare them before reuse.
    private sealed class IdLookup<T> where T : class
    {
        private readonly T[] _items;
        private readonly string[] _ids;
        public Dictionary<string, T> ById { get; }
        public IdLookup(IReadOnlyList<T> items, Func<T, string> id)
        {
            _items = new T[items.Count]; _ids = new string[items.Count];
            ById = new(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                _items[i] = items[i]; _ids[i] = id(items[i]);
                ById.TryAdd(_ids[i], items[i]);
            }
        }
        public bool Matches(IReadOnlyList<T> items, Func<T, string> id)
        {
            if (_items.Length != items.Count) return false;
            for (var i = 0; i < items.Count; i++)
                if (!ReferenceEquals(_items[i], items[i]) || _ids[i] != id(items[i])) return false;
            return true;
        }
        public static IdLookup<T> Refresh(IdLookup<T>? previous, IReadOnlyList<T> items, Func<T, string> id) =>
            previous is not null && previous.Matches(items, id) ? previous : new(items, id);
    }

    private sealed class WireTopology
    {
        public IdLookup<Conductor> Items { get; }
        private readonly string[][] _branchIds;
        public ILookup<string, Conductor> ByBranch { get; }
        public Dictionary<string, double> Offsets { get; } = [];
        public WireTopology(IReadOnlyList<Conductor> wires)
        {
            Items = new(wires, w => w.Id);
            _branchIds = wires.Select(w => w.BranchIds.ToArray()).ToArray();
            ByBranch = wires.SelectMany(w => w.BranchIds.Select(id => (id, wire: w))).ToLookup(p => p.id, p => p.wire);
            foreach (var group in wires.Where(w => w.BranchIds.Count > 0).GroupBy(w => string.Join("|", w.BranchIds)))
            {
                var shared = group.OrderBy(w => w.Id).ToArray();
                if (shared.Length > 1)
                    for (var i = 0; i < shared.Length; i++) Offsets.TryAdd(shared[i].Id, (i - (shared.Length - 1) / 2.0) * .8);
            }
        }
        public bool Matches(IReadOnlyList<Conductor> wires)
        {
            if (!Items.Matches(wires, w => w.Id)) return false;
            for (var i = 0; i < wires.Count; i++)
                if (!SameIds(_branchIds[i], wires[i].BranchIds)) return false;
            return true;
        }
    }

    private sealed class MaterialTopology
    {
        private readonly IdLookup<MaterialItem> _items;
        private readonly string?[] _branches;
        public ILookup<string, MaterialItem> ByBranch { get; }
        public MaterialTopology(IReadOnlyList<MaterialItem> materials)
        {
            _items = new(materials, m => m.Id);
            _branches = materials.Select(m => m.BranchId).ToArray();
            ByBranch = materials.Where(m => m.BranchId is not null).ToLookup(m => m.BranchId!);
        }
        public bool Matches(IReadOnlyList<MaterialItem> materials)
        {
            if (!_items.Matches(materials, m => m.Id)) return false;
            for (var i = 0; i < materials.Count; i++)
                if (_branches[i] != materials[i].BranchId) return false;
            return true;
        }
    }

    private sealed class TwistTopology
    {
        private readonly IdLookup<TwistGroup> _items;
        private readonly string[][] _members;
        public Dictionary<string, TwistGroup> ByWire { get; }
        public TwistTopology(IReadOnlyList<TwistGroup> twists)
        {
            _items = new(twists, t => t.Id);
            _members = twists.Select(t => t.ConductorIds.ToArray()).ToArray();
            ByWire = new();
            foreach (var twist in twists)
                foreach (var id in twist.ConductorIds) ByWire.TryAdd(id, twist);
        }
        public bool Matches(IReadOnlyList<TwistGroup> twists)
        {
            if (!_items.Matches(twists, t => t.Id)) return false;
            for (var i = 0; i < twists.Count; i++)
                if (!SameIds(_members[i], twists[i].ConductorIds)) return false;
            return true;
        }
    }

    private static bool SameIds(string[] before, IReadOnlyList<string> after)
    {
        if (before.Length != after.Count) return false;
        for (var i = 0; i < before.Length; i++)
            if (before[i] != after[i]) return false;
        return true;
    }

    private sealed record ProjectLookups(IdLookup<PartDefinition> Parts, IdLookup<RouteBranch> Branches,
        IdLookup<ConnectorInstance> Connectors, IdLookup<Splice> Splices, IdLookup<CableInstance> Cables,
        WireTopology Wires, MaterialTopology Materials, TwistTopology Twists);
    private sealed class LookupMemo { public ProjectLookups? Value; }
    private static readonly ConditionalWeakTable<HarnessProject, LookupMemo> ProjectLookupCache = new();

    private static ProjectLookups Lookups(HarnessProject project)
    {
        var memo = ProjectLookupCache.GetOrCreateValue(project);
        lock (memo)
        {
            var old = memo.Value;
            var assembly = project.Assembly;
            // Publish only after every comparison/rebuild has succeeded. A malformed edit may throw,
            // and must never leave a partially refreshed snapshot for the next attempt.
            return memo.Value = new(
                IdLookup<PartDefinition>.Refresh(old?.Parts, project.Parts, p => p.Id),
                IdLookup<RouteBranch>.Refresh(old?.Branches, assembly.Branches, b => b.Id),
                IdLookup<ConnectorInstance>.Refresh(old?.Connectors, assembly.Connectors, c => c.Id),
                IdLookup<Splice>.Refresh(old?.Splices, assembly.Splices, s => s.Id),
                IdLookup<CableInstance>.Refresh(old?.Cables, assembly.Cables, c => c.Id),
                old?.Wires is { } wires && wires.Matches(assembly.Conductors) ? wires : new(assembly.Conductors),
                old?.Materials is { } materials && materials.Matches(assembly.Materials) ? materials : new(assembly.Materials),
                old?.Twists is { } twists && twists.Matches(assembly.TwistGroups) ? twists : new(assembly.TwistGroups));
        }
    }

    private sealed class IndexMemo { public SceneSpatialIndex? Value; }
    private static readonly ConditionalWeakTable<Sheet, IndexMemo> SheetIndexCache = new();
    private static DrawingScene IndexedScene(Sheet sheet, bool draft, List<ScenePrimitive> primitives)
    {
        var memo = SheetIndexCache.GetOrCreateValue(sheet);
        lock (memo)
        {
            var scene = new DrawingScene(sheet.Id, sheet.WidthMm, sheet.HeightMm, draft, primitives, memo.Value);
            memo.Value = scene.Index;
            return scene;
        }
    }
}
