namespace Kabelaar.Core;

public static class Ids { public static string New() => Guid.NewGuid().ToString("N"); }
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double b) => new(a.X * b, a.Y * b);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Point2 b) => (this - b).Length;
}
public enum LayoutMode { Scaled, Free }
public enum PartKind { Housing, Terminal, Seal, Plug, RingTerminal, Splice, Wire, Cable, Sleeve, Accessory }
public enum EndpointKind { Cavity, Splice, Free }
public enum CavityState { Unspecified, Occupied, Unused, Plugged }
public enum LengthSource { RouteAndAllowances, Manual }
public enum AnnotationKind { Text, Line, Arc, Leader, Dimension, Image, WireTable, PinTable, BomTable, RevisionTable }
public enum DimensionDirection { Aligned, Horizontal, Vertical }

public sealed class HarnessProject
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Ids.New();
    public string Title { get; set; } = "Nieuwe kabelboom";
    public string DrawingNumber { get; set; } = "";
    public string Revision { get; set; } = "A";
    public string Author { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public HarnessAssembly Assembly { get; set; } = new();
    public List<PartDefinition> Parts { get; set; } = [];
    public List<Sheet> Sheets { get; set; } = [new()];
    public List<RevisionEntry> Revisions { get; set; } = [];
    public List<ProjectAsset> Assets { get; set; } = [];
}
public sealed class HarnessAssembly
{
    public List<ConnectorInstance> Connectors { get; set; } = [];
    public List<Splice> Splices { get; set; } = [];
    public List<Conductor> Conductors { get; set; } = [];
    public List<RouteBranch> Branches { get; set; } = [];
    public List<TwistGroup> TwistGroups { get; set; } = [];
    public List<MaterialItem> Materials { get; set; } = [];
    public List<CableInstance> Cables { get; set; } = [];
}
public sealed class PartDefinition
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Nieuw onderdeel";
    public string PartNumber { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string Revision { get; set; } = "1";
    public PartKind Kind { get; set; } = PartKind.Housing;
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#202D3A";
    public double CrossSectionMm2 { get; set; } = 0.5;
    public double WidthMm { get; set; } = 20;
    public double HeightMm { get; set; } = 12;
    public List<PinDefinition> Pins { get; set; } = [];
    public List<SymbolPath> Symbol { get; set; } = [];
    public List<SymbolLabel> Labels { get; set; } = [];
    public string? SourceAssetId { get; set; }
    public string Projection { get; set; } = "Front";
    public bool RequiresTerminal { get; set; } = true;
    public bool RequiresSeal { get; set; }
    public double? MinCrossSectionMm2 { get; set; }
    public double? MaxCrossSectionMm2 { get; set; }
}
public sealed class PinDefinition
{
    public string Id { get; set; } = Ids.New();
    public string Label { get; set; } = "1";
    public Point2 Position { get; set; }
    public int MaxWires { get; set; } = 1;
}
public sealed class SymbolPath
{
    public List<Point2> Points { get; set; } = [];
    public bool Closed { get; set; }
    public bool Smooth { get; set; }
    public bool Hidden { get; set; }
}
public sealed class SymbolLabel
{
    public string Text { get; set; } = "";
    public Point2 Position { get; set; }
    public double HeightMm { get; set; } = 2.5;
}
public sealed class ConnectorInstance
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "J1";
    public string PartId { get; set; } = "";
    public bool IncludedInAssembly { get; set; } = true;
    public string Notes { get; set; } = "";
    public List<Cavity> Cavities { get; set; } = [];
    public List<MaterialItem> Accessories { get; set; } = [];
}
public sealed class Cavity
{
    public string Id { get; set; } = Ids.New();
    public string PinDefinitionId { get; set; } = "";
    public string Label { get; set; } = "1";
    public CavityState State { get; set; } = CavityState.Unspecified;
    public string? TerminalPartId { get; set; }
    public string? SealPartId { get; set; }
    public string? PlugPartId { get; set; }
    public int MaxWires { get; set; } = 1;
}
public sealed class Splice
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "SP1";
    public string? PartId { get; set; }
    public string Notes { get; set; } = "";
}
public sealed class Endpoint
{
    public EndpointKind Kind { get; set; } = EndpointKind.Free;
    public string? EntityId { get; set; }
    public string? CavityId { get; set; }
    public string Description { get; set; } = "Vrij uiteinde";
}
public sealed class Conductor
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "W1";
    public Endpoint From { get; set; } = new();
    public Endpoint To { get; set; } = new();
    public string? WirePartId { get; set; }
    public string Color { get; set; } = "#202D3A";
    public string ColorCode { get; set; } = "BK";
    public double CrossSectionMm2 { get; set; } = 0.5;
    public string Signal { get; set; } = "";
    public List<string> BranchIds { get; set; } = [];
    public LengthSource LengthSource { get; set; }
    public double ManualCutLengthMm { get; set; }
    public double StartAllowanceMm { get; set; }
    public double EndAllowanceMm { get; set; }
    public double ProcessingAllowanceMm { get; set; }
    public string? CableId { get; set; }
    public string Notes { get; set; } = "";
}
public sealed class RouteBranch
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public List<Point2> Points { get; set; } = [];
    public bool Smooth { get; set; }
    public double PhysicalLengthMm { get; set; }
    public bool LengthLocked { get; set; }
}
public sealed class TwistGroup
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Twisted pair";
    public List<string> ConductorIds { get; set; } = [];
    public double PitchMm { get; set; } = 20;
    public string Notes { get; set; } = "";
}
public sealed class CableInstance
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public string PartId { get; set; } = "";
    public double CutLengthMm { get; set; }
}
public sealed class MaterialItem
{
    public string Id { get; set; } = Ids.New();
    public string PartId { get; set; } = "";
    public double Quantity { get; set; } = 1;
    public string Unit { get; set; } = "st";
    public string? BranchId { get; set; }
    public string Notes { get; set; } = "";
}
public sealed class Sheet
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Blad 1";
    public double WidthMm { get; set; } = 420;
    public double HeightMm { get; set; } = 297;
    public double MarginMm { get; set; } = 10;
    public double PrintScale { get; set; } = 1;
    public string? LogoAssetId { get; set; }
    public List<DrawingView> Views { get; set; } = [new()];
    public List<DrawingLayer> Layers { get; set; } =
    [
        new() { Id = "main", Name = "Kabelboom" },
        new() { Id = "notes", Name = "Maatvoering en tekst" },
        new() { Id = "reference", Name = "Onderleggers", Locked = true, Print = false }
    ];
}
public sealed class DrawingLayer
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public bool Print { get; set; } = true;
}
public sealed class DrawingView
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Hoofdaanzicht";
    public LayoutMode Mode { get; set; } = LayoutMode.Scaled;
    public Point2 Origin { get; set; } = new(20, 20);
    public double Scale { get; set; } = 1;
    public List<PlacedComponent> Components { get; set; } = [];
    public List<PlacedRoute> Routes { get; set; } = [];
    public List<Annotation> Annotations { get; set; } = [];
}
public sealed class PlacedComponent
{
    public string Id { get; set; } = Ids.New();
    public string EntityId { get; set; } = "";
    public Point2 Position { get; set; }
    public double RotationDegrees { get; set; }
    public Point2 LabelOffset { get; set; } = new(0, -10);
    public string LayerId { get; set; } = "main";
}
public sealed class PlacedRoute
{
    public string Id { get; set; } = Ids.New();
    public string BranchId { get; set; } = "";
    public List<Point2> Points { get; set; } = [];
    public bool Smooth { get; set; }
    public string LayerId { get; set; } = "main";
}
public sealed class Annotation
{
    public string Id { get; set; } = Ids.New();
    public AnnotationKind Kind { get; set; }
    public List<Point2> Points { get; set; } = [];
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#243244";
    public double FontSizeMm { get; set; } = 3;
    public double LineWidthMm { get; set; } = 0.25;
    public string LayerId { get; set; } = "notes";
    public bool Locked { get; set; }
    public string? EntityId { get; set; }
    public string? AssetId { get; set; }
    public double WidthMm { get; set; } = 80;
    public double HeightMm { get; set; } = 40;
    public DimensionDirection DimensionDirection { get; set; }
    public string Tolerance { get; set; } = "";
    public double? DimensionOverrideMm { get; set; }
    public double OffsetMm { get; set; } = 10;
    public List<string> Columns { get; set; } = [];
}
public sealed class RevisionEntry
{
    public string Revision { get; set; } = "A";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
}
public sealed class ProjectAsset
{
    public string Id { get; set; } = Ids.New();
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = [];
}
