using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpfApp1;

public sealed class VehicleInferenceResult
{
    public string? Model { get; set; }
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
    public string? ValueSource { get; set; }
    public string? ValueEvidence { get; set; }
    public string? InferenceReason { get; set; }
    public string? BrandEvidence { get; set; }
    public string? SuspicionReason { get; set; }
    public string? ContentCategory { get; set; }
    public bool IsMapMod { get; set; }
    public int ConfidenceScore { get; set; }
    public string? ConfidenceTier { get; set; }
    public bool IsSuspicious { get; set; }
    public string? ReviewCategory { get; set; }
    public int ReviewPriority { get; set; }
    public string? ReviewConflictSummary { get; set; }

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(Model) ||
        !string.IsNullOrWhiteSpace(Brand) ||
        !string.IsNullOrWhiteSpace(Country) ||
        !string.IsNullOrWhiteSpace(Type) ||
        !string.IsNullOrWhiteSpace(BodyStyle) ||
        !string.IsNullOrWhiteSpace(ConfigType) ||
        !string.IsNullOrWhiteSpace(Configuration) ||
        !string.IsNullOrWhiteSpace(InsuranceClass) ||
        YearMin.HasValue ||
        YearMax.HasValue ||
        Value.HasValue ||
        Population.HasValue;

    public void Apply(VehicleInferenceResult other)
    {
        Model ??= other.Model;
        Brand ??= other.Brand;
        Country ??= other.Country;
        Type ??= other.Type;
        BodyStyle ??= other.BodyStyle;
        ConfigType ??= other.ConfigType;
        Configuration ??= other.Configuration;
        InsuranceClass ??= other.InsuranceClass;
        YearMin ??= other.YearMin;
        YearMax ??= other.YearMax;
        Value ??= other.Value;
        Population ??= other.Population;
        ValueSource ??= other.ValueSource;
        ValueEvidence ??= other.ValueEvidence;
        InferenceReason ??= other.InferenceReason;
        BrandEvidence ??= other.BrandEvidence;
        SuspicionReason ??= other.SuspicionReason;
        ContentCategory ??= other.ContentCategory;
        IsMapMod = IsMapMod || other.IsMapMod;
        ConfidenceScore = Math.Max(ConfidenceScore, other.ConfidenceScore);
        ConfidenceTier ??= other.ConfidenceTier;
        IsSuspicious = IsSuspicious || other.IsSuspicious;
        if (string.IsNullOrWhiteSpace(ReviewCategory)) ReviewCategory = other.ReviewCategory;
        ReviewPriority = Math.Max(ReviewPriority, other.ReviewPriority);
        if (string.IsNullOrWhiteSpace(ReviewConflictSummary)) ReviewConflictSummary = other.ReviewConflictSummary;
    }

    public VehicleInferenceResult Clone() => new()
    {
        Model = Model,
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
        ValueSource = ValueSource,
        ValueEvidence = ValueEvidence,
        InferenceReason = InferenceReason,
        BrandEvidence = BrandEvidence,
        SuspicionReason = SuspicionReason,
        ContentCategory = ContentCategory,
        IsMapMod = IsMapMod,
        ConfidenceScore = ConfidenceScore,
        ConfidenceTier = ConfidenceTier,
        IsSuspicious = IsSuspicious,
        ReviewCategory = ReviewCategory,
        ReviewPriority = ReviewPriority,
        ReviewConflictSummary = ReviewConflictSummary
    };
}

public sealed class VehicleInferenceService
{
    private const string DataDirectoryRelativePath = "Data";
    private const string ProfilesFileName = "vehicle-profiles.json";
    private const string MakesFileName = "makes.json";
    private const string PricingRulesFileName = "pricing-rules.json";
    private const string TrimKeywordsFileName = "trim-keywords.json";

    private readonly List<VehicleProfile> _profiles;
    private readonly List<MakeRule> _makeRules;
    private readonly List<TrimKeywordRule> _trimRules;
    private readonly PricingRules _pricingRules;
    private readonly RealVehiclePricingService _pricingService;

    private VehicleInferenceService(
        IEnumerable<VehicleProfile> profiles,
        IEnumerable<MakeRule> makeRules,
        IEnumerable<TrimKeywordRule> trimRules,
        PricingRules pricingRules,
        RealVehiclePricingService pricingService)
    {
        _profiles = profiles.ToList();
        _makeRules = makeRules.OrderByDescending(x => x.Token.Length).ToList();
        _trimRules = trimRules.OrderByDescending(x => x.Token.Length).ToList();
        _pricingRules = pricingRules;
        _pricingService = pricingService;
    }

    public static VehicleInferenceService CreateDefault()
    {
        var dataRoot = FindDataDirectory();
        var profiles = LoadProfiles(dataRoot);
        var makes = LoadMakes(dataRoot);
        var trimRules = LoadTrimKeywords(dataRoot);
        var pricingRules = LoadPricingRules(dataRoot) ?? PricingRules.CreateDefault();

        if (profiles.Count == 0)
        {
            profiles.Add(BuildBuiltInVolvoXc90Profile());
        }

        if (makes.Count == 0)
        {
            makes.AddRange(BuiltInMakeRules());
        }

        if (trimRules.Count == 0)
        {
            trimRules.AddRange(BuiltInTrimRules());
        }

        return new VehicleInferenceService(profiles, makes, trimRules, pricingRules, RealVehiclePricingService.CreateDefault());
    }

