using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace WpfApp1;

public sealed class VehicleConfigItem : INotifyPropertyChanged
{
    public string ModName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string InfoPath { get; init; } = string.Empty;
    public bool IsZip { get; init; }
    public bool HasBackingInfoFile { get; set; } = true;
    public string ModelKey { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string? VehicleName { get; set; }
    public string? Brand { get; set; }
    public string? Country { get; set; }
    public string? Type { get; set; }
    public string? BodyStyle { get; set; }
    public string? ConfigType { get; set; }
    public string? Configuration { get; set; }
    public string? InsuranceClass { get; set; }
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public double? Value { get; set; }
    public int? Population { get; set; }
    public JsonNode? Json { get; set; }

    // Cached search blob (lowercased once) so the filter never re-allocates per keystroke.
    private string? _searchBlob;

    // Cached missing-field results. Recomputed only when InvalidateCache() is called,
    // not on every binding read / sort comparison / scroll frame.
    private IReadOnlyList<string>? _missingFields;
    private string? _missingSummary;
    private bool? _hasMissing;

    public string SearchBlob => _searchBlob ??= BuildSearchBlob();

    public string MissingSummary
    {
        get
        {
            if (_missingSummary == null)
            {
                var missing = GetMissingFields();
                _missingSummary = missing.Count == 0 ? string.Empty : string.Join(", ", missing);
            }
            return _missingSummary;
        }
    }

    public bool HasMissing => _hasMissing ??= GetMissingFields().Count > 0;

    public string MissingCountLabel
    {
        get
        {
            var count = GetMissingFields().Count;
            return count == 1 ? "1 missing" : count + " missing";
        }
    }

    public bool HasMissingPopulation => !Population.HasValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Drops cached derived values. Call after any field mutation, before NotifyChanges().
    /// </summary>
    public void InvalidateCache()
    {
        _missingFields = null;
        _missingSummary = null;
        _hasMissing = null;
        _searchBlob = null;
    }

    public void NotifyChanges()
    {
        InvalidateCache();
        OnPropertyChanged(nameof(HasBackingInfoFile));
        OnPropertyChanged(nameof(VehicleName));
        OnPropertyChanged(nameof(Brand));
        OnPropertyChanged(nameof(Country));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(BodyStyle));
        OnPropertyChanged(nameof(ConfigType));
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(InsuranceClass));
        OnPropertyChanged(nameof(YearMin));
        OnPropertyChanged(nameof(YearMax));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Population));
        OnPropertyChanged(nameof(MissingSummary));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingCountLabel));
        OnPropertyChanged(nameof(HasMissingPopulation));
    }

    public IReadOnlyList<string> GetMissingFields()
    {
        if (_missingFields != null)
        {
            return _missingFields;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Brand)) missing.Add("Brand");
        if (string.IsNullOrWhiteSpace(Country)) missing.Add("Country");
        if (string.IsNullOrWhiteSpace(Type)) missing.Add("Type");
        if (string.IsNullOrWhiteSpace(BodyStyle)) missing.Add("Body Style");
        if (string.IsNullOrWhiteSpace(ConfigType)) missing.Add("Config Type");
        if (string.IsNullOrWhiteSpace(Configuration)) missing.Add("Configuration");
        if (string.IsNullOrWhiteSpace(InsuranceClass)) missing.Add("Insurance Class");
        if (!YearMin.HasValue || !YearMax.HasValue) missing.Add("Years");
        if (!Value.HasValue) missing.Add("Value");
        if (!Population.HasValue) missing.Add("Population");
        _missingFields = missing;
        return _missingFields;
    }

    private string BuildSearchBlob()
    {
        // Single lowercased string searched with one Contains call per item.
        return string.Join('\n', new[]
        {
            ModName,
            ModelKey,
            ConfigKey,
            VehicleName ?? string.Empty
        }).ToLowerInvariant();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
