namespace Kabelaar.Core;

public static partial class HarnessOperations
{
    public static IEditCommand AddReferencePoint(HarnessProject project, DrawingView view, PhysicalReferencePoint point, Point2 position)
    {
        if (!project.Sheets.SelectMany(s => s.Views).Contains(view) || !Geometry.IsFinite(position) || point.MaxWires < 0 ||
            string.IsNullOrWhiteSpace(point.Id) || string.IsNullOrWhiteSpace(point.Name) || point.Description is null || !Enum.IsDefined(point.Symbol) ||
            project.Assembly.ReferencePoints.Any(p => p.Id == point.Id || p.Name.Equals(point.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Geef een unieke naam en geldige positie voor het referentiepunt.");
        var copy = GraphEdit.Clone(point);
        var placed = new PlacedComponent { EntityId = copy.Id, Position = position };
        var validation = GraphEdit.Clone(project);
        validation.Assembly.ReferencePoints.Add(GraphEdit.Clone(copy));
        GraphEdit.View(validation, view.Id).Components.Add(GraphEdit.Clone(placed));
        GraphEdit.RequireValid(validation);
        return new DelegateCommand("Fysiek referentiepunt toevoegen", () =>
        { project.Assembly.ReferencePoints.Add(copy); view.Components.Add(placed); }, () =>
        { view.Components.Remove(placed); project.Assembly.ReferencePoints.Remove(copy); });
    }
}