    public VehicleInferenceResult Infer(VehicleConfigItem item, Action<string>? progress = null, CancellationToken cancellationToken = default, InferenceRunOptions? options = null)
    {
        options ??= InferenceRunOptions.Default;
        progress?.Invoke("Classifying mod content...");
        var normalized = NormalizeCombined(item);
        var contentCategory = ClassifyContentCategory(item, normalized);
        item.ContentCategory = contentCategory;
        item.IsMapMod = string.Equals(contentCategory, "Map", StringComparison.OrdinalIgnoreCase);

        if (item.IsMapMod)
        {
            return new VehicleInferenceResult
            {
                ContentCategory = contentCategory,
                IsMapMod = true,
                ConfidenceScore = 100,
                ConfidenceTier = "Verified",
                InferenceReason = "Mod content classifier",
                BrandEvidence = "This mod looks like a map/level package, so vehicle autofill was skipped.",
                IsSuspicious = false
            };
        }

        progress?.Invoke("Matching local profiles and mod-level identity clues...");
        var profile = MatchProfile(item);
        var sourceIdentity = AnalyzeSourceIdentity(item, profile);
        var patternIdentity = AnalyzeStrongIdentityPatterns(item, normalized);
        if (patternIdentity.Confidence > sourceIdentity.Confidence)
        {
            sourceIdentity = patternIdentity;
        }

        var makeMatch = MatchMake(item, normalized, profile);
        var candidateBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateBrand(candidateBrands, item.VehicleInfoBrand);
        AddCandidateBrand(candidateBrands, profile?.Brand);
        AddCandidateBrand(candidateBrands, sourceIdentity.Brand);
        AddCandidateBrand(candidateBrands, makeMatch.Rule?.Brand);
        AddCandidateBrand(candidateBrands, item.SourceHintMake);

        RealVehicleIdentityResult? internetIdentity = null;
        if (options.AllowInternetIdentity && ShouldUseInternetIdentity(profile, sourceIdentity, makeMatch, item, normalized))
        {
            progress?.Invoke("Verifying mod identity with targeted internet search...");
            internetIdentity = _pricingService.TryResolveIdentity(item, candidateBrands.ToList(), progress, cancellationToken);
            if (internetIdentity != null && internetIdentity.ConfidenceScore >= sourceIdentity.Confidence)
            {
                sourceIdentity = new SourceIdentityInfo(
                    internetIdentity.Brand,
                    !string.IsNullOrWhiteSpace(internetIdentity.Model) ? internetIdentity.Model : sourceIdentity.Model,
                    internetIdentity.ConfidenceScore,
                    internetIdentity.Evidence ?? "Online identity search");
            }
        }

        var make = !string.IsNullOrWhiteSpace(sourceIdentity.Brand)
            ? _makeRules.FirstOrDefault(x => x.Brand.Equals(sourceIdentity.Brand, StringComparison.OrdinalIgnoreCase)) ?? makeMatch.Rule
            : makeMatch.Rule;
        var trimMatches = MatchTrimRules(item, normalized);
        var guessedYears = GuessYears(normalized, profile);
        if (item.VehicleInfoYearMin.HasValue || item.VehicleInfoYearMax.HasValue)
        {
            guessedYears = (item.VehicleInfoYearMin ?? guessedYears.min, item.VehicleInfoYearMax ?? guessedYears.max);
        }
        var bodyStyle = item.VehicleInfoBodyStyle ?? profile?.BodyStyle ?? GuessBodyStyle(normalized, trimMatches);
        var segment = profile?.Segment ?? GuessSegment(normalized, bodyStyle, trimMatches, make?.Tier);
        var configType = GuessConfigType(normalized);
        var insuranceClass = GuessInsuranceClass(normalized, trimMatches);
        var inferredModel = !string.IsNullOrWhiteSpace(item.SourceHintModel)
            ? item.SourceHintModel
            : !string.IsNullOrWhiteSpace(sourceIdentity.Model)
                ? sourceIdentity.Model
                : internetIdentity?.Model;
        var configuration = BuildTrimConfiguration(item);

        if (IsTrailerLikeSource(normalized))
        {
            bodyStyle = "Trailer";
            segment = "utility_trailer";
            configType = "Utility";
            insuranceClass = "commercial";
        }

        var result = new VehicleInferenceResult
        {
            ContentCategory = contentCategory,
            IsMapMod = false,
            Model = inferredModel ?? item.VehicleInfoName ?? item.VehicleName,
            Brand = internetIdentity?.Brand ?? sourceIdentity.Brand ?? item.VehicleInfoBrand ?? profile?.Brand ?? make?.Brand,
            Country = item.VehicleInfoCountry ?? profile?.Country ?? make?.Country,
            Type = item.VehicleInfoType ?? GuessVehicleType(bodyStyle),
            BodyStyle = bodyStyle,
            ConfigType = configType,
            Configuration = configuration,
            InsuranceClass = insuranceClass,
            YearMin = profile?.YearMin ?? guessedYears.min,
            YearMax = profile?.YearMax ?? guessedYears.max,
            ConfidenceScore = profile != null ? 100 : Math.Max(makeMatch.Score, sourceIdentity.Confidence),
            ConfidenceTier = profile != null ? "Verified" : "Fallback",
            BrandEvidence = profile != null ? $"Exact vehicle profile: {profile.Brand ?? profile.BaseValues.Brand}" : !string.IsNullOrWhiteSpace(sourceIdentity.Evidence) ? sourceIdentity.Evidence : makeMatch.Reason,
            InferenceReason = profile != null ? "Exact profile match" : !string.IsNullOrWhiteSpace(sourceIdentity.Brand) ? "Mod-level identity" : makeMatch.MatchSource,
            IsSuspicious = makeMatch.IsSuspicious,
            SuspicionReason = makeMatch.SuspicionReason
        };

        if (!string.IsNullOrWhiteSpace(item.SourceHintMake))
        {
            var hintedMake = _makeRules.FirstOrDefault(x => x.Brand.Equals(item.SourceHintMake, StringComparison.OrdinalIgnoreCase) || x.Token.Equals(item.SourceHintMake, StringComparison.OrdinalIgnoreCase));
            result.Brand = item.SourceHintMake.Trim();
            if (!string.IsNullOrWhiteSpace(hintedMake?.Country))
            {
                result.Country = hintedMake.Country;
            }
            result.ConfidenceScore = Math.Max(result.ConfidenceScore, 95);
            result.BrandEvidence = $"User review hint: {item.SourceHintMake.Trim()}";
            result.InferenceReason = "User review input";
            result.IsSuspicious = false;
            result.SuspicionReason = null;
        }

        if (!string.IsNullOrWhiteSpace(item.SourceHintModel))
        {
            result.Model = item.SourceHintModel.Trim();
            result.InferenceReason = "User review input";
        }

        if (item.SourceHintYearMin.HasValue || item.SourceHintYearMax.HasValue)
        {
            result.YearMin = item.SourceHintYearMin ?? item.SourceHintYearMax ?? result.YearMin;
            result.YearMax = item.SourceHintYearMax ?? item.SourceHintYearMin ?? result.YearMax;
            result.InferenceReason = "User review input";
        }

        var structuredBrand = item.VehicleInfoBrand;
        var hasStrongStructuredBrand = StrongBrandSignal(structuredBrand, !string.IsNullOrWhiteSpace(structuredBrand) ? 90 : 0);
        var hasStrongSourceBrand = StrongBrandSignal(sourceIdentity.Brand, sourceIdentity.Confidence);
        var hasStrongInternetBrand = StrongBrandSignal(internetIdentity?.Brand, internetIdentity?.ConfidenceScore ?? 0);
        var hasStrongConfigBrand = !makeMatch.IsSuspicious && StrongBrandSignal(makeMatch.Rule?.Brand, makeMatch.Score);

        if (!item.HasSourceHints && BrandsConflict(sourceIdentity.Brand, makeMatch.Rule?.Brand))
        {
            result.Brand = sourceIdentity.Brand;
            result.ConfidenceScore = Math.Max(result.ConfidenceScore, 88);
            result.IsSuspicious = true;
            result.SuspicionReason = AppendReason(result.SuspicionReason, $"Mod-level identity points to {sourceIdentity.Brand}, but config-level text looked closer to {makeMatch.Rule?.Brand}.");
            result.BrandEvidence = sourceIdentity.Evidence;
            result.InferenceReason = "Mod-level identity";
        }

        if (!item.HasSourceHints && hasStrongInternetBrand && (BrandsConflict(internetIdentity?.Brand, structuredBrand) || BrandsConflict(internetIdentity?.Brand, sourceIdentity.Brand) || BrandsConflict(internetIdentity?.Brand, makeMatch.Rule?.Brand)))
        {
            var canPromoteInternetIdentity = (!hasStrongStructuredBrand && !hasStrongSourceBrand && !hasStrongConfigBrand)
                || ((internetIdentity?.ConfidenceScore ?? 0) >= Math.Max(sourceIdentity.Confidence, makeMatch.Score) + 10 && makeMatch.IsSuspicious);

            if (canPromoteInternetIdentity && !string.IsNullOrWhiteSpace(internetIdentity?.Brand))
            {
                result.Brand = internetIdentity.Brand;
                result.BrandEvidence = internetIdentity.Evidence ?? result.BrandEvidence;
                result.InferenceReason = "Internet-verified mod identity";
                result.ConfidenceScore = Math.Max(result.ConfidenceScore, internetIdentity.ConfidenceScore);
                result.IsSuspicious = makeMatch.IsSuspicious;
                result.SuspicionReason = makeMatch.IsSuspicious
                    ? AppendReason(result.SuspicionReason, $"Online identity leaned toward {internetIdentity.Brand}, but local config text disagreed.")
                    : result.SuspicionReason;
                if (!string.IsNullOrWhiteSpace(internetIdentity.Model))
                {
                    result.Model = internetIdentity.Model;
                }
            }
            else
            {
                result.IsSuspicious = true;
                result.ConfidenceScore = Math.Min(result.ConfidenceScore, 78);
                result.SuspicionReason = AppendReason(result.SuspicionReason, $"Online identity suggested {internetIdentity?.Brand}, but stronger local evidence still points elsewhere.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(internetIdentity?.Brand) && !string.Equals(result.Brand, internetIdentity.Brand, StringComparison.OrdinalIgnoreCase) && internetIdentity.ConfidenceScore >= 84 && !item.HasSourceHints)
        {
            result.Brand = internetIdentity.Brand;
            result.BrandEvidence = internetIdentity.Evidence ?? result.BrandEvidence;
            result.InferenceReason = "Internet-verified mod identity";
            result.ConfidenceScore = Math.Max(result.ConfidenceScore, internetIdentity.ConfidenceScore);
            result.IsSuspicious = false;
            result.SuspicionReason = null;
            if (!string.IsNullOrWhiteSpace(internetIdentity.Model))
            {
                result.Model = internetIdentity.Model;
            }
        }

        if (IsTrailerLikeSource(normalized) && string.IsNullOrWhiteSpace(item.SourceHintMake))
        {
            result.Brand = sourceIdentity.Confidence >= 90 ? sourceIdentity.Brand : null;
            result.Country = !string.IsNullOrWhiteSpace(result.Brand) ? result.Country : null;
        }

        if (profile != null)
        {
            result.Apply(profile.BaseValues.Clone());
            var configMatch = profile.MatchConfig(item);
            if (configMatch != null)
            {
                result.Apply(configMatch.Clone());
            }
        }

        if (!result.Value.HasValue || !result.Population.HasValue)
        {
            progress?.Invoke("Applying local heuristic pricing and population rules...");
            var estimate = EstimateValueAndPopulation(item, normalized, result, profile, make, trimMatches, segment, bodyStyle);
            result.Value ??= estimate.value;
            result.Population ??= estimate.population;
        }

        if (options.AllowOnlinePricing && ShouldUseOnlinePricing(item, result, normalized))
        {
            var livePrice = _pricingService.TryLookup(item, result, progress, cancellationToken);
            if (livePrice != null && livePrice.EstimatedValue > 0 && livePrice.ConfidenceScore >= 60)
            {
                var localValueMissing = !result.Value.HasValue || result.Value.Value <= 0;
                var localConfidenceWeak = result.ConfidenceScore < 80 || result.IsSuspicious;
                var canAdoptOnlineValuation = !result.IsSuspicious
                    || item.HasSourceHints
                    || livePrice.ConfidenceScore >= 85;

                if ((localValueMissing || localConfidenceWeak || livePrice.ConfidenceScore >= 80) && canAdoptOnlineValuation)
                {
                    result.Value = livePrice.EstimatedValue;
                    result.ValueSource = livePrice.Source;
                    result.ValueEvidence = livePrice.Evidence;
                    if (!result.Population.HasValue || livePrice.ConfidenceScore >= 80)
                    {
                        result.Population = RealVehiclePricingService.EstimatePopulationBandFromValue(livePrice.EstimatedValue);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(livePrice.Evidence))
                {
                    result.ValueEvidence = AppendReason(result.ValueEvidence, "Online value was found but not applied automatically because identity confidence was still weak.");
                }
            }
        }

        if (!IsTrailerLikeSource(normalized) && VehicleNameStronglyConflictsWithBrand(item, result.Brand))
        {
            result.IsSuspicious = true;
            result.SuspicionReason = AppendReason(result.SuspicionReason, "Vehicle/config text appears to point to a different brand than the selected match.");
        }

        if (result.ConfidenceScore > 0 && result.ConfidenceScore < 40 && string.IsNullOrWhiteSpace(profile?.Brand) && IsWeakSourceName(item.ModName))
        {
            result.IsSuspicious = true;
            result.SuspicionReason = AppendReason(result.SuspicionReason, "Mod identity confidence stayed weak after local and online checks.");
        }

        result.ConfidenceTier = ClassifyConfidenceTier(result);

        var reviewAssessment = ReviewDecisionService.Analyze(item, result, holdWeakMatchesForReview: true);
        result.ReviewCategory = reviewAssessment.Category;
        result.ReviewPriority = reviewAssessment.Priority;
        result.ReviewConflictSummary = reviewAssessment.ConflictSummary;
        if (result.IsSuspicious && string.IsNullOrWhiteSpace(result.SuspicionReason))
        {
            result.SuspicionReason = reviewAssessment.Summary;
        }

        return result;
    }

    private static string ClassifyConfidenceTier(VehicleInferenceResult result)
    {
        if (result.IsMapMod) return "Verified";
        if (!string.IsNullOrWhiteSpace(result.InferenceReason) && result.InferenceReason.Contains("User review", StringComparison.OrdinalIgnoreCase)) return "Reviewed";
        if (result.ConfidenceScore >= 95 && !result.IsSuspicious) return "Verified";
        if (result.ConfidenceScore >= 80 && !result.IsSuspicious) return "Strong";
        if (result.ConfidenceScore >= 60 && !result.IsSuspicious) return "Likely";
        return "Fallback";
    }

    public void FlushPricingCache()
    {
        _pricingService.FlushCacheToDisk();
    }

    private static bool StrongBrandSignal(string? brand, int confidence)
    {
        return !string.IsNullOrWhiteSpace(brand) && confidence >= 80;
    }

    private static bool BrandsConflict(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
               && !string.IsNullOrWhiteSpace(right)
               && !left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCandidateBrand(HashSet<string> brands, string? brand)
    {
        if (!string.IsNullOrWhiteSpace(brand))
        {
            brands.Add(brand.Trim());
        }
    }

    private static string ClassifyContentCategory(VehicleConfigItem item, string normalized)
    {
        if (LooksLikeMapMod(item, normalized))
        {
            return "Map";
        }

        if (LooksLikeVehicleMod(item, normalized))
        {
            return "Vehicle";
        }

        return "Unknown";
    }

    private static bool LooksLikeMapMod(VehicleConfigItem item, string normalized)
    {
        var mapSignals = 0;
        var vehicleSignals = 0;

        if (ContainsAny(normalized, "map", "track", "raceway", "circuit", "terrain", "county", "city", "island", "highway", "freeway", "mountain", "forest", "desert", "airport", "gridmap", "hirochi", "tokyo", "level"))
        {
            mapSignals += 2;
        }

        if (ContainsAny(normalized, "config", "trim", "sedan", "wagon", "coupe", "pickup", "van", "suv", "gt r", "gtr", "e320", "e63", "vito"))
        {
            vehicleSignals += 2;
        }

        if (!string.IsNullOrWhiteSpace(item.ModelKey) || !string.IsNullOrWhiteSpace(item.ConfigKey))
        {
            vehicleSignals += 1;
        }

        return mapSignals > vehicleSignals && mapSignals >= 2;
    }

    private static bool LooksLikeVehicleMod(VehicleConfigItem item, string normalized)
    {
        if (!string.IsNullOrWhiteSpace(item.ModelKey) || !string.IsNullOrWhiteSpace(item.ConfigKey))
        {
            return true;
        }

        return ContainsAny(normalized, "sedan", "wagon", "coupe", "truck", "van", "suv", "hatch", "convertible", "gtr", "skyline", "mercedes", "bmw", "audi", "toyota", "nissan");
    }

    private bool ShouldUseInternetIdentity(VehicleProfile? profile, SourceIdentityInfo sourceIdentity, MakeMatch makeMatch, VehicleConfigItem item, string normalized)
    {
        if (item.HasSourceHints || string.Equals(item.ContentCategory, "Map", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasStructuredVehicleMetadata = !string.IsNullOrWhiteSpace(item.VehicleInfoBrand) ||
                                          !string.IsNullOrWhiteSpace(item.VehicleInfoName) ||
                                          item.VehicleInfoYearMin.HasValue ||
                                          item.VehicleInfoYearMax.HasValue;

        if (profile != null && sourceIdentity.Confidence >= 90 && !makeMatch.IsSuspicious)
        {
            return false;
        }

        if (hasStructuredVehicleMetadata && sourceIdentity.Confidence >= 80 && !makeMatch.IsSuspicious)
        {
            return false;
        }

        var isBulkAutofillMode = RealVehiclePricingService.IsBulkAutofillMode();
        if (isBulkAutofillMode)
        {
            if (!makeMatch.IsSuspicious && (profile != null || sourceIdentity.Confidence >= 70 || makeMatch.Score >= 78))
            {
                return false;
            }

            return makeMatch.IsSuspicious || sourceIdentity.Confidence < 65 || makeMatch.Score < 65;
        }

        if (makeMatch.Score <= 0)
        {
            return true;
        }

        if (makeMatch.IsSuspicious)
        {
            return true;
        }

        if (!hasStructuredVehicleMetadata && sourceIdentity.Confidence < 85)
        {
            return true;
        }

        return HasStrongPlatformCode(normalized) && sourceIdentity.Confidence < 90;
    }

    private SourceIdentityInfo AnalyzeStrongIdentityPatterns(VehicleConfigItem item, string normalized)
    {
        var haystack = NormalizeText(string.Join(' ', new[]
        {
            Path.GetFileNameWithoutExtension(item.SourcePath),
            item.ModName,
            item.ModelKey,
            item.ConfigKey,
            item.VehicleName,
            item.Configuration
        }.Where(x => !string.IsNullOrWhiteSpace(x))));

        foreach (var pattern in IdentityPatterns)
        {
            if (Regex.IsMatch(haystack, pattern.Pattern, RegexOptions.IgnoreCase))
            {
                return new SourceIdentityInfo(pattern.Brand, pattern.Model, pattern.Confidence, pattern.Evidence);
            }
        }

        return new SourceIdentityInfo(null, null, 0, string.Empty);
    }

    private static bool HasStrongPlatformCode(string normalized)
        => IdentityPatterns.Any(x => Regex.IsMatch(normalized, x.Pattern, RegexOptions.IgnoreCase));

    private (double? value, int? population) EstimateValueAndPopulation(
        VehicleConfigItem item,
        string normalized,
        VehicleInferenceResult inferred,
        VehicleProfile? profile,
        MakeRule? make,
        IReadOnlyList<TrimKeywordRule> trimMatches,
        string segment,
        string bodyStyle)
    {
        var segmentRule = _pricingRules.GetSegment(segment) ?? _pricingRules.GetSegment(GuessSegment(normalized, bodyStyle, trimMatches, make?.Tier));
        if (segmentRule == null)
        {
            return (null, null);
        }

        var tier = profile?.BrandTier ?? make?.Tier ?? "mainstream";
        var brandMultiplier = _pricingRules.GetBrandTierMultiplier(tier);

        var value = segmentRule.BaseValue * brandMultiplier;
        double population = segmentRule.BasePopulation;

        foreach (var trim in trimMatches)
        {
            value *= trim.ValueMultiplier;
            population *= trim.PopulationMultiplier;
        }

        if (ContainsAny(normalized, "police", "ambulance", "patrol", "sheriff"))
        {
            population = Math.Min(population, 40d);
        }

        var ageMultiplier = CalculateAgeMultiplier(inferred.YearMin, inferred.YearMax, normalized);
        value *= ageMultiplier;

        if (ContainsAny(normalized, "concept", "prototype", "one-off", "limited", "special edition"))
        {
            value *= 1.25;
            population *= 0.35;
        }

        if (ContainsAny(normalized, "beater", "junk", "rust", "project", "parts car"))
        {
            value *= 0.55;
            population *= 0.85;
        }

        if (ContainsAny(normalized, "widebody", "stance", "slammed", "drift", "crawler", "lifted", "offroad", "custom"))
        {
            population *= 0.6;
        }

        value = RoundValue(value);
        var finalPopulation = Math.Clamp((int)Math.Round((double)population), 1, 10000);
        return (value, finalPopulation);
    }

    private static double CalculateAgeMultiplier(int? yearMin, int? yearMax, string normalized)
    {
        var referenceYear = yearMax ?? yearMin;
        if (!referenceYear.HasValue)
        {
            if (ContainsAny(normalized, "classic", "vintage", "retro", "restomod")) return 1.15;
            return 1.0;
        }

        var age = Math.Max(0, DateTime.Now.Year - referenceYear.Value);
        if (age <= 2) return 1.0;
        if (age <= 6) return 0.95;
        if (age <= 12) return 0.87;
        if (age <= 20) return 0.76;
        if (ContainsAny(normalized, "classic", "restomod", "collector", "heritage")) return 1.05;
        return 0.68;
    }

    private static double RoundValue(double value)
    {
        if (value < 20000) return Math.Round(value / 250d) * 250d;
        if (value < 100000) return Math.Round(value / 500d) * 500d;
        return Math.Round(value / 1000d) * 1000d;
    }

    private static bool ShouldUseOnlinePricing(VehicleConfigItem item, VehicleInferenceResult result, string normalized)
    {
        if (RealVehiclePricingService.IsBulkAutofillMode())
        {
            return false;
        }

        if (item.HasSourceHints || string.Equals(result.InferenceReason, "User review input", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (result.IsMapMod || string.Equals(result.ContentCategory, "Map", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.Brand) || string.IsNullOrWhiteSpace(result.Model))
        {
            return false;
        }

        if (result.IsSuspicious || result.ConfidenceScore < 60)
        {
            return false;
        }

        var hasStrongLocalEstimate = result.Value.HasValue && result.Value.Value > 0 &&
                                     result.Population.HasValue && result.Population.Value > 0 &&
                                     result.ConfidenceScore >= 80 &&
                                     !result.IsSuspicious;
        if (hasStrongLocalEstimate)
        {
            return false;
        }

        if (!result.Value.HasValue || result.Value.Value <= 0)
        {
            return result.ConfidenceScore >= 70 && (result.YearMin.HasValue || result.YearMax.HasValue);
        }

        return result.ConfidenceScore >= 85 && ContainsAny(normalized, "trailer", "utility", "flatbed", "gooseneck", "fifth wheel");
    }

    private static string GuessVehicleType(string? bodyStyle)
    {
        return bodyStyle switch
        {
            "Truck" or "Van" => "Truck",
            "Trailer" => "Trailer",
            _ => "Car"
        };
    }

    private static string GuessInsuranceClass(string normalized, IReadOnlyList<TrimKeywordRule> trimMatches)
    {
        var insurance = trimMatches.Select(t => t.InsuranceClass).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(insurance)) return insurance!;
        return ContainsAny(normalized, "police", "ambulance", "taxi", "service", "trailer", "utility", "hauler", "flatbed", "gooseneck") ? "commercial" : "dailyDriver";
    }

    private static string GuessConfigType(string normalized)
    {
        if (ContainsAny(normalized, "custom", "tuned", "swap", "widebody", "stance", "drift", "drag", "crawler", "lifted", "slammed", "converted"))
            return "Custom";
        if (ContainsAny(normalized, "utility trailer", "flatbed", "car hauler", "enclosed trailer", "gooseneck", "fifth wheel"))
            return "Utility";
        return "Factory";
    }

    private static (int? min, int? max) GuessYears(string normalized, VehicleProfile? profile)
    {
        if (profile?.YearMin != null || profile?.YearMax != null)
        {
            return (profile.YearMin, profile.YearMax);
        }

        var fourDigit = Regex.Matches(normalized, @"\b(19\d{2}|20\d{2})\b")
            .Select(m => int.Parse(m.Value))
            .Where(y => y >= 1950 && y <= DateTime.Now.Year + 1)
            .ToList();
        if (fourDigit.Count > 0)
        {
            return (fourDigit.Min(), fourDigit.Max());
        }

        if (Regex.IsMatch(normalized, @"\be\d{2}\b")) return (1995, 2006);
        if (Regex.IsMatch(normalized, @"\bmk ?4\b")) return (1997, 2005);
        if (Regex.IsMatch(normalized, @"\bmk ?5\b")) return (2003, 2009);
        if (Regex.IsMatch(normalized, @"\bmk ?6\b")) return (2008, 2016);
        if (Regex.IsMatch(normalized, @"\bmk ?7\b")) return (2012, 2020);

        return (null, null);
    }

    private MakeMatch MatchMake(VehicleConfigItem item, string normalized, VehicleProfile? profile)
    {
        if (!string.IsNullOrWhiteSpace(profile?.Brand))
        {
            var exact = _makeRules.FirstOrDefault(x => x.Brand.Equals(profile.Brand, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return new MakeMatch(exact, 100, "Vehicle profile", $"Exact vehicle profile matched {exact.Brand}.", false, null);
            }
        }

        MakeRule? bestRule = null;
        var bestScore = 0;
        string? bestReason = null;
        string? bestSource = null;
        var suspicious = false;
        string? suspiciousReason = null;

        foreach (var rule in _makeRules)
        {
            var evaluation = EvaluateMakeRule(item, rule);
            if (evaluation.Score <= 0)
            {
                continue;
            }

            if (evaluation.Score > bestScore)
            {
                bestRule = rule;
                bestScore = evaluation.Score;
                bestReason = evaluation.Reason;
                bestSource = evaluation.Source;
                suspicious = evaluation.IsSuspicious;
                suspiciousReason = evaluation.SuspicionReason;
            }
        }

        return new MakeMatch(bestRule, bestScore, bestSource ?? "Heuristic alias match", bestReason, suspicious, suspiciousReason);
    }

    private IReadOnlyList<TrimKeywordRule> MatchTrimRules(VehicleConfigItem item, string normalized)
    {
        var matches = new List<TrimKeywordRule>();
        foreach (var rule in _trimRules)
        {
            if (TryMatchTokenInImportantText(item, rule.Token, allowShortTokens: false, out _, out _))
            {
                matches.Add(rule);
            }
            else if (ContainsToken(normalized, NormalizeText(rule.Token), allowShortTokens: false))
            {
                matches.Add(rule);
            }
        }

        return matches;
    }

    private VehicleProfile? MatchProfile(VehicleConfigItem item)
    {
        return _profiles
            .Where(p => p.IsMatch(item))
            .OrderByDescending(p => p.Score(item))
            .ThenByDescending(p => p.HasConfigRules)
            .FirstOrDefault();
    }

    private static string GuessBodyStyle(string normalized, IReadOnlyList<TrimKeywordRule> trimMatches)
    {
        var fromTrim = trimMatches.Select(t => t.BodyStyle).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(fromTrim)) return fromTrim!;

        if (ContainsAny(normalized, "utility trailer", "enclosed trailer", "flatbed", "car hauler", "gooseneck", "fifth wheel", "camper trailer", "boat trailer", "tow dolly", "trailer")) return "Trailer";
        if (ContainsAny(normalized, "suv", "crossover", "xc90", "xc60", "qx", "x5", "x3", "gls", "gle", "roamer")) return "SUV";
        if (ContainsAny(normalized, "wagon", "estate", "touring", "avant", "shooting brake")) return "Wagon";
        if (ContainsAny(normalized, "van", "minivan", "mpv", "cargo", "transit", "sprinter")) return "Van";
        if (ContainsAny(normalized, "truck", "pickup", "ute", "2500", "3500", "hd", "f150", "silverado", "sierra", "ram")) return "Truck";
        if (ContainsAny(normalized, "coupe", "fastback", "2 door")) return "Coupe";
        if (ContainsAny(normalized, "hatch", "hatchback")) return "Hatchback";
        if (ContainsAny(normalized, "convertible", "cabrio", "roadster", "spyder", "spider")) return "Convertible";
        if (ContainsAny(normalized, "sedan", "saloon", "limousine")) return "Sedan";
        return "Car";
    }

    private static string GuessSegment(string normalized, string? bodyStyle, IReadOnlyList<TrimKeywordRule> trimMatches, string? tier)
    {
        var fromTrim = trimMatches.Select(t => t.Segment).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(fromTrim)) return fromTrim!;

        if (ContainsAny(normalized, "utility trailer", "enclosed trailer", "flatbed", "car hauler", "gooseneck", "fifth wheel", "camper trailer", "boat trailer", "tow dolly", "trailer")) return "utility_trailer";
        if (ContainsAny(normalized, "supercar", "hypercar", "exotic", "ferrari", "lamborghini", "mclaren", "pagani", "koenigsegg")) return "supercar";
        if (ContainsAny(normalized, "muscle", "hellcat", "zl1", "gt500", "amg gt", "corvette", "911 turbo", "gtr", "gt-r")) return "sports_car";
        if (ContainsAny(normalized, "luxury", "executive", "flagship", "s class", "7 series", "a8")) return bodyStyle == "SUV" ? "luxury_suv" : "luxury_sedan";
        if (ContainsAny(normalized, "compact", "civic", "corolla", "golf", "focus", "mazda3", "impreza")) return "compact_car";
        if (ContainsAny(normalized, "camry", "accord", "altima", "sonata", "optima", "passat", "mazda 6", "mazda6")) return "midsize_sedan";
        if (ContainsAny(normalized, "hilux", "ranger", "f150", "silverado", "sierra", "tacoma", "frontier")) return "pickup_truck";
        if (ContainsAny(normalized, "cargo", "transit", "sprinter", "promaster", "express", "savana")) return "van";
        if (ContainsAny(normalized, "pilot", "highlander", "explorer", "tahoe", "suburban", "durango", "telluride", "palisade")) return tier is "premium" or "luxury" ? "luxury_suv" : "suv";
        if (ContainsAny(normalized, "wrangler", "defender", "g wagon", "g-wagon", "bronco", "offroad")) return "offroad_suv";

        return bodyStyle switch
        {
            "Truck" => "pickup_truck",
            "Van" => "van",
            "Trailer" => "utility_trailer",
            "SUV" => tier is "premium" or "luxury" ? "luxury_suv" : "suv",
            "Coupe" => tier == "exotic" ? "supercar" : "sports_car",
            "Convertible" => tier == "exotic" ? "supercar" : "sports_car",
            "Hatchback" => "compact_car",
            "Wagon" => tier is "premium" or "luxury" ? "luxury_wagon" : "wagon",
            _ => tier is "premium" or "luxury" ? "premium_sedan" : "midsize_sedan"
        };
    }

    private static string NormalizeCombined(VehicleConfigItem item)
    {
        var sourceName = Path.GetFileNameWithoutExtension(item.SourcePath);
        var parts = new[] { sourceName, item.ModName, item.ModelKey, item.ConfigKey, item.VehicleName, item.Configuration, item.VehicleInfoName, item.VehicleInfoBrand, item.VehicleInfoBodyStyle, item.VehicleInfoCountry, item.VehicleInfoType, item.VehicleInfoYearMin?.ToString(), item.VehicleInfoYearMax?.ToString(), item.SourceHintMake, item.SourceHintModel, item.SourceHintYearMin?.ToString(), item.SourceHintYearMax?.ToString() }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!);

        return NormalizeText(string.Join(' ', parts));
    }

    private static string NormalizeText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var text = input.Replace('_', ' ').Replace('-', ' ');
        text = Regex.Replace(text, @"\[(.*?)\]", " $1 ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.ToLowerInvariant();
    }

    private static bool ContainsAny(string normalized, params string[] needles)
        => needles.Any(n => ContainsToken(normalized, NormalizeText(n), allowShortTokens: false) || normalized.Contains(NormalizeText(n), StringComparison.OrdinalIgnoreCase));

    private static bool ContainsToken(string normalizedHaystack, string normalizedToken, bool allowShortTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedHaystack) || string.IsNullOrWhiteSpace(normalizedToken))
        {
            return false;
        }

        var haystackTokens = normalizedHaystack.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenParts = normalizedToken.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokenParts.Length == 0)
        {
            return false;
        }

        if (!allowShortTokens && normalizedToken.Length <= 2)
        {
            return haystackTokens.Any(t => string.Equals(t, normalizedToken, StringComparison.OrdinalIgnoreCase));
        }

        if (tokenParts.Length == 1)
        {
            return haystackTokens.Any(t => string.Equals(t, normalizedToken, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i <= haystackTokens.Length - tokenParts.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < tokenParts.Length; j++)
            {
                if (!string.Equals(haystackTokens[i + j], tokenParts[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildImportantHaystack(params string?[] values)
        => NormalizeText(string.Join(' ', values.Where(v => !string.IsNullOrWhiteSpace(v))!));

    private static bool TryMatchTokenInImportantText(VehicleConfigItem item, string token, bool allowShortTokens, out string source, out int score)
    {
        var normalizedToken = NormalizeText(token);
        var sourceName = Path.GetFileNameWithoutExtension(item.SourcePath);
        var fields = new (string Name, string? Value, int Score)[]
        {
            ("User make", item.SourceHintMake, 98),
            ("Source name", sourceName, 94),
            ("Mod name", item.ModName, 72),
            ("Model key", item.ModelKey, 36)
        };

        foreach (var field in fields)
        {
            var normalizedValue = NormalizeText(field.Value);
            if (ContainsToken(normalizedValue, normalizedToken, allowShortTokens))
            {
                source = field.Name;
                score = field.Score + Math.Min(20, normalizedToken.Length * 2);
                return true;
            }
        }

        source = string.Empty;
        score = 0;
        return false;
    }

    private (int Score, string Source, string Reason, bool IsSuspicious, string? SuspicionReason) EvaluateMakeRule(VehicleConfigItem item, MakeRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Token))
        {
            return (0, string.Empty, string.Empty, false, null);
        }

        var allowShortTokens = rule.Token.Length <= 2;
        if (!TryMatchTokenInImportantText(item, rule.Token, allowShortTokens, out var source, out var score))
        {
            return (0, string.Empty, string.Empty, false, null);
        }

        var suspicious = false;
        string? suspiciousReason = null;
        if (string.Equals(source, "Source name", StringComparison.OrdinalIgnoreCase) || string.Equals(source, "Mod name", StringComparison.OrdinalIgnoreCase))
        {
            var sourceCandidate = string.Equals(source, "Source name", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(item.SourcePath)
                : item.ModName;
            if (IsWeakSourceName(sourceCandidate))
            {
                suspicious = true;
                suspiciousReason = "Brand was inferred only from a weak archive/folder name.";
                score = Math.Min(score, 42);
            }
            else
            {
                score = Math.Max(score, 82);
            }
        }

        if (string.Equals(source, "Vehicle name", StringComparison.OrdinalIgnoreCase) || string.Equals(source, "Configuration", StringComparison.OrdinalIgnoreCase) || string.Equals(source, "Config key", StringComparison.OrdinalIgnoreCase))
        {
            suspicious = true;
            suspiciousReason = AppendReason(suspiciousReason, "Config-level text looked more like a trim/package than a trustworthy make anchor.");
            score = Math.Min(score, 34);
        }

        if (allowShortTokens && !string.Equals(source, "Source name", StringComparison.OrdinalIgnoreCase) && !string.Equals(source, "Mod name", StringComparison.OrdinalIgnoreCase) && !string.Equals(source, "Model key", StringComparison.OrdinalIgnoreCase))
        {
            suspicious = true;
            suspiciousReason = AppendReason(suspiciousReason, "Match came from a very short alias, which is easier to false-trigger.");
            score = Math.Min(score, 32);
        }

        return (score, source, $"Matched make alias '{rule.Token}' from {source}.", suspicious, suspiciousReason);
    }


    private static bool IsWeakSourceName(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return true;
        }

        var normalized = sourceName.Replace('_', ' ').Replace('-', ' ').Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        var junkTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mod", "car", "vehicle", "pack", "repo", "updated", "fixed", "final", "new", "beta", "release", "config", "test"
        };

        var strongTokens = tokens.Count(t => t.Length >= 3 && !junkTokens.Contains(t) && !t.All(char.IsDigit));
        return strongTokens < 2;
    }

    private bool VehicleNameStronglyConflictsWithBrand(VehicleConfigItem item, string? chosenBrand)
    {
        if (string.IsNullOrWhiteSpace(chosenBrand))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.SourceHintMake))
        {
            return false;
        }

        var importantText = BuildImportantHaystack(item.VehicleName, item.Configuration, item.ModelKey, item.ConfigKey);
        if (string.IsNullOrWhiteSpace(importantText))
        {
            return false;
        }

        var chosenRule = _makeRules.FirstOrDefault(x => x.Brand.Equals(chosenBrand, StringComparison.OrdinalIgnoreCase));
        var chosenMatched = chosenRule != null && ContainsToken(importantText, chosenRule.Token, allowShortTokens: chosenRule.Token.Length <= 2);

        if (chosenMatched)
        {
            return false;
        }

        var conflicting = _makeRules
            .Where(x => !x.Brand.Equals(chosenBrand, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => ContainsToken(importantText, x.Token, allowShortTokens: x.Token.Length <= 2));

        return conflicting != null;
    }

    private SourceIdentityInfo AnalyzeSourceIdentity(VehicleConfigItem item, VehicleProfile? profile)
    {
        if (!string.IsNullOrWhiteSpace(item.VehicleInfoBrand) || !string.IsNullOrWhiteSpace(item.VehicleInfoName))
        {
            var sourceInfoConfidence = (!string.IsNullOrWhiteSpace(item.VehicleInfoBrand) ? 55 : 0) + (!string.IsNullOrWhiteSpace(item.VehicleInfoName) ? 35 : 0);
            sourceInfoConfidence += (item.VehicleInfoYearMin.HasValue || item.VehicleInfoYearMax.HasValue) ? 5 : 0;
            sourceInfoConfidence += !string.IsNullOrWhiteSpace(item.VehicleInfoBodyStyle) ? 5 : 0;
            return new SourceIdentityInfo(item.VehicleInfoBrand, item.VehicleInfoName, Math.Min(99, sourceInfoConfidence), "Vehicle info.json metadata");
        }

        if (!string.IsNullOrWhiteSpace(profile?.Brand))
        {
            return new SourceIdentityInfo(profile.Brand, null, 100, "Exact vehicle profile");
        }

        var sourceName = Path.GetFileNameWithoutExtension(item.SourcePath);
        var sourceText = NormalizeText(sourceName);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new SourceIdentityInfo(null, null, 0, string.Empty);
        }

        var matchedRule = _makeRules
            .Select(rule => new { Rule = rule, Score = ScoreSourceMakeRule(rule, sourceText) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Rule.Token.Length)
            .Select(x => x.Rule)
            .FirstOrDefault();

        string? model = null;
        var confidence = 0;
        var evidence = string.Empty;

        if (matchedRule != null)
        {
            confidence = 90;
            evidence = $"Source archive/folder strongly suggests {matchedRule.Brand}.";
            model = ExtractSourceModel(sourceName, matchedRule);
        }
        else if (!IsWeakSourceName(sourceName) && !string.IsNullOrWhiteSpace(sourceName))
        {
            model = ExtractSourceModel(sourceName, null);
            confidence = string.IsNullOrWhiteSpace(model) ? 0 : 52;
            evidence = string.IsNullOrWhiteSpace(model) ? string.Empty : "Source archive/folder provided a likely model-family clue.";
        }

        return new SourceIdentityInfo(matchedRule?.Brand, model, confidence, evidence);
    }

    private int ScoreSourceMakeRule(MakeRule rule, string sourceText)
    {
        var token = NormalizeText(rule.Token);
        if (string.IsNullOrWhiteSpace(token))
        {
            return 0;
        }

        if (!ContainsToken(sourceText, token, allowShortTokens: token.Length <= 2))
        {
            return 0;
        }

        var score = 80 + Math.Min(15, token.Length * 2);
        if (token.Length <= 2)
        {
            score -= 25;
        }

        return score;
    }

    private static string? ExtractSourceModel(string? sourceName, MakeRule? matchedRule)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        var working = sourceName.Replace('_', ' ').Replace('-', ' ').Trim();
        if (matchedRule != null)
        {
            working = Regex.Replace(working, Regex.Escape(matchedRule.Brand), " ", RegexOptions.IgnoreCase);
            working = Regex.Replace(working, Regex.Escape(matchedRule.Token), " ", RegexOptions.IgnoreCase);
        }

        working = Regex.Replace(working, @"\b(beamng|mod|car|vehicle|pack|repo|release|updated|fixed|beta|config|configs|automation)\b", " ", RegexOptions.IgnoreCase);
        working = Regex.Replace(working, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(working) || LooksLikeGenericConfigDescriptor(working))
        {
            return null;
        }

        return working;
    }

    private static bool LooksLikeGenericConfigDescriptor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = NormalizeText(value);
        return normalized.Length < 3 || ContainsAny(normalized, "police", "sport", "touring", "luxury", "custom", "config", "base", "awd", "fwd", "rwd", "dct", "manual", "auto");
    }

    private static bool ShouldPreferSourceModel(string? currentConfiguration, string? sourceModel)
    {
        if (string.IsNullOrWhiteSpace(sourceModel))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentConfiguration))
        {
            return true;
        }

        return LooksLikeGenericConfigDescriptor(currentConfiguration);
    }

    private static string? AppendReason(string? existing, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return reason;
        }

        if (existing.Contains(reason, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return $"{existing} {reason}".Trim();
    }

    private static string BuildTrimConfiguration(VehicleConfigItem item)
    {
        var preferred = !string.IsNullOrWhiteSpace(item.ConfigKey) ? item.ConfigKey : item.Configuration;
        return PrettyConfiguration(preferred ?? item.ConfigKey, null);
    }

    private static bool IsTrailerLikeSource(string normalized)
        => ContainsAny(normalized, "utility trailer", "enclosed trailer", "flatbed", "car hauler", "gooseneck", "fifth wheel", "camper trailer", "boat trailer", "tow dolly", "trailer", "hauler");

    private static string PrettyConfiguration(string configKey, string? existing)
    {
        if (!string.IsNullOrWhiteSpace(existing)) return existing!;
        var text = configKey.Replace('_', ' ').Replace('-', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text)) return configKey;
        return Regex.Replace(text, @"\b\w", m => m.Value.ToUpperInvariant());
    }

    private static string? FindDataDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, DataDirectoryRelativePath),
            Path.Combine(AppContext.BaseDirectory, "WpfApp1", DataDirectoryRelativePath),
            Path.Combine(Environment.CurrentDirectory, DataDirectoryRelativePath),
            Path.Combine(Environment.CurrentDirectory, "WpfApp1", DataDirectoryRelativePath)
        };

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(Directory.Exists);
    }

    private static List<VehicleProfile> LoadProfiles(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return new List<VehicleProfile>();
        var path = Path.Combine(dataRoot, ProfilesFileName);
        if (!File.Exists(path)) return new List<VehicleProfile>();

        try
        {
            var root = JsonSerializer.Deserialize<VehicleProfilesDocument>(File.ReadAllText(path), JsonOptions);
            return root?.Profiles?.Select(ToVehicleProfile).ToList() ?? new List<VehicleProfile>();
        }
        catch
        {
            return new List<VehicleProfile>();
        }
    }

    private static List<MakeRule> LoadMakes(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return new List<MakeRule>();
        var path = Path.Combine(dataRoot, MakesFileName);
        if (!File.Exists(path)) return new List<MakeRule>();
        try
        {
            var root = JsonSerializer.Deserialize<MakesDocument>(File.ReadAllText(path), JsonOptions);
            return root?.Makes?
                .SelectMany(m => (m.Aliases ?? new List<string> { m.Brand ?? string.Empty })
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => new MakeRule(a!, m.Brand ?? a!, m.Country ?? string.Empty, m.Tier ?? "mainstream")))
                .ToList() ?? new List<MakeRule>();
        }
        catch
        {
            return new List<MakeRule>();
        }
    }

    private static List<TrimKeywordRule> LoadTrimKeywords(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return new List<TrimKeywordRule>();
        var path = Path.Combine(dataRoot, TrimKeywordsFileName);
        if (!File.Exists(path)) return new List<TrimKeywordRule>();
        try
        {
            var root = JsonSerializer.Deserialize<TrimKeywordsDocument>(File.ReadAllText(path), JsonOptions);
            return root?.Keywords?.Select(k => new TrimKeywordRule
            {
                Token = NormalizeText(k.Token),
                ValueMultiplier = k.ValueMultiplier ?? 1.0,
                PopulationMultiplier = k.PopulationMultiplier ?? 1.0,
                InsuranceClass = k.InsuranceClass,
                BodyStyle = k.BodyStyle,
                Segment = k.Segment
            }).Where(x => x.Token.Length > 0).ToList() ?? new List<TrimKeywordRule>();
        }
        catch
        {
            return new List<TrimKeywordRule>();
        }
    }

    private static PricingRules? LoadPricingRules(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return null;
        var path = Path.Combine(dataRoot, PricingRulesFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<PricingRulesDocument>(File.ReadAllText(path), JsonOptions);
            return PricingRules.FromDto(dto);
        }
        catch
        {
            return null;
        }
    }

    private static VehicleProfile ToVehicleProfile(VehicleProfileDto dto)
    {
        var profile = new VehicleProfile(
            dto.Aliases ?? new List<string>(),
            dto.ModelAliases ?? new List<string>(),
            ToInferenceResult(dto.BaseValues),
            dto.Brand,
            dto.Country,
            dto.BodyStyle,
            dto.Segment,
            dto.BrandTier,
            dto.YearMin,
            dto.YearMax);

        if (dto.Configs != null)
        {
            foreach (var kvp in dto.Configs)
            {
                profile.AddConfig(kvp.Key, ToInferenceResult(kvp.Value));
            }
        }

        return profile;
    }

    private static VehicleInferenceResult ToInferenceResult(VehicleInferenceResultDto? dto)
    {
        if (dto == null) return new VehicleInferenceResult();
        return new VehicleInferenceResult
        {
            Brand = dto.Brand,
            Country = dto.Country,
            Type = dto.Type,
            BodyStyle = dto.BodyStyle,
            ConfigType = dto.ConfigType,
            Configuration = dto.Configuration,
            InsuranceClass = dto.InsuranceClass,
            YearMin = dto.YearMin,
            YearMax = dto.YearMax,
            Value = dto.Value,
            Population = dto.Population
        };
    }

    private static VehicleProfile BuildBuiltInVolvoXc90Profile()
    {
        var profile = new VehicleProfile(
            aliases: new[] { "xc90", "volvo xc90" },
            modelAliases: new[] { "xc90" },
            baseValues: new VehicleInferenceResult
            {
                Brand = "Volvo",
                Country = "Sweden",
                Type = "Car",
                BodyStyle = "SUV",
                ConfigType = "Factory",
                InsuranceClass = "dailyDriver",
                YearMin = 2020,
                YearMax = 2024
            },
            brand: "Volvo",
            country: "Sweden",
            bodyStyle: "SUV",
            segment: "luxury_suv",
            brandTier: "premium",
            yearMin: 2020,
            yearMax: 2024);

        profile.AddConfig("d4_inscription", new VehicleInferenceResult { Configuration = "D4 Inscription (A)", Value = 54500, Population = 420 });
        profile.AddConfig("d4_fwd_momentum", new VehicleInferenceResult { Configuration = "D4 FWD Momentum (A)", Value = 47500, Population = 1650 });
        profile.AddConfig("d4_momentum", new VehicleInferenceResult { Configuration = "D4 Momentum (A)", Value = 49500, Population = 1250 });
        profile.AddConfig("d5_inscription", new VehicleInferenceResult { Configuration = "D5 Inscription (A)", Value = 58500, Population = 300 });
        profile.AddConfig("d5_momentum", new VehicleInferenceResult { Configuration = "D5 Momentum (A)", Value = 53500, Population = 700 });
        profile.AddConfig("swedish_police", new VehicleInferenceResult { Configuration = "Swedish Police (A)", Value = 57000, Population = 18, InsuranceClass = "commercial" });
        profile.AddConfig("t5_inscription", new VehicleInferenceResult { Configuration = "T5 Inscription (A)", Value = 55000, Population = 520 });
        profile.AddConfig("t5_inscription_usdm", new VehicleInferenceResult { Configuration = "T5 Inscription [USDM] (A)", Value = 55500, Population = 700 });
        profile.AddConfig("t5_momentum", new VehicleInferenceResult { Configuration = "T5 Momentum (A)", Value = 47500, Population = 1500 });
        profile.AddConfig("t5_momentum_usdm", new VehicleInferenceResult { Configuration = "T5 Momentum [USDM] (A)", Value = 48000, Population = 1900 });
        profile.AddConfig("t6_inscription", new VehicleInferenceResult { Configuration = "T6 Inscription (A)", Value = 61500, Population = 260 });
        profile.AddConfig("t6_inscription_usdm", new VehicleInferenceResult { Configuration = "T6 Inscription [USDM] (A)", Value = 62000, Population = 360 });
        profile.AddConfig("t6_momentum", new VehicleInferenceResult { Configuration = "T6 Momentum (A)", Value = 56500, Population = 620 });
        profile.AddConfig("t6_momentum_usdm", new VehicleInferenceResult { Configuration = "T6 Momentum [USDM] (A)", Value = 57000, Population = 820 });
        profile.AddConfig("t8_rdesign", new VehicleInferenceResult { Configuration = "T8 R-Design (A)", Value = 70500, Population = 95 });
        profile.AddConfig("t8_rdesign_beigeint", new VehicleInferenceResult { Configuration = "T8 R-Design [Beige Interior] (A)", Value = 71000, Population = 60 });
        profile.AddConfig("t8_rdesign_beigeint_usdm", new VehicleInferenceResult { Configuration = "T8 R-Design [Beige Interior] [USDM] (A)", Value = 71500, Population = 80 });
        profile.AddConfig("t8_rdesign_usdm", new VehicleInferenceResult { Configuration = "T8 R-Design [USDM] (A)", Value = 71000, Population = 120 });
        return profile;
    }

    private static IEnumerable<MakeRule> BuiltInMakeRules()
    {
        return new[]
        {
            new MakeRule("volvo", "Volvo", "Sweden", "premium"),
            new MakeRule("ford", "Ford", "United States", "mainstream"),
            new MakeRule("chevrolet", "Chevrolet", "United States", "mainstream"),
            new MakeRule("toyota", "Toyota", "Japan", "mainstream"),
            new MakeRule("honda", "Honda", "Japan", "mainstream"),
            new MakeRule("bmw", "BMW", "Germany", "premium"),
            new MakeRule("mercedes", "Mercedes-Benz", "Germany", "luxury"),
            new MakeRule("audi", "Audi", "Germany", "premium")
        };
    }

    private static IEnumerable<TrimKeywordRule> BuiltInTrimRules()
    {
        return new[]
        {
            new TrimKeywordRule { Token = "police", ValueMultiplier = 1.08, PopulationMultiplier = 0.04, InsuranceClass = "commercial" },
            new TrimKeywordRule { Token = "taxi", ValueMultiplier = 0.96, PopulationMultiplier = 0.18, InsuranceClass = "commercial" },
            new TrimKeywordRule { Token = "base", ValueMultiplier = 0.9, PopulationMultiplier = 1.3 },
            new TrimKeywordRule { Token = "sport", ValueMultiplier = 1.18, PopulationMultiplier = 0.7 },
            new TrimKeywordRule { Token = "luxury", ValueMultiplier = 1.14, PopulationMultiplier = 0.72 }
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private sealed class VehicleProfilesDocument { public List<VehicleProfileDto>? Profiles { get; set; } }
    private sealed class VehicleProfileDto
    {
        public List<string>? Aliases { get; set; }
        public List<string>? ModelAliases { get; set; }
        public string? Brand { get; set; }
        public string? Country { get; set; }
        public string? BodyStyle { get; set; }
        public string? Segment { get; set; }
        public string? BrandTier { get; set; }
        public int? YearMin { get; set; }
        public int? YearMax { get; set; }
        public VehicleInferenceResultDto? BaseValues { get; set; }
        public Dictionary<string, VehicleInferenceResultDto>? Configs { get; set; }
    }

    private sealed class VehicleInferenceResultDto
    {
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
    }

    private sealed class MakesDocument { public List<MakeDto>? Makes { get; set; } }
    private sealed class MakeDto
    {
        public string? Brand { get; set; }
        public string? Country { get; set; }
        public string? Tier { get; set; }
        public List<string>? Aliases { get; set; }
    }

    private sealed class TrimKeywordsDocument { public List<TrimKeywordDto>? Keywords { get; set; } }
    private sealed class TrimKeywordDto
    {
        public string Token { get; set; } = string.Empty;
        public double? ValueMultiplier { get; set; }
        public double? PopulationMultiplier { get; set; }
        public string? InsuranceClass { get; set; }
        public string? BodyStyle { get; set; }
        public string? Segment { get; set; }
    }

    private sealed class PricingRulesDocument
    {
        public Dictionary<string, double>? BrandTierMultipliers { get; set; }
        public Dictionary<string, SegmentRuleDto>? Segments { get; set; }
    }

    private sealed class SegmentRuleDto
    {
        public double BaseValue { get; set; }
        public int BasePopulation { get; set; }
    }


    private sealed record MakeMatch(MakeRule? Rule, int Score, string MatchSource, string? Reason, bool IsSuspicious, string? SuspicionReason);
    private sealed record SourceIdentityInfo(string? Brand, string? Model, int Confidence, string Evidence);
    private sealed record IdentityPattern(string Pattern, string Brand, string? Model, int Confidence, string Evidence);

    private static readonly IdentityPattern[] IdentityPatterns =
    {
        new(@"\bw210\b|\be320\b|\be420\b|\be55\b", "Mercedes-Benz", "E-Class (W210)", 97, "Strong chassis/model clues point to Mercedes-Benz W210 E-Class."),
        new(@"\bw211\b|\be500\b", "Mercedes-Benz", "E-Class (W211)", 96, "Strong chassis/model clues point to Mercedes-Benz W211 E-Class."),
        new(@"\bw212\b", "Mercedes-Benz", "E-Class (W212)", 96, "Strong chassis/model clues point to Mercedes-Benz W212 E-Class."),
        new(@"\bw213\b|\be63\b", "Mercedes-Benz", "E-Class (W213)", 97, "Strong chassis/model clues point to Mercedes-Benz W213 E-Class."),
        new(@"\bvito\b|\bv class\b|\bviano\b", "Mercedes-Benz", "Vito / V-Class", 95, "Van-family clues point to Mercedes-Benz Vito / V-Class."),
        new(@"\br32\b|\bskyline r32\b", "Nissan", "Skyline GT-R (R32)", 96, "Strong chassis clues point to Nissan Skyline R32."),
        new(@"\br33\b|\bskyline r33\b", "Nissan", "Skyline GT-R (R33)", 96, "Strong chassis clues point to Nissan Skyline R33."),
        new(@"\br34\b|\bskyline r34\b", "Nissan", "Skyline GT-R (R34)", 97, "Strong chassis clues point to Nissan Skyline R34."),
        new(@"\br35\b|\bgt r\b|\bgtr\b", "Nissan", "GT-R (R35)", 95, "Strong chassis/model clues point to Nissan GT-R."),
        new(@"\bae86\b|\btrueno\b|\blevin\b", "Toyota", "Corolla AE86", 96, "Strong chassis/model clues point to Toyota AE86."),
        new(@"\bs13\b|\b180sx\b|\b240sx\b|\bsilvia\b", "Nissan", "Silvia / 180SX (S13)", 95, "Strong chassis/model clues point to Nissan S13/180SX/Silvia."),
        new(@"\bs14\b", "Nissan", "Silvia (S14)", 95, "Strong chassis clues point to Nissan Silvia S14."),
        new(@"\bs15\b", "Nissan", "Silvia (S15)", 95, "Strong chassis clues point to Nissan Silvia S15."),
        new(@"\bjzx100\b|\bchaser\b|\bmark ii\b", "Toyota", "Mark II / Chaser (JZX100)", 95, "Strong chassis/model clues point to Toyota JZX100 family."),
    };

    private sealed class MakeRule
    {
        public MakeRule(string token, string brand, string country, string tier)
        {
            Token = NormalizeText(token);
            Brand = brand;
            Country = country;
            Tier = tier;
        }

        public string Token { get; }
        public string Brand { get; }
        public string Country { get; }
        public string Tier { get; }
    }

    private sealed class TrimKeywordRule
    {
        public string Token { get; set; } = string.Empty;
        public double ValueMultiplier { get; set; } = 1.0;
        public double PopulationMultiplier { get; set; } = 1.0;
        public string? InsuranceClass { get; set; }
        public string? BodyStyle { get; set; }
        public string? Segment { get; set; }
    }

    private sealed class PricingRules
    {
        private readonly Dictionary<string, double> _brandTierMultipliers;
        private readonly Dictionary<string, SegmentRule> _segments;

        private PricingRules(Dictionary<string, double> brandTierMultipliers, Dictionary<string, SegmentRule> segments)
        {
            _brandTierMultipliers = brandTierMultipliers;
            _segments = segments;
        }

        public static PricingRules CreateDefault() => new(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["economy"] = 0.92,
                ["mainstream"] = 1.0,
                ["premium"] = 1.2,
                ["luxury"] = 1.4,
                ["exotic"] = 2.2
            },
            new Dictionary<string, SegmentRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["compact_car"] = new SegmentRule(23000, 4200),
                ["midsize_sedan"] = new SegmentRule(32000, 2400),
                ["premium_sedan"] = new SegmentRule(52000, 850),
                ["luxury_sedan"] = new SegmentRule(76000, 320),
                ["suv"] = new SegmentRule(41000, 2200),
                ["luxury_suv"] = new SegmentRule(62000, 780),
                ["offroad_suv"] = new SegmentRule(52000, 380),
                ["wagon"] = new SegmentRule(34000, 850),
                ["luxury_wagon"] = new SegmentRule(61000, 220),
                ["pickup_truck"] = new SegmentRule(47000, 1700),
                ["van"] = new SegmentRule(39000, 950),
                ["utility_trailer"] = new SegmentRule(8500, 1200),
                ["sports_car"] = new SegmentRule(58000, 180),
                ["supercar"] = new SegmentRule(210000, 12)
            });

        public static PricingRules FromDto(PricingRulesDocument? dto)
        {
            if (dto == null) return CreateDefault();
            var defaults = CreateDefault();
            var brandTiers = dto.BrandTierMultipliers != null && dto.BrandTierMultipliers.Count > 0
                ? new Dictionary<string, double>(dto.BrandTierMultipliers, StringComparer.OrdinalIgnoreCase)
                : defaults._brandTierMultipliers;
            var segments = dto.Segments != null && dto.Segments.Count > 0
                ? dto.Segments.ToDictionary(k => k.Key, v => new SegmentRule(v.Value.BaseValue, v.Value.BasePopulation), StringComparer.OrdinalIgnoreCase)
                : defaults._segments;
            return new PricingRules(brandTiers, segments);
        }

        public double GetBrandTierMultiplier(string tier)
            => _brandTierMultipliers.TryGetValue(tier, out var value) ? value : 1.0;

        public SegmentRule? GetSegment(string key)
            => _segments.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class SegmentRule
    {
        public SegmentRule(double baseValue, int basePopulation)
        {
            BaseValue = baseValue;
            BasePopulation = basePopulation;
        }

        public double BaseValue { get; }
        public int BasePopulation { get; }
    }
}

