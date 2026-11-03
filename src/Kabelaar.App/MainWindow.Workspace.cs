using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Kabelaar.Core;
using Kabelaar.Infrastructure;

namespace Kabelaar.App;

public partial class MainWindow
{
    private WorkspacePreferences _workspace = new();
    private readonly WorkspacePreferencesStore _workspaceStore = new();
    private readonly string _workspacePath = Path.Combine(App.DataDirectory, "werkruimte.json");
    private HashSet<string> _deletedLibraryIds = new(StringComparer.Ordinal);
    private readonly List<(DependencyObject Element, DependencyProperty Property, Brush Original, bool Foreground)> _themeBrushes = [];
    private GridLength _libraryWidth = new(220), _propertiesWidth = new(290), _tablesHeight = new(210);

    private void InitializeWorkspace()
    {
        if (App.IsBackgroundAcceptance)
        {
            CaptureThemeBrushes(this); ApplyWorkspace(); return;
        }
        try { _workspace = _workspaceStore.Load(_workspacePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { App.Log(error); Status("Werkruimtevoorkeuren konden niet worden gelezen; standaardweergave is actief."); }
        try { _deletedLibraryIds = _libraries.GetDeletedPartIds(_userLibrary); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { App.Log(error); Status("De lijst met verwijderde bibliotheekonderdelen kon niet worden gelezen."); }
        CaptureThemeBrushes(this);
        ApplyWorkspace();
    }

    private bool IsLibraryPartVisible(PartDefinition part) => !_deletedLibraryIds.Contains(part.Id);

    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FormDialog(this, "Werkruimte", 490);
        var library = dialog.Form.Check("Project en bibliotheek tonen", _workspace.ShowLibrary);
        var properties = dialog.Form.Check("Eigenschappen tonen", _workspace.ShowProperties);
        var tables = dialog.Form.Check("Aansluitlijst, stuklijst en controle tonen", _workspace.ShowTables);
        var dark = dialog.Form.Check("Donkere interface", _workspace.DarkMode);
        var scale = dialog.Form.Number("Interfaceschaal (0,75–2)", _workspace.UiScale);
        dialog.Form.Heading("Standaarden voor nieuwe objecten");
        dialog.Form.Note("Deze instellingen wijzigen geen bestaande tekenobjecten, productiegegevens of afdrukschaal.");
        var wireColor = dialog.Form.Color("Nieuwe ader: tekenkleur", _workspace.WireColor);
        var wireWidth = dialog.Form.Number("Nieuw aanzicht: aderlijndikte (mm)", _workspace.WireLineWidthMm);
        var noteColor = dialog.Form.Color("Nieuwe annotatie: kleur", _workspace.AnnotationColor);
        var font = dialog.Form.Number("Nieuwe annotatie: teksthoogte (mm)", _workspace.AnnotationFontSizeMm);
        var line = dialog.Form.Number("Nieuwe annotatie: lijndikte (mm)", _workspace.AnnotationLineWidthMm);
        var arrow = dialog.Form.Number("Nieuwe leader: pijlgrootte (mm)", _workspace.LeaderArrowSizeMm);
        WorkspacePreferences? proposed = null;
        dialog.OnAccept = () =>
        {
            proposed = new()
            {
                ShowLibrary = library.IsChecked == true, ShowProperties = properties.IsChecked == true,
                ShowTables = tables.IsChecked == true, DarkMode = dark.IsChecked == true,
                UiScale = EditData.Number(scale.Text, "Interfaceschaal"), WireColor = wireColor.Text,
                WireLineWidthMm = EditData.Number(wireWidth.Text, "Aderlijndikte"), AnnotationColor = noteColor.Text,
                AnnotationFontSizeMm = EditData.Number(font.Text, "Teksthoogte"),
                AnnotationLineWidthMm = EditData.Number(line.Text, "Lijndikte"),
                LeaderArrowSizeMm = EditData.Number(arrow.Text, "Pijlgrootte")
            };
            proposed.Validate(); return true;
        };
        if (dialog.ShowDialog() != true || proposed is null) return;
        try
        {
            await _workspaceStore.SaveAsync(proposed, _workspacePath);
            _workspace = proposed; ApplyWorkspace(); Status("Werkruimtevoorkeuren bewaard.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { App.Log(error); MessageBox.Show(this, error.Message, "Werkruimte niet bewaard"); }
    }

    private void ApplyWorkspace()
    {
        if (LibraryColumn.Width.Value > 0) _libraryWidth = LibraryColumn.Width;
        if (PropertiesColumn.Width.Value > 0) _propertiesWidth = PropertiesColumn.Width;
        if (TablesRow.Height.Value > 0) _tablesHeight = TablesRow.Height;
        LibraryColumn.MinWidth = _workspace.ShowLibrary ? 165 : 0;
        LibraryColumn.Width = _workspace.ShowLibrary ? _libraryWidth : new GridLength(0);
        LeftSplitterColumn.Width = new GridLength(_workspace.ShowLibrary ? 5 : 0);
        PropertiesColumn.MinWidth = _workspace.ShowProperties ? 225 : 0;
        PropertiesColumn.Width = _workspace.ShowProperties ? _propertiesWidth : new GridLength(0);
        RightSplitterColumn.Width = new GridLength(_workspace.ShowProperties ? 5 : 0);
        TablesRow.MinHeight = _workspace.ShowTables ? 100 : 0;
        TablesRow.Height = _workspace.ShowTables ? _tablesHeight : new GridLength(0);
        TablesSplitterRow.Height = new GridLength(_workspace.ShowTables ? 5 : 0);
        foreach (UIElement child in WorkspaceGrid.Children)
        {
            var column = Grid.GetColumn(child); var row = Grid.GetRow(child);
            var visible = column is 0 or 1 ? _workspace.ShowLibrary : column is 3 or 4 ? _workspace.ShowProperties :
                row is 1 or 2 ? _workspace.ShowTables : true;
            child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (Content is FrameworkElement content) content.LayoutTransform = new ScaleTransform(_workspace.UiScale, _workspace.UiScale);
        ApplyWorkspaceTheme();
    }

    private void CaptureThemeBrushes(DependencyObject element)
    {
        void Capture(DependencyProperty property, bool foreground)
        {
            if (element.GetValue(property) is SolidColorBrush brush)
            {
                var c = brush.Color;
                // Leave accent colours and the dark application heading untouched.
                if (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 65)
                    _themeBrushes.Add((element, property, brush, foreground));
            }
        }
        switch (element)
        {
            case HarnessCanvas: return;
            case Control: Capture(Control.BackgroundProperty, false); Capture(Control.ForegroundProperty, true); break;
            case Panel: Capture(Panel.BackgroundProperty, false); break;
            case Border: Capture(Border.BackgroundProperty, false); break;
            case TextBlock: Capture(TextBlock.ForegroundProperty, true); break;
        }
        if (element is Window { Content: DependencyObject content }) { CaptureThemeBrushes(content); return; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) CaptureThemeBrushes(VisualTreeHelper.GetChild(element, i));
    }

    private void ApplyWorkspaceTheme()
    {
        var darkBackground = new SolidColorBrush(Color.FromRgb(30, 38, 49));
        var darkInput = new SolidColorBrush(Color.FromRgb(43, 54, 68));
        var darkText = new SolidColorBrush(Color.FromRgb(232, 239, 246));
        foreach (var (element, property, original, foreground) in _themeBrushes)
        {
            var color = ((SolidColorBrush)original).Color;
            var change = foreground ? color.R + color.G + color.B < 570 : color.R + color.G + color.B > 570;
            element.SetValue(property, _workspace.DarkMode && change ? foreground ? darkText : darkBackground : original);
        }
        foreach (var type in new[] { typeof(TextBox), typeof(ComboBox), typeof(Button), typeof(ListBox), typeof(TreeView),
                     typeof(TabControl), typeof(TabItem), typeof(DataGrid), typeof(DataGridColumnHeader), typeof(CheckBox), typeof(MenuItem) })
        {
            if (!_workspace.DarkMode) { Resources.Remove(type); continue; }
            var style = new Style(type, Application.Current.TryFindResource(type) as Style);
            style.Setters.Add(new Setter(Control.ForegroundProperty, darkText));
            style.Setters.Add(new Setter(Control.BackgroundProperty, darkInput));
            if (type == typeof(DataGrid)) style.Setters.Add(new Setter(DataGrid.AlternatingRowBackgroundProperty, darkBackground));
            Resources[type] = style;
        }
        if (_workspace.DarkMode)
        {
            var textStyle = new Style(typeof(TextBlock), Application.Current.TryFindResource(typeof(TextBlock)) as Style);
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, darkText)); Resources[typeof(TextBlock)] = textStyle;
        }
        else Resources.Remove(typeof(TextBlock));
    }

    private void ApplyNewWireDefaults(Conductor wire) => wire.Color = _workspace.WireColor;
    private void ApplyNewAnnotationDefaults(Annotation note)
    {
        note.Color = _workspace.AnnotationColor; note.FontSizeMm = _workspace.AnnotationFontSizeMm;
        note.LineWidthMm = _workspace.AnnotationLineWidthMm; note.ArrowSizeMm = _workspace.LeaderArrowSizeMm;
    }
    private void ApplyNewViewDefaults(DrawingView view) => view.Style.WireLineWidthMm = _workspace.WireLineWidthMm;
}
