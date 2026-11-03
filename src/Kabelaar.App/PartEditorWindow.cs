using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Kabelaar.Core;
using Kabelaar.Infrastructure;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Kabelaar.App;

internal sealed class PartEditorWindow : Window
{
    public PartDefinition Result { get; private set; }
    private readonly PartDefinition _part;
    private readonly PropertyForm _form = new();
    private readonly SKElement _drawing = new() { MinHeight = 140, Focusable = true };
    private readonly ObservableCollection<PinEditRow> _pins;
    private readonly DataGrid _pinGrid;
    private readonly List<Point2> _pending = [];
    private readonly TextBox _name, _number, _supplier, _revision, _description, _width, _height, _color, _section, _min, _max;
    private readonly ComboBox _kind, _mode;
    private readonly CheckBox _terminal, _seal, _hidden;
    private double _scale = 5, _ox, _oy;

    public PartEditorWindow(Window owner, PartDefinition part, CadImportResult? cad = null)
    {
        Owner = owner; Title = cad is null ? "Onderdeel en pinnen definiëren" : "STEP — projectie en pinnen vastleggen";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width); Height = Math.Min(850, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(900, Width); MinHeight = Math.Min(650, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        _part = EditData.Clone(part); Result = _part;
        _pins = new(_part.Pins.Select(p => new PinEditRow { Id = p.Id, Label = p.Label, Function = p.Function, X = p.Position.X, Y = p.Position.Y, MaxWires = p.MaxWires }));
        var dock = new DockPanel { Margin = new(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuleren", IsCancel = true };
        var save = new Button { Content = "Onderdeel bewaren", MinWidth = 160 };
        save.SetResourceReference(StyleProperty, "PrimaryButton"); save.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new());
        var scroll = new ScrollViewer { Content = _form.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(scroll);
        _name = _form.Text("Naam", _part.Name); _number = _form.Text("Artikelnummer", _part.PartNumber);
        _supplier = _form.Text("Leverancier", _part.Supplier); _revision = _form.Text("Onderdeelrevisie", _part.Revision);
        _kind = _form.Choice("Soort", Enum.GetValues<PartKind>(), _part.Kind);
        _description = _form.Text("Omschrijving", _part.Description, true);
        _width = _form.Number("Breedte (mm)", _part.WidthMm); _height = _form.Number("Hoogte (mm)", _part.HeightMm);
        _color = _form.Text("Kleur (#RRGGBB)", _part.Color); _section = _form.Number("Draaddoorsnede (mm²)", _part.CrossSectionMm2);
        _min = _form.Text("Minimale doorsnede (optioneel)", _part.MinCrossSectionMm2?.ToString() ?? "");
        _max = _form.Text("Maximale doorsnede (optioneel)", _part.MaxCrossSectionMm2?.ToString() ?? "");
        _terminal = _form.Check("Terminal verplicht bij bezette cavity", _part.RequiresTerminal);
        _seal = _form.Check("Seal verplicht bij bezette cavity", _part.RequiresSeal);
        var right = new Grid(); Grid.SetColumn(right, 2); grid.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new() { Height = new GridLength(180) });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new StackPanel();
        controls.Children.Add(new TextBlock { Text = "Aanzicht, symbool en expliciete pinmapping", FontSize = 17, FontWeight = FontWeights.SemiBold });
        if (cad is not null)
        {
            var projection = new ComboBox { ItemsSource = cad.Views.Select(v => v.Name), SelectedItem = _part.Projection, Margin = new(0, 8, 0, 8) };
            projection.SelectionChanged += (_, _) =>
            {
                var v = cad.Views.First(x => x.Name == (string)projection.SelectedItem);
                _part.Symbol = EditData.Clone(v.Paths); _part.Projection = v.Name;
                _part.WidthMm = Math.Max(.1, v.WidthMm); _part.HeightMm = Math.Max(.1, v.HeightMm);
                _width.Text = EditData.Format(_part.WidthMm); _height.Text = EditData.Format(_part.HeightMm);
                _pending.Clear(); _drawing.InvalidateVisual();
            };
            controls.Children.Add(projection);
            controls.Children.Add(new TextBlock { Text = $"Geometrie omgerekend naar {cad.Units} · {cad.KernelVersion} · pinnummers worden handmatig vastgelegd", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
            if (cad.Warnings.Count > 0) controls.Children.Add(new TextBlock { Text = string.Join("\n", cad.Warnings), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange });
        }
        var toolbar = new WrapPanel { Margin = new(0, 8, 0, 6) };
        _mode = new ComboBox { Width = 180, ItemsSource = new[] { "Pin plaatsen / verplaatsen", "Lijn (2 punten)", "Boog (3 punten)", "Tekst plaatsen" }, SelectedIndex = 0 };
        _mode.SelectionChanged += (_, _) => _pending.Clear();
        _hidden = new CheckBox { Content = "Verborgen lijnen", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
        _hidden.Checked += (_, _) => _drawing.InvalidateVisual(); _hidden.Unchecked += (_, _) => _drawing.InvalidateVisual();
        toolbar.Children.Add(_mode); toolbar.Children.Add(_hidden);
        var undo = new Button { Content = "Laatste symboollijn weg" };
        undo.Click += (_, _) => { if (_part.Symbol.Count > 0) _part.Symbol.RemoveAt(_part.Symbol.Count - 1); _drawing.InvalidateVisual(); };
        toolbar.Children.Add(undo); controls.Children.Add(toolbar); right.Children.Add(controls);
        var preview = new Grid();
        preview.ColumnDefinitions.Add(new());
        if (cad is not null)
        {
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(.8, GridUnitType.Star) });
            var mesh = MeshPreview(cad.Mesh); Grid.SetColumn(mesh, 1); preview.Children.Add(mesh);
        }
        preview.Children.Add(new Border { Child = _drawing, BorderBrush = Brushes.LightGray, BorderThickness = new(1), Background = Brushes.WhiteSmoke });
        Grid.SetRow(preview, 1); right.Children.Add(preview);
        _pinGrid = new DataGrid { ItemsSource = _pins, AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = true,
            CanUserDeleteRows = true, Margin = new(0, 8, 0, 0), SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, property) in new[] { ("Pin", "Label"), ("Functie", "Function"), ("X mm", "X"), ("Y mm", "Y"), ("Max. aders", "MaxWires") })
            _pinGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _pinGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(() => _drawing.InvalidateVisual());
        _pinGrid.SelectionChanged += (_, _) => _drawing.InvalidateVisual();
        Grid.SetRow(_pinGrid, 2); right.Children.Add(_pinGrid);
        var note = new TextBlock { Text = "Selecteer een pinrij en klik de positie in het 2D-aanzicht. Zonder selectie voegt een klik een pin toe. Coördinaten zijn millimeters; nummering en capaciteit zijn expliciete productiegegevens.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 8, 0, 12) };
        Grid.SetRow(note, 3); right.Children.Add(note);
        dock.Children.Add(grid); Content = dock;
        _drawing.PaintSurface += Paint;
        _drawing.MouseLeftButtonDown += (_, e) =>
        {
            var pixel = e.GetPosition(_drawing);
            var p = new Point2(Math.Round((pixel.X - _ox) / _scale, 2), Math.Round((pixel.Y - _oy) / _scale, 2));
            if (_mode.SelectedIndex == 0)
            {
                if (_pinGrid.SelectedItem is PinEditRow row) { row.X = p.X; row.Y = p.Y; _pinGrid.Items.Refresh(); }
                else _pins.Add(new() { Label = (_pins.Count + 1).ToString(), X = p.X, Y = p.Y });
            }
            else if (_mode.SelectedIndex == 3)
            {
                var text = FormDialog.Ask(this, "Symbooltekst", "Tekst");
                if (text is not null) _part.Labels.Add(new() { Text = text, Position = p });
            }
            else
            {
                _pending.Add(p);
                if (_mode.SelectedIndex == 1 && _pending.Count == 2) { _part.Symbol.Add(new() { Points = [.. _pending] }); _pending.Clear(); }
                if (_mode.SelectedIndex == 2 && _pending.Count == 3)
                {
                    var center = _pending[0]; var radius = center.DistanceTo(_pending[1]);
                    var start = Math.Atan2(_pending[1].Y - center.Y, _pending[1].X - center.X);
                    var end = Math.Atan2(_pending[2].Y - center.Y, _pending[2].X - center.X);
                    if (end <= start) end += Math.PI * 2;
                    _part.Symbol.Add(new() { Points = Enumerable.Range(0, 49).Select(i => center + new Point2(Math.Cos(start + (end - start) * i / 48), Math.Sin(start + (end - start) * i / 48)) * radius).ToList() });
                    _pending.Clear();
                }
            }
            _drawing.InvalidateVisual();
        };
    }
    private void Accept()
    {
        try
        {
            _pinGrid.CommitEdit(DataGridEditingUnit.Cell, true); _pinGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_pins.Any(p => string.IsNullOrWhiteSpace(p.Label) || p.MaxWires < 1 || !double.IsFinite(p.X) || !double.IsFinite(p.Y))) throw new ArgumentException("Controleer pinnummers, posities en aderlimieten.");
            if (_pins.Select(p => p.Label).Distinct().Count() != _pins.Count) throw new ArgumentException("Pinnummers moeten uniek zijn.");
            if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("Vul een onderdeelnaam in.");
            if (!SKColor.TryParse(_color.Text, out _)) throw new ArgumentException("Gebruik een geldige kleur, bijvoorbeeld #243244.");
            _part.Name = _name.Text; _part.PartNumber = _number.Text; _part.Supplier = _supplier.Text; _part.Revision = _revision.Text;
            _part.Kind = PropertyForm.Value<PartKind>(_kind); _part.Description = _description.Text;
            _part.WidthMm = EditData.Number(_width.Text, "Breedte", .01); _part.HeightMm = EditData.Number(_height.Text, "Hoogte", .01);
            _part.Color = _color.Text; _part.CrossSectionMm2 = EditData.Number(_section.Text, "Doorsnede", 0);
            _part.MinCrossSectionMm2 = string.IsNullOrWhiteSpace(_min.Text) ? null : EditData.Number(_min.Text, "Minimum", 0);
            _part.MaxCrossSectionMm2 = string.IsNullOrWhiteSpace(_max.Text) ? null : EditData.Number(_max.Text, "Maximum", 0);
            if (_part.MinCrossSectionMm2 > _part.MaxCrossSectionMm2) throw new ArgumentException("Minimumdoorsnede is groter dan maximumdoorsnede.");
            _part.RequiresTerminal = _terminal.IsChecked == true; _part.RequiresSeal = _seal.IsChecked == true;
            _part.Pins = _pins.Select(p => new PinDefinition { Id = p.Id, Label = p.Label, Function = p.Function, Position = new(p.X, p.Y), MaxWires = p.MaxWires }).ToList();
            Result = _part; DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Controleer onderdeelgegevens"); }
    }
    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas; canvas.Clear(SKColors.White);
        var points = _part.Symbol.SelectMany(p => p.Points).Concat(_pins.Select(p => new Point2(p.X, p.Y))).ToArray();
        var minX = Math.Min(-_part.WidthMm / 2, points.Length == 0 ? 0 : points.Min(p => p.X));
        var maxX = Math.Max(_part.WidthMm / 2, points.Length == 0 ? 0 : points.Max(p => p.X));
        var minY = Math.Min(-_part.HeightMm / 2, points.Length == 0 ? 0 : points.Min(p => p.Y));
        var maxY = Math.Max(_part.HeightMm / 2, points.Length == 0 ? 0 : points.Max(p => p.Y));
        _scale = Math.Min((_drawing.ActualWidth - 50) / (maxX - minX), (_drawing.ActualHeight - 50) / (maxY - minY));
        _scale = Math.Max(.01, _scale); _ox = _drawing.ActualWidth / 2 - (minX + maxX) / 2 * _scale; _oy = _drawing.ActualHeight / 2 - (minY + maxY) / 2 * _scale;
        var dpi = e.Info.Width / Math.Max(1, _drawing.ActualWidth); canvas.Scale((float)dpi); canvas.Translate((float)_ox, (float)_oy); canvas.Scale((float)_scale);
        using var paint = new SKPaint { Color = SKColor.Parse("#223446"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.2 / _scale) };
        if (_part.Symbol.Count == 0) canvas.DrawRect((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY), paint);
        foreach (var path in _part.Symbol.Where(p => !p.Hidden || _hidden.IsChecked == true))
        {
            using var builder = new SKPathBuilder();
            if (path.Points.Count == 0) continue;
            builder.MoveTo((float)path.Points[0].X, (float)path.Points[0].Y);
            foreach (var p in path.Points.Skip(1)) builder.LineTo((float)p.X, (float)p.Y);
            if (path.Closed) builder.Close();
            using var sk = builder.Detach();
            paint.Color = path.Hidden ? SKColors.LightGray : SKColor.Parse("#223446"); canvas.DrawPath(sk, paint);
        }
        paint.Style = SKPaintStyle.Fill;
        using var font = new SKFont(SKTypeface.Default, (float)(12 / _scale));
        foreach (var pin in _pins)
        {
            paint.Color = ReferenceEquals(_pinGrid.SelectedItem, pin) ? SKColors.OrangeRed : SKColor.Parse("#087F8C");
            canvas.DrawCircle((float)pin.X, (float)pin.Y, (float)(4 / _scale), paint);
            canvas.DrawText(pin.Label, (float)(pin.X + 6 / _scale), (float)(pin.Y - 5 / _scale), SKTextAlign.Left, font, paint);
        }
        foreach (var label in _part.Labels) { using var f = new SKFont(SKTypeface.Default, (float)label.HeightMm); canvas.DrawText(label.Text, (float)label.Position.X, (float)label.Position.Y, SKTextAlign.Left, f, paint); }
        foreach (var p in _pending) { paint.Color = SKColors.Orange; canvas.DrawCircle((float)p.X, (float)p.Y, (float)(4 / _scale), paint); }
    }
    private static FrameworkElement MeshPreview(CadMesh mesh)
    {
        var grid = new DockPanel { Margin = new(8, 0, 0, 0) };
        var tools = new StackPanel();
        tools.Children.Add(new TextBlock { Text = "3D-preview · los van de productieprojectie", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        tools.Children.Add(new TextBlock { Text = "Links slepen: draaien · midden/rechts slepen: verschuiven · wiel: zoomen", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 4, 0, 4) });
        var fit = new Button { Content = "Passend weergeven", ToolTip = "Home" }; tools.Children.Add(fit);
        DockPanel.SetDock(tools, Dock.Bottom); grid.Children.Add(tools);
        var viewport = new Viewport3D { ClipToBounds = true };
        var geometry = new MeshGeometry3D();
        foreach (var p in mesh.Vertices) geometry.Positions.Add(new(p.X, p.Y, p.Z));
        foreach (var t in mesh.Triangles) foreach (var i in t) geometry.TriangleIndices.Add(i);
        var model = new GeometryModel3D(geometry, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(65, 130, 145)))) { BackMaterial = new DiffuseMaterial(Brushes.LightSlateGray) };
        var group = new Model3DGroup(); group.Children.Add(new AmbientLight(Color.FromRgb(110, 110, 110)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, 2, -3))); group.Children.Add(model);
        viewport.Children.Add(new ModelVisual3D { Content = group });
        var camera = new CadPreviewCamera(mesh.Vertices);
        var interaction = new Border { Child = viewport, Background = Brushes.Transparent, Focusable = true, ClipToBounds = true };
        void Update()
        {
            var pose = camera.Pose;
            viewport.Camera = new PerspectiveCamera(new(pose.Position.X, pose.Position.Y, pose.Position.Z),
                new(pose.Target.X - pose.Position.X, pose.Target.Y - pose.Position.Y, pose.Target.Z - pose.Position.Z),
                new(pose.Up.X, pose.Up.Y, pose.Up.Z), CadPreviewCamera.FieldOfViewDegrees)
            { NearPlaneDistance = pose.NearPlane, FarPlaneDistance = pose.FarPlane };
        }
        void Fit()
        { camera.Fit(Math.Max(1, interaction.ActualWidth) / Math.Max(1, interaction.ActualHeight)); Update(); }
        fit.Click += (_, _) => Fit();
        interaction.Loaded += (_, _) => Fit();
        Point? previous = null; MouseButton? button = null;
        interaction.MouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right)) return;
            interaction.Focus(); previous = e.GetPosition(interaction); button = e.ChangedButton;
            interaction.CaptureMouse(); e.Handled = true;
        };
        interaction.MouseMove += (_, e) =>
        {
            if (previous is not { } start || button is null || !interaction.IsMouseCaptured) return;
            var current = e.GetPosition(interaction); var delta = current - start; previous = current;
            if (button == MouseButton.Left) camera.Orbit(-delta.X * .45, delta.Y * .45);
            else camera.Pan(delta.X, delta.Y, Math.Max(1, interaction.ActualWidth));
            Update(); e.Handled = true;
        };
        interaction.MouseUp += (_, e) =>
        { if (e.ChangedButton != button) return; previous = null; button = null; interaction.ReleaseMouseCapture(); e.Handled = true; };
        interaction.LostMouseCapture += (_, _) => { previous = null; button = null; };
        interaction.MouseWheel += (_, e) => { camera.Zoom(e.Delta / 120d); Update(); e.Handled = true; };
        interaction.KeyDown += (_, e) => { if (e.Key == Key.Home) { Fit(); e.Handled = true; } };
        Update(); grid.Children.Add(interaction); return grid;
    }
    public sealed class PinEditRow
    {
        public string Id { get; set; } = Ids.New();
        public string Label { get; set; } = "";
        public string Function { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public int MaxWires { get; set; } = 1;
    }
}