public sealed class VehicleProfile
{
    private readonly List<VehicleProfileConfigRule> _configRules = new();
    private readonly HashSet<string> _aliases;
    private readonly HashSet<string> _modelAliases;

    public VehicleInferenceResult BaseValues { get; }
    public string? Brand { get; }
    public string? Country { get; }
    public string? BodyStyle { get; }
    public string? Segment { get; }
    public string? BrandTier { get; }
    public int? YearMin { get; }
    public int? YearMax { get; }
    public bool HasConfigRules => _configRules.Count > 0;

    public VehicleProfile(
        IEnumerable<string> aliases,
        IEnumerable<string> modelAliases,
        VehicleInferenceResult baseValues,
        string? brand = null,
        string? country = null,
        string? bodyStyle = null,
        string? segment = null,
        string? brandTier = null,
        int? yearMin = null,
        int? yearMax = null)
    {
        _aliases = aliases.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToHashSet();
        _modelAliases = modelAliases.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToHashSet();
        BaseValues = baseValues;
        Brand = brand ?? baseValues.Brand;
        Country = country ?? baseValues.Country;
        BodyStyle = bodyStyle ?? baseValues.BodyStyle;
        Segment = segment;
        BrandTier = brandTier;
        YearMin = yearMin ?? baseValues.YearMin;
        YearMax = yearMax ?? baseValues.YearMax;
    }

