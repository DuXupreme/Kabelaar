using System.Windows;
using System.Windows.Controls;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private void DrawingStyle_Click(object sender, RoutedEventArgs e) => OpenDrawingStyle();

    private void OpenDrawingStyle()
    {
        if (_busy is not null || _saving) return;
        var view = _view;
        var dialog = new FormDialog(this, "Tekenstijl · " + view.Name, 570);
        var form = dialog.Form;
        form.Note($"Geldt alleen voor {_sheet.Name} · {view.Name}. Andere aanzichten behouden hun eigen opmaak.");
        form.Heading("Voorinstellingen");
        form.Note("Kies een startpunt en pas hieronder de waarden aan. De tekening verandert pas na Toepassen; één keer Ongedaan maken herstelt alles.");
        Action<PresentationPreset>? choosePreset = null;
        form.Action("Productieoverzicht", () => choosePreset!(PresentationPreset.ProductionOverview));
        form.Action("Aansluitdetail", () => choosePreset!(PresentationPreset.ConnectionDetail));
        var presetNote = new TextBlock { Text = "Connectorlabels behouden hun huidige instellingen.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 10) };
        form.Panel.Children.Add(presetNote);
        form.Heading("Aders en connectoren");
        var style = view.Style;
        var showWireLabels = form.Check("Automatische aderlabels tonen", style.ShowWireLabels);
        var showTwistLabels = form.Check("Automatische twistlabels tonen", style.ShowTwistLabels);
        var sectionWidth = form.Check("Dikkere voedingsdraden weergeven", style.UseWireSectionWidth);
        var wireWidth = form.Number("Aderlijndikte (mm)", style.WireLineWidthMm);
        var wireFont = form.Number("Teksthoogte aderlabels (mm)", style.WireLabelFontSizeMm);
        var connectorWidth = form.Number("Connectorlijndikte (mm)", style.ConnectorLineWidthMm);
        form.Heading("Twistweergave");
        var twistMode = form.Choice("Getekende twists", Enum.GetValues<TwistDisplayMode>(), style.TwistDisplayMode,
            mode => mode switch
            {
                TwistDisplayMode.Markers => "Korte twistmarkeringen",
                TwistDisplayMode.Parallel => "Parallelle aders",
                _ => "Over de hele route"
            });
        var amplitude = form.Number("Uitwijking twistweergave (mm)", style.TwistAmplitudeMm);
        var pitch = form.Number("Getekende spoed (mm; 0 = fysieke spoed)", style.TwistDisplayPitchMm);
        form.Note("De twistweergave verandert geen fysieke spoed, routelengte of kniplengte.");

        var preset = PresentationPreset.None;
        choosePreset = choice =>
        {
            preset = choice;
            var overview = choice == PresentationPreset.ProductionOverview;
            showWireLabels.IsChecked = !overview;
            showTwistLabels.IsChecked = !overview;
            sectionWidth.IsChecked = overview;
            wireWidth.Text = EditData.Format(overview ? .25 : .35);
            wireFont.Text = EditData.Format(2.2);
            connectorWidth.Text = EditData.Format(overview ? .18 : .25);
            amplitude.Text = EditData.Format(overview ? .45 : .7);
            pitch.Text = EditData.Format(overview ? 6 : 0);
            SelectChoice(twistMode, overview ? TwistDisplayMode.Markers : TwistDisplayMode.Full);
            presetNote.Text = overview
                ? "Productieoverzicht: connectornamen aan; artikelnummers, pinnummers, pindots en splicelabels uit. Connectornamen 2,5 mm. Vergrotingen en labelposities blijven behouden."
                : "Aansluitdetail: connectornamen, artikelnummers, pinnummers, pindots en splicelabels aan. Connectornamen 3 mm. Vergrotingen en labelposities blijven behouden.";
        };
        form.Note("De zichtbaarheid, teksthoogte, labelpositie en vergroting van één connector stel je in bij de eigenschappen van die connector.");
        dialog.OnAccept = () =>
        {
            var next = new DrawingStyle
            {
                ShowWireLabels = showWireLabels.IsChecked == true,
                ShowTwistLabels = showTwistLabels.IsChecked == true,
                UseWireSectionWidth = sectionWidth.IsChecked == true,
                WireLineWidthMm = EditData.Number(wireWidth.Text, "Aderlijndikte", .01),
                WireLabelFontSizeMm = EditData.Number(wireFont.Text, "Teksthoogte aderlabels", .1),
                ConnectorLineWidthMm = EditData.Number(connectorWidth.Text, "Connectorlijndikte", .01),
                TwistAmplitudeMm = EditData.Number(amplitude.Text, "Uitwijking twistweergave", 0),
                TwistDisplayPitchMm = EditData.Number(pitch.Text, "Getekende spoed", 0),
                TwistDisplayMode = PropertyForm.Value<TwistDisplayMode>(twistMode)
            };
            Execute(DrawingStyleCommand(view, next, preset));
            return true;
        };
        dialog.ShowDialog();
    }

    private enum PresentationPreset { None, ProductionOverview, ConnectionDetail }

    private IEditCommand DrawingStyleCommand(DrawingView view, DrawingStyle next, PresentationPreset preset)
    {
        var commands = new List<IEditCommand> { HarnessOperations.SetDrawingStyle(view, next) };
        if (preset != PresentationPreset.None)
        {
            var overview = preset == PresentationPreset.ProductionOverview;
            foreach (var placement in view.Components)
            {
                var connector = _project.Assembly.Connectors.Any(c => c.Id == placement.EntityId);
                commands.Add(HarnessOperations.SetComponentPresentation(_project, view, placement,
                    placement.DisplayScale, showPins: !overview, showPinNumbers: !overview,
                    showLabel: connector || !overview, showPartNumber: !overview, labelFontSizeMm: overview ? 2.5 : 3));
            }
        }
        return new CompositeCommand($"Tekenstijl wijzigen · {view.Name}", commands.ToArray());
    }

    private static void SelectChoice<T>(ComboBox choice, T value)
    {
        for (var i = 0; i < choice.Items.Count; i++)
            if (Equals(((ComboBoxItem)choice.Items[i]).Tag, value)) { choice.SelectedIndex = i; return; }
    }

    private ComponentPresentationFields BuildComponentPresentation(PropertyForm form, PlacedComponent placement, bool connector)
    {
        form.Heading("Weergave in dit aanzicht");
        var scale = form.Number(connector ? "Connectorvergroting (1 = ware grootte)" : "Vergroting splice-markering", placement.DisplayScale);
        if (scale is not null)
        {
            scale.IsEnabled = _view.Mode == LayoutMode.Free;
            form.Note("Afzonderlijk vergroten kan in vrije opmaak. De afbeelding en getekende aansluitpunten veranderen; productiematen blijven behouden. Op schaal blijft de connector op ware grootte.");
        }
        var label = form.Check(connector ? "Connectornaam tonen" : "Splicenaam tonen", placement.ShowLabel);
        var partNumber = connector ? form.Check("Artikelnummer tonen", placement.ShowPartNumber) : null;
        var pins = connector ? form.Check("Pindots tonen", placement.ShowPins) : null;
        var pinNumbers = connector ? form.Check("Pinnummers tonen", placement.ShowPinNumbers) : null;
        var font = form.Number("Teksthoogte naam (mm)", placement.LabelFontSizeMm);
        return new(_view, placement, scale, pins, pinNumbers, label, partNumber, font);
    }

    // These fields share the placement refresh policy with position/rotation: a model
    // change updates only that field, preserving unrelated input that has not been applied.
    private sealed class ComponentPresentationFields
    {
        private readonly PlacedComponent _placement;
        private readonly DrawingView _view;
        private readonly TextBox? _scale;
        private readonly CheckBox? _pins, _pinNumbers, _partNumber;
        private readonly CheckBox _label;
        private readonly TextBox _font;
        private double _knownScale, _knownFont;
        private bool _knownPins, _knownPinNumbers, _knownLabel, _knownPartNumber;

        public ComponentPresentationFields(DrawingView view, PlacedComponent placement, TextBox? scale, CheckBox? pins,
            CheckBox? pinNumbers, CheckBox label, CheckBox? partNumber, TextBox font)
        {
            _view = view; _placement = placement; _scale = scale; _pins = pins; _pinNumbers = pinNumbers;
            _label = label; _partNumber = partNumber; _font = font;
            Remember();
        }

        public IEditCommand Command(HarnessProject project, DrawingView view, PlacedComponent placement,
            Point2 position, double rotation) =>
            HarnessOperations.SetComponentPresentation(project, view, placement,
                _scale is null ? placement.DisplayScale : EditData.Number(_scale.Text, "Connectorvergroting", .001),
                _pins?.IsChecked ?? placement.ShowPins, _pinNumbers?.IsChecked ?? placement.ShowPinNumbers,
                _label.IsChecked == true, _partNumber?.IsChecked ?? placement.ShowPartNumber,
                EditData.Number(_font.Text, "Teksthoogte naam", .1), position, rotation);

        public void Refresh()
        {
            if (_scale is not null) _scale.IsEnabled = _view.Mode == LayoutMode.Free;
            if (_scale is not null && _placement.DisplayScale != _knownScale) _scale.Text = EditData.Format(_placement.DisplayScale);
            if (_pins is not null && _placement.ShowPins != _knownPins) _pins.IsChecked = _placement.ShowPins;
            if (_pinNumbers is not null && _placement.ShowPinNumbers != _knownPinNumbers) _pinNumbers.IsChecked = _placement.ShowPinNumbers;
            if (_placement.ShowLabel != _knownLabel) _label.IsChecked = _placement.ShowLabel;
            if (_partNumber is not null && _placement.ShowPartNumber != _knownPartNumber) _partNumber.IsChecked = _placement.ShowPartNumber;
            if (_placement.LabelFontSizeMm != _knownFont) _font.Text = EditData.Format(_placement.LabelFontSizeMm);
            Remember();
        }

        private void Remember()
        {
            _knownScale = _placement.DisplayScale; _knownPins = _placement.ShowPins; _knownPinNumbers = _placement.ShowPinNumbers;
            _knownLabel = _placement.ShowLabel; _knownPartNumber = _placement.ShowPartNumber; _knownFont = _placement.LabelFontSizeMm;
        }
    }
}
