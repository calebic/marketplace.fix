using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp1;

public partial class BulkEditWindow : Window
{
    private readonly List<VehicleConfigItem> _selectedModItems;
    private readonly List<VehicleConfigItem> _filteredItems;
    private readonly List<VehicleConfigItem> _flaggedItems;
    private readonly string _selectedModDisplayName;
    private bool _isInitializing;
    public BulkEditRequest? Request { get; private set; }

    public BulkEditWindow(IEnumerable<VehicleConfigItem> selectedModItems, IEnumerable<VehicleConfigItem> filteredItems, IEnumerable<VehicleConfigItem> flaggedItems, string selectedModDisplayName)
    {
        _isInitializing = true;
        InitializeComponent();
        _selectedModItems = selectedModItems?.Where(x => x != null).Distinct().ToList() ?? new List<VehicleConfigItem>();
        _filteredItems = filteredItems?.Where(x => x != null).Distinct().ToList() ?? new List<VehicleConfigItem>();
        _flaggedItems = flaggedItems?.Where(x => x != null).Distinct().ToList() ?? new List<VehicleConfigItem>();
        _selectedModDisplayName = string.IsNullOrWhiteSpace(selectedModDisplayName) ? "selected mod" : selectedModDisplayName;

        if (_selectedModItems.Count == 0)
        {
            ScopeComboBox.SelectedIndex = _filteredItems.Count > 0 ? 1 : 2;
        }

        _isInitializing = false;
        UpdateScopeSummary();
    }

    private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        UpdateScopeSummary();
    }

    private void UpdateScopeSummary()
    {
        if (ScopeComboBox == null || ScopeSummaryTextBlock == null || FooterSummaryTextBlock == null || ApplyButton == null)
        {
            return;
        }

        var scope = GetScope();
        var (count, label, detail) = scope switch
        {
            BulkEditScope.SelectedMod => (_selectedModItems.Count, _selectedModDisplayName, "will be affected"),
            BulkEditScope.FilteredResults => (_filteredItems.Count, "current filtered results", "currently match the workspace filters"),
            BulkEditScope.FlaggedConfigs => (_flaggedItems.Count, "flagged configs", "currently need review or are suspicious"),
            _ => (0, "current target", "")
        };

        ScopeSummaryTextBlock.Text = $"Target: {label} · {count} config(s) {detail}.";
        FooterSummaryTextBlock.Text = count <= 0
            ? "There are no configs in the current target scope."
            : $"{count} config(s) ready for bulk edit.";
        ApplyButton.IsEnabled = count > 0;
    }

    private BulkEditScope GetScope()
    {
        return ScopeComboBox?.SelectedIndex switch
        {
            1 => BulkEditScope.FilteredResults,
            2 => BulkEditScope.FlaggedConfigs,
            _ => BulkEditScope.SelectedMod
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var request = new BulkEditRequest
        {
            Scope = GetScope(),
            ApplyInsuranceClass = ApplyInsuranceClassCheckBox.IsChecked == true,
            InsuranceClass = (InsuranceClassComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim(),
            ApplyPopulation = ApplyPopulationCheckBox.IsChecked == true,
            Population = TryParseNullableInt(PopulationTextBox.Text),
            ApplyValue = ApplyValueCheckBox.IsChecked == true,
            Value = TryParseNullableDouble(ValueTextBox.Text),
            ApplyBodyStyle = ApplyBodyStyleCheckBox.IsChecked == true,
            BodyStyle = BodyStyleTextBox.Text?.Trim(),
            ApplyType = ApplyTypeCheckBox.IsChecked == true,
            Type = TypeTextBox.Text?.Trim(),
            ApplyConfigType = ApplyConfigTypeCheckBox.IsChecked == true,
            ConfigType = ConfigTypeTextBox.Text?.Trim()
        };

        var errors = new List<string>();
        if (!request.HasAnyChanges)
        {
            errors.Add("Choose at least one field to apply.");
        }
        if (request.ApplyInsuranceClass && string.IsNullOrWhiteSpace(request.InsuranceClass)) errors.Add("Insurance Class");
        if (request.ApplyPopulation && (!request.Population.HasValue || request.Population.Value < 0)) errors.Add("Population");
        if (request.ApplyValue && (!request.Value.HasValue || request.Value.Value <= 0)) errors.Add("Value");
        if (request.ApplyBodyStyle && string.IsNullOrWhiteSpace(request.BodyStyle)) errors.Add("Body Style");
        if (request.ApplyType && string.IsNullOrWhiteSpace(request.Type)) errors.Add("Type");
        if (request.ApplyConfigType && string.IsNullOrWhiteSpace(request.ConfigType)) errors.Add("Config Type");

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show("Fix the following before applying:\n- " + string.Join("\n- ", errors), "Bulk Edit Fields", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Request = request;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static int? TryParseNullableInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? TryParseNullableDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
            || double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            ? value
            : null;
    }
}

public enum BulkEditScope
{
    SelectedMod,
    FilteredResults,
    FlaggedConfigs
}

public sealed class BulkEditRequest
{
    public BulkEditScope Scope { get; init; }
    public bool ApplyInsuranceClass { get; init; }
    public string? InsuranceClass { get; init; }
    public bool ApplyPopulation { get; init; }
    public int? Population { get; init; }
    public bool ApplyValue { get; init; }
    public double? Value { get; init; }
    public bool ApplyBodyStyle { get; init; }
    public string? BodyStyle { get; init; }
    public bool ApplyType { get; init; }
    public string? Type { get; init; }
    public bool ApplyConfigType { get; init; }
    public string? ConfigType { get; init; }

    public bool HasAnyChanges => ApplyInsuranceClass || ApplyPopulation || ApplyValue || ApplyBodyStyle || ApplyType || ApplyConfigType;
}
