using System.ComponentModel;
using Kabelaar.Core;

namespace Kabelaar.App;

public partial class MainWindow
{
    private readonly HarnessReportCache _reportCache = new();
    private HarnessReportSnapshot? _visibleReports;
    private BomDisplay[] _bomRows = [];

    private void RefreshReports()
    {
        var reports = _reportCache.Get(_project);
        if (ReferenceEquals(reports, _visibleReports)) return;
        var wires = reports.Wires;
        if (!ReferenceEquals(wires, _visibleReports?.Wires))
        {
            if (_wireRows.Length != wires.Count || _wireRows.Where((row, i) => row.Id != wires[i].Id).Any())
            {
                _wireRows = wires.Select(row => new WireDisplay(row)).ToArray();
                WireGrid.ItemsSource = _wireRows;
            }
            else for (var i = 0; i < _wireRows.Length; i++) _wireRows[i].Update(wires[i]);
        }
        var bom = reports.Bom;
        if (!ReferenceEquals(bom, _visibleReports?.Bom) || !ReferenceEquals(reports.Suppliers, _visibleReports?.Suppliers))
        {
            if (_bomRows.Length != bom.Count || _bomRows.Where((row, i) => row.PartId != bom[i].PartId || row.Unit != bom[i].Unit).Any())
            {
                _bomRows = bom.Select(row => new BomDisplay(row, reports.Suppliers.GetValueOrDefault(row.PartId, ""))).ToArray();
                BomGrid.ItemsSource = _bomRows;
            }
            else for (var i = 0; i < _bomRows.Length; i++)
                _bomRows[i].Update(bom[i], reports.Suppliers.GetValueOrDefault(bom[i].PartId, ""));
        }
        _visibleReports = reports;
    }

    private sealed class BomDisplay(BomRow row, string supplier) : INotifyPropertyChanged
    {
        private BomRow _row = row;
        private string _supplier = supplier;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string PartId => _row.PartId;
        public string PartNumber => _row.PartNumber;
        public string Description => _row.Description;
        public string Quantity => _row.Quantity.ToString("0.######");
        public string Unit => _row.Unit;
        public string Supplier => _supplier;
        public void Update(BomRow next, string nextSupplier)
        {
            var previous = _row; var previousSupplier = _supplier;
            _row = next; _supplier = nextSupplier;
            void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (previous.PartNumber != next.PartNumber) Changed(nameof(PartNumber));
            if (previous.Description != next.Description) Changed(nameof(Description));
            if (!previous.Quantity.Equals(next.Quantity)) Changed(nameof(Quantity));
            if (previous.Unit != next.Unit) Changed(nameof(Unit));
            if (previousSupplier != nextSupplier) Changed(nameof(Supplier));
        }
    }
}
