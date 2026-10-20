namespace Kabelaar.Core;

/// <summary>Synthetic fixtures only. No customer drawings or company parts are embedded.</summary>
public static class DemoFactory
{
    public static HarnessProject Create()
    {
        var project = new HarnessProject
        {
            Title = "Voorbeeld kabelboom", DrawingNumber = "DEMO-001", Revision = "A",
            Author = "Kabelaar", Customer = "Demonstratie",
            Notes = "Synthetisch voorbeeld. Controleer onderdelen en productiematen voor werkelijk gebruik."
        };
        var housing = Housing(6);
        var terminal = new PartDefinition
        {
            Name = "Voorbeeld krimpterminal", PartNumber = "DEMO-T050", Kind = PartKind.Terminal,
            MinCrossSectionMm2 = .25, MaxCrossSectionMm2 = 1
        };
        var seal = new PartDefinition { Name = "Voorbeeld draadseal", PartNumber = "DEMO-S050", Kind = PartKind.Seal };
        var plug = new PartDefinition { Name = "Voorbeeld afdichtplug", PartNumber = "DEMO-P01", Kind = PartKind.Plug };
        var sleeve = new PartDefinition { Name = "Voorbeeld beschermmantel", PartNumber = "DEMO-M10", Kind = PartKind.Sleeve };
        housing.RequiresSeal = true;
        project.Parts.AddRange([housing, terminal, seal, plug, sleeve]);
        var left = HarnessOperations.NewConnector(housing, "J1");
        var right = HarnessOperations.NewConnector(housing, "J2");
        foreach (var connector in new[] { left, right })
        {
            for (var i = 0; i < connector.Cavities.Count; i++)
            {
                var cavity = connector.Cavities[i];
                cavity.State = i < 5 ? CavityState.Occupied : CavityState.Plugged;
                if (i < 5) { cavity.TerminalPartId = terminal.Id; cavity.SealPartId = seal.Id; }
                else cavity.PlugPartId = plug.Id;
            }
        }
        project.Assembly.Connectors.AddRange([left, right]);
        var view = project.Sheets[0].Views[0];
        view.Mode = LayoutMode.Free;
        var leftPlacement = new PlacedComponent { EntityId = left.Id, Position = new(35, 60) };
        var rightPlacement = new PlacedComponent { EntityId = right.Id, Position = new(275, 60), RotationDegrees = 180 };
        view.Components.AddRange([leftPlacement, rightPlacement]);
        var colors = new[] { ("RD", "#D63D45"), ("BK", "#26303D"), ("GN", "#28956A"), ("WH", "#A5ACB6"), ("BU", "#307AC0") };
        for (var i = 0; i < 5; i++)
        {
            var wirePart = new PartDefinition { Name = $"Voorbeeld draad {colors[i].Item1} 0,5 mm²", PartNumber = $"DEMO-W05-{colors[i].Item1}", Kind = PartKind.Wire, Color = colors[i].Item2 };
            project.Parts.Add(wirePart);
            var start = Geometry.PinWorld(project, leftPlacement, left.Cavities[i].Id)!.Value;
            var end = Geometry.PinWorld(project, rightPlacement, right.Cavities[i].Id)!.Value;
            var points = new List<Point2> { start, new(90, 60 + i * 5), new(220, 60 + i * 5), end };
            var branch = new RouteBranch { Name = $"Tak {i + 1}", Points = [.. points], PhysicalLengthMm = 480 + i * 5, Smooth = true };
            project.Assembly.Branches.Add(branch);
            view.Routes.Add(new() { BranchId = branch.Id, Points = [.. points], Smooth = true });
            project.Assembly.Conductors.Add(new()
            {
                Name = $"W{i + 1}", From = new() { Kind = EndpointKind.Cavity, EntityId = left.Id, CavityId = left.Cavities[i].Id },
                To = new() { Kind = EndpointKind.Cavity, EntityId = right.Id, CavityId = right.Cavities[i].Id },
                WirePartId = wirePart.Id, Color = colors[i].Item2, ColorCode = colors[i].Item1,
                CrossSectionMm2 = .5, BranchIds = [branch.Id], StartAllowanceMm = 10, EndAllowanceMm = 10, Signal = $"SIG{i + 1}"
            });
        }
        project.Assembly.TwistGroups.Add(new() { Name = "Paar 1", ConductorIds = project.Assembly.Conductors.Take(2).Select(w => w.Id).ToList(), PitchMm = 16 });
        project.Assembly.TwistGroups.Add(new() { Name = "Triad 1", ConductorIds = project.Assembly.Conductors.Skip(2).Select(w => w.Id).ToList(), PitchMm = 20 });
        project.Assembly.Materials.Add(new() { PartId = sleeve.Id, Quantity = .35, Unit = "m", Notes = "Middenbundel" });
        project.Sheets[0].Name = "Kabelboom en draadgegevens";
        view.Name = "Productieaanzicht";
        view.Annotations.AddRange([
            new() { Kind = AnnotationKind.Text, Text = "KABELBOOM · VOORBEELD", Points = [new(20, 10)], FontSizeMm = 5 },
            new() { Kind = AnnotationKind.Text, Text = "MONTAGE\nPaar W1/W2 en triad W3/W4/W5 twisten.\nPin 6 aan beide connectoren afdichten.\nKniplengtes bevatten 10 mm aansluittoeslag per zijde.", Points = [new(20, 125)], FontSizeMm = 3 },
            new() { Kind = AnnotationKind.Dimension, EntityId = project.Assembly.Branches[0].Id, Points = [new(45, 105), new(265, 105)], OffsetMm = 8, Tolerance = "±5" },
            new() { Kind = AnnotationKind.WireTable, Points = [new(20, 155)], WidthMm = 350, FontSizeMm = 2.5 },
            new() { Kind = AnnotationKind.RevisionTable, Points = [new(20, 216)], WidthMm = 170, FontSizeMm = 2.1 }
        ]);
        var reportSheet = new Sheet { Name = "Onderdelen en aansluitingen" };
        reportSheet.Views[0].Name = "Productielijsten";
        reportSheet.Views[0].Annotations.AddRange([
            new() { Kind = AnnotationKind.Text, Text = "ONDERDELENLIJST", Points = [new(0, 7)], FontSizeMm = 4 },
            new() { Kind = AnnotationKind.BomTable, Points = [new(0, 15)], WidthMm = 380, FontSizeMm = 2.6 },
            new() { Kind = AnnotationKind.Text, Text = "PINBEZETTING", Points = [new(0, 103)], FontSizeMm = 4 },
            new() { Kind = AnnotationKind.PinTable, Points = [new(0, 111)], WidthMm = 380, FontSizeMm = 2.4,
                Columns = ["Connector", "Pin", "Wires", "Connections", "Terminal", "Seal", "Plug"] }
        ]);
        project.Sheets.Add(reportSheet);
        project.Revisions.Add(new() { Revision = "A", Author = "Kabelaar", Description = "Eerste synthetische demonstratie" });
        return project;
    }

