using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kabelaar.Core;
using Kabelaar.Rendering;
using Geometry = Kabelaar.Core.Geometry;

namespace Kabelaar.App;

public partial class MainWindow
{
    private sealed record PropertyOption(string? Id, string Label);
    private sealed record EndpointOption(string Key, string Label, EndpointKind Kind, string? EntityId = null, string? CavityId = null);
    private IReadOnlyList<object?>? _propertiesState;
    private Action? _refreshPlacementProperties;

    private void ShowProperties()
    {
        Canvas.EditableVertices.Clear();
        Canvas.GeometryHandles.Clear();
        if (Canvas.Selection.Count == 0)
        {
            var state = ProjectPropertiesState();
            if (_propertiesState?.SequenceEqual(state) == true) return;
            _propertiesState = state; _refreshPlacementProperties = null;
            PropertiesPanel.Children.Clear();
            var projectForm = new PropertyForm { CanApply = () => !_propertiesRefreshQueued };
            PropertiesPanel.Children.Add(projectForm.Panel);
            PropertiesHeading.Text = "PROJECT";
            BuildProjectProperties(projectForm);
            Canvas.InvalidateVisual();
            return;
        }
        var id = SelectedId;
        var connector = _project.Assembly.Connectors.Find(c => c.Id == id || c.Cavities.Any(p => p.Id == id));
        var placement = _view.Components.Find(p => p.Id == id || p.EntityId == id || p.EntityId == connector?.Id);
        connector ??= _project.Assembly.Connectors.Find(c => c.Id == placement?.EntityId);
        var splice = _project.Assembly.Splices.Find(s => s.Id == id || s.Id == placement?.EntityId);
        var selectionState = Canvas.Selection.Count != 1 ? null
            : connector is not null ? ConnectorPropertiesState(connector, placement)
            : splice is not null ? SplicePropertiesState(splice, placement) : null;
        if (selectionState is not null && _propertiesState?.SequenceEqual(selectionState) == true)
        {
            _refreshPlacementProperties?.Invoke();
            return;
        }
        _propertiesState = selectionState; _refreshPlacementProperties = null;
        PropertiesPanel.Children.Clear();
        var form = new PropertyForm { CanApply = () => !_propertiesRefreshQueued };
        PropertiesPanel.Children.Add(form.Panel);
        var wire = _project.Assembly.Conductors.Find(w => w.Id == id);
        var annotation = _view.Annotations.Find(a => a.Id == id);
        var route = SelectedRoute();

        if (Canvas.Selection.Count > 1)
        {
            PropertiesHeading.Text = $"SELECTIE · {Canvas.Selection.Count} objecten";
            BuildMultipleProperties(form);
            Canvas.InvalidateVisual();
            return;
        }
        if (connector is not null)
        {
            PropertiesHeading.Text = $"CONNECTOR · {connector.Name}";
            BuildConnectorProperties(form, connector, placement);
        }
        else if (wire is not null)
        {
            PropertiesHeading.Text = $"ADER · {wire.Name}";
            BuildWireProperties(form, wire);
        }
        else if (splice is not null)
        {
            PropertiesHeading.Text = $"SPLICE · {splice.Name}";
            BuildSpliceProperties(form, splice, placement);
        }
        else if (annotation is not null)
        {
            PropertiesHeading.Text = AnnotationTitle(annotation.Kind).ToUpperInvariant();
            BuildAnnotationProperties(form, annotation);
        }
        else if (route is not null)
        {
            PropertiesHeading.Text = "ROUTE";
            BuildRouteProperties(form, route);
        }
        else
        {
            PropertiesHeading.Text = "PROJECT";
            BuildProjectProperties(form);
        }
        if (route is not null && Canvas.Tool == EditorTool.Select && Canvas.Selection.Count == 1 &&
            _sheet.Layers.Find(l => l.Id == route.LayerId)?.Locked != true)
            RefreshGeometryHandles(wire, route);
        Canvas.InvalidateVisual();
    }

    // Keep values AND object identities: undo and property replacement can restore another
    // instance with the same ID, and existing button closures must never retain the old one.
    private IReadOnlyList<object?> ProjectPropertiesState()
    {
        var part = SelectedPart;
        var state = new List<object?>
        {
            "project", _project, _sheet, _view, _project.Title, _project.DrawingNumber, _project.Revision,
            _project.Author, _project.Customer, _project.Notes, _sheet.Name, _view.Name, _view.Mode,
            _project.Assembly.Connectors.Count, _project.Assembly.Conductors.Count, _project.Sheets.Count,
            part, part?.Name, part?.PartNumber, part?.Supplier
        };
        foreach (var group in _project.Assembly.TwistGroups)
            state.AddRange([group, group.Id, group.Name, group.ConductorIds.Count]);
        state.Add("cables");
        foreach (var cable in _project.Assembly.Cables)
            state.AddRange([cable, cable.Id, cable.Name, cable.CutLengthMm]);
        state.Add("materials");
        foreach (var material in _project.Assembly.Materials)
            state.AddRange([material, material.Id, material.Quantity, material.Unit,
                _project.Parts.Find(p => p.Id == material.PartId)?.PartNumber]);
        return state;
    }

