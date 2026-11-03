namespace Kabelaar.Core;

public sealed record SelectionInsertProposal(IEditCommand Command, HarnessProject PreviewProject,
    IReadOnlyList<string> SelectionIds, IReadOnlyList<string> Summary);

/// <summary>Copies physical objects only on explicit insertion. Capturing and previewing are read-only.</summary>
public static partial class SelectionOperations
{
    public static HarnessProject Capture(HarnessProject source, DrawingView sourceView, IEnumerable<string> selectedIds)
    {
        if (!source.Sheets.SelectMany(s => s.Views).Contains(sourceView)) throw new ArgumentException("Het aanzicht hoort niet bij het project.");
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var p in sourceView.Components.Where(p => ids.Contains(p.Id))) ids.Add(p.EntityId);
        var block = GraphEdit.Clone(source);
        var originalSheet = source.Sheets.Single(s => s.Views.Contains(sourceView));
        var sheet = block.Sheets.Single(s => s.Id == originalSheet.Id);
        var view = sheet.Views.Single(v => v.Id == sourceView.Id);
        block.Id = Ids.New(); block.Title = "Selectieblok"; block.DrawingNumber = "BLOK"; block.Revisions.Clear();
        block.Sheets = [sheet]; sheet.Views = [view]; sheet.LogoAssetId = null;
        sheet.TitleBlock = new(); sheet.ZoneColumns = sheet.ZoneRows = 0;
        view.Components.RemoveAll(p => !ids.Contains(p.EntityId));
        var entityIds = view.Components.Select(p => p.EntityId).ToHashSet();
        var explicitBranches = sourceView.Routes.Where(r => ids.Contains(r.Id) || ids.Contains(r.BranchId)).Select(r => r.BranchId).ToHashSet();
        var a = block.Assembly;
        a.Connectors.RemoveAll(c => !entityIds.Contains(c.Id)); a.Splices.RemoveAll(s => !entityIds.Contains(s.Id));
        a.ReferencePoints.RemoveAll(p => !entityIds.Contains(p.Id));
        a.Conductors.RemoveAll(w => !ids.Contains(w.Id));
        var wireIds = a.Conductors.Select(w => w.Id).ToHashSet();
        var branchIds = a.Conductors.SelectMany(w => w.BranchIds).Concat(explicitBranches).ToHashSet();
        a.Branches.RemoveAll(b => !branchIds.Contains(b.Id)); view.Routes.RemoveAll(r => !branchIds.Contains(r.BranchId));
        // Invisible route sections would make a standalone block misleading rather than reusable.
        if (branchIds.Any(id => !view.Routes.Any(r => r.BranchId == id)))
            throw new InvalidOperationException("Toon eerst de volledige route van alle geselecteerde aders in dit aanzicht.");
        view.WirePresentations.RemoveAll(p => !wireIds.Contains(p.ConductorId));
        view.Annotations.RemoveAll(a => !ids.Contains(a.Id));
        var notes = new List<string>(); var detached = 0;
        foreach (var w in a.Conductors)
            foreach (var e in new[] { w.From, w.To })
                if (e.Kind != EndpointKind.Free && (e.EntityId is null || !entityIds.Contains(e.EntityId)))
                {
                    e.Kind = EndpointKind.Free; e.EntityId = e.CavityId = null;
                    e.Description = "Extern uiteinde uit selectieblok; aansluiting kiezen"; detached++;
                }
        if (detached != 0) notes.Add($"{detached} externe draadeinden worden vrij ingevoegd; kies hun aansluiting expliciet.");
        foreach (var connector in a.Connectors)
        {
            if (connector.MatingConnectorId is { } mating && !entityIds.Contains(mating))
            { connector.MatingConnectorId = null; notes.Add($"Tegenconnector van {connector.Name} valt buiten de selectie en is niet gekopieerd."); }
            foreach (var cavity in connector.Cavities)
                if (cavity.State == CavityState.Occupied && !a.Conductors.Any(w => Uses(w.From, connector.Id, cavity.Id) || Uses(w.To, connector.Id, cavity.Id)))
                    cavity.State = CavityState.Unused;
            foreach (var material in connector.Accessories)
                if (material.BranchId is { } branch && !branchIds.Contains(branch)) material.BranchId = null;
        }
        foreach (var cable in a.Cables.ToArray())
        {
            var members = source.Assembly.Conductors.Where(w => w.CableId == cable.Id).Select(w => w.Id).ToArray();
            if (members.Length == 0 || members.Any(id => !wireIds.Contains(id)))
            {
                a.Cables.Remove(cable);
                foreach (var w in a.Conductors.Where(w => w.CableId == cable.Id))
                { w.CableId = null; notes.Add($"{w.Name}: onvolledige kabelgroep niet gekopieerd; eigen draadartikel controleren."); }
            }
        }
        foreach (var twist in a.TwistGroups.ToArray())
            if (!twist.ConductorIds.All(wireIds.Contains))
            {
                a.TwistGroups.Remove(twist);
                if (twist.ConductorIds.Any(wireIds.Contains)) notes.Add($"Twistgroep {twist.Name} is onvolledig en niet gekopieerd.");
            }
        a.Materials.RemoveAll(m => m.BranchId is null || !branchIds.Contains(m.BranchId) ||
            source.Assembly.Conductors.Where(w => w.BranchIds.Contains(m.BranchId)).Any(w => !wireIds.Contains(w.Id)));
        var linkedIds = entityIds.Concat(wireIds).Concat(branchIds).ToHashSet();
        foreach (var annotation in view.Annotations)
            if (annotation.EntityId is { } linked && !linkedIds.Contains(linked))
            { annotation.EntityId = null; notes.Add($"Koppeling van annotatie {annotation.Kind} valt buiten de selectie en is losgemaakt."); }
        view.DrawOrder.RemoveAll(id => !linkedIds.Contains(id) && !view.Annotations.Any(n => n.Id == id));
        var partIds = a.Connectors.Select(c => c.PartId).Concat(a.Connectors.Select(c => c.MatingPartId))
            .Concat(a.Connectors.SelectMany(c => c.Cavities).SelectMany(c => new[] { c.TerminalPartId, c.SealPartId, c.PlugPartId }))
            .Concat(a.Connectors.SelectMany(c => c.Accessories).Select(m => m.PartId)).Concat(a.Splices.Select(s => s.PartId))
            .Concat(a.Conductors.Select(w => w.WirePartId)).Concat(a.Cables.Select(c => c.PartId)).Concat(a.Materials.Select(m => m.PartId))
            .Where(id => id is not null).ToHashSet();
        block.Parts.RemoveAll(p => !partIds.Contains(p.Id));
        var assets = block.Parts.Select(p => p.SourceAssetId).Concat(view.Annotations.Select(n => n.AssetId)).Where(id => id is not null).ToHashSet();
        block.Assets.RemoveAll(a => !assets.Contains(a.Id));
        block.Notes = string.Join("\n", notes.Distinct());
        if (view.Components.Count + view.Routes.Count + view.Annotations.Count == 0)
            throw new InvalidOperationException("Selecteer minstens één zichtbaar object.");
        GraphEdit.RequireValid(block);
        return block;
    }

    public static SelectionInsertProposal PrepareInsert(HarnessProject target, DrawingView targetView, HarnessProject block,
        Point2 offset, int count = 1, Point2? step = null)
    {
        if (count is < 1 or > 1000 || !Geometry.IsFinite(offset) || step is { } delta && !Geometry.IsFinite(delta))
            throw new ArgumentException("Aantal moet 1–1000 zijn en afstanden moeten eindig zijn.");
        if (!target.Sheets.SelectMany(s => s.Views).Contains(targetView) || block.Sheets.Count != 1 || block.Sheets[0].Views.Count != 1)
            throw new ArgumentException("Een selectieblok vereist precies één blad en één aanzicht.");
        GraphEdit.RequireValid(block);
        var after = GraphEdit.Clone(target); var view = GraphEdit.View(after, targetView.Id);
        var selected = new List<string>(); var summary = new List<string>();
        if (!string.IsNullOrWhiteSpace(block.Notes)) summary.AddRange(block.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        for (var n = 0; n < count; n++)
        {
            var copy = GraphEdit.Clone(block); var sourceView = copy.Sheets[0].Views[0];
            var translation = offset + (step ?? new Point2()) * n;
            if (view.Mode == LayoutMode.Scaled)
            {
                if (sourceView.Components.Any(p => Math.Abs(p.DisplayScale - 1) > 1e-9) || sourceView.WirePresentations.Any(p => p.Routes.Count > 0))
                    throw new InvalidOperationException("Dit blok bevat vrije opmaak. Gebruik een vrij aanzicht of herstel eerst de schaalgeometrie.");
                foreach (var route in sourceView.Routes)
                    if (Math.Abs(Geometry.Length(route) - copy.Assembly.Branches.Single(b => b.Id == route.BranchId).PhysicalLengthMm) > Geometry.LengthToleranceMm)
                        throw new InvalidOperationException("De blokgeometrie komt niet overeen met de fysieke maat. Voeg in vrije opmaak in.");
            }
            var idMap = RemapDefinitions(after, copy);
            var allIds = copy.Assembly.Connectors.Select(c => c.Id).Concat(copy.Assembly.Connectors.SelectMany(c => c.Cavities).Select(c => c.Id))
                .Concat(copy.Assembly.Connectors.SelectMany(c => c.Accessories).Select(m => m.Id)).Concat(copy.Assembly.Splices.Select(s => s.Id))
                .Concat(copy.Assembly.ReferencePoints.Select(p => p.Id)).Concat(copy.Assembly.Conductors.Select(w => w.Id))
                .Concat(copy.Assembly.Branches.Select(b => b.Id)).Concat(copy.Assembly.Cables.Select(c => c.Id))
                .Concat(copy.Assembly.TwistGroups.Select(t => t.Id)).Concat(copy.Assembly.Materials.Select(m => m.Id))
                .Concat(sourceView.Components.Select(c => c.Id)).Concat(sourceView.Routes.Select(r => r.Id)).Concat(sourceView.Annotations.Select(a => a.Id));
            foreach (var id in allIds) idMap[id] = Ids.New();
            string Map(string id) => idMap.GetValueOrDefault(id, id);
            string? Optional(string? id) => id is null ? null : Map(id);
            void End(Endpoint e) { e.EntityId = Optional(e.EntityId); e.CavityId = Optional(e.CavityId); }
            void Material(MaterialItem m) { m.Id = Map(m.Id); m.PartId = Map(m.PartId); m.BranchId = Optional(m.BranchId); }
            foreach (var c in copy.Assembly.Connectors)
            {
                c.Id = Map(c.Id); c.PartId = Map(c.PartId); c.MatingPartId = Optional(c.MatingPartId); c.MatingConnectorId = Optional(c.MatingConnectorId);
                c.Name = UniqueName(c.Name, after.Assembly.Connectors.Select(x => x.Name).Concat(copy.Assembly.Connectors.TakeWhile(x => x != c).Select(x => x.Name)));
                foreach (var cavity in c.Cavities)
                { cavity.Id = Map(cavity.Id); cavity.PinDefinitionId = Map(cavity.PinDefinitionId); cavity.TerminalPartId = Optional(cavity.TerminalPartId); cavity.SealPartId = Optional(cavity.SealPartId); cavity.PlugPartId = Optional(cavity.PlugPartId); }
                foreach (var m in c.Accessories) Material(m);
            }
            foreach (var s in copy.Assembly.Splices)
            { s.Id = Map(s.Id); s.PartId = Optional(s.PartId); s.Name = UniqueName(s.Name, after.Assembly.Splices.Select(x => x.Name).Concat(copy.Assembly.Splices.TakeWhile(x => x != s).Select(x => x.Name))); }
            foreach (var r in copy.Assembly.ReferencePoints)
            { r.Id = Map(r.Id); r.Name = UniqueName(r.Name, after.Assembly.ReferencePoints.Select(x => x.Name).Concat(copy.Assembly.ReferencePoints.TakeWhile(x => x != r).Select(x => x.Name))); }
            foreach (var w in copy.Assembly.Conductors)
            {
                w.Id = Map(w.Id); w.Name = UniqueName(w.Name, after.Assembly.Conductors.Select(x => x.Name).Concat(copy.Assembly.Conductors.TakeWhile(x => x != w).Select(x => x.Name)));
                End(w.From); End(w.To); w.BranchIds = w.BranchIds.Select(Map).ToList(); w.WirePartId = Optional(w.WirePartId); w.CableId = Optional(w.CableId);
            }
            foreach (var b in copy.Assembly.Branches) { b.Id = Map(b.Id); Translate(b, translation); }
            foreach (var c in copy.Assembly.Cables) { c.Id = Map(c.Id); c.PartId = Map(c.PartId); c.Name = UniqueName(c.Name, after.Assembly.Cables.Select(x => x.Name)); }
            foreach (var t in copy.Assembly.TwistGroups) { t.Id = Map(t.Id); t.ConductorIds = t.ConductorIds.Select(Map).ToList(); }
            foreach (var m in copy.Assembly.Materials) Material(m);
            var targetSheet = after.Sheets.Single(s => s.Views.Contains(view));
            var layers = new Dictionary<string, string>();
            foreach (var layer in copy.Sheets[0].Layers)
            {
                var existing = targetSheet.Layers.Find(l => l.Id == layer.Id);
                if (existing is not null && existing.Name == layer.Name && existing.Locked == layer.Locked && existing.Visible == layer.Visible && existing.Print == layer.Print)
                    layers[layer.Id] = existing.Id;
                else
                { var prior = layer.Id; layer.Id = Ids.New(); layers[prior] = layer.Id; targetSheet.Layers.Add(layer); }
            }
            foreach (var p in sourceView.Components)
            { p.Id = Map(p.Id); p.EntityId = Map(p.EntityId); p.Position += translation; p.LayerId = layers[p.LayerId]; selected.Add(p.EntityId); }
            foreach (var r in sourceView.Routes)
            { r.Id = Map(r.Id); r.BranchId = Map(r.BranchId); r.LayerId = layers[r.LayerId]; Translate(r, translation); }
            foreach (var p in sourceView.WirePresentations)
            {
                p.ConductorId = Map(p.ConductorId); if (p.LabelPosition is { } label) p.LabelPosition = label + translation;
                foreach (var r in p.Routes) { r.BranchId = Map(r.BranchId); TransformGeometry(r.Geometry, p => p + translation, p => p); }
            }
            foreach (var a in sourceView.Annotations)
            { a.Id = Map(a.Id); a.EntityId = Optional(a.EntityId); a.AssetId = Optional(a.AssetId); a.LayerId = layers[a.LayerId]; a.Points = a.Points.Select(p => p + translation).ToList(); selected.Add(a.Id); }
            selected.AddRange(copy.Assembly.Conductors.Select(w => w.Id));
            selected.AddRange(copy.Assembly.Branches.Where(b => !copy.Assembly.Conductors.Any(w => w.BranchIds.Contains(b.Id))).Select(b => b.Id));
            var aTarget = after.Assembly; var aCopy = copy.Assembly;
            aTarget.Connectors.AddRange(aCopy.Connectors); aTarget.Splices.AddRange(aCopy.Splices); aTarget.ReferencePoints.AddRange(aCopy.ReferencePoints);
            aTarget.Conductors.AddRange(aCopy.Conductors); aTarget.Branches.AddRange(aCopy.Branches); aTarget.Cables.AddRange(aCopy.Cables);
            aTarget.TwistGroups.AddRange(aCopy.TwistGroups); aTarget.Materials.AddRange(aCopy.Materials);
            view.Components.AddRange(sourceView.Components); view.Routes.AddRange(sourceView.Routes); view.Annotations.AddRange(sourceView.Annotations);
            view.WirePresentations.AddRange(sourceView.WirePresentations); view.DrawOrder.AddRange(sourceView.DrawOrder.Select(Map));
            summary.Add($"Kopie {n + 1}: {aCopy.Conductors.Count} aders, {aCopy.Connectors.Count} connectoren, {aCopy.Splices.Count} splices, {aCopy.ReferencePoints.Count} referentiepunten.");
        }
        GraphEdit.RequireValid(after);
        return new(GraphEdit.Command("Selectieblok invoegen", target, after), after, selected.Distinct().ToArray(), summary);
    }

    private static Dictionary<string, string> RemapDefinitions(HarnessProject target, HarnessProject block)
    {
        var map = new Dictionary<string, string>();
        foreach (var asset in block.Assets)
        {
            var existing = target.Assets.Find(a => a.Id == asset.Id);
            if (existing is not null && (existing.FileName != asset.FileName || existing.MediaType != asset.MediaType || !existing.Data.AsSpan().SequenceEqual(asset.Data)))
                throw new InvalidOperationException("Een bronbijlage heeft dezelfde identiteit maar andere inhoud.");
            if (existing is null) target.Assets.Add(GraphEdit.Clone(asset));
        }
        foreach (var part in block.Parts)
        {
            var existing = target.Parts.Find(p => p.Id == part.Id);
            if (existing is not null && System.Text.Json.JsonSerializer.Serialize(existing) != System.Text.Json.JsonSerializer.Serialize(part))
                throw new InvalidOperationException("Een onderdeelrevisie heeft dezelfde identiteit maar andere inhoud. Importeer eerst een nieuwe revisie.");
            if (existing is null) target.Parts.Add(GraphEdit.Clone(part));
        }
        return map;
    }
    internal static string UniqueName(string preferred, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = string.IsNullOrWhiteSpace(preferred) ? "Object" : preferred;
        if (!names.Contains(stem)) return stem;
        for (var i = 2; ; i++) if (!names.Contains(stem + "_" + i)) return stem + "_" + i;
    }
    private static bool Uses(Endpoint e, string entity, string cavity) => e.Kind == EndpointKind.Cavity && e.EntityId == entity && e.CavityId == cavity;
    private static void Translate(RouteBranch b, Point2 offset)
    { b.Points = b.Points.Select(p => p + offset).ToList(); if (b.Geometry is { } g) TransformGeometry(g, p => p + offset, p => p); }
    private static void Translate(PlacedRoute r, Point2 offset)
    { r.Points = r.Points.Select(p => p + offset).ToList(); if (r.Geometry is { } g) TransformGeometry(g, p => p + offset, p => p); }
    internal static void TransformGeometry(RouteGeometry geometry, Func<Point2, Point2> point, Func<Point2, Point2> vector)
    { foreach (var k in geometry.Knots) { k.Position = point(k.Position); k.InHandle = vector(k.InHandle); k.OutHandle = vector(k.OutHandle); } }
}
