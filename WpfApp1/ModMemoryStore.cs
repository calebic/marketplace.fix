using System.Text.Json;
using System.IO;

namespace WpfApp1;

public sealed class ModMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ModMemoryStore(string path)
    {
        _path = path;
    }

    public PersistedModMemory Load()
    {
        try
        {
            var json = AtomicFileIO.ReadAllTextWithBackup(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PersistedModMemory();
            }

            return JsonSerializer.Deserialize<PersistedModMemory>(json, JsonOptions) ?? new PersistedModMemory();
        }
        catch (Exception ex)
        {
            AppPaths.AppendStateLog("mod-memory-load", ex.Message);
            return new PersistedModMemory();
        }
    }

    public void SaveFrom(IEnumerable<VehicleConfigItem> items)
    {
        Save(BuildSnapshot(items));
    }

    public void Save(PersistedModMemory memory)
    {
        try
        {
            AtomicFileIO.WriteAllTextAtomic(_path, JsonSerializer.Serialize(memory, JsonOptions));
        }
        catch (Exception ex)
        {
            AppPaths.AppendStateLog("mod-memory-save", ex.Message);
        }
    }

    public static PersistedModMemory BuildSnapshot(IEnumerable<VehicleConfigItem> items)
    {
        var memory = new PersistedModMemory();
        foreach (var item in items)
        {
            var key = BuildConfigKey(item);
            memory.Configs[key] = PersistedConfigMemory.From(item);

            var modKey = BuildModKey(item);
            if (!memory.Mods.TryGetValue(modKey, out var mod))
            {
                mod = new PersistedModEntry
                {
                    ModPath = item.SourcePath,
                    IsZip = item.IsZip,
                    ContentCategory = item.ContentCategory,
                    IsMapMod = item.IsMapMod,
                    LastUpdatedUtc = DateTime.UtcNow
                };
                memory.Mods[modKey] = mod;
            }

            mod.ContentCategory = item.ContentCategory ?? mod.ContentCategory;
            mod.IsMapMod = item.IsMapMod || mod.IsMapMod;
            mod.IgnoreFromReview = item.IgnoreFromRenamer || mod.IgnoreFromReview;
            mod.SourceHintMake = item.SourceHintMake;
            mod.SourceHintModel = item.SourceHintModel;
            mod.SourceHintYearMin = item.SourceHintYearMin;
            mod.SourceHintYearMax = item.SourceHintYearMax;
            mod.LastUpdatedUtc = DateTime.UtcNow;
        }

        return memory;
    }

    public static string BuildConfigKey(VehicleConfigItem item)
        => $"{item.SourcePath}|{item.InfoPath}".ToLowerInvariant();

    public static string BuildModKey(VehicleConfigItem item)
        => $"{item.SourcePath}|{item.IsZip}".ToLowerInvariant();
}

public sealed class PersistedModMemory
{
    public Dictionary<string, PersistedConfigMemory> Configs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PersistedModEntry> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PersistedModEntry
{
    public string ModPath { get; set; } = string.Empty;
    public bool IsZip { get; set; }
    public string? ContentCategory { get; set; }
    public bool IsMapMod { get; set; }
    public bool IgnoreFromReview { get; set; }
    public string? SourceHintMake { get; set; }
    public string? SourceHintModel { get; set; }
    public int? SourceHintYearMin { get; set; }
    public int? SourceHintYearMax { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

public sealed class PersistedConfigMemory
{
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
    public string? SourceHintMake { get; set; }
    public string? SourceHintModel { get; set; }
    public int? SourceHintYearMin { get; set; }
    public int? SourceHintYearMax { get; set; }

    public static PersistedConfigMemory From(VehicleConfigItem item) => new()
    {
        VehicleName = item.VehicleName,
        Brand = item.Brand,
        Country = item.Country,
        Type = item.Type,
        BodyStyle = item.BodyStyle,
        ConfigType = item.ConfigType,
        Configuration = item.Configuration,
        InsuranceClass = item.InsuranceClass,
        YearMin = item.YearMin,
        YearMax = item.YearMax,
        Value = item.Value,
        Population = item.Population,
        LastAutoFillStatus = item.LastAutoFillStatus,
        LastAutoFillSource = item.LastAutoFillSource,
        LastAutoFillDetail = item.LastAutoFillDetail,
        InferenceReason = item.InferenceReason,
        ConfidenceScore = item.ConfidenceScore,
        ConfidenceTier = item.ConfidenceTier,
        IdentityEvidence = item.IdentityEvidence,
        ValuationEvidence = item.ValuationEvidence,
        ReviewReason = item.ReviewReason,
        ReviewCategory = item.ReviewCategory,
        ReviewPriority = item.ReviewPriority,
        ReviewConflictSummary = item.ReviewConflictSummary,
        DecisionOrigin = item.DecisionOrigin,
        LastDecisionUtc = item.LastDecisionUtc,
        LastLookupSourceName = item.LastLookupSourceName,
        LastLookupSourceUrl = item.LastLookupSourceUrl,
        LastLookupUtc = item.LastLookupUtc,
        IsSuspicious = item.IsSuspicious,
        IgnoreFromRenamer = item.IgnoreFromRenamer,
        ContentCategory = item.ContentCategory,
        IsMapMod = item.IsMapMod,
        SourceHintMake = item.SourceHintMake,
        SourceHintModel = item.SourceHintModel,
        SourceHintYearMin = item.SourceHintYearMin,
        SourceHintYearMax = item.SourceHintYearMax
    };

    public void ApplyTo(VehicleConfigItem item)
    {
        item.VehicleName = VehicleName;
        item.Brand = Brand;
        item.Country = Country;
        item.Type = Type;
        item.BodyStyle = BodyStyle;
        item.ConfigType = ConfigType;
        item.Configuration = Configuration;
        item.InsuranceClass = InsuranceClass;
        item.YearMin = YearMin;
        item.YearMax = YearMax;
        item.Value = Value;
        item.Population = Population;
        item.LastAutoFillStatus = LastAutoFillStatus;
        item.LastAutoFillSource = LastAutoFillSource;
        item.LastAutoFillDetail = LastAutoFillDetail;
        item.InferenceReason = InferenceReason;
        item.ConfidenceScore = ConfidenceScore;
        item.ConfidenceTier = ConfidenceTier;
        item.IdentityEvidence = IdentityEvidence;
        item.ValuationEvidence = ValuationEvidence;
        item.ReviewReason = ReviewReason;
        item.ReviewCategory = ReviewCategory;
        item.ReviewPriority = ReviewPriority;
        item.ReviewConflictSummary = ReviewConflictSummary;
        item.DecisionOrigin = DecisionOrigin;
        item.LastDecisionUtc = LastDecisionUtc;
        item.LastLookupSourceName = LastLookupSourceName;
        item.LastLookupSourceUrl = LastLookupSourceUrl;
        item.LastLookupUtc = LastLookupUtc;
        item.IsSuspicious = IsSuspicious;
        item.IgnoreFromRenamer = IgnoreFromRenamer;
        item.ContentCategory = ContentCategory;
        item.IsMapMod = IsMapMod;
        item.SourceHintMake = SourceHintMake;
        item.SourceHintModel = SourceHintModel;
        item.SourceHintYearMin = SourceHintYearMin;
        item.SourceHintYearMax = SourceHintYearMax;
    }
}