    private IReadOnlyList<object?> ConnectorPropertiesState(ConnectorInstance connector, PlacedComponent? placement)
    {
        var part = _project.Parts.Find(p => p.Id == connector.PartId);
        var state = new List<object?>
        {
            "connector", _project, _sheet, _view, SelectedId, connector, connector.Name, connector.PartId,
            connector.IncludedInAssembly, connector.Notes, placement, placement?.LayerId,
            part, part?.Name, part?.PartNumber
        };
        foreach (var layer in _sheet.Layers) state.AddRange([layer, layer.Name, layer.Locked]);
        state.Add("cavities");
        foreach (var cavity in connector.Cavities)
            state.AddRange([cavity, cavity.Label, cavity.State, cavity.MaxWires, cavity.PinDefinitionId,
                cavity.TerminalPartId, cavity.SealPartId, cavity.PlugPartId]);
        state.Add("wires");
        foreach (var wire in _project.Assembly.Conductors)
            if (wire.From.EntityId == connector.Id || wire.To.EntityId == connector.Id)
                state.AddRange([wire.Id, wire.Name, wire.From.EntityId, wire.From.CavityId, wire.To.EntityId, wire.To.CavityId]);
        state.Add("accessories");
        foreach (var accessory in connector.Accessories)
            state.AddRange([accessory, accessory.Quantity, accessory.Unit, _project.Parts.Find(p => p.Id == accessory.PartId)?.PartNumber]);
        return state;
    }

    private IReadOnlyList<object?> SplicePropertiesState(Splice splice, PlacedComponent? placement)
    {
        var state = new List<object?>
        {
            "splice", _project, _sheet, _view, SelectedId, splice, splice.Name, splice.PartId,
            splice.Notes, placement, placement?.LayerId
        };
        foreach (var layer in _sheet.Layers) state.AddRange([layer, layer.Name, layer.Locked]);
        foreach (var part in _project.Parts.Where(p => p.Kind == PartKind.Splice))
            state.AddRange([part, part.PartNumber, part.Name]);
        return state;
    }

    private void BuildProjectProperties(PropertyForm form)
    {
        var title = form.Text("Projecttitel", _project.Title);
        var number = form.Text("Tekeningnummer", _project.DrawingNumber);
        var revision = form.Text("Revisie", _project.Revision);
        var author = form.Text("Auteur", _project.Author);
        var customer = form.Text("Klant", _project.Customer);
        var notes = form.Text("Projectnotities", _project.Notes, true);
        form.Action("Projectgegevens toepassen", () =>
        {
            var before = (_project.Title, _project.DrawingNumber, _project.Revision, _project.Author, _project.Customer, _project.Notes);
            var after = (title.Text.Trim(), number.Text.Trim(), revision.Text.Trim(), author.Text.Trim(), customer.Text.Trim(), notes.Text);
            Execute(new DelegateCommand("Projectgegevens wijzigen",
                () => (_project.Title, _project.DrawingNumber, _project.Revision, _project.Author, _project.Customer, _project.Notes) = after,
                () => (_project.Title, _project.DrawingNumber, _project.Revision, _project.Author, _project.Customer, _project.Notes) = before));
        }, true);
        form.Heading("Actief aanzicht");
        form.Note($"{_sheet.Name} · {_view.Name}\n{(_view.Mode == LayoutMode.Scaled ? "Geometrie bepaalt de routelengte." : "Vrije opmaak bewaart de productiematen.")}");
        form.Action("Tekenstijl…", OpenDrawingStyle);
        form.Note($"{_project.Assembly.Connectors.Count} connectoren · {_project.Assembly.Conductors.Count} aders · {_project.Sheets.Count} bladen");
        if (SelectedPart is { } part)
        {
            form.Heading("Bibliotheekselectie");
            form.Note($"{part.Name}\n{part.PartNumber}\n{part.Supplier}");
        }
        BuildAssemblyOverview(form);
    }

