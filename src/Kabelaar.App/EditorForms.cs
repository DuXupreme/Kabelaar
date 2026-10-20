using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kabelaar.Core;

namespace Kabelaar.App;

internal static class EditData
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ProjectOptions = CreateProjectOptions();
    private static JsonSerializerOptions CreateProjectOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ProjectAsset))
                foreach (var property in info.Properties.Where(p => p.Name == nameof(ProjectAsset.Data)))
                    property.ShouldSerialize = (_, _) => false;
        });
        return new(Options) { TypeInfoResolver = resolver };
    }
    public static T Clone<T>(T value)
    {
        if (value is HarnessProject project)
        {
            var copy = JsonSerializer.Deserialize<HarnessProject>(JsonSerializer.SerializeToUtf8Bytes(project, ProjectOptions), ProjectOptions)!;
            copy.Assets = CloneAssets(project.Assets);
            return (T)(object)copy;
        }
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
    }
    // Assets are immutable after import in the editor. Snapshot metadata and lists are detached,
    // while large byte buffers can be shared with save/recovery without base64 copies on the UI thread.
    public static List<ProjectAsset> CloneAssets(IEnumerable<ProjectAsset> assets) => assets.Select(a =>
        new ProjectAsset { Id = a.Id, FileName = a.FileName, MediaType = a.MediaType, Data = a.Data }).ToList();
    public static double Number(string value, string label, double? minimum = null)
    {
        if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ||
            !double.IsFinite(n) || (minimum.HasValue && n < minimum.Value))
            throw new ArgumentException($"{label}: voer een geldig getal{(minimum.HasValue ? $" van minimaal {minimum}" : "")} in.");
        return n;
    }
    public static string Format(double n) => n.ToString("0.######", CultureInfo.CurrentCulture);
}

internal sealed class PropertyForm
{
    public Func<bool>? CanApply { get; init; }
    public StackPanel Panel { get; } = new();
    public TextBox Text(string label, string value, bool multiline = false)
    {
        Label(label);
        var box = new TextBox { Text = value, Margin = new(0, 0, 0, 9), AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 65 : 30 };
        AutomationProperties(box, label);
        Panel.Children.Add(box); return box;
    }
    public TextBox Number(string label, double value) => Text(label, EditData.Format(value));
    public ComboBox Choice<T>(string label, IEnumerable<T> items, T? selected = default, Func<T, string>? display = null)
    {
        Label(label);
        var box = new ComboBox { Margin = new(0, 0, 0, 9) };
        foreach (var item in items)
            box.Items.Add(new ComboBoxItem { Content = display is null ? item?.ToString() : display(item), Tag = item });
        box.SelectedIndex = 0;
        for (var i = 0; i < box.Items.Count; i++)
            if (Equals(((ComboBoxItem)box.Items[i]).Tag, selected)) { box.SelectedIndex = i; break; }
        AutomationProperties(box, label);
        Panel.Children.Add(box); return box;
    }
    public static T Value<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem).Tag;
    public CheckBox Check(string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new(0, 5, 0, 10) };
        Panel.Children.Add(box); return box;
    }
    public void Heading(string text)
    {
        Panel.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 8) });
    }
    public void Note(string text) => Panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 10), FontSize = 12 });
    public void Action(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 5, 0, 3) };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) =>
        {
            if (CanApply?.Invoke() == false) return;
            try { action(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Bewerking niet toegepast", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        Panel.Children.Add(button);
    }
    public void Label(string text) => Panel.Children.Add(new TextBlock { Text = text, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 4), FontSize = 12 });
    private static void AutomationProperties(DependencyObject box, string label) => System.Windows.Automation.AutomationProperties.SetName(box, label);
}

internal sealed class FormDialog : Window
{
    public PropertyForm Form { get; } = new();
    public Func<bool>? OnAccept { get; set; }
    public FormDialog(Window owner, string title, double width = 480)
    {
        Owner = owner; Title = title; Width = Math.Min(width, SystemParameters.WorkArea.Width); MaxHeight = Math.Min(800, SystemParameters.WorkArea.Height);
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = Brushes.White;
        var dock = new DockPanel { Margin = new(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "Annuleren", IsCancel = true, MinWidth = 90 };
        var okay = new Button { Content = "Toepassen", IsDefault = true, MinWidth = 100 };
        okay.SetResourceReference(StyleProperty, "PrimaryButton");
        okay.Click += (_, _) =>
        {
            try { if (OnAccept?.Invoke() != false) DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Controleer de invoer", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(okay);
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = Form.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(180, MaxHeight - 130) });
        Content = dock;
    }
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var dialog = new FormDialog(owner, title);
        var field = dialog.Form.Text(label, initial);
        return dialog.ShowDialog() == true ? field.Text : null;
    }
}
