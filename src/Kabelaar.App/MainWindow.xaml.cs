using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using Kabelaar.Rendering;
using Microsoft.Win32;

namespace Kabelaar.App;

public partial class MainWindow : Window
{
    private HarnessProject _project = new();
    private Sheet _sheet;
    private DrawingView _view;
    private readonly CommandHistory _history = new();
    private readonly ProjectStore _store = new();
    private readonly RecoveryStore _recovery = new();
    private readonly LibraryStore _libraries = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _validationTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly string _sessionId = Ids.New();
    private RecoveryEntry? _recoveryEntry;
    private RecoveryEntry? _recoveredFrom;
    private ProjectLease? _lease;
    private string? _path, _fingerprint;
    private bool _refreshing, _closing, _saving, _autosaving, _suspendAutosave;
    private CancellationTokenSource? _busy;
    private readonly string _userLibrary = Path.Combine(App.DataDirectory, "onderdelen.kabelaarlib");
    private readonly SemaphoreSlim _librarySaveGate = new(1, 1);
    private List<ValidationIssue> _issues = [];
    private string _treeSignature = "", _librarySignature = "";
    private readonly Dictionary<string, double> _refreshTimings = [];
    private readonly Dictionary<string, double> _sceneTimings = [];
    private WireDisplay[] _wireRows = [];
    private bool _propertiesRefreshQueued;
    private PartDefinition? SelectedPart => LibraryList.SelectedItem as PartDefinition;
    private string? SelectedId => Canvas.Selection.FirstOrDefault();