    private void BuildConnectorProperties(PropertyForm form, ConnectorInstance connector, PlacedComponent? placement)
    {
        var part = _project.Parts.Find(p => p.Id == connector.PartId);
        form.Note($"{part?.Name ?? "Onbekend onderdeel"}\n{part?.PartNumber}");
        var name = form.Text("Connectornaam", connector.Name);
        var included = form.Check("Meenemen in assemblage en stuklijst", connector.IncludedInAssembly);
        var notes = form.Text("Montagenotities", connector.Notes, true);
        TextBox? x = null, y = null, angle = null, labelX = null, labelY = null;
        ComboBox? layer = null;
        ComponentPresentationFields? presentation = null;
        if (placement is not null)
        {
            form.Heading("Plaatsing in dit aanzicht");
            x = form.Number("X (mm)", placement.Position.X); y = form.Number("Y (mm)", placement.Position.Y);
            angle = form.Number("Rotatie (graden)", placement.RotationDegrees);
            labelX = form.Number("Labelverschuiving X (mm)", placement.LabelOffset.X);
            labelY = form.Number("Labelverschuiving Y (mm)", placement.LabelOffset.Y);
            layer = LayerChoice(form, placement.LayerId);
            presentation = BuildComponentPresentation(form, placement, connector: true);
            var knownPosition = placement.Position;
            var knownRotation = placement.RotationDegrees;
            var knownOffset = placement.LabelOffset;
            _refreshPlacementProperties = () =>
            {
                // Update only values changed by a command; preserve text the user is still editing.
                if (placement.Position.X != knownPosition.X) x.Text = EditData.Format(placement.Position.X);
                if (placement.Position.Y != knownPosition.Y) y.Text = EditData.Format(placement.Position.Y);
                if (placement.RotationDegrees != knownRotation) angle.Text = EditData.Format(placement.RotationDegrees);
                if (placement.LabelOffset.X != knownOffset.X) labelX.Text = EditData.Format(placement.LabelOffset.X);
                if (placement.LabelOffset.Y != knownOffset.Y) labelY.Text = EditData.Format(placement.LabelOffset.Y);
                knownPosition = placement.Position; knownRotation = placement.RotationDegrees; knownOffset = placement.LabelOffset;
                presentation.Refresh();
            };
        }
        form.Action("Connector toepassen", () =>
        {
            var next = EditData.Clone(connector);
            next.Name = name.Text.Trim(); next.IncludedInAssembly = included.IsChecked == true; next.Notes = notes.Text;
            var commands = new List<IEditCommand>();
            if (placement is not null)
            {
                commands.Add(presentation!.Command(_project, _view, placement,
                    new(EditData.Number(x!.Text, "X"), EditData.Number(y!.Text, "Y")), EditData.Number(angle!.Text, "Rotatie")));
                var beforeOffset = placement.LabelOffset; var beforeLayer = placement.LayerId;
                var offset = new Point2(EditData.Number(labelX!.Text, "Label X"), EditData.Number(labelY!.Text, "Label Y"));
                var layerId = PropertyForm.Value<DrawingLayer>(layer!).Id;
                commands.Add(new DelegateCommand("Connectorweergave", () => { placement.LabelOffset = offset; placement.LayerId = layerId; },
                    () => { placement.LabelOffset = beforeOffset; placement.LayerId = beforeLayer; }));
            }
            commands.Add(PropertyReplace(_project.Assembly.Connectors, connector, next, "Connectorgegevens"));
            Execute(new CompositeCommand("Connector wijzigen", commands.ToArray()));
        }, true);
        form.Heading("Pinnen en gemonteerde onderdelen");
        form.Note("Elke terminal, seal en plug wordt per pin geteld. Een toegestane dubbele krimp gebruikt één terminal.");
        foreach (var cavity in connector.Cavities)
        {
            var wireNames = _project.Assembly.Conductors.Where(w => EndpointMatches(w.From, connector.Id, cavity.Id) || EndpointMatches(w.To, connector.Id, cavity.Id))
                .Select(w => w.Name);
            form.Action($"Pin {cavity.Label} · {CavityTitle(cavity.State)} · {string.Join(", ", wireNames)}", () => EditCavityProperties(connector, cavity));
        }
        form.Heading("Connectoraccessoires");
        foreach (var accessory in connector.Accessories)
        {
            var itemPart = _project.Parts.Find(p => p.Id == accessory.PartId);
            form.Action($"{itemPart?.PartNumber ?? "?"} · {EditData.Format(accessory.Quantity)} {accessory.Unit}",
                () => EditAssemblyMaterial(connector.Accessories, accessory, $"Accessoire van {connector.Name}"));
        }
        form.Action("Connectoraccessoire toevoegen", () => EditAssemblyMaterial(connector.Accessories, null, $"Accessoire van {connector.Name}"));
    }

    private void EditCavityProperties(ConnectorInstance connector, Cavity cavity)
    {
        var dialog = new FormDialog(this, $"{connector.Name} · pin {cavity.Label}");
        var form = dialog.Form;
        var state = form.Choice("Bezetting", Enum.GetValues<CavityState>(), cavity.State, CavityTitle);
        var terminal = PartChoice(form, "Gemonteerde terminal", cavity.TerminalPartId, PartKind.Terminal);
        var seal = PartChoice(form, "Gemonteerde draadseal", cavity.SealPartId, PartKind.Seal);
        var plug = PartChoice(form, "Gemonteerde afdichtplug", cavity.PlugPartId, PartKind.Plug);
        var maxWires = form.Number("Maximaal aantal draadeinden", cavity.MaxWires);
        var definition = _project.Parts.Find(p => p.Id == connector.PartId)?.Pins.Find(p => p.Id == cavity.PinDefinitionId);
        form.Note($"Onderdeeldefinitie: maximaal {definition?.MaxWires ?? 1} draadeind(en). Een afdichtplug kan niet naast een draad of terminal worden gemonteerd.");
        dialog.OnAccept = () =>
        {
            var next = EditData.Clone(cavity);
            next.State = PropertyForm.Value<CavityState>(state);
            next.TerminalPartId = PropertyForm.Value<PropertyOption>(terminal).Id;
            next.SealPartId = PropertyForm.Value<PropertyOption>(seal).Id;
            next.PlugPartId = PropertyForm.Value<PropertyOption>(plug).Id;
            var count = EditData.Number(maxWires.Text, "Aantal draadeinden", 1);
            if (count != Math.Truncate(count) || count > (definition?.MaxWires ?? 1))
                throw new ArgumentException("Gebruik een heel aantal binnen de capaciteit van de pin in de onderdeeldefinitie.");
            next.MaxWires = (int)count;
            Change("Pinmontage wijzigen", connector.Cavities, cavity, next);
            return true;
        };
        dialog.ShowDialog();
    }

