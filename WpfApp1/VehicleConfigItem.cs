using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace WpfApp1;

public sealed class VehicleConfigItem : INotifyPropertyChanged
{
    private static readonly HashSet<string> MissingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "-", "n/a", "na", "none", "null", "unknown", "tbd", "missing"
    };

    public string ModName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string InfoPath { get; init; } = string.Empty;
    public bool IsZip { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string? ConfigPcPath { get; set; }
    public bool HasConfigPc { get; set; }
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
    public JsonNode? VehicleInfoJson { get; set; }
    public string? VehicleInfoPath { get; set; }
    public string? VehicleInfoName { get; set; }
    public string? VehicleInfoBrand { get; set; }
    public string? VehicleInfoCountry { get; set; }
    public string? VehicleInfoType { get; set; }
    public string? VehicleInfoBodyStyle { get; set; }
    public int? VehicleInfoYearMin { get; set; }
    public int? VehicleInfoYearMax { get; set; }
    public string? LastAutoFillStatus { get; set; }
    public string? LastAutoFillSource { get; set; }
    public string? LastAutoFillDetail { get; set; }
    public string? InferenceReason { get; set; }
    public string? ReviewReason { get; set; }
    public bool IsSuspicious { get; set; }
    public bool IgnoreFromRenamer { get; set; }
    public string? ContentCategory { get; set; }
    public bool IsMapMod { get; set; }
    public string? SourceHintMake { get; set; }
    public string? CurrentLicensePlate { get; set; }
    public string? SourceHintModel { get; set; }
    public int? SourceHintYearMin { get; set; }
    public int? SourceHintYearMax { get; set; }
    public bool HasSourceHints => !string.IsNullOrWhiteSpace(SourceHintMake) || !string.IsNullOrWhiteSpace(SourceHintModel) || SourceHintYearMin.HasValue || SourceHintYearMax.HasValue;
    public bool HasHardcodedPlate => !string.IsNullOrWhiteSpace(CurrentLicensePlate);
    public string LicensePlateStatusText => !HasConfigPc ? "No .pc file" : (HasHardcodedPlate ? CurrentLicensePlate! : "Blank / dynamic");

    public string MissingSummary
    {
        get
        {
            var missing = GetMissingFields();
            return missing.Count == 0 ? string.Empty : string.Join(", ", missing);
        }
    }

    public bool HasMissing => GetMissingFields().Count > 0;
    public string HasMissingText => HasMissing ? "Yes" : string.Empty;
    public string IsSuspiciousText => IsSuspicious ? "Yes" : string.Empty;
    public bool HasMissingPopulation => !Population.HasValue;
    public bool NeedsReview => HasMissing || IsSuspicious;

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
        OnPropertyChanged(nameof(InsuranceClass));
        OnPropertyChanged(nameof(YearMin));
        OnPropertyChanged(nameof(YearMax));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Population));
        OnPropertyChanged(nameof(VehicleInfoPath));
        OnPropertyChanged(nameof(VehicleInfoName));
        OnPropertyChanged(nameof(VehicleInfoBrand));
        OnPropertyChanged(nameof(VehicleInfoCountry));
        OnPropertyChanged(nameof(VehicleInfoType));
        OnPropertyChanged(nameof(VehicleInfoBodyStyle));
        OnPropertyChanged(nameof(VehicleInfoYearMin));
        OnPropertyChanged(nameof(VehicleInfoYearMax));
        OnPropertyChanged(nameof(LastAutoFillStatus));
        OnPropertyChanged(nameof(LastAutoFillSource));
        OnPropertyChanged(nameof(LastAutoFillDetail));
        OnPropertyChanged(nameof(InferenceReason));
        OnPropertyChanged(nameof(ReviewReason));
        OnPropertyChanged(nameof(IsSuspicious));
        OnPropertyChanged(nameof(IgnoreFromRenamer));
        OnPropertyChanged(nameof(ContentCategory));
        OnPropertyChanged(nameof(IsMapMod));
        OnPropertyChanged(nameof(SourceHintMake));
        OnPropertyChanged(nameof(CurrentLicensePlate));
        OnPropertyChanged(nameof(HasConfigPc));
        OnPropertyChanged(nameof(ConfigPcPath));
        OnPropertyChanged(nameof(SourceHintModel));
        OnPropertyChanged(nameof(SourceHintYearMin));
        OnPropertyChanged(nameof(SourceHintYearMax));
        OnPropertyChanged(nameof(HasSourceHints));
        OnPropertyChanged(nameof(HasHardcodedPlate));
        OnPropertyChanged(nameof(LicensePlateStatusText));
        OnPropertyChanged(nameof(MissingSummary));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasMissingText));
        OnPropertyChanged(nameof(IsSuspiciousText));
        OnPropertyChanged(nameof(HasMissingPopulation));
        OnPropertyChanged(nameof(NeedsReview));
    }

    public IReadOnlyList<string> GetMissingFields()
    {
        var missing = new List<string>();
        if (IsMissingText(Brand)) missing.Add("Brand");
        if (IsMissingText(Country)) missing.Add("Country");
        if (IsMissingText(Type)) missing.Add("Type");
        if (IsMissingText(BodyStyle)) missing.Add("Body Style");
        if (IsMissingText(ConfigType)) missing.Add("Config Type");
        if (IsMissingText(Configuration)) missing.Add("Configuration");
        if (IsMissingText(InsuranceClass)) missing.Add("Insurance Class");
        if (!YearMin.HasValue || !YearMax.HasValue) missing.Add("Years");
        if (!Value.HasValue || Value.Value <= 0) missing.Add("Value");
        if (!Population.HasValue || Population.Value <= 0) missing.Add("Population");
        return missing;
    }

    public static bool IsMissingText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return MissingTokens.Contains(value.Trim());
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
