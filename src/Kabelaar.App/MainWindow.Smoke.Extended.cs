using System.Text.Json;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async Task CheckExtendedEditingAsync(string output, Action<bool, string> check)
    {
        ReplaceProject(DemoFactory.Create(), null, null);
        var physical = JsonSerializer.Serialize(_project.Assembly);
        var wire = _project.Assembly.Conductors[0]; Canvas.Select(wire.Id);
        var style = WireStyle(wire); style.LabelText = "Vrij label 0.9.4"; style.ShowLabel = true; style.LabelPosition = new(40, 35);
        _history.Execute(WirePresentationCommand(wire, style, "Vrij label testen"));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(Canvas.Scene!.Primitives.OfType<SceneText>().Any(p => p.EntityId == wire.Id && p.Text == style.LabelText), "Vrij draadlabel bereikt de zichtbare tekening zonder productiegegevens te veranderen");
        check(JsonSerializer.Serialize(_project.Assembly) == physical, "Labelwijziging behoudt fysieke aders, aansluitingen en kniplengtes");
        Canvas.Select(wire.Id); ShowProperties();
        var handle = Canvas.GeometryHandles.Single(h => h.WireId == wire.Id && h.Kind == GeometryHandleKind.Label);
        var token = _history.StateToken;
        var gesture = new CanvasEditGesture(handle.Position, handle.Position + new Point2(8, 5), new HashSet<string> { wire.Id }, handle);
        BeginGeometryEdit(gesture with { Current = gesture.Start }); UpdateGeometryEdit(gesture);
        check(_history.StateToken == token && _view.WirePresentations.Single(p => p.ConductorId == wire.Id).LabelPosition == new Point2(40, 35), "Labelsleep toont een tijdelijke voorvertoning zonder de opgeslagen labelpositie te wijzigen");
        CancelGeometryEdit(); check(!Canvas.HasGeometryPreview && _history.StateToken == token, "Annuleren van een labelsleep laat tekening en geschiedenis intact");
        BeginGeometryEdit(gesture with { Current = gesture.Start }); CommitGeometryEdit(gesture);
        check(_view.WirePresentations.Single(p => p.ConductorId == wire.Id).LabelPosition != new Point2(40, 35), "Bevestigen van labelsleep wijzigt de positie");
        _history.Undo(); check(_view.WirePresentations.Single(p => p.ConductorId == wire.Id).LabelPosition == new Point2(40, 35), "Eén undo herstelt de vorige labelpositie");
        style = WireStyle(wire); style.LabelText = "";
        _history.Execute(WirePresentationCommand(wire, style, "Leeg label testen"));
        check(!Canvas.Scene!.Primitives.Any(p => p.EntityId == wire.Id && p.Category == "wire-label"), "Expliciet leeg draadlabel blijft verborgen");
        _history.Undo();

        var table = new Annotation { Kind = AnnotationKind.FreeTable, Points = [new(25, 35)], Columns = ["Naam", "Waarde"],
            Rows = [new TableRow { Cells = ["Test", "Vrije cel"] }], WidthMm = 60, RowHeightMm = 8 };
        Add("Vrije tabel testen", _view.Annotations, table); Canvas.Select(table.Id); ShowProperties();
        var beforeTable = JsonSerializer.Serialize(table);
        var corner = Canvas.GeometryHandles.Single(h => h.Kind == GeometryHandleKind.TableCorner);
        var resize = new CanvasEditGesture(corner.Position, corner.Position + new Point2(10, 4), new HashSet<string> { table.Id }, corner);
        BeginGeometryEdit(resize with { Current = resize.Start }); UpdateGeometryEdit(resize);
        check(JsonSerializer.Serialize(table) == beforeTable && Canvas.PreviewPrimitives.OfType<SceneText>().Any(p => p.Text == "Vrije cel"), "Tabelslepen toont echte cellen zonder voortijdige documentwijziging");
        CommitGeometryEdit(resize);
        check(_view.Annotations.Single(a => a.Id == table.Id).WidthMm > table.WidthMm, "Versleepbaar tabelhandvat wijzigt de breedte");
        _history.Undo(); check(JsonSerializer.Serialize(_view.Annotations.Single(a => a.Id == table.Id)) == beforeTable, "Undo herstelt tabelcellen, afmetingen en opmaak exact");

        var leader = new Annotation { Kind = AnnotationKind.Leader, Points = [new(20, 20), new(35, 30)], Text = "Aanwijzing", ArrowSizeMm = 3, TextFrame = true };
        Add("Leader testen", _view.Annotations, leader); Canvas.Select(leader.Id); ShowProperties();
        var leaderHandle = Canvas.GeometryHandles.Single(h => h.Kind == GeometryHandleKind.AnnotationPoint && h.KnotId == "0");
        var move = new CanvasEditGesture(leaderHandle.Position, leaderHandle.Position + new Point2(4, 2), new HashSet<string> { leader.Id }, leaderHandle);
        BeginGeometryEdit(move with { Current = move.Start }); CommitGeometryEdit(move);
        check(_view.Annotations.Single(a => a.Id == leader.Id).Points[1] == leader.Points[1], "Leaderhandvat verplaatst alleen het gekozen punt"); _history.Undo();

        var prior = JsonSerializer.Serialize(_project);
        InsertClipboardContent("Geplakte testtekst", null, 0, 0, false);
        check(_view.Annotations.Any(a => a.Text == "Geplakte testtekst"), "Vervangbare klembordinvoer plaatst tekst zonder toegang tot het echte klembord");
        _history.Undo(); check(JsonSerializer.Serialize(_project) == prior, "Undo van plakken herstelt het document exact");
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        InsertClipboardContent(null, encoded.ToArray(), 2, 2, false);
        var pasted = _view.Annotations.Last();
        check(pasted.Kind == AnnotationKind.Image && _project.Assets.Any(a => a.Id == pasted.AssetId), "Geplakte afbeelding wordt een ingesloten afdrukbare asset");
        _history.Undo(); check(JsonSerializer.Serialize(_project) == prior, "Undo verwijdert ook de nieuw ingevoegde afbeeldingsasset");

        EndpointSnapCheck.IsChecked = true;
        var snap = SnapToEndpoint(ToSheet(leader.Points[0]) + new Point2(.2, .2));
        check(snap.DistanceTo(ToSheet(leader.Points[0])) < .001, "Leaderuiteinden zijn snapdoelen zonder fysieke verbinding te creëren");
        EndpointSnapCheck.IsChecked = false; var free = ToSheet(leader.Points[0]) + new Point2(.2, .2);
        check(SnapToEndpoint(free) == free, "Endpoint-snapping heeft een eigen schakelaar"); EndpointSnapCheck.IsChecked = true;

        var block = SelectionOperations.Capture(_project, _view, new[] { wire.Id });
        var insert = SelectionOperations.PrepareInsert(_project, _view, block, new(20, 20));
        var untouched = JsonSerializer.Serialize(_project); var beforeCount = _project.Assembly.Conductors.Count;
        check(insert.PreviewProject.Assembly.Conductors.Count == beforeCount + 1 && JsonSerializer.Serialize(_project) == untouched, "Kopievoorstel toont extra fysieke ader zonder het project te wijzigen");
        _history.Execute(insert.Command); check(_project.Assembly.Conductors.Count == beforeCount + 1, "Selectiekopie gebruikt de gezamenlijke bewerking");
        _history.Undo(); check(JsonSerializer.Serialize(_project) == untouched, "Undo van kopie herstelt alle fysieke en getekende gegevens");

        var path = System.IO.Path.Combine(output, "Schema4-editor.kabelaar");
        await _store.SaveAsync(_project, path); var loaded = (await _store.LoadAsync(path)).Project;
        check(loaded.SchemaVersion == 4 && loaded.Sheets[0].Views[0].Annotations.Any(a => a.Kind == AnnotationKind.FreeTable && a.Rows[0].Cells[1] == "Vrije cel"), "Schema4 bewaart vrije tabelcellen en editorwijzigingen");
        var reversed = new HarnessProject(); var reversedView = reversed.Sheets[0].Views[0]; reversedView.Mode = LayoutMode.Free;
        var firstBranch = new RouteBranch { Points = [new(0, 0), new(10, 0)], PhysicalLengthMm = 10 };
        var lastBranch = new RouteBranch { Points = [new(20, 0), new(10, 0)], PhysicalLengthMm = 10 };
        var reversedWire = new Conductor { BranchIds = [firstBranch.Id, lastBranch.Id] };
        reversed.Assembly.Branches.AddRange([firstBranch, lastBranch]); reversed.Assembly.Conductors.Add(reversedWire);
        reversedView.Routes.AddRange([new PlacedRoute { BranchId = firstBranch.Id, Points = firstBranch.Points.ToList() },
            new PlacedRoute { BranchId = lastBranch.Id, Points = lastBranch.Points.ToList() }]);
        var endpoint = new PhysicalReferencePoint { Name = "REF-END" }; var interior = new PhysicalReferencePoint { Name = "REF-INTERNAL" };
        HarnessOperations.AddReferencePoint(reversed, reversedView, endpoint, new(20, 0)).Execute();
        HarnessOperations.AddReferencePoint(reversed, reversedView, interior, new(10, 0)).Execute();
        ReplaceProject(reversed, null, null);
        var candidates = FindConnectionCandidates([reversedWire]);
        check(candidates.Any(c => !c.From && c.Target?.EntityId == endpoint.Id) && candidates.All(c => c.Target?.EntityId != interior.Id),
            "Verbindingsvoorstel gebruikt het echte uiteinde van omgekeerd doorlopen routetakken");
        var snapTargets = new List<ScenePrimitive>(); AddExtendedSnapTargets(snapTargets);
        check(snapTargets.OfType<SceneEllipse>().Any(p => p.EntityId == reversedWire.Id && p.Center == ToSheet(new(20, 0))),
            "Vrije-eindpuntsnap volgt de georiënteerde volledige aderroute");
        ReplaceProject(DemoFactory.Create(), null, null); Canvas.ClearSelection();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