    private void BuildWireProperties(PropertyForm form, Conductor wire)
    {
        BuildWireDrawingProperties(form, wire);
        form.Heading("Productiegegevens");
        var name = form.Text("Draadnummer", wire.Name);
        var fromOptions = EndpointChoices();
        var from = form.Choice("Van aansluiting", fromOptions, CurrentEndpointOption(fromOptions, wire.From), o => o.Label);
        var fromDescription = form.Text("Omschrijving vrij begin", wire.From.Description);
        var toOptions = EndpointChoices();
        var to = form.Choice("Naar aansluiting", toOptions, CurrentEndpointOption(toOptions, wire.To), o => o.Label);
        var toDescription = form.Text("Omschrijving vrij einde", wire.To.Description);
        void UpdateEnds()
        {
            fromDescription.IsEnabled = PropertyForm.Value<EndpointOption>(from).Kind == EndpointKind.Free;
            toDescription.IsEnabled = PropertyForm.Value<EndpointOption>(to).Kind == EndpointKind.Free;
        }
        from.SelectionChanged += (_, _) => UpdateEnds(); to.SelectionChanged += (_, _) => UpdateEnds(); UpdateEnds();
        var part = PartChoice(form, "Draadartikel", wire.WirePartId, PartKind.Wire);
        var colorCode = form.Text("Fysieke kleurcode", wire.ColorCode);
        var color = form.Color("Tekenkleur (#RRGGBB)", wire.Color);
        var section = form.Number("Doorsnede (mm²)", wire.CrossSectionMm2);
        var signal = form.Text("Signaal / functie", wire.Signal);
        var cableOptions = new[] { new PropertyOption(null, "Losse draad") }.Concat(_project.Assembly.Cables.Select(c => new PropertyOption(c.Id, c.Name))).ToArray();
        var cable = form.Choice("Onderdeel van kabel", cableOptions, cableOptions.FirstOrDefault(o => o.Id == wire.CableId), o => o.Label);
        form.Heading("Kniplengte");
        var source = form.Choice("Lengtebron", Enum.GetValues<LengthSource>(), wire.LengthSource,
            s => s == LengthSource.Manual ? "Handmatige kniplengte" : "Route + expliciete toeslagen");
        var manual = form.Number("Handmatige kniplengte (mm)", wire.ManualCutLengthMm);
        var startAllowance = form.Number("Aansluittoeslag begin (mm)", wire.StartAllowanceMm);
        var endAllowance = form.Number("Aansluittoeslag einde (mm)", wire.EndAllowanceMm);
        var processing = form.Number("Verwerkingstoeslag (mm)", wire.ProcessingAllowanceMm);
        void UpdateLengthFields()
        {
            var isManual = PropertyForm.Value<LengthSource>(source) == LengthSource.Manual;
            manual.IsEnabled = isManual;
            startAllowance.IsEnabled = endAllowance.IsEnabled = processing.IsEnabled = !isManual;
        }
        source.SelectionChanged += (_, _) => UpdateLengthFields(); UpdateLengthFields();
        form.Note($"Route: {EditData.Format(HarnessReports.RouteLength(_project, wire))} mm\nKniplengte: {EditData.Format(HarnessReports.CutLength(_project, wire))} mm\nTwisten voegt nooit automatisch een toeslag toe.");
        var notes = form.Text("Productienotities", wire.Notes, true);
        form.Action("Ader toepassen", () =>
        {
            var next = EditData.Clone(wire);
            next.Name = name.Text.Trim(); next.WirePartId = PropertyForm.Value<PropertyOption>(part).Id;
            next.ColorCode = colorCode.Text.Trim(); next.Color = PropertyColor(color.Text);
            next.CrossSectionMm2 = EditData.Number(section.Text, "Doorsnede", .000001);
            next.Signal = signal.Text.Trim(); next.CableId = PropertyForm.Value<PropertyOption>(cable).Id;
            next.LengthSource = PropertyForm.Value<LengthSource>(source);
            next.ManualCutLengthMm = EditData.Number(manual.Text, "Handmatige kniplengte", next.LengthSource == LengthSource.Manual ? .001 : 0);
            next.StartAllowanceMm = EditData.Number(startAllowance.Text, "Begintoeslag", 0);
            next.EndAllowanceMm = EditData.Number(endAllowance.Text, "Eindtoeslag", 0);
            next.ProcessingAllowanceMm = EditData.Number(processing.Text, "Verwerkingstoeslag", 0);
            next.Notes = notes.Text;
            var fromEnd = EndpointFromChoice(from, fromDescription.Text);
            var toEnd = EndpointFromChoice(to, toDescription.Text);
            var endpoints = HarnessOperations.SetConductorEndpoints(_project, wire, fromEnd, toEnd);
            var before = EditData.Clone(wire);
            Execute(new CompositeCommand("Ader wijzigen", endpoints,
                new DelegateCommand("Productiegegevens", () => CopyWireFields(next, wire), () => CopyWireFields(before, wire))));
        }, true);
        form.Heading("Routetakken");
        form.Action("Routetakken kiezen en ordenen…", () => EditWireRoute(wire));
        form.Action("Extra ader op dezelfde route", () => AddWireAlongExistingRoute(wire));
        foreach (var id in wire.BranchIds)
        {
            var branch = _project.Assembly.Branches.Find(b => b.Id == id);
            var route = _view.Routes.Find(r => r.BranchId == id);
            if (branch is null) { form.Note("Onbekende routetak; herstel de route voordat je productie-uitvoer maakt."); continue; }
            form.Note($"{branch.Name}: {EditData.Format(branch.PhysicalLengthMm)} mm{(branch.LengthLocked ? " · vergrendeld" : "")}");
            if (route is not null)
                form.Action($"Bewerk {branch.Name}", () => Canvas.Select(route.BranchId));
        }
    }

