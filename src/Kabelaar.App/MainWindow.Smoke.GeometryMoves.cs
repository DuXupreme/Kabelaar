using System.Text.Json;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async Task CheckGeometryMovesAsync(Action<bool, string> check)
    {
        var original = _project;
        var originalScope = _wireDragScope;
        var originalMode = _endpointMoveMode;
        var originalBundle = _editWholeBundle;
        try
        {
            var f = await Fixture();
            var before = JsonSerializer.Serialize(f.Project); var token = _history.StateToken;
            var startHandle = Canvas.GeometryHandles.First(h => h.Kind == GeometryHandleKind.Knot);
            var start = Gesture(startHandle);
            _endpointMoveMode = EndpointMoveMode.KeepConnection;
            BeginGeometryEdit(start); UpdateGeometryEdit(start with { Current = start.Current + new Point2(0, 18) });
            check(!Canvas.PreviewRejected && JsonSerializer.Serialize(f.Project) == before,
                "Verbinding behouden: een vast uiteinde heeft een voorvertoning zonder modelwijziging");
            CommitGeometryEdit(start with { Current = start.Current + new Point2(0, 18) }); await Idle();
            check(Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id).Knots[0].Position == f.FromPlacement.Position &&
                f.First.From.CavityId == f.From.Cavities[0].Id && f.FromPlacement.Position == new Point2(30, 80),
                "Een behouden aansluiting blijft op dezelfde pin en verplaatst de connector niet");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Undo herstelt ook een geblokkeerde endpointverplaatsing zonder verloren gegevens");
            var bendHandle = Canvas.GeometryHandles.Where(h => h.Kind == GeometryHandleKind.Knot).ElementAt(2);
            var captureGesture = Gesture(bendHandle);
            BeginGeometryEdit(captureGesture); UpdateGeometryEdit(captureGesture with { Current = captureGesture.Current + new Point2(0, 20) });
            Canvas.CancelGeometryCapture();
            check(!Canvas.HasGeometryPreview && before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Dezelfde annulering als bij verloren muiscapture verwijdert alleen de tijdelijke voorvertoning");
            BeginGeometryEdit(captureGesture); UpdateGeometryEdit(captureGesture with { Current = captureGesture.Current + new Point2(0, 20) });
            Canvas.Cancel();
            check(!Canvas.HasGeometryPreview && before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "De Escape-annuleringsroute behoudt project en undo-geschiedenis");

            f = await Fixture();
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            startHandle = Canvas.GeometryHandles.First(h => h.Kind == GeometryHandleKind.Knot);
            start = Gesture(startHandle);
            _endpointMoveMode = EndpointMoveMode.MoveConnectedNode;
            var moved = start with { Current = start.Current + new Point2(0, 16) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved);
            var expected = ToModel(moved.Current);
            var previewFirst = Canvas.PreviewPrimitives.OfType<ScenePath>().First(p => p.EntityId == f.First.Id && p.Category == "wire").Points.ToArray();
            check(JsonSerializer.Serialize(f.Project) == before && !Canvas.PreviewRejected,
                "Aangesloten knooppunt meenemen houdt model en geschiedenis tijdens de voorvertoning intact");
            CommitGeometryEdit(moved); await Idle();
            var committedFirst = Canvas.Scene!.Primitives.OfType<ScenePath>().First(p => p.EntityId == f.First.Id && p.Category == "wire").Points;
            check(f.FromPlacement.Position == expected && f.First.From.EntityId == f.From.Id && f.Second.From.EntityId == f.From.Id &&
                SceneBuilder.ConductorPoints(f.Project, f.View, f.Second)[0] == expected &&
                SamePoints(previewFirst, committedFirst),
                "Meenemen verplaatst de echte connector en alle aangesloten uiteinden; geselecteerde preview en resultaat stemmen overeen");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Eén undo herstelt connector, aangesloten aders en individuele geometrie samen");

            f = await Fixture();
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            startHandle = Canvas.GeometryHandles.First(h => h.Kind == GeometryHandleKind.Knot);
            start = Gesture(startHandle);
            _endpointMoveMode = EndpointMoveMode.DetachThisEndpoint;
            moved = start with { Current = start.Current + new Point2(0, 19) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved); CommitGeometryEdit(moved); await Idle();
            check(f.First.From.Kind == EndpointKind.Free && f.First.To.CavityId == f.To.Cavities[0].Id &&
                f.Second.From.CavityId == f.From.Cavities[0].Id && f.FromPlacement.Position == new Point2(30, 80) &&
                Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id).Knots[0].Position == ToModel(moved.Current) &&
                f.Project.Assembly.Conductors.Count == 2 && f.Branch.PhysicalLengthMm == 200,
                "Losmaken verandert alleen het verplaatste aderuiteinde en behoudt de andere aansluiting, ader en fysieke maat");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Eén undo herstelt de losgemaakte aansluiting en pinbezetting");

            f = await Fixture();
            Canvas.Select(f.Second.Id, true); await Idle();
            _wireDragScope = WireDragScope.Segment;
            _endpointMoveMode = EndpointMoveMode.DetachThisEndpoint;
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            start = new(ToSheet(new(55, 80)), ToSheet(new(55, 80)), new HashSet<string> { f.First.Id, f.Second.Id });
            moved = start with { Current = start.Current + new Point2(0, 17) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved);
            check(Canvas.HasGeometryPreview && !Canvas.PreviewRejected &&
                before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Twee geselecteerde aders losmaken toont beide vrije uiteinden zonder voortijdige wijziging van pinbezetting");
            CommitGeometryEdit(moved); await Idle();
            var afterMultiDetach = JsonSerializer.Serialize(f.Project);
            check(f.First.From.Kind == EndpointKind.Free && f.Second.From.Kind == EndpointKind.Free &&
                f.From.Cavities[0].State == CavityState.Unused && f.To.Cavities[0].State == CavityState.Occupied &&
                f.First.To.CavityId == f.To.Cavities[0].Id && f.Second.To.CavityId == f.To.Cavities[0].Id &&
                f.Project.Assembly.Conductors.Count == 2 && f.Branch.PhysicalLengthMm == 200,
                "Gelijktijdig losmaken van alle aders maakt de gedeelde cavity ongebruikt en behoudt de andere aansluitingen");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken &&
                f.From.Cavities[0].State == CavityState.Occupied,
                "Eén undo herstelt beide draadeinden, pinbezetting en geometrie na gezamenlijk losmaken");
            _history.Redo(); await Idle();
            check(afterMultiDetach == JsonSerializer.Serialize(f.Project) && f.From.Cavities[0].State == CavityState.Unused,
                "Redo berekent de gedeelde pinbezetting opnieuw en herstelt exact dezelfde gezamenlijke losmaking");

            f = await Fixture();
            _wireDragScope = WireDragScope.Segment;
            start = new(ToSheet(new(105, 80)), ToSheet(new(105, 80)), new HashSet<string> { f.First.Id });
            moved = start with { Current = start.Current + new Point2(0, 15) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved); CommitGeometryEdit(moved); await Idle();
            var segmentShape = Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id);
            check(segmentShape.Knots[1].Position.Y == 95 && segmentShape.Knots[2].Position.Y == 95 &&
                segmentShape.Knots[3].Position.Y == 80 && segmentShape.Knots[0].Position.Y == 80 &&
                segmentShape.Segments.Count == 4 && f.Project.Assembly.Conductors.Count == 2,
                "Segmentslepen verplaatst uitsluitend de twee segmentknopen en behoudt aangrenzende segmenten en aderaantallen");
            _history.Undo(); await Idle();
            _wireDragScope = WireDragScope.WholeWire;
            BeginGeometryEdit(start); UpdateGeometryEdit(moved); CommitGeometryEdit(moved); await Idle();
            var wholeShape = Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id);
            check(wholeShape.Knots.Skip(1).Take(3).All(k => k.Position.Y == 95) &&
                wholeShape.Knots[0].Position == new Point2(30, 80) && wholeShape.Knots[^1].Position == new Point2(230, 80),
                "Hele-ader-slepen neemt alle tussenpunten mee en behoudt de twee gekozen aansluitingen");

            f = await Fixture();
            _history.Execute(HarnessOperations.SetWireRouteGeometry(f.Project, f.View, f.First, f.Route,
                Geometry.WithBend(Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id), 8)));
            _history.Execute(HarnessOperations.SetWireRouteGeometry(f.Project, f.View, f.Second, f.Route,
                Geometry.WithBend(Geometry.EditableRouteGeometry(f.View, f.Route, f.Second.Id), -8)));
            Canvas.Select(f.First.Id); await Idle();
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            _editWholeBundle = true;
            start = new(ToSheet(new(105, 80)), ToSheet(new(105, 80)), new HashSet<string> { f.First.Id });
            moved = start with { Current = start.Current + new Point2(0, 12) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved); CommitGeometryEdit(moved); await Idle();
            check(f.View.WirePresentations.All(p => p.Routes.Count == 0) && f.Route.Geometry is not null &&
                f.Branch.PhysicalLengthMm == 200 && f.Project.Assembly.Conductors.Count == 2,
                "Hele bundel bewerken vervangt afzonderlijke vormen door één gedeelde route zonder materiaal- of lengtewijziging");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "Undo herstelt beide afzonderlijke adervormen na een bundelbewerking");

            f = await Fixture();
            Canvas.Select(f.From.Id, true); await Idle();
            start = new(ToSheet(new(105, 80)), ToSheet(new(105, 80)), new HashSet<string> { f.First.Id, f.From.Id });
            moved = start with { Current = start.Current + new Point2(0, 11) };
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            BeginGeometryEdit(start); UpdateGeometryEdit(moved);
            previewFirst = Canvas.PreviewPrimitives.OfType<ScenePath>().First(p => p.EntityId == f.First.Id && p.Category == "wire").Points.ToArray();
            CommitGeometryEdit(moved); await Idle();
            committedFirst = Canvas.Scene!.Primitives.OfType<ScenePath>().First(p => p.EntityId == f.First.Id && p.Category == "wire").Points;
            check(f.FromPlacement.Position == new Point2(30, 91) &&
                Geometry.EditableRouteGeometry(f.View, f.Route, f.First.Id).Knots[0].Position == f.FromPlacement.Position &&
                SamePoints(previewFirst, committedFirst),
                "Gemengde selectie van connector en ader overschrijft de verplaatste pin niet met een oude positie");
            _history.Undo(); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken,
                "De gemengde verplaatsing wordt als één transactie teruggedraaid");

            f = await Fixture();
            f.View.Mode = LayoutMode.Scaled; f.Branch.LengthLocked = true;
            Refresh(); Canvas.Select(f.First.Id); await Idle();
            before = JsonSerializer.Serialize(f.Project); token = _history.StateToken;
            var middle = Canvas.GeometryHandles.Where(h => h.Kind == GeometryHandleKind.Knot).ElementAt(2);
            start = Gesture(middle); moved = start with { Current = start.Current + new Point2(0, 25) };
            BeginGeometryEdit(start); UpdateGeometryEdit(moved);
            check(Canvas.HasGeometryPreview && Canvas.PreviewRejected && before == JsonSerializer.Serialize(f.Project),
                "Een lengtevergrendeling toont de afgewezen werkelijke geometrie zonder het schaalvaste model te wijzigen");
            CommitGeometryEdit(moved); await Idle();
            check(before == JsonSerializer.Serialize(f.Project) && token == _history.StateToken && !Canvas.HasGeometryPreview,
                "Een afgewezen lengtewijziging maakt geen undo-stap en laat alle gegevens intact");
        }
        finally
        {
            CancelGeometryEdit(); _wireDragScope = originalScope; _endpointMoveMode = originalMode; _editWholeBundle = originalBundle;
            ReplaceProject(original, null, null);
        }

        CanvasEditGesture Gesture(CanvasGeometryHandle handle) => new(handle.Position, handle.Position, new HashSet<string> { Canvas.Selection.First() }, handle);
        static bool SamePoints(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First.DistanceTo(p.Second) < 1e-8);
        async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task<(HarnessProject Project, DrawingView View, RouteBranch Branch, PlacedRoute Route,
            Conductor First, Conductor Second, ConnectorInstance From, ConnectorInstance To, PlacedComponent FromPlacement)> Fixture()
        {
            _editWholeBundle = false; _endpointMoveMode = EndpointMoveMode.KeepConnection; _wireDragScope = WireDragScope.WholeWire;
            var project = new HarnessProject { Title = "Synthetische verplaatscontrole" };
            var view = project.Sheets[0].Views[0]; view.Mode = LayoutMode.Free;
            var part = new PartDefinition { Name = "Test connector", Kind = PartKind.Housing, Pins = [new() { Label = "1", Position = new(0, 0), MaxWires = 4 }] };
            project.Parts.Add(part);
            var from = HarnessOperations.NewConnector(part, "J1"); var to = HarnessOperations.NewConnector(part, "J2");
            project.Assembly.Connectors.AddRange([from, to]);
            var fromPlacement = new PlacedComponent { EntityId = from.Id, Position = new(30, 80) };
            view.Components.AddRange([fromPlacement, new() { EntityId = to.Id, Position = new(230, 80) }]);
            var branch = new RouteBranch { Points = [new(30, 80), new(80, 80), new(130, 80), new(180, 80), new(230, 80)], PhysicalLengthMm = 200 };
            var route = new PlacedRoute { BranchId = branch.Id, Points = [.. branch.Points] };
            project.Assembly.Branches.Add(branch); view.Routes.Add(route);
            Conductor Wire(string name) => new() { Name = name, BranchIds = [branch.Id],
                From = new() { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = from.Cavities[0].Id },
                To = new() { Kind = EndpointKind.Cavity, EntityId = to.Id, CavityId = to.Cavities[0].Id } };
            var first = Wire("W1"); var second = Wire("W2"); second.Color = "#2563EB";
            project.Assembly.Conductors.AddRange([first, second]);
            from.Cavities[0].State = to.Cavities[0].State = CavityState.Occupied;
            ReplaceProject(project, null, null); Canvas.Select(first.Id); await Idle();
            return (project, view, branch, route, first, second, from, to, fromPlacement);
        }
    }
}
