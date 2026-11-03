using System.Text.Json;

namespace Kabelaar.Core;

/// <summary>Detached preparation for explicit, infrequent bulk edits; live view identities are retained.</summary>
internal static class GraphEdit
{
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static DrawingView View(HarnessProject project, string id) => project.Sheets.SelectMany(s => s.Views).Single(v => v.Id == id);
    public static string Fingerprint(HarnessProject project) => JsonSerializer.Serialize(new
    {
        project.SchemaVersion, project.Assembly, project.Parts, project.Assets, project.Sheets
    });
    public static IEditCommand Command(string name, HarnessProject target, HarnessProject after)
    {
        var before = Capture(target);
        var prepared = Capture(Clone(after));
        var fingerprint = Fingerprint(target);
        return new DelegateCommand(name, () =>
        {
            if (Fingerprint(target) != fingerprint)
                throw new InvalidOperationException("Het project is gewijzigd sinds dit voorstel. Maak een nieuw voorstel.");
            Apply(target, prepared);
        }, () => Apply(target, before));
    }
    private sealed record ViewState(string Id, List<PlacedComponent> Components, List<PlacedRoute> Routes,
        List<WirePresentation> WirePresentations, List<Annotation> Annotations, List<string> DrawOrder);
    private sealed record SheetState(string Id, List<DrawingLayer> Layers, ViewState[] Views);
    private sealed record State(HarnessAssembly Assembly, List<PartDefinition> Parts, List<ProjectAsset> Assets, SheetState[] Sheets);
    private static State Capture(HarnessProject p) => new(p.Assembly, p.Parts, p.Assets, p.Sheets.Select(s =>
        new SheetState(s.Id, s.Layers, s.Views.Select(v => new ViewState(v.Id, v.Components, v.Routes,
            v.WirePresentations, v.Annotations, v.DrawOrder)).ToArray())).ToArray());
    private static void Apply(HarnessProject p, State state)
    {
        // Every destination is checked before any mutation, including when undoing after document navigation.
        var sheets = state.Sheets.Select(s => (State: s, Target: p.Sheets.Single(x => x.Id == s.Id))).ToArray();
        var views = sheets.SelectMany(s => s.State.Views.Select(v => (State: v, Target: s.Target.Views.Single(x => x.Id == v.Id)))).ToArray();
        p.Assembly = state.Assembly; p.Parts = state.Parts; p.Assets = state.Assets;
        foreach (var s in sheets) s.Target.Layers = s.State.Layers;
        foreach (var v in views)
        {
            v.Target.Components = v.State.Components; v.Target.Routes = v.State.Routes;
            v.Target.WirePresentations = v.State.WirePresentations; v.Target.Annotations = v.State.Annotations;
            v.Target.DrawOrder = v.State.DrawOrder;
        }
    }
    public static void RequireValid(HarnessProject project)
    {
        var errors = StructuralValidator.Validate(project);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors.Take(8)));
    }
}