    private void BuildSpliceProperties(PropertyForm form, Splice splice, PlacedComponent? placement)
    {
        var name = form.Text("Splicenaam", splice.Name);
        var part = PartChoice(form, "Spliceartikel", splice.PartId, PartKind.Splice);
        var notes = form.Text("Montagenotities", splice.Notes, true);
        TextBox? x = null, y = null, labelX = null, labelY = null;
        ComboBox? layer = null;
        ComponentPresentationFields? presentation = null;
        if (placement is not null)
        {
            x = form.Number("X (mm)", placement.Position.X); y = form.Number("Y (mm)", placement.Position.Y);
            labelX = form.Number("Labelverschuiving X (mm)", placement.LabelOffset.X);
            labelY = form.Number("Labelverschuiving Y (mm)", placement.LabelOffset.Y);
            layer = LayerChoice(form, placement.LayerId);
            presentation = BuildComponentPresentation(form, placement, connector: false);
            var knownPosition = placement.Position;
            var knownOffset = placement.LabelOffset;
            _refreshPlacementProperties = () =>
            {
                if (placement.Position.X != knownPosition.X) x.Text = EditData.Format(placement.Position.X);
                if (placement.Position.Y != knownPosition.Y) y.Text = EditData.Format(placement.Position.Y);
                if (placement.LabelOffset.X != knownOffset.X) labelX.Text = EditData.Format(placement.LabelOffset.X);
                if (placement.LabelOffset.Y != knownOffset.Y) labelY.Text = EditData.Format(placement.LabelOffset.Y);
                knownPosition = placement.Position; knownOffset = placement.LabelOffset;
                presentation.Refresh();
            };
        }
        form.Action("Splice toepassen", () =>
        {
            var next = EditData.Clone(splice);
            next.Name = name.Text.Trim(); next.PartId = PropertyForm.Value<PropertyOption>(part).Id; next.Notes = notes.Text;
            var commands = new List<IEditCommand>();
            if (placement is not null)
            {
                commands.Add(presentation!.Command(_project, _view, placement, new(EditData.Number(x!.Text, "X"), EditData.Number(y!.Text, "Y")), placement.RotationDegrees));
                var previousLayer = placement.LayerId; var nextLayer = PropertyForm.Value<DrawingLayer>(layer!).Id;
                var previousOffset = placement.LabelOffset;
                var nextOffset = new Point2(EditData.Number(labelX!.Text, "Label X"), EditData.Number(labelY!.Text, "Label Y"));
                commands.Add(new DelegateCommand("Spliceweergave",
                    () => { placement.LayerId = nextLayer; placement.LabelOffset = nextOffset; },
                    () => { placement.LayerId = previousLayer; placement.LabelOffset = previousOffset; }));
            }
            commands.Add(PropertyReplace(_project.Assembly.Splices, splice, next, "Splicegegevens"));
            Execute(new CompositeCommand("Splice wijzigen", commands.ToArray()));
        }, true);
    }