    public void AddConfig(string keyOrToken, VehicleInferenceResult values) => _configRules.Add(new VehicleProfileConfigRule(keyOrToken, values));
    public bool IsMatch(VehicleConfigItem item) => Score(item) > 0;

    public int Score(VehicleConfigItem item)
    {
        var score = 0;
        var haystack = NormalizeForMatch(item.ModName, item.ModelKey, item.ConfigKey, item.VehicleName, item.Configuration);
        if (!string.IsNullOrWhiteSpace(item.ModelKey) && _modelAliases.Contains(item.ModelKey.ToLowerInvariant())) score += 120;
        foreach (var alias in _aliases)
        {
            if (ContainsProfileAlias(haystack, alias)) score += alias.Length >= 5 ? 60 : 24;
        }
        return score;
    }

    public VehicleInferenceResult? MatchConfig(VehicleConfigItem item)
    {
        return _configRules
            .Where(r => r.IsMatch(item))
            .OrderByDescending(r => r.Score(item))
            .Select(r => r.Values)
            .FirstOrDefault();
    }

    private static string NormalizeForMatch(params string?[] values)
    {
        var text = string.Join(' ', values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
        text = text.Replace('_', ' ').Replace('-', ' ');
        text = Regex.Replace(text, @"\[(.*?)\]", " $1 ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.ToLowerInvariant();
    }

    private static bool ContainsProfileAlias(string haystack, string alias)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        var parts = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aliasParts = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (aliasParts.Length == 0)
        {
            return false;
        }

        if (aliasParts.Length == 1)
        {
            return parts.Any(p => string.Equals(p, alias, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i <= parts.Length - aliasParts.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < aliasParts.Length; j++)
            {
                if (!string.Equals(parts[i + j], aliasParts[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class VehicleProfileConfigRule
{
    private readonly string _token;
    public VehicleInferenceResult Values { get; }

    public VehicleProfileConfigRule(string token, VehicleInferenceResult values)
    {
        _token = token.Trim().ToLowerInvariant();
        Values = values;
    }

    public bool IsMatch(VehicleConfigItem item) => Score(item) > 0;

    public int Score(VehicleConfigItem item)
    {
        var configKey = item.ConfigKey.Trim().ToLowerInvariant();
        if (configKey.Equals(_token, StringComparison.OrdinalIgnoreCase)) return 1000;
        var haystack = string.Join(' ', new[] { item.ConfigKey, item.VehicleName, item.Configuration }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)).ToLowerInvariant();
        if (ContainsProfileAliasLocal(haystack, _token)) return 500;
        var normalizedToken = _token.Replace('_', ' ');
        if (ContainsProfileAliasLocal(haystack.Replace('_', ' '), normalizedToken)) return 250;
        return 0;
    }

    private static bool ContainsProfileAliasLocal(string haystack, string alias)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        var parts = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aliasParts = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (aliasParts.Length == 0)
        {
            return false;
        }

        if (aliasParts.Length == 1)
        {
            return parts.Any(p => string.Equals(p, alias, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i <= parts.Length - aliasParts.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < aliasParts.Length; j++)
            {
                if (!string.Equals(parts[i + j], aliasParts[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
