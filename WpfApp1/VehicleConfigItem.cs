using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace WpfApp1;

public sealed class VehicleConfigItem : INotifyPropertyChanged
{
    public string ModName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string InfoPath { get; init; } = string.Empty;
    public bool IsZip { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string? VehicleName { get; set; }
    public string? Brand { get; set; }
    public string? Country { get; set; }
    public string? Type { get; set; }
    public string? BodyStyle { get; set; }
    public string? ConfigType { get; set; }
    public string? Configuration { get; set; }
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public double? Value { get; set; }
    public int? Population { get; set; }
    public JsonNode? Json { get; set; }

    public string MissingSummary
    {
        get
        {
            var missing = GetMissingFields();
            return missing.Count == 0 ? string.Empty : string.Join(", ", missing);
        }
    }

    public bool HasMissing => GetMissingFields().Count > 0;
    public bool HasMissingPopulation => !Population.HasValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyChanges()
    {
        OnPropertyChanged(nameof(VehicleName));
        OnPropertyChanged(nameof(Brand));
        OnPropertyChanged(nameof(Country));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(BodyStyle));
        OnPropertyChanged(nameof(ConfigType));
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(YearMin));
        OnPropertyChanged(nameof(YearMax));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Population));
        OnPropertyChanged(nameof(MissingSummary));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasMissingPopulation));
    }

    public IReadOnlyList<string> GetMissingFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Brand)) missing.Add("Brand");
        if (string.IsNullOrWhiteSpace(Country)) missing.Add("Country");
        if (string.IsNullOrWhiteSpace(Type)) missing.Add("Type");
        if (string.IsNullOrWhiteSpace(BodyStyle)) missing.Add("Body Style");
        if (string.IsNullOrWhiteSpace(ConfigType)) missing.Add("Config Type");
        if (string.IsNullOrWhiteSpace(Configuration)) missing.Add("Configuration");
        if (!YearMin.HasValue || !YearMax.HasValue) missing.Add("Years");
        if (!Value.HasValue) missing.Add("Value");
        if (!Population.HasValue) missing.Add("Population");
        return missing;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