    private void BuildRouteProperties(PropertyForm form, PlacedRoute route)
    {
        var branch = _project.Assembly.Branches.Find(b => b.Id == route.BranchId);
        if (branch is null) { form.Note("De fysieke tak voor deze route ontbreekt."); return; }
        var name = form.Text("Taknaam", branch.Name);
        var length = form.Number("Fysieke taklengte (mm)", branch.PhysicalLengthMm);
        var locked = form.Check("Fysieke taklengte vergrendelen", branch.LengthLocked);
        form.Note(_view.Mode == LayoutMode.Scaled
            ? "Op schaal moet de fysieke maat bij de route passen. Wijzig de punten om de maat te veranderen; ontgrendel eerst een vastgezette lengte."
            : "Vrije routebewerkingen veranderen de productiemaat niet.");
        form.Action("Maat en vergrendeling toepassen", () =>
        {
            var command = HarnessOperations.SetPhysicalLength(_project, branch, EditData.Number(length.Text, "Taklengte", .001), locked.IsChecked == true);
            var before = branch.Name; var after = name.Text.Trim();
            Execute(new CompositeCommand("Takgegevens wijzigen", command, new DelegateCommand("Taknaam", () => branch.Name = after, () => branch.Name = before)));
        });
        form.Heading("Geometrie in dit aanzicht");
        var points = form.Text("Routepunten: één X; Y per regel (mm)", FormatPropertyPoints(route.Points), true);
        points.MinHeight = 110;
        var smooth = form.Check("Vloeiende route", route.Smooth);
        var layer = LayerChoice(form, route.LayerId);
        form.Note("Aangesloten uiteinden blijven op hun pin of splice. Gebruik de adereigenschappen om een aansluiting te veranderen.");
        form.Action("Routegeometrie toepassen", () =>
        {
            var nextPoints = ParsePropertyPoints(points.Text, 2);
            VerifyAttachedRoutePoints(route, nextPoints);
            var geometry = Geometry.EditableRouteGeometry(_view, route);
            if (nextPoints.Count == geometry.Knots.Count) geometry = Geometry.WithPoints(geometry, nextPoints);
            else geometry = Geometry.FromLegacy(nextPoints, smooth.IsChecked == true);
            if (smooth.IsChecked != route.Smooth)
                geometry = Geometry.WithCurveKind(geometry, smooth.IsChecked == true ? RouteSegmentKind.Cubic : RouteSegmentKind.Line, _bendOffsetMm);
            var representative = _project.Assembly.Conductors.FirstOrDefault(w => w.BranchIds.Contains(route.BranchId));
            var command = representative is null ? HarnessOperations.SetRouteGeometry(_project, _view, route, geometry)
                : HarnessOperations.SetWireRouteGeometry(_project, _view, representative, route, geometry, true);
            var beforeLayer = route.LayerId; var afterLayer = PropertyForm.Value<DrawingLayer>(layer).Id;
            Execute(new CompositeCommand("Routegeometrie wijzigen", command,
                new DelegateCommand("Routelaag", () => route.LayerId = afterLayer, () => route.LayerId = beforeLayer)));
        }, true);
        form.Action("Fysieke tak splitsen op tussenpunt…", () => SplitRouteSection(route));
    }