    public static HarnessProject CreatePerformance(int conductorCount = 2000)
    {
        if (conductorCount is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(conductorCount));
        var project = new HarnessProject { Title = "Prestatieproef", DrawingNumber = "PERF-001" };
        var housing = Housing(conductorCount);
        housing.RequiresTerminal = false;
        housing.HeightMm = conductorCount * 2;
        var wirePart = new PartDefinition { Name = "Testdraad", PartNumber = "PERF-W", Kind = PartKind.Wire };
        project.Parts.AddRange([housing, wirePart]);
        var from = HarnessOperations.NewConnector(housing, "J1");
        var to = HarnessOperations.NewConnector(housing, "J2");
        project.Assembly.Connectors.AddRange([from, to]);
        var view = project.Sheets[0].Views[0];
        var left = new PlacedComponent { EntityId = from.Id, Position = new(30, 30) };
        var right = new PlacedComponent { EntityId = to.Id, Position = new(330, 30) };
        view.Components.AddRange([left, right]);
        for (var i = 0; i < conductorCount; i++)
        {
            from.Cavities[i].State = to.Cavities[i].State = CavityState.Occupied;
            var start = Geometry.PinWorld(project, left, from.Cavities[i].Id)!.Value;
            var end = Geometry.PinWorld(project, right, to.Cavities[i].Id)!.Value;
            var points = new List<Point2> { start, new(150, start.Y + 10), new(230, end.Y - 10), end };
            var branch = new RouteBranch { Name = $"B{i + 1}", Points = points, Smooth = true, PhysicalLengthMm = Geometry.Length(points, true) };
            project.Assembly.Branches.Add(branch);
            view.Routes.Add(new() { BranchId = branch.Id, Points = [.. points], Smooth = true });
            project.Assembly.Conductors.Add(new()
            {
                Name = $"W{i + 1:D5}", WirePartId = wirePart.Id, BranchIds = [branch.Id],
                From = new() { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = from.Cavities[i].Id },
                To = new() { Kind = EndpointKind.Cavity, EntityId = to.Id, CavityId = to.Cavities[i].Id }
            });
        }
        return project;
    }