    public MainWindow()
    {
        _sheet = _project.Sheets[0]; _view = _sheet.Views[0];
        InitializeComponent();
        InitializeWorkspace();
        ApplyNewViewDefaults(_view);
        Canvas.ContextRequested += ShowCanvasContextMenu;
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        // A deferred refresh must not allow a stale property button to run. Blocking
        // input at the panel avoids toggling IsEnabled on hundreds of cavity editors,
        // which otherwise invalidates their styles and delays the changed drawing.
        PropertiesPanel.PreviewMouseDown += (_, e) => { if (_propertiesRefreshQueued) e.Handled = true; };
        PropertiesPanel.PreviewKeyDown += (_, e) => { if (_propertiesRefreshQueued) e.Handled = true; };
        _history.Changed += (_, _) => { if (!_refreshing) Refresh(deferValidation: true); };
        _validationTimer.Tick += (_, _) => { _validationTimer.Stop(); if (_busy is null) RefreshValidation(); };
        Canvas.SelectionChanged += SelectionChanged;
        Canvas.ViewActivated += id =>
        {
            var view = _sheet.Views.Find(v => v.Id == id);
            if (view is null || ReferenceEquals(view, _view)) return;
            _view = view; Canvas.Selection.Clear(); Refresh();
        };
        Canvas.CanvasClicked += CanvasClick;
        Canvas.DragCompleted += CanvasDrag;
        Canvas.VertexDragCompleted += VertexDrag;
        Canvas.GeometryEditStarted += BeginGeometryEdit;
        Canvas.GeometryEditUpdated += UpdateGeometryEdit;
        Canvas.GeometryEditCompleted += CommitGeometryEdit;
        Canvas.GeometryEditCancelled += CancelGeometryEdit;
        Canvas.FinishDrawing += FinishDrawing;
        Canvas.SnapProvider = SnapToEndpoint;
        Canvas.PointerMoved += p => PositionText.Text = $"X {p.X:0.0}   Y {p.Y:0.0} mm";
        _autosave.Tick += async (_, _) => await AutoSaveAsync();
        _autosave.Start();
        Refresh();
    }
    public async Task InitializeAsync(string? projectPath)
    {
        if (projectPath is not null) { await OpenPathAsync(projectPath); return; }
        if (File.Exists(_userLibrary))
        {
            try
            {
                // The user's catalog owns stable identities; only explicit external imports remap IDs.
                var catalog = (await _store.LoadAsync(_userLibrary)).Project;
                _project.Parts.AddRange(catalog.Parts);
                _project.Assets.AddRange(catalog.Assets);
            }
            catch (Exception ex) { App.Log(ex); Status("De eigen bibliotheek kon niet worden geladen. Importeren blijft mogelijk."); }
        }
        Refresh(); Canvas.Fit();
        var recoveries = await _recovery.ListAsync();
        if (recoveries.Count > 0 &&
            MessageBox.Show(this, $"Er zijn {recoveries.Count} herstelbestanden. Wil je deze bekijken?",
                "Niet-opgeslagen werk", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            await ShowRecoveryAsync();
    }
    private void Status(string message) => StatusText.Text = message;
    private Point2 ToModel(Point2 sheet) => (sheet - _view.Origin) * (1 / (_view.Scale * _sheet.PrintScale));
    private Point2 ToSheet(Point2 model) => _view.Origin + model * (_view.Scale * _sheet.PrintScale);
    private void Execute(IEditCommand command)
    {
        if (_busy is not null || _saving) { Status("Wacht tot de actieve bewerking klaar is."); return; }
        try { _history.Execute(command); Status(command.Name); }
        catch (Exception ex) { App.Log(ex); if (App.IsBackgroundAcceptance) throw; MessageBox.Show(this, ex.Message, "Bewerking niet toegepast", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Change<T>(string name, List<T> list, T original, T replacement)
    {
        var index = list.IndexOf(original);
        if (index < 0) return;
        Execute(new DelegateCommand(name, () => list[index] = replacement, () => list[index] = original));
    }
    private void Add<T>(string name, List<T> list, T item) => Execute(new DelegateCommand(name, () => list.Add(item), () => list.Remove(item)));
    private void Refresh(bool deferValidation = false)
    {
        var timer = Stopwatch.StartNew();
        void Measure(string stage) { _refreshTimings[stage] = timer.Elapsed.TotalMilliseconds; timer.Restart(); }
        _refreshing = true;
        try
        {
            if (!_project.Sheets.Contains(_sheet)) _sheet = _project.Sheets.First();
            if (!_sheet.Views.Contains(_view)) _view = _sheet.Views.First();
            ProjectHeading.Text = $"{_project.Title}   ·   {_project.DrawingNumber}   ·   Rev. {_project.Revision}";
            Title = $"{(_history.IsDirty ? "● " : "")}{_project.Title} — Kabelaar";
            SheetHeading.Text = $"{_sheet.Name}  ·  {_sheet.WidthMm:g} × {_sheet.HeightMm:g} mm  ·  {_view.Name}";
            LayoutModeBox.SelectedIndex = _view.Mode == LayoutMode.Scaled ? 0 : 1;
            ModeText.Text = $"{(_view.Mode == LayoutMode.Scaled ? "Op schaal" : "Vrije opmaak")} · Afdruk 1:{1 / _sheet.PrintScale:g}";
            SaveStateText.Text = _history.IsDirty ? "Niet opgeslagen" : _path is null ? "Nieuw project" : Path.GetFileName(_path);
            UndoMenu.IsEnabled = _history.CanUndo; RedoMenu.IsEnabled = _history.CanRedo;
            UndoMenu.Header = _history.UndoName is { } undo ? $"Ongedaan maken: {undo}" : "Ongedaan maken";
            RedoMenu.Header = _history.RedoName is { } redo ? $"Opnieuw: {redo}" : "Opnieuw";
            RefreshTree();
            RefreshLibrary();
            Measure("treeLibrary");
            RefreshReports();
            Measure("reports");
            if (deferValidation)
            {
                _validationTimer.Stop(); _validationTimer.Start();
                IssuesTab.Header = "Controle wordt bijgewerkt…";
            }
            else RefreshValidation();
            Measure("validation");
            Canvas.ActiveViewId = _view.Id;
            Canvas.SetScene(SceneBuilder.Build(_project, _sheet,
                timing: App.IsBackgroundAcceptance ? (stage, elapsed) => _sceneTimings[stage] = elapsed : null));
            Measure("scene");
            if (deferValidation) QueuePropertiesRefresh();
            else ShowProperties();
            Measure("properties");
        }
        finally { _refreshing = false; }
    }
    private void QueuePropertiesRefresh()
    {
        // Present the changed drawing first. Reflowing text editors can otherwise hold up
        // that frame; a background dispatcher turn immediately refreshes the property pane.
        if (_propertiesRefreshQueued) return;
        _propertiesRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            try { ShowProperties(); }
            finally { _propertiesRefreshQueued = false; }
        }, DispatcherPriority.Background);
    }
    private void RefreshValidation()
    {
        _validationTimer.Stop();
        _issues = ProductionValidator.Validate(_project).ToList();
        var before = _refreshing;
        _refreshing = true;
        try
        {
            IssuesGrid.ItemsSource = _issues;
            IssuesTab.Header = $"Controle ({_issues.Count(i => i.Severity == Severity.Error)} fouten)";
        }
        finally { _refreshing = before; }
    }
    private void RefreshTree()
    {
        var signature = _project.Id + _sheet.Id + _view.Id + string.Join("|", _project.Sheets.Select(s => s.Id + s.Name +
            string.Join("", s.Views.Select(v => v.Id + v.Name + string.Join("", v.Annotations.Select(a => a.Id + a.Text)))))) +
            string.Join("|", _project.Assembly.Connectors.Select(c => c.Id + c.Name));
        if (signature == _treeSignature) return;
        _treeSignature = signature;
        ProjectTree.Items.Clear();
        foreach (var sheet in _project.Sheets)
        {
            var node = new TreeViewItem { Header = sheet.Name, Tag = sheet, IsExpanded = sheet == _sheet, Margin = new(0, 4, 0, 4) };
            foreach (var view in sheet.Views)
            {
                var viewNode = new TreeViewItem { Header = view.Name, Tag = view, IsSelected = view == _view };
                foreach (var annotation in view.Annotations)
                    viewNode.Items.Add(new TreeViewItem { Header = $"{annotation.Kind} · {annotation.Text.Replace("\n", " ")[..Math.Min(annotation.Text.Length, 30)]}",
                        Tag = (view, annotation.Id) });
                node.Items.Add(viewNode);
            }
            ProjectTree.Items.Add(node);
        }
        var components = new TreeViewItem { Header = $"Onderdelen ({_project.Assembly.Connectors.Count})", IsExpanded = false };
        foreach (var connector in _project.Assembly.Connectors)
            components.Items.Add(new TreeViewItem { Header = connector.Name, Tag = connector.Id });
        ProjectTree.Items.Add(components);
    }
    private void RefreshLibrary()
    {
        var id = SelectedPart?.Id;
        var filter = LibraryFilter.Text ?? "";
        var signature = filter + "|" + _project.Id + string.Join("|", _project.Parts.Select(p => p.Id + p.Revision + p.Name + p.PartNumber + p.Supplier + p.Kind));
        if (signature == _librarySignature) return;
        _librarySignature = signature;
        LibraryList.ItemsSource = _project.Parts.Where(IsLibraryPartVisible).Where(p => $"{p.Name} {p.PartNumber} {p.Supplier} {p.Kind}"
            .Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Kind).ThenBy(p => p.Name).ToArray();
        LibraryList.SelectedItem = _project.Parts.Find(p => p.Id == id);
    }
    private void SelectionChanged()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            WireGrid.SelectedItem = (WireGrid.ItemsSource as WireDisplay[])?.FirstOrDefault(x => Canvas.Selection.Contains(x.Id));
            ShowProperties();
        }
        finally { _refreshing = false; }
    }
    private void Tree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_refreshing || e.NewValue is not TreeViewItem item) return;
        if (item.Tag is DrawingView view)
        {
            if (ReferenceEquals(view, _view)) return;
            _sheet = _project.Sheets.First(s => s.Views.Contains(view)); _view = view;
            Canvas.ClearSelection(); Canvas.Cancel(); Refresh(); Canvas.Fit();
        }
        else if (item.Tag is Sheet sheet) { if (ReferenceEquals(sheet, _sheet)) return; _sheet = sheet; _view = sheet.Views[0]; Refresh(); Canvas.Fit(); }
        else if (item.Tag is ValueTuple<DrawingView, string> annotation)
        {
            _sheet = _project.Sheets.First(s => s.Views.Contains(annotation.Item1)); _view = annotation.Item1;
            Refresh(); Canvas.Select(annotation.Item2);
        }
        else if (item.Tag is string id) Canvas.Select(id);
    }
    private void LibraryFilter_Changed(object sender, TextChangedEventArgs e) { if (!_refreshing && LibraryList is not null) RefreshLibrary(); }
    private void Library_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing && Canvas.Selection.Count == 0) ShowProperties(); }
    private void Library_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedPart?.Kind is PartKind.Housing or PartKind.RingTerminal) SetTool(EditorTool.Connector);
        else EditPart_Click(sender, e);
    }
    private void WireGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_refreshing && WireGrid.SelectedItem is WireDisplay row) Canvas.Select(row.Id); }
    private void WireGrid_DoubleClick(object sender, MouseButtonEventArgs e) { if (WireGrid.SelectedItem is WireDisplay row) Canvas.Select(row.Id); }
    private void IssuesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || IssuesGrid.SelectedItem is not ValidationIssue issue || string.IsNullOrEmpty(issue.EntityId)) return;
        var target = _project.Sheets.SelectMany(s => s.Views.Select(v => (s, v))).FirstOrDefault(x =>
            x.v.Components.Any(c => c.EntityId == issue.EntityId) || x.v.Routes.Any(r => r.BranchId == issue.EntityId));
        if (target.v is not null && target.v != _view) { _sheet = target.s; _view = target.v; Refresh(); Canvas.Fit(); }
        Canvas.Select(issue.EntityId);
    }
    private void GridChanged(object sender, RoutedEventArgs e)
    {
        try { Canvas.GridSize = EditData.Number(GridSizeBox.Text, "Raster", .1); }
        catch { GridSizeBox.Text = EditData.Format(Canvas.GridSize); }
        Canvas.InvalidateVisual();
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.2);
    private void Undo_Click(object sender, RoutedEventArgs e) { if (_busy is null && !_saving) _history.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (_busy is null && !_saving) _history.Redo(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Validate_Click(object sender, RoutedEventArgs e) { Refresh(); TablesTab.SelectedItem = IssuesTab; Status($"Controle voltooid: {_issues.Count} bevindingen."); }
    private void CancelBusy_Click(object sender, RoutedEventArgs e) => _busy?.Cancel();
    private async Task BusyAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_busy is not null) return;
        using var cancel = new CancellationTokenSource();
        _busy = cancel; BusyText.Text = message; BusyOverlay.Visibility = Visibility.Visible;
        try { await action(cancel.Token); }
        catch (OperationCanceledException) { Status("Bewerking geannuleerd."); }
        catch (Exception ex) { App.Log(ex); MessageBox.Show(this, ex.Message, message, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = null; BusyOverlay.Visibility = Visibility.Collapsed; Refresh(); }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var input = Keyboard.FocusedElement is TextBox or ComboBox || Keyboard.FocusedElement is DependencyObject d &&
            FindParent<DataGrid>(d)?.IsReadOnly == false;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: Save_Click(this, e); break;
                case Key.O: Open_Click(this, e); break;
                case Key.N: New_Click(this, e); break;
                case Key.Z when !input: Undo_Click(this, e); break;
                case Key.Y when !input: Redo_Click(this, e); break;
                case Key.D when !input: Duplicate_Click(this, e); break;
                case Key.V when !input: Paste_Click(this, e); break;
                default: return;
            }
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        { SaveAs_Click(this, e); e.Handled = true; return; }
        if (e.Key == Key.Escape) { SetTool(EditorTool.Select); e.Handled = true; return; }
        if (input) return;
        switch (e.Key)
        {
            case Key.W: SetTool(EditorTool.Wire); break;
            case Key.R: Rotate_Click(this, e); break;
            case Key.Delete: Delete_Click(this, e); break;
            case Key.Home: Canvas.Fit(); break;
            case Key.Enter: FinishDrawing(); break;
            case Key.F7: Validate_Click(this, e); break;
            case Key.F1: Help_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }
    private static T? FindParent<T>(DependencyObject obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T result) return result;
            obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
    private sealed class WireDisplay(WireRow row) : INotifyPropertyChanged
    {
        private WireRow _row = row;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(WireRow next)
        {
            if (_row == next) return;
            var previous = _row; _row = next;
            void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (previous.Name != next.Name) Changed(nameof(Name));
            if (previous.From != next.From) Changed(nameof(From));
            if (previous.To != next.To) Changed(nameof(To));
            if (previous.Color != next.Color) Changed(nameof(Color));
            if (!previous.CrossSectionMm2.Equals(next.CrossSectionMm2)) Changed(nameof(CrossSection));
            if (!previous.RouteLengthMm.Equals(next.RouteLengthMm)) Changed(nameof(RouteLength));
            if (!previous.CutLengthMm.Equals(next.CutLengthMm)) Changed(nameof(CutLength));
            if (previous.Signal != next.Signal) Changed(nameof(Signal));
        }
        public string Id => _row.Id;
        public string Name => _row.Name;
        public string From => _row.From;
        public string To => _row.To;
        public string Color => _row.Color;
        public string CrossSection => _row.CrossSectionMm2.ToString("0.######");
        public string RouteLength => _row.RouteLengthMm.ToString("0.######");
        public string CutLength => _row.CutLengthMm.ToString("0.######");
        public string Signal => _row.Signal;
    }
}