    private void BuildAnnotationProperties(PropertyForm form, Annotation annotation)
    {
        var points = form.Text("Posities: één X; Y per regel (mm)", FormatPropertyPoints(annotation.Points), annotation.Points.Count > 1);
        var text = form.Text(annotation.Kind == AnnotationKind.Dimension ? "Maattekst (leeg = automatisch)" : "Tekst", annotation.Text, true);
        var color = form.Color("Kleur (#RRGGBB)", annotation.Color);
        var fontSize = form.Number("Teksthoogte (mm)", annotation.FontSizeMm);
        var lineWidth = form.Number("Lijndikte (mm)", annotation.LineWidthMm);
        var layer = LayerChoice(form, annotation.LayerId);
        var locked = form.Check("Positie vergrendelen", annotation.Locked);
        TextBox? width = null, height = null, offset = null, tolerance = null, manualDimension = null;
        ComboBox? dimensionDirection = null, dimensionBranch = null, tableConnector = null;
        var columnChecks = new List<(string Key, CheckBox Box)>();
        var isTable = annotation.Kind is AnnotationKind.WireTable or AnnotationKind.PinTable or AnnotationKind.BomTable or AnnotationKind.RevisionTable;
        if (isTable || annotation.Kind == AnnotationKind.Image)
        {
            width = form.Number("Breedte (mm)", annotation.WidthMm);
            if (annotation.Kind == AnnotationKind.Image) height = form.Number("Hoogte (mm)", annotation.HeightMm);
        }
        if (annotation.Kind == AnnotationKind.Dimension)
        {
            dimensionDirection = form.Choice("Maatrichting", Enum.GetValues<DimensionDirection>(), annotation.DimensionDirection,
                d => d switch { DimensionDirection.Horizontal => "Horizontaal", DimensionDirection.Vertical => "Verticaal", _ => "Uitgelijnd" });
            var branches = new[] { new PropertyOption(null, "Afstand tussen de meetpunten") }.Concat(_project.Assembly.Branches.Select(b => new PropertyOption(b.Id, $"{b.Name} · {EditData.Format(b.PhysicalLengthMm)} mm"))).ToArray();
            dimensionBranch = form.Choice("Maat koppelen aan", branches, branches.FirstOrDefault(o => o.Id == annotation.EntityId), o => o.Label);
            offset = form.Number("Afstand maatlijn (mm)", annotation.OffsetMm);
            tolerance = form.Text("Tolerantie", annotation.Tolerance);
            manualDimension = form.Text("Handmatige maatwaarde (leeg = automatisch)", annotation.DimensionOverrideMm is { } value ? EditData.Format(value) : "");
            form.Note("Een handmatige maat of maattekst verandert geen productiematen. Een gekoppelde takmaat volgt de fysieke taklengte.");
        }
        if (isTable)
        {
            if (annotation.Kind is AnnotationKind.WireTable or AnnotationKind.PinTable)
            {
                var connectors = new[] { new PropertyOption(null, "Alle connectoren") }.Concat(
                    _project.Assembly.Connectors.OrderBy(c => c.Name).Select(c => new PropertyOption(c.Id, c.Name))).ToArray();
                tableConnector = form.Choice("Aansluitingen van", connectors, connectors.FirstOrDefault(c => c.Id == annotation.EntityId), c => c.Label);
                form.Note(annotation.Kind == AnnotationKind.WireTable
                    ? "Een connectorfilter toont alleen de aders die op die connector zijn aangesloten."
                    : "Een connectorfilter toont alleen de pinnen van die connector.");
            }
            form.Heading("Tabelkolommen");
            var (defaults, rows) = SceneBuilder.TableData(_project, annotation.Kind);
            var available = rows.FirstOrDefault()?.Keys.ToArray() ?? TableColumnsFor(annotation.Kind);
            var selected = annotation.Columns.Count > 0 ? annotation.Columns : defaults;
            var ordered = selected.Concat(available).Distinct().ToArray();
            foreach (var key in ordered)
                columnChecks.Add((key, form.Check(SceneBuilder.TableColumnTitle(key), selected.Contains(key))));
            form.Note("De tabel blijft gekoppeld aan de actuele projectgegevens.");
        }
        form.Action("Annotatie toepassen", () =>
        {
            var next = EditData.Clone(annotation);
            var minimumPoints = annotation.Kind is AnnotationKind.Line or AnnotationKind.Leader or AnnotationKind.Dimension ? 2 : annotation.Kind == AnnotationKind.Arc ? 3 : 1;
            next.Points = ParsePropertyPoints(points.Text, minimumPoints);
            if (annotation.Locked && !next.Points.SequenceEqual(annotation.Points) && locked.IsChecked == true)
                throw new InvalidOperationException("Ontgrendel de annotatie om de positie te veranderen.");
            next.Text = text.Text; next.Color = PropertyColor(color.Text);
            next.FontSizeMm = EditData.Number(fontSize.Text, "Teksthoogte", .1);
            next.LineWidthMm = EditData.Number(lineWidth.Text, "Lijndikte", .01);
            next.LayerId = PropertyForm.Value<DrawingLayer>(layer).Id; next.Locked = locked.IsChecked == true;
            if (width is not null) next.WidthMm = EditData.Number(width.Text, "Breedte", .1);
            if (height is not null) next.HeightMm = EditData.Number(height.Text, "Hoogte", .1);
            if (dimensionDirection is not null)
            {
                next.DimensionDirection = PropertyForm.Value<DimensionDirection>(dimensionDirection);
                next.EntityId = PropertyForm.Value<PropertyOption>(dimensionBranch!).Id;
                next.OffsetMm = EditData.Number(offset!.Text, "Maatlijnafstand");
                next.Tolerance = tolerance!.Text.Trim();
                next.DimensionOverrideMm = string.IsNullOrWhiteSpace(manualDimension!.Text) ? null : EditData.Number(manualDimension.Text, "Handmatige maat", 0);
            }
            if (isTable)
            {
                if (tableConnector is not null) next.EntityId = PropertyForm.Value<PropertyOption>(tableConnector).Id;
                next.Columns = columnChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Key).ToList();
                if (next.Columns.Count == 0) throw new ArgumentException("Kies minstens één tabelkolom.");
            }
            Change("Annotatie wijzigen", _view.Annotations, annotation, next);
        }, true);
    }

    private ComboBox LayerChoice(PropertyForm form, string layerId) => form.Choice("Laag", _sheet.Layers, _sheet.Layers.Find(l => l.Id == layerId), l => l.Name + (l.Locked ? " (vergrendeld)" : ""));
    private ComboBox PartChoice(PropertyForm form, string label, string? selectedId, PartKind kind)
    {
        var options = new[] { new PropertyOption(null, "Niet gekozen / niet gemonteerd") }.Concat(_project.Parts.Where(p => p.Kind == kind)
            .OrderBy(p => p.PartNumber).Select(p => new PropertyOption(p.Id, $"{p.PartNumber} · {p.Name}"))).ToArray();
        return form.Choice(label, options, options.FirstOrDefault(p => p.Id == selectedId), p => p.Label);
    }
    private List<EndpointOption> EndpointChoices()
    {
        var result = new List<EndpointOption> { new("free", "Vrij uiteinde", EndpointKind.Free) };
        result.AddRange(_project.Assembly.Connectors.OrderBy(c => c.Name).SelectMany(c => c.Cavities.Select(p =>
            new EndpointOption($"{c.Id}:{p.Id}", $"{c.Name} · pin {p.Label}", EndpointKind.Cavity, c.Id, p.Id))));
        result.AddRange(_project.Assembly.Splices.OrderBy(s => s.Name).Select(s => new EndpointOption(s.Id, s.Name, EndpointKind.Splice, s.Id)));
        return result;
    }
    private static EndpointOption CurrentEndpointOption(List<EndpointOption> options, Endpoint endpoint) =>
        options.FirstOrDefault(o => o.Kind == endpoint.Kind && o.EntityId == endpoint.EntityId && o.CavityId == endpoint.CavityId) ?? options[0];
    private static Endpoint EndpointFromChoice(ComboBox choice, string freeDescription)
    {
        var option = PropertyForm.Value<EndpointOption>(choice);
        return new() { Kind = option.Kind, EntityId = option.EntityId, CavityId = option.CavityId, Description = option.Kind == EndpointKind.Free ? freeDescription.Trim() : "" };
    }
    private static bool EndpointMatches(Endpoint endpoint, string connectorId, string cavityId) =>
        endpoint.Kind == EndpointKind.Cavity && endpoint.EntityId == connectorId && endpoint.CavityId == cavityId;
    private static IEditCommand PropertyReplace<T>(List<T> items, T before, T after, string name)
    {
        var index = items.IndexOf(before);
        if (index < 0) throw new InvalidOperationException("Dit object bestaat niet meer; selecteer het opnieuw.");
        return new DelegateCommand(name, () => items[index] = after, () => items[index] = before);
    }
    private static void CopyWireFields(Conductor source, Conductor target)
    {
        target.Name = source.Name; target.WirePartId = source.WirePartId; target.Color = source.Color; target.ColorCode = source.ColorCode;
        target.CrossSectionMm2 = source.CrossSectionMm2; target.Signal = source.Signal; target.CableId = source.CableId;
        target.LengthSource = source.LengthSource; target.ManualCutLengthMm = source.ManualCutLengthMm;
        target.StartAllowanceMm = source.StartAllowanceMm; target.EndAllowanceMm = source.EndAllowanceMm; target.ProcessingAllowanceMm = source.ProcessingAllowanceMm;
        target.Notes = source.Notes;
    }
    private void VerifyAttachedRoutePoints(PlacedRoute route, IReadOnlyList<Point2> points)
    {
        foreach (var wire in _project.Assembly.Conductors)
            foreach (var (endpoint, first) in new[] { (wire.From, true), (wire.To, false) })
            {
                if (endpoint.Kind == EndpointKind.Free || wire.BranchIds.Count == 0 ||
                    (first ? wire.BranchIds[0] : wire.BranchIds[^1]) != route.BranchId) continue;
                var pin = Geometry.EndpointPosition(_project, _view, endpoint);
                if (pin is null) continue;
                var atStart = route.Points[0].DistanceTo(pin.Value) <= route.Points[^1].DistanceTo(pin.Value);
                var before = atStart ? route.Points[0] : route.Points[^1];
                var after = atStart ? points[0] : points[^1];
                if (before.DistanceTo(after) > .001) throw new InvalidOperationException("Een aangesloten route-uiteinde moet op de bestaande aansluiting blijven. Wijzig de aansluiting bij de adereigenschappen.");
            }
    }
    private static string FormatPropertyPoints(IEnumerable<Point2> points) => string.Join(Environment.NewLine, points.Select(p => $"{EditData.Format(p.X)}; {EditData.Format(p.Y)}"));
    private static List<Point2> ParsePropertyPoints(string text, int minimum)
    {
        var result = new List<Point2>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(';', StringSplitOptions.TrimEntries);
            if (fields.Length != 2) throw new ArgumentException("Gebruik per regel twee coördinaten met een puntkomma ertussen: X; Y.");
            result.Add(new(EditData.Number(fields[0], "X"), EditData.Number(fields[1], "Y")));
        }
        if (result.Count < minimum) throw new ArgumentException($"Deze geometrie vereist minstens {minimum} punt(en).");
        return result;
    }
    private static string PropertyColor(string text)
    {
        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color color)
                return color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : color.ToString();
        }
        catch (FormatException) { }
        throw new ArgumentException("Voer een geldige kleur in, bijvoorbeeld #2563EB.");
    }
    private static string CavityTitle(CavityState state) => state switch
    {
        CavityState.Occupied => "Bezet", CavityState.Unused => "Ongebruikt", CavityState.Plugged => "Afgedicht", _ => "Nog niet bepaald"
    };
    private static string AnnotationTitle(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Text => "Tekst", AnnotationKind.Line => "Lijn", AnnotationKind.Arc => "Boog", AnnotationKind.Leader => "Montageaanwijzing",
        AnnotationKind.Dimension => "Maatvoering", AnnotationKind.Image => "Afbeelding", AnnotationKind.WireTable => "Draadlijst",
        AnnotationKind.PinTable => "Aansluitlijst", AnnotationKind.BomTable => "Stuklijst", AnnotationKind.RevisionTable => "Revisietabel", _ => "Annotatie"
    };
    private static string[] TableColumnsFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.WireTable => ["Name", "From", "To", "PartNumber", "Color", "CrossSectionMm2", "RouteLengthMm", "AllowanceMm", "CutLengthMm", "LengthSource", "Signal", "Cable"],
        AnnotationKind.PinTable => ["Connector", "Pin", "State", "Terminal", "Seal", "Plug", "Wires", "Connections"],
        AnnotationKind.BomTable => ["PartNumber", "Description", "Kind", "Quantity", "Unit", "References"],
        AnnotationKind.RevisionTable => ["Revision", "Description", "Author", "Date"], _ => []
    };
}