    public static HarnessProject CreateAcceptancePerformance()
    {
        var project = new HarnessProject { Title = "Acceptatieproef 100 connectoren / 1.000 aders", DrawingNumber = "PERF-100-1000" };
        var housing = Housing(20);
        housing.RequiresTerminal = false;
        housing.WidthMm = 12; housing.HeightMm = 34;
        for (var i = 0; i < housing.Pins.Count; i++) housing.Pins[i].Position = new(6, (i - 9.5) * 1.5);
        housing.Symbol = [new() { Closed = true, Points = [new(-6, -17), new(6, -17), new(6, 17), new(-6, 17)] }];
        var wirePart = new PartDefinition { Name = "Synthetische testdraad", PartNumber = "PERF-W05", Kind = PartKind.Wire };
        project.Parts.AddRange([housing, wirePart]);
        var sheet = project.Sheets[0];
        sheet.WidthMm = 1189; sheet.HeightMm = 841;
        var view = sheet.Views[0];
        for (var pair = 0; pair < 50; pair++)
        {
            var from = HarnessOperations.NewConnector(housing, $"J{pair * 2 + 1:D3}");
            var to = HarnessOperations.NewConnector(housing, $"J{pair * 2 + 2:D3}");
            project.Assembly.Connectors.AddRange([from, to]);
            var x = 25 + pair % 5 * 220;
            var y = 35 + pair / 5 * 70;
            var left = new PlacedComponent { EntityId = from.Id, Position = new(x, y) };
            var right = new PlacedComponent { EntityId = to.Id, Position = new(x + 160, y), RotationDegrees = 180 };
            view.Components.AddRange([left, right]);
            for (var i = 0; i < 20; i++)
            {
                var fromCavity = from.Cavities[i];
                var toCavity = to.Cavities[19 - i];
                fromCavity.State = toCavity.State = CavityState.Occupied;
                var start = Geometry.PinWorld(project, left, fromCavity.Id)!.Value;
                var end = Geometry.PinWorld(project, right, toCavity.Id)!.Value;
                var points = new List<Point2> { start, new(x + 55, start.Y + 8), new(x + 105, end.Y + 8), end };
                var number = pair * 20 + i + 1;
                var branch = new RouteBranch { Name = $"B{number:D4}", Points = points, Smooth = true, PhysicalLengthMm = Geometry.Length(points, true) };
                project.Assembly.Branches.Add(branch);
                view.Routes.Add(new() { BranchId = branch.Id, Points = [.. points], Smooth = true });
                project.Assembly.Conductors.Add(new()
                {
                    Name = $"W{number:D4}", WirePartId = wirePart.Id, BranchIds = [branch.Id], StartAllowanceMm = 10, EndAllowanceMm = 10,
                    From = new() { Kind = EndpointKind.Cavity, EntityId = from.Id, CavityId = fromCavity.Id },
                    To = new() { Kind = EndpointKind.Cavity, EntityId = to.Id, CavityId = toCavity.Id }
                });
            }
        }
        return project;
    }

    private static PartDefinition Housing(int pins) => new()
    {
        Name = $"Voorbeeld connector {pins}-polig", PartNumber = $"DEMO-H{pins:00}", Kind = PartKind.Housing,
        WidthMm = 24, HeightMm = Math.Max(20, pins * 5),
        Pins = Enumerable.Range(0, pins).Select(i => new PinDefinition { Label = (i + 1).ToString(), Position = new(12, i * 5 - (pins - 1) * 2.5) }).ToList(),
        Symbol = [new() { Closed = true, Points = [new(-12, -Math.Max(10, pins * 2.5)), new(12, -Math.Max(10, pins * 2.5)), new(12, Math.Max(10, pins * 2.5)), new(-12, Math.Max(10, pins * 2.5))] }]
    };
}
