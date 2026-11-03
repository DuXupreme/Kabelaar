using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;
using Kabelaar.Rendering;

namespace Kabelaar.App;

public partial class MainWindow
{
    internal sealed record ConnectionCandidate(Conductor Wire, bool From, string Label, Endpoint? Target = null,
        Conductor? JunctionWire = null, PlacedRoute? JunctionRoute = null, double Fraction = 0);

    internal IReadOnlyList<ConnectionCandidate> FindConnectionCandidates(IEnumerable<Conductor> wires)
    {
        var result = new List<ConnectionCandidate>(); var tolerance = 2 / (_view.Scale * _sheet.PrintScale);
        foreach (var wire in wires)
        {
            if (wire.BranchIds.Count == 0) continue;
            if (wire.From.Kind != EndpointKind.Free && wire.To.Kind != EndpointKind.Free) continue;
            var wirePoints = SceneBuilder.ConductorPoints(_project, _view, wire);
            if (wirePoints.Count < 2) continue;
            foreach (var from in new[] { true, false })
            {
                var end = from ? wire.From : wire.To; if (end.Kind != EndpointKind.Free) continue;
                var route = _view.Routes.Find(r => r.BranchId == (from ? wire.BranchIds[0] : wire.BranchIds[^1]));
                if (route is null) continue;
                var point = from ? wirePoints[0] : wirePoints[^1]; var label = $"{wire.Name} · {(from ? "begin" : "einde")}";
                foreach (var option in EndpointChoices().Where(o => o.Kind != EndpointKind.Free))
                {
                    var target = new Endpoint { Kind = option.Kind, EntityId = option.EntityId, CavityId = option.CavityId, Description = "" };
                    if (Geometry.EndpointPosition(_project, _view, target) is { } p && p.DistanceTo(point) <= tolerance)
                        result.Add(new(wire, from, $"{label} → {option.Label}", target));
                }
                foreach (var other in _project.Assembly.Conductors.Where(w => w.Id != wire.Id))
                {
                    foreach (var targetRoute in _view.Routes.Where(r => other.BranchIds.Contains(r.BranchId) && !wire.BranchIds.Contains(r.BranchId)))
                    {
                        if (_sheet.Layers.Find(l => l.Id == targetRoute.LayerId)?.Visible == false) continue;
                        var path = Geometry.Flatten(_view, targetRoute, other.Id); var length = Geometry.Length(path);
                        if (length < .01) continue;
                        var traversed = 0.0; var bestDistance = double.PositiveInfinity; var bestFraction = 0.0;
                        for (var i = 1; i < path.Count; i++)
                        {
                            var d = path[i] - path[i - 1]; var denominator = d.X * d.X + d.Y * d.Y;
                            var t = denominator < 1e-12 ? 0 : Math.Clamp(((point.X - path[i - 1].X) * d.X + (point.Y - path[i - 1].Y) * d.Y) / denominator, 0, 1);
                            var distance = (path[i - 1] + d * t).DistanceTo(point);
                            var fraction = (traversed + d.Length * t) / length;
                            if (distance < bestDistance) { bestDistance = distance; bestFraction = fraction; }
                            traversed += d.Length;
                        }
                        if (bestDistance <= tolerance && bestFraction > .001 && bestFraction < .999)
                            result.Add(new(wire, from, $"{label} → nieuwe fysieke splice in {other.Name}", null, other, targetRoute, bestFraction));
                    }
                }
            }
        }
        return result;
    }

    private void SuggestConnections_Click(object sender, RoutedEventArgs e)
    {
        var wires = _project.Assembly.Conductors.Where(w => Canvas.Selection.Count == 0 || Canvas.Selection.Contains(w.Id)).ToArray();
        OfferConnectionCandidates(wires, false);
    }
    private void OfferConnectionCandidates(IEnumerable<Conductor> wires, bool onlyJunctions)
    {
        RunEditorAction(() =>
        {
            var candidates = FindConnectionCandidates(wires).Where(c => !onlyJunctions || c.JunctionWire is not null).ToArray();
            if (candidates.Length == 0) { if (!onlyJunctions) Status("Geen nabijgelegen aansluitingen bij de vrije uiteinden gevonden."); return; }
            var choose = new FormDialog(this, "Verbinding voorstellen", 640);
            var candidate = choose.Form.Choice("Kies één verbinding", candidates, candidates[0], c => c.Label);
            choose.Form.Note("Kruisingen en gelijke netnamen worden niet verbonden. Een nieuwe splice splitst een fysieke ader; nieuwe kniplengtes moeten daarna worden gecontroleerd.");
            if (choose.ShowDialog() != true) return;
            var item = PropertyForm.Value<ConnectionCandidate>(candidate);
            var proposal = item.Target is { } target ? ConnectionOperations.PrepareConnect(_project, _view, item.Wire, item.From, target)
                : ConnectionOperations.PrepareJunction(_project, _view, item.JunctionWire!, item.JunctionRoute!, item.Fraction,
                    NextName("SP", _project.Assembly.Splices.Select(s => s.Name)), item.Wire, item.From);
            PreviewConnection(proposal);
        });
    }
    private void PreviewConnection(ConnectionProposal proposal)
    {
        var state = _history.StateToken; var dialog = new FormDialog(this, "Voorgestelde fysieke verbinding", 760);
        dialog.Form.Note(string.Join(Environment.NewLine, proposal.Summary));
        var sheet = proposal.PreviewProject.Sheets.Find(s => s.Id == _sheet.Id) ?? proposal.PreviewProject.Sheets[0];
        var canvas = new HarnessCanvas { Height = 300, IsHitTestVisible = false, Focusable = false };
        canvas.SetScene(SceneBuilder.Build(proposal.PreviewProject, sheet)); canvas.Loaded += (_, _) => canvas.Fit();
        dialog.Form.Panel.Children.Add(canvas);
        if (dialog.ShowDialog() != true) return;
        if (state != _history.StateToken) throw new InvalidOperationException("De tekening is gewijzigd; maak het voorstel opnieuw.");
        Execute(proposal.Command);
    }
}
