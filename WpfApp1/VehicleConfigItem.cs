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
    public int ConfidenceScore { get; set; }
    public string? ConfidenceTier { get; set; }
    public string? IdentityEvidence { get; set; }
    public string? ValuationEvidence { get; set; }
    public string? ReviewReason { get; set; }
    public string? ReviewCategory { get; set; }
    public int ReviewPriority { get; set; }
    public string? ReviewConflictSummary { get; set; }
    public string? DecisionOrigin { get; set; }
    public DateTime? LastDecisionUtc { get; set; }
    public string? LastLookupSourceName { get; set; }
    public string? LastLookupSourceUrl { get; set; }
    public DateTime? LastLookupUtc { get; set; }
    public bool IsSuspicious { get; set; }
    public bool IgnoreFromRenamer { get; set; }
    public string? ContentCategory { get; set; }
    public bool IsMapMod { get; set; }
    public string? SourceCategory { get; set; }
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
    public bool HasBlockingReviewGaps => GetBlockingReviewFields().Count > 0;
    public bool HasValuationGaps => !Value.HasValue || Value.Value <= 0 || !Population.HasValue || Population.Value <= 0;
    public bool HasExplicitReviewAssessment => ReviewPriority > 0 || !string.IsNullOrWhiteSpace(ReviewCategory) || !string.IsNullOrWhiteSpace(ReviewReason);
    public int ReviewCategoryRank => GetReviewCategoryRank(ReviewCategory);
    public int ReviewSortScore
    {
        get
        {
            if (IgnoreFromRenamer || IsMapMod)
            {
                return 0;
            }

            var activeWeight = NeedsReview ? 10000 : 0;
            var categoryWeight = ReviewCategoryRank * 100;
            var priorityWeight = Math.Clamp(ReviewPriority, 0, 100) * 10;
            var suspicionWeight = IsSuspicious ? 15 : 0;
            var fallbackGapWeight = !HasExplicitReviewAssessment && HasBlockingReviewGaps ? 20 : 0;
            return activeWeight + categoryWeight + priorityWeight + suspicionWeight + fallbackGapWeight;
        }
    }

    public bool NeedsReview
    {
        get
        {
            if (IgnoreFromRenamer || IsMapMod)
            {
                return false;
            }

            if (HasExplicitReviewAssessment)
            {
                return IsSuspicious || HasBlockingReviewCategory();
            }

            return HasBlockingReviewGaps || IsSuspicious || HasBlockingReviewCategory();
        }
    }


    public VehicleConfigItem CreateWorkingCopy()
    {
        return new VehicleConfigItem
        {
            ModName = ModName,
            SourcePath = SourcePath,
            InfoPath = InfoPath,
            IsZip = IsZip,
            ModelKey = ModelKey,
            ConfigKey = ConfigKey,
            ConfigPcPath = ConfigPcPath,
            HasConfigPc = HasConfigPc,
            VehicleName = VehicleName,
            Brand = Brand,
            Country = Country,
            Type = Type,
            BodyStyle = BodyStyle,
            ConfigType = ConfigType,
            Configuration = Configuration,
            InsuranceClass = InsuranceClass,
            YearMin = YearMin,
            YearMax = YearMax,
            Value = Value,
            Population = Population,
            Json = Json?.DeepClone(),
            VehicleInfoJson = VehicleInfoJson?.DeepClone(),
            VehicleInfoPath = VehicleInfoPath,
            VehicleInfoName = VehicleInfoName,
            VehicleInfoBrand = VehicleInfoBrand,
            VehicleInfoCountry = VehicleInfoCountry,
            VehicleInfoType = VehicleInfoType,
            VehicleInfoBodyStyle = VehicleInfoBodyStyle,
            VehicleInfoYearMin = VehicleInfoYearMin,
            VehicleInfoYearMax = VehicleInfoYearMax,
            LastAutoFillStatus = LastAutoFillStatus,
            LastAutoFillSource = LastAutoFillSource,
            LastAutoFillDetail = LastAutoFillDetail,
            InferenceReason = InferenceReason,
            ConfidenceScore = ConfidenceScore,
            ConfidenceTier = ConfidenceTier,
            IdentityEvidence = IdentityEvidence,
            ValuationEvidence = ValuationEvidence,
            ReviewReason = ReviewReason,
            ReviewCategory = ReviewCategory,
            ReviewPriority = ReviewPriority,
            ReviewConflictSummary = ReviewConflictSummary,
            DecisionOrigin = DecisionOrigin,
            LastDecisionUtc = LastDecisionUtc,
            LastLookupSourceName = LastLookupSourceName,
            LastLookupSourceUrl = LastLookupSourceUrl,
            LastLookupUtc = LastLookupUtc,
            IsSuspicious = IsSuspicious,
            IgnoreFromRenamer = IgnoreFromRenamer,
            ContentCategory = ContentCategory,
            IsMapMod = IsMapMod,
            SourceCategory = SourceCategory,
            SourceHintMake = SourceHintMake,
            CurrentLicensePlate = CurrentLicensePlate,
            SourceHintModel = SourceHintModel,
            SourceHintYearMin = SourceHintYearMin,
            SourceHintYearMax = SourceHintYearMax
        };
    }

    public void CopyMutableStateFrom(VehicleConfigItem source)
    {
        ModName = source.ModName;
        SourcePath = source.SourcePath;
        ConfigPcPath = source.ConfigPcPath;
        HasConfigPc = source.HasConfigPc;
        VehicleName = source.VehicleName;
        Brand = source.Brand;
        Country = source.Country;
        Type = source.Type;
        BodyStyle = source.BodyStyle;
        ConfigType = source.ConfigType;
        Configuration = source.Configuration;
        InsuranceClass = source.InsuranceClass;
        YearMin = source.YearMin;
        YearMax = source.YearMax;
        Value = source.Value;
        Population = source.Population;
        Json = source.Json?.DeepClone();
        VehicleInfoJson = source.VehicleInfoJson?.DeepClone();
        VehicleInfoPath = source.VehicleInfoPath;
        VehicleInfoName = source.VehicleInfoName;
        VehicleInfoBrand = source.VehicleInfoBrand;
        VehicleInfoCountry = source.VehicleInfoCountry;
        VehicleInfoType = source.VehicleInfoType;
        VehicleInfoBodyStyle = source.VehicleInfoBodyStyle;
        VehicleInfoYearMin = source.VehicleInfoYearMin;
        VehicleInfoYearMax = source.VehicleInfoYearMax;
        LastAutoFillStatus = source.LastAutoFillStatus;
        LastAutoFillSource = source.LastAutoFillSource;
        LastAutoFillDetail = source.LastAutoFillDetail;
        InferenceReason = source.InferenceReason;
        ConfidenceScore = source.ConfidenceScore;
        ConfidenceTier = source.ConfidenceTier;
        IdentityEvidence = source.IdentityEvidence;
        ValuationEvidence = source.ValuationEvidence;
        ReviewReason = source.ReviewReason;
        ReviewCategory = source.ReviewCategory;
        ReviewPriority = source.ReviewPriority;
        ReviewConflictSummary = source.ReviewConflictSummary;
        DecisionOrigin = source.DecisionOrigin;
        LastDecisionUtc = source.LastDecisionUtc;
        LastLookupSourceName = source.LastLookupSourceName;
        LastLookupSourceUrl = source.LastLookupSourceUrl;
        LastLookupUtc = source.LastLookupUtc;
        IsSuspicious = source.IsSuspicious;
        IgnoreFromRenamer = source.IgnoreFromRenamer;
        ContentCategory = source.ContentCategory;
        IsMapMod = source.IsMapMod;
        SourceCategory = source.SourceCategory;
        SourceHintMake = source.SourceHintMake;
        CurrentLicensePlate = source.CurrentLicensePlate;
        SourceHintModel = source.SourceHintModel;
        SourceHintYearMin = source.SourceHintYearMin;
        SourceHintYearMax = source.SourceHintYearMax;
    }

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
        OnPropertyChanged(nameof(ConfidenceScore));
        OnPropertyChanged(nameof(ConfidenceTier));
        OnPropertyChanged(nameof(IdentityEvidence));
        OnPropertyChanged(nameof(ValuationEvidence));
        OnPropertyChanged(nameof(ReviewReason));
        OnPropertyChanged(nameof(ReviewCategory));
        OnPropertyChanged(nameof(ReviewPriority));
        OnPropertyChanged(nameof(HasExplicitReviewAssessment));
        OnPropertyChanged(nameof(ReviewCategoryRank));
        OnPropertyChanged(nameof(ReviewSortScore));
        OnPropertyChanged(nameof(ReviewConflictSummary));
        OnPropertyChanged(nameof(DecisionOrigin));
        OnPropertyChanged(nameof(LastDecisionUtc));
        OnPropertyChanged(nameof(LastLookupSourceName));
        OnPropertyChanged(nameof(LastLookupSourceUrl));
        OnPropertyChanged(nameof(LastLookupUtc));
        OnPropertyChanged(nameof(IsSuspicious));
        OnPropertyChanged(nameof(IgnoreFromRenamer));
        OnPropertyChanged(nameof(ContentCategory));
        OnPropertyChanged(nameof(IsMapMod));
        OnPropertyChanged(nameof(SourceCategory));
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
        OnPropertyChanged(nameof(HasBlockingReviewGaps));
        OnPropertyChanged(nameof(HasValuationGaps));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(ReviewSortScore));
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

    private IReadOnlyList<string> GetBlockingReviewFields()
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
        return missing;
    }

    private bool HasBlockingReviewCategory()
    {
        if (string.IsNullOrWhiteSpace(ReviewCategory))
        {
            return false;
        }

        return !string.Equals(ReviewCategory, "None", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ReviewCategory, "Map", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ReviewCategory, "Value uncertainty", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetReviewCategoryRank(string? category) => category?.Trim() switch
    {
        "Identity conflict" => 6,
        "Missing identity" => 5,
        "Year conflict" => 4,
        "Metadata conflict" => 3,
        "Weak evidence" => 2,
        "Value uncertainty" => 1,
        _ => 0
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
