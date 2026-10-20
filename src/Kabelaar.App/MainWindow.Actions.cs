using System.IO;
using System.Windows;
using Kabelaar.Core;
using Microsoft.Win32;
using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Kabelaar.App;

public partial class MainWindow
{
    private async void Reference_Click(object sender, RoutedEventArgs e)
    {
        var chooser = new OpenFileDialog { Filter = "Afbeelding of PDF|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.pdf", CheckFileExists = true };
        if (chooser.ShowDialog(this) != true) return;
        if (new FileInfo(chooser.FileName).Length > 100 * 1024 * 1024) { MessageBox.Show(this, "Gebruik een referentie kleiner dan 100 MB."); return; }
        var assets = new List<ProjectAsset>();
        ProjectAsset? rendered = null;
        var widthMm = 120.0; var heightMm = 80.0;
        if (Path.GetExtension(chooser.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            PdfDocument? document = null;
            await BusyAsync("PDF-onderlegger lezen…", async token =>
            {
                var file = await StorageFile.GetFileFromPathAsync(chooser.FileName);
                document = await PdfDocument.LoadFromFileAsync(file);
            });
            if (document is null) return;
            var pageText = FormDialog.Ask(this, "PDF-pagina kiezen", $"Paginanummer (1–{document.PageCount})", "1");
            if (pageText is null) return;
            if (!uint.TryParse(pageText, out var pageNumber) || pageNumber < 1 || pageNumber > document.PageCount)
            { MessageBox.Show(this, "Ongeldig paginanummer."); return; }
            await BusyAsync("PDF-pagina voorbereiden…", async token =>
            {
                using var page = document.GetPage(pageNumber - 1);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Min(6000, page.Size.Width * 2.5) });
                token.ThrowIfCancellationRequested();
                stream.Seek(0);
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                rendered = new() { FileName = Path.GetFileNameWithoutExtension(chooser.FileName) + $"-p{pageNumber}.png", MediaType = "image/png", Data = bytes };
                widthMm = page.Size.Width * 25.4 / 96; heightMm = page.Size.Height * 25.4 / 96;
                assets.Add(new() { FileName = Path.GetFileName(chooser.FileName), MediaType = "application/pdf", Data = await File.ReadAllBytesAsync(chooser.FileName, token) });
            });
        }
        else
        {
            await BusyAsync("Afbeelding lezen…", async token =>
            {
                var bytes = await File.ReadAllBytesAsync(chooser.FileName, token);
                using var bitmap = SKBitmap.Decode(bytes) ?? throw new IOException("Deze afbeelding is niet leesbaar.");
                widthMm = Math.Min(250, bitmap.Width * 25.4 / 96); heightMm = widthMm * bitmap.Height / bitmap.Width;
                rendered = new() { FileName = Path.GetFileName(chooser.FileName), MediaType = ImageMediaType(Path.GetExtension(chooser.FileName)), Data = bytes };
            });
        }
        if (rendered is null) return;
        assets.Add(rendered);
        var annotation = new Annotation { Kind = AnnotationKind.Image, AssetId = rendered.Id, WidthMm = widthMm, HeightMm = heightMm,
            LayerId = "reference", Locked = true, Points = [new(0, 0)], Text = rendered.FileName };
        var referenceView = _view;
        Execute(new DelegateCommand("Onderlegger invoegen",
            () => { _project.Assets.AddRange(assets); referenceView.Annotations.Add(annotation); },
            () => { referenceView.Annotations.Remove(annotation); foreach (var asset in assets) _project.Assets.Remove(asset); }));
        Canvas.Select(annotation.Id);
        Status("Onderlegger toegevoegd en vergrendeld. Eigenschappen wijzigen positie, maat en vergrendeling.");
    }
    private static IEditCommand RemoveItem<T>(string name, List<T> list, T item)
    {
        var index = -1;
        return new DelegateCommand(name, () => { index = list.IndexOf(item); if (index >= 0) list.RemoveAt(index); },
            () => { if (index >= 0) list.Insert(Math.Min(index, list.Count), item); });
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy is not null || Canvas.Selection.Count == 0) return;
        var ids = Canvas.Selection.ToHashSet();
        if (Canvas.Scene?.Primitives.Any(p => p.ViewId == _view.Id && p.Locked &&
            (ids.Contains(p.EntityId) || (p.PlacementId is { } placementId && ids.Contains(placementId)))) == true)
        { Status("Ontgrendel eerst de geselecteerde objecten of hun laag voordat je ze verwijdert."); return; }
        var selectedConnectors = _project.Assembly.Connectors.Where(c => ids.Contains(c.Id)).ToArray();
        var selectedSplices = _project.Assembly.Splices.Where(s => ids.Contains(s.Id)).ToArray();
        var selectedWires = _project.Assembly.Conductors.Where(w => ids.Contains(w.Id)).ToArray();
        var hasPhysical = selectedConnectors.Length + selectedSplices.Length + selectedWires.Length > 0;
        var physical = false;
        if (hasPhysical)
        {
            var dialog = new FormDialog(this, "Selectie verwijderen");
            var option = dialog.Form.Choice("Wat verwijderen?", new[] { "Alleen de weergave in dit aanzicht", "Fysieke onderdelen / aders uit de assemblage" });
            dialog.Form.Note("Fysiek verwijderen verwijdert ook alle bijbehorende aanzichten. Aangesloten aders houden een expliciet vrij uiteinde. Ongedaan maken herstelt alles.");
            if (dialog.ShowDialog() != true) return;
            physical = option.SelectedIndex == 1;
        }
        var commands = new List<IEditCommand>();
        var selectedBranches = selectedWires.SelectMany(w => w.BranchIds).Concat(ids).ToHashSet();
        if (physical)
        {
            var entityIds = selectedConnectors.Select(c => c.Id).Concat(selectedSplices.Select(s => s.Id)).ToHashSet();
            foreach (var wire in _project.Assembly.Conductors.Except(selectedWires))
            {
                var from = wire.From.EntityId is not null && entityIds.Contains(wire.From.EntityId) ? new Endpoint { Description = "Vrij na verwijderen onderdeel" } : wire.From;
                var to = wire.To.EntityId is not null && entityIds.Contains(wire.To.EntityId) ? new Endpoint { Description = "Vrij na verwijderen onderdeel" } : wire.To;
                if (from != wire.From || to != wire.To)
                    commands.Add(HarnessOperations.SetConductorEndpoints(_project, wire, from, to));
            }
            foreach (var group in _project.Assembly.TwistGroups.Where(g => g.ConductorIds.Any(id => selectedWires.Any(w => w.Id == id))).ToArray())
                commands.Add(RemoveItem("Twistgroep verwijderen", _project.Assembly.TwistGroups, group));
            foreach (var wire in selectedWires) commands.Add(RemoveItem("Ader verwijderen", _project.Assembly.Conductors, wire));
            var orphanBranches = _project.Assembly.Branches.Where(b => selectedBranches.Contains(b.Id) &&
                !_project.Assembly.Conductors.Except(selectedWires).Any(w => w.BranchIds.Contains(b.Id))).ToArray();
            foreach (var branch in orphanBranches)
            {
                foreach (var material in _project.Assembly.Materials.Where(m => m.BranchId == branch.Id))
                    commands.Add(new DelegateCommand("Materiaal loskoppelen van route", () => material.BranchId = null, () => material.BranchId = branch.Id));
                commands.Add(RemoveItem("Route verwijderen", _project.Assembly.Branches, branch));
            }
            foreach (var connector in selectedConnectors) commands.Add(RemoveItem("Connector verwijderen", _project.Assembly.Connectors, connector));
            foreach (var splice in selectedSplices) commands.Add(RemoveItem("Splice verwijderen", _project.Assembly.Splices, splice));
            foreach (var view in _project.Sheets.SelectMany(s => s.Views))
            {
                foreach (var p in view.Components.Where(p => entityIds.Contains(p.EntityId)).ToArray()) commands.Add(RemoveItem("Weergave verwijderen", view.Components, p));
                foreach (var r in view.Routes.Where(r => orphanBranches.Any(b => b.Id == r.BranchId)).ToArray()) commands.Add(RemoveItem("Routeweergave verwijderen", view.Routes, r));
                foreach (var a in view.Annotations.Where(a => !(view == _view && ids.Contains(a.Id)) && a.EntityId is not null && (entityIds.Contains(a.EntityId) || orphanBranches.Any(b => b.Id == a.EntityId))).ToArray())
                    commands.Add(RemoveItem("Gekoppelde annotatie verwijderen", view.Annotations, a));
            }
            // Recompute occupancy after all removals as one reversible command.
            var states = _project.Assembly.Connectors.SelectMany(c => c.Cavities).ToDictionary(c => c, c => c.State);
            commands.Add(new DelegateCommand("Pinbezetting bijwerken", () =>
            {
                foreach (var cavity in _project.Assembly.Connectors.SelectMany(c => c.Cavities))
                    if (cavity.State == CavityState.Occupied && !_project.Assembly.Conductors.Any(w => w.From.CavityId == cavity.Id || w.To.CavityId == cavity.Id))
                        cavity.State = CavityState.Unused;
            }, () => { foreach (var state in states) state.Key.State = state.Value; }));
        }
        else
        {
            foreach (var p in _view.Components.Where(p => ids.Contains(p.EntityId) || ids.Contains(p.Id)).ToArray())
                commands.Add(RemoveItem("Weergave verwijderen", _view.Components, p));
            foreach (var r in _view.Routes.Where(r => selectedBranches.Contains(r.BranchId) || ids.Contains(r.Id)).ToArray())
                commands.Add(RemoveItem("Routeweergave verwijderen", _view.Routes, r));
        }
        foreach (var a in _view.Annotations.Where(a => ids.Contains(a.Id)).ToArray())
            commands.Add(RemoveItem("Annotatie verwijderen", _view.Annotations, a));
        if (commands.Count == 0) return;
        Execute(new CompositeCommand("Selectie verwijderen", commands.ToArray())); Canvas.ClearSelection();
    }
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var connector = _project.Assembly.Connectors.Find(c => c.Id == SelectedId);
        var placement = _view.Components.Find(c => c.EntityId == SelectedId);
        if (connector is not null && placement is not null)
        {
            var copy = EditData.Clone(connector); copy.Id = Ids.New(); copy.Name = NextName("J", _project.Assembly.Connectors.Select(c => c.Name));
            foreach (var cavity in copy.Cavities) { cavity.Id = Ids.New(); if (cavity.State == CavityState.Occupied) cavity.State = CavityState.Unspecified; }
            foreach (var material in copy.Accessories) material.Id = Ids.New();
            Execute(HarnessOperations.AddConnector(_project, _view, copy, placement.Position + new Point2(20, 20)));
            Canvas.Select(copy.Id); return;
        }
        var annotation = _view.Annotations.Find(a => a.Id == SelectedId);
        if (annotation is not null)
        {
            var copy = EditData.Clone(annotation); copy.Id = Ids.New(); copy.Points = copy.Points.Select(p => p + new Point2(10, 10)).ToList();
            Add("Annotatie dupliceren", _view.Annotations, copy); Canvas.Select(copy.Id); return;
        }
        Status("Selecteer een connector of annotatie om te dupliceren. Gebruik een detailaanzicht om dezelfde assemblage opnieuw af te beelden.");
    }
}
