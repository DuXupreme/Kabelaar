using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async Task CheckPresentationEditingAsync(Action<bool, string> check)
    {
        var placement = _view.Components.First(p => _project.Assembly.Connectors.Any(c => c.Id == p.EntityId));
        var connector = _project.Assembly.Connectors.Single(c => c.Id == placement.EntityId);
        Canvas.Select(placement.EntityId);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var before = JsonSerializer.Serialize(_project);
        var historyBefore = _history.StateToken;
        var typedName = connector.Name + "-nog-niet-toegepast";
        Field<TextBox>("Connectornaam").Text = typedName;
        Field<TextBox>("Connectorvergroting (1 = ware grootte)").Text = "0.65";
        var style = _view.Style.Copy();
        style.ShowWireLabels = false; style.ShowTwistLabels = false;
        style.UseWireSectionWidth = true; style.TwistDisplayMode = TwistDisplayMode.Markers;
        var otherViews = _project.Sheets.SelectMany(s => s.Views).Where(v => v != _view)
            .ToDictionary(v => v.Id, v => JsonSerializer.Serialize(v));
        _history.Execute(DrawingStyleCommand(_view, style, PresentationPreset.ProductionOverview));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(Field<TextBox>("Connectornaam").Text == typedName &&
              Field<TextBox>("Connectorvergroting (1 = ware grootte)").Text == "0.65",
            "Tekenstijl bewaart nog niet toegepaste connectornaam en vergroting in het eigenschappenpaneel");
        check(!_view.Style.ShowWireLabels && _view.Style.UseWireSectionWidth &&
              !placement.ShowPins && placement.ShowLabel && !placement.ShowPartNumber &&
              _project.Sheets.SelectMany(s => s.Views).Where(v => v != _view)
                  .All(v => otherViews[v.Id] == JsonSerializer.Serialize(v)),
            "Voorinstelling wijzigt alleen de tekenstijl en connectorlabels van het actieve aanzicht");
        var cavityId = connector.Cavities[0].Id;
        var pin = ToSheet(Geometry.PinWorld(_project, placement, cavityId)!.Value);
        var snapped = SnapToEndpoint(pin + new Point2(.4, .3));
        var snappedEndpoint = FindEndpoint(ToModel(snapped));
        check(snapped.DistanceTo(pin) < 1e-9 && snappedEndpoint.EntityId == connector.Id && snappedEndpoint.CavityId == cavityId,
            "Verborgen pindots blijven in productieoverzicht exact bereikbaar via snapping en aansluitidentiteit");
        _history.Undo();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(_history.StateToken == historyBefore && JsonSerializer.Serialize(_project) == before,
            "Eén undo herstelt de complete tekenstijlvoorinstelling");
        check(Field<TextBox>("Connectornaam").Text == typedName,
            "Undo van tekenstijl laat een nog niet toegepaste connectornaam staan");

        var oldPosition = placement.Position;
        var oldScale = placement.DisplayScale;
        var lengths = _project.Assembly.Branches.Select(b => b.PhysicalLengthMm).ToArray();
        var endpoints = _project.Assembly.Conductors.Select(w => JsonSerializer.Serialize(new { w.From, w.To })).ToArray();
        Field<TextBox>("X (mm)").Text = EditData.Format(oldPosition.X + 6);
        Field<TextBox>("Rotatie (graden)").Text = "90";
        Field<TextBox>("Teksthoogte naam (mm)").Text = "2.5";
        Button("Connector toepassen").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(placement.DisplayScale == .65 && placement.Position.X == oldPosition.X + 6 &&
              placement.RotationDegrees == 90 && placement.LabelFontSizeMm == 2.5 &&
              _project.Assembly.Connectors.Single(c => c.Id == connector.Id).Name == typedName,
            "Eigenschappen passen connectorvergroting, plaatsing, rotatie en labelhoogte samen toe");
        var movedPin = ToSheet(Geometry.PinWorld(_project, placement, cavityId)!.Value);
        check(SnapToEndpoint(movedPin + new Point2(.15, .1)).DistanceTo(movedPin) < 1e-9,
            "Snapping volgt gewijzigde connectorvergroting, plaatsing en rotatie");
        check(lengths.SequenceEqual(_project.Assembly.Branches.Select(b => b.PhysicalLengthMm)) &&
              endpoints.SequenceEqual(_project.Assembly.Conductors.Select(w => JsonSerializer.Serialize(new { w.From, w.To }))),
            "Connectoropmaak via het eigenschappenpaneel behoudt fysieke lengtes en aansluitidentiteiten");
        _history.Undo();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(placement.Position == oldPosition && placement.DisplayScale == oldScale &&
              JsonSerializer.Serialize(_project) == before,
            "Eén undo herstelt gecombineerde connectoropmaak en gegevens");

        var table = _view.Annotations.FirstOrDefault(a => a.Kind == AnnotationKind.WireTable);
        if (table is null)
        {
            table = new Annotation { Kind = AnnotationKind.WireTable, Points = [new(10, 15)] };
            _view.Annotations.Add(table);
            try { await CheckTableFilterAsync(table, connector.Id, check); }
            finally { _view.Annotations.Remove(table); }
        }
        else await CheckTableFilterAsync(table, connector.Id, check);
        Canvas.ClearSelection(); Refresh();
        check(JsonSerializer.Serialize(_project) == before, "Presentatiecontroles laten het synthetische project ongewijzigd achter");

        T Field<T>(string name) where T : FrameworkElement => PropertiesPanel.Children.OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<T>()).Single(c => AutomationProperties.GetName(c) == name);
        Button Button(string content) => PropertiesPanel.Children.OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Button>()).Single(b => b.Content as string == content);
    }

    private async Task CheckTableFilterAsync(Annotation table, string connectorId, Action<bool, string> check)
    {
        var before = JsonSerializer.Serialize(table);
        Canvas.Select(table.Id);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var panel = PropertiesPanel.Children.OfType<StackPanel>().Single();
        var choice = panel.Children.OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Aansluitingen van");
        var option = choice.Items.OfType<ComboBoxItem>().Select(i => (PropertyOption)i.Tag).Single(o => o.Id == connectorId);
        SelectChoice(choice, option);
        panel.Children.OfType<Button>().Single(b => b.Content as string == "Annotatie toepassen")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(_view.Annotations.Single(a => a.Id == table.Id).EntityId == connectorId,
            "De aansluitlijst krijgt via Eigenschappen een connectorfilter");
        _history.Undo();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(JsonSerializer.Serialize(_view.Annotations.Single(a => a.Id == table.Id)) == before,
            "Undo herstelt de oorspronkelijke tabelselectie");
    }
}
