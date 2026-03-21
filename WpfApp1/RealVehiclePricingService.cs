using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpfApp1;

public sealed class RealVehiclePricingService
{
    private static int _lookupTimeoutSeconds = 8;
    private static readonly HttpClient Http = BuildHttpClient();
    private static readonly object GlobalCacheGate = new();
    private static readonly Dictionary<string, RealVehicleIdentityResult> IdentityCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> NegativeLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> HostCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> HostLastRequestUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NegativeLookupTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchEngineCooldown = TimeSpan.FromMinutes(2);
    private static bool _bulkAutofillMode;
    private readonly string _cachePath;
    private Dictionary<string, PricingCacheEntry>? _cache;
    private int _pendingCacheWrites;
    private static readonly object RuleDataGate = new();
    private static LookupRuleData? _lookupRuleData;

    public RealVehiclePricingService(string cachePath)
    {
        _cachePath = cachePath;
    }

    public static void ConfigureLookupTimeoutSeconds(int seconds)
    {
        _lookupTimeoutSeconds = Math.Clamp(seconds, 3, 30);
        try
        {
            Http.Timeout = TimeSpan.FromSeconds(_lookupTimeoutSeconds);
        }
        catch
        {
            // keep non-blocking if a request is currently active
        }
    }

    public static int GetLookupTimeoutSeconds()
    {
        lock (GlobalCacheGate)
        {
            return Math.Clamp(_lookupTimeoutSeconds, 3, 30);
        }
    }

    public static RealVehiclePricingService CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNGMarketplaceConfigEditor",
            "Cache");
        Directory.CreateDirectory(root);
        return new RealVehiclePricingService(Path.Combine(root, "pricing-cache.json"));
    }

    public static void SetBulkAutofillMode(bool enabled)
    {
        lock (GlobalCacheGate)
        {
            _bulkAutofillMode = enabled;
        }
    }

    public static bool IsBulkAutofillMode()
    {
        lock (GlobalCacheGate)
        {
            return _bulkAutofillMode;
        }
    }

    public void FlushCacheToDisk()
    {
        try
        {
            lock (GlobalCacheGate)
            {
                if (_cache == null || _pendingCacheWrites <= 0)
                {
                    return;
                }
            }

            SaveCache(force: true);
        }
        catch
        {
            // keep non-blocking
        }
    }

    public RealVehicleIdentityResult? TryResolveIdentity(VehicleConfigItem item, IReadOnlyList<string> candidateBrands, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BuildIdentityQuery(item);
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var identityCacheKey = BuildIdentityCacheKey(query, candidateBrands);
            lock (GlobalCacheGate)
            {
                if (IdentityCache.TryGetValue(identityCacheKey, out var cachedIdentity))
                {
                    return CloneIdentityResult(cachedIdentity);
                }
            }

            var candidates = (candidateBrands ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                candidates.AddRange(new[] { "Mercedes-Benz", "BMW", "Audi", "Toyota", "Nissan", "Lexus", "Ford", "Chevrolet", "Honda", "Volvo" });
            }

            var wikiIdentity = TryResolveIdentityFromWikipediaSearch(query, candidates, progress, cancellationToken);
            if (wikiIdentity != null)
            {
                lock (GlobalCacheGate)
                {
                    IdentityCache[identityCacheKey] = CloneIdentityResult(wikiIdentity);
                }

                return wikiIdentity;
            }

            progress?.Invoke($"Searching for mod identity: {query}");
            var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
            var html = GetStringWithPolicies(url, cancellationToken);
            var decoded = WebUtility.HtmlDecode(html);
            var flattened = Regex.Replace(decoded, "<[^>]+>", " ");
            flattened = Regex.Replace(flattened, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(flattened))
            {
                return null;
            }

            var bestBrand = default(string);
            var bestScore = 0;
            foreach (var brand in candidates)
            {
                var brandTokens = brand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var score = 0;
                foreach (var token in brandTokens)
                {
                    if (token.Length < 3)
                    {
                        continue;
                    }

                    score += Regex.Matches(flattened, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase).Count * 12;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestBrand = brand;
                }
            }

            if (bestScore <= 0 || string.IsNullOrWhiteSpace(bestBrand))
            {
                return null;
            }

            var result = new RealVehicleIdentityResult
            {
                Brand = bestBrand,
                Model = GuessModel(query, bestBrand),
                ConfidenceScore = Math.Min(92, 55 + bestScore),
                Evidence = $"Online identity search for '{query}' repeatedly matched {bestBrand}."
            };

            lock (GlobalCacheGate)
            {
                IdentityCache[identityCacheKey] = CloneIdentityResult(result);
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private RealVehicleIdentityResult? TryResolveIdentityFromWikipediaSearch(string query, IReadOnlyCollection<string> candidateBrands, Action<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Invoke("Searching Wikipedia identity index...");
            var apiUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch="
                + Uri.EscapeDataString(query)
                + "&utf8=&format=json&srlimit=5";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            using var response = SendWithPolicies(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = response.Content.ReadAsStream(cancellationToken);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("search", out var searchElement) ||
                searchElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestBrand = null;
            var bestScore = 0;
            string? bestEvidence = null;
            foreach (var item in searchElement.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                var snippet = item.TryGetProperty("snippet", out var snippetElement) ? snippetElement.GetString() : null;
                var combined = WebUtility.HtmlDecode($"{title} {snippet}");
                if (string.IsNullOrWhiteSpace(combined))
                {
                    continue;
                }

                foreach (var brand in candidateBrands)
                {
                    var score = 0;
                    foreach (var token in brand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (token.Length < 3)
                        {
                            continue;
                        }

                        score += Regex.Matches(combined, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase).Count * 18;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBrand = brand;
                        bestEvidence = string.IsNullOrWhiteSpace(title)
                            ? "Wikipedia identity search matched the vehicle search terms."
                            : $"Wikipedia search matched {brand} via '{title}'.";
                    }
                }
            }

            if (bestScore <= 0 || string.IsNullOrWhiteSpace(bestBrand))
            {
                return null;
            }

            return new RealVehicleIdentityResult
            {
                Brand = bestBrand,
                Model = GuessModel(query, bestBrand),
                ConfidenceScore = Math.Min(95, 60 + bestScore),
                Evidence = bestEvidence
            };
        }
        catch
        {
            return null;
        }
    }

    public RealVehiclePriceResult? TryLookup(VehicleConfigItem item, VehicleInferenceResult inference, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Invoke("Preparing online price query...");
            var query = BuildQuery(item, inference);
            if (query == null)
            {
                return null;
            }

            lock (GlobalCacheGate)
            {
                if (NegativeLookupCache.TryGetValue(query.CacheKey, out var blockedUntil) && blockedUntil > DateTimeOffset.UtcNow)
                {
                    return null;
                }
            }

            var cache = GetCache();
            if (cache.TryGetValue(query.CacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                return cached.ToResult();
            }

            var resolved = ResolveVehicleIdentity(query, progress, cancellationToken) ?? query;
            var result = QueryMarketSearch(resolved, progress, cancellationToken);
            if (result == null)
            {
                lock (GlobalCacheGate)
                {
                    NegativeLookupCache[query.CacheKey] = DateTimeOffset.UtcNow.Add(NegativeLookupTtl);
                }
                return null;
            }

            cache[query.CacheKey] = PricingCacheEntry.FromResult(result, TimeSpan.FromDays(14));
            _pendingCacheWrites++;
            SaveCache(force: !_bulkAutofillMode || _pendingCacheWrites >= 10);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildIdentityQuery(VehicleConfigItem item)
    {
        var sourceName = Path.GetFileNameWithoutExtension(item.SourcePath);
        var text = string.Join(" ", new[]
        {
            sourceName,
            item.ModName,
            item.ModelKey,
            item.VehicleInfoBrand,
            item.VehicleInfoName,
            item.SourceHintMake,
            item.SourceHintModel,
            item.VehicleName
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));

        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private RealVehicleQuery? BuildQuery(VehicleConfigItem item, VehicleInferenceResult inference)
    {
        var title = string.Join(' ', new[]
        {
            item.VehicleInfoName,
            item.VehicleName,
            item.ModName,
            item.ModelKey,
            item.ConfigKey,
            item.SourceHintModel,
            item.Configuration,
            item.VehicleInfoBrand,
            inference.Brand,
            item.VehicleInfoBodyStyle,
            inference.BodyStyle
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));

        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (ContainsAny(title, "trailer", "utility trailer", "flatbed", "car hauler", "gooseneck", "fifth wheel", "camper", "rv", "dolly"))
        {
            return new RealVehicleQuery
            {
                SearchTitle = title,
                Year = inference.YearMax ?? inference.YearMin,
                Make = inference.Brand,
                BodyStyle = "Trailer",
                CacheKey = BuildFamilyCacheKey(inference.YearMax ?? inference.YearMin, inference.Brand, GuessModel(title, inference.Brand), "Trailer")
            };
        }

        if (VehicleConfigItem.IsMissingText(inference.Brand) && !LooksLikeRealVehicle(title))
        {
            return null;
        }

        var model = !string.IsNullOrWhiteSpace(item.SourceHintModel) ? item.SourceHintModel!.Trim() : GuessModel(title, inference.Brand);
        return new RealVehicleQuery
        {
            SearchTitle = title,
            Year = inference.YearMax ?? inference.YearMin,
            Make = inference.Brand,
            Model = model,
            BodyStyle = inference.BodyStyle,
            Trim = item.Configuration,
            CacheKey = BuildFamilyCacheKey(inference.YearMax ?? inference.YearMin, inference.Brand, model, inference.BodyStyle)
        };
    }


    public InternetLookupResult? ManualLookup(string? make, string? model, int? yearMin, int? yearMax, string? bodyStyle, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return ManualLookupOptions(make, model, yearMin, yearMax, bodyStyle, progress, cancellationToken)
            .FirstOrDefault();
    }

    public List<InternetLookupResult> ManualLookupOptions(string? make, string? model, int? yearMin, int? yearMax, string? bodyStyle, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var queryYear = yearMax ?? yearMin;
        if (string.IsNullOrWhiteSpace(make) && string.IsNullOrWhiteSpace(model))
        {
            return new List<InternetLookupResult>();
        }

        var searchTitle = string.Join(' ', new[] { queryYear?.ToString(), make, model, bodyStyle }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        var query = new RealVehicleQuery
        {
            Year = queryYear,
            Make = string.IsNullOrWhiteSpace(make) ? null : make.Trim(),
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            BodyStyle = string.IsNullOrWhiteSpace(bodyStyle) ? null : bodyStyle.Trim(),
            SearchTitle = searchTitle,
            CacheKey = NormalizeKey(searchTitle)
        };

        var insuranceClass = string.Equals(bodyStyle, "Truck", StringComparison.OrdinalIgnoreCase) || string.Equals(bodyStyle, "Trailer", StringComparison.OrdinalIgnoreCase)
            ? "commercial"
            : string.Equals(bodyStyle, "SUV", StringComparison.OrdinalIgnoreCase)
                ? "standard"
                : "sport";

        var candidates = QueryWikipediaFirstManualCandidates(query, progress, cancellationToken);
        if (candidates.Count == 0)
        {
            candidates = QueryTrustedLookupCandidates(query, progress, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return new List<InternetLookupResult>();
        }

        return candidates
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.EstimatedValue.HasValue ? 1 : 0)
            .ThenBy(x => x.SourceUrl?.Length ?? int.MaxValue)
            .Take(5)
            .Select(x =>
            {
                var recommendation = BuildLookupRecommendation(query, x.EstimatedValue);
                var finalValue = recommendation.RecommendedValue ?? x.EstimatedValue;
                var finalPopulation = recommendation.RecommendedPopulation ?? EstimatePopulationBandFromValue(finalValue);
                var evidence = x.Evidence;
                if (recommendation.RecommendedValue.HasValue || recommendation.RecommendedPopulation.HasValue)
                {
                    var recommendationBits = new List<string>();
                    if (recommendation.RecommendedValue.HasValue)
                    {
                        recommendationBits.Add($"recommended price ${recommendation.RecommendedValue.Value:N0}");
                    }

                    if (recommendation.RecommendedPopulation.HasValue)
                    {
                        recommendationBits.Add($"recommended population {recommendation.RecommendedPopulation.Value:N0}");
                    }

                    if (recommendationBits.Count > 0)
                    {
                        evidence = string.IsNullOrWhiteSpace(evidence)
                            ? "Lookup recommendation: " + string.Join(", ", recommendationBits)
                            : evidence + " | Lookup recommendation: " + string.Join(", ", recommendationBits);
                    }
                }

                return new InternetLookupResult
                {
                    Brand = query.Make,
                    Model = query.Model,
                    YearMin = yearMin,
                    YearMax = yearMax,
                    BodyStyle = bodyStyle,
                    EstimatedValue = finalValue.HasValue ? RoundValue(finalValue.Value) : null,
                    Population = finalPopulation,
                    InsuranceClass = insuranceClass,
                    Evidence = evidence,
                    SourceUrl = x.SourceUrl,
                    SourceName = x.SourceName,
                    VerificationStatus = x.VerificationStatus,
                    ConfidenceScore = x.Score
                };
            })
            .ToList();
    }

    private LookupRecommendation BuildLookupRecommendation(RealVehicleQuery query, double? sourceValue)
    {
        var data = GetLookupRuleData();
        var profile = FindBestLookupProfile(data, query);

        var segmentKey = profile?.Segment;
        if (string.IsNullOrWhiteSpace(segmentKey))
        {
            segmentKey = InferSegmentKey(query.BodyStyle);
        }

        if (string.IsNullOrWhiteSpace(segmentKey) || !data.Segments.TryGetValue(segmentKey!, out var segment))
        {
            segmentKey = "sedan";
            if (!data.Segments.TryGetValue(segmentKey, out segment))
            {
                return new LookupRecommendation
                {
                    RecommendedValue = sourceValue.HasValue ? RoundValue(sourceValue.Value) : null,
                    RecommendedPopulation = EstimatePopulationBandFromValue(sourceValue)
                };
            }
        }

        var brandTier = profile?.BrandTier;
        if (string.IsNullOrWhiteSpace(brandTier) && !string.IsNullOrWhiteSpace(query.Make))
        {
            data.BrandToTier.TryGetValue(query.Make!, out brandTier);
        }

        if (string.IsNullOrWhiteSpace(brandTier))
        {
            brandTier = "mainstream";
        }

        var tierMultiplier = data.BrandTierMultipliers.TryGetValue(brandTier!, out var tier) ? tier : 1.0;
        var currentYear = DateTime.UtcNow.Year;
        var age = query.Year.HasValue ? Math.Max(0, currentYear - query.Year.Value) : 10;
        var ageFactor = Math.Clamp(Math.Pow(0.93, Math.Min(age, 20)), 0.24, 1.10);
        var heuristicValue = segment.BaseValue * tierMultiplier * ageFactor;

        if (!sourceValue.HasValue || sourceValue.Value <= 0)
        {
            sourceValue = heuristicValue;
        }
        else
        {
            sourceValue = (sourceValue.Value * 0.7) + (heuristicValue * 0.3);
        }

        var rarityFactor = brandTier switch
        {
            "economy" => 1.35,
            "mainstream" => 1.0,
            "premium" => 0.72,
            "luxury" => 0.45,
            "exotic" => 0.18,
            _ => 1.0
        };

        var agePopulationFactor = age switch
        {
            <= 2 => 0.85,
            <= 6 => 1.0,
            <= 12 => 0.92,
            <= 20 => 0.76,
            _ => 0.58
        };

        var recommendedPopulation = (int)Math.Round(segment.BasePopulation * rarityFactor * agePopulationFactor);
        recommendedPopulation = NormalizePopulationBand(recommendedPopulation);

        return new LookupRecommendation
        {
            RecommendedValue = RoundValue(sourceValue.Value),
            RecommendedPopulation = recommendedPopulation
        };
    }

    private static int NormalizePopulationBand(int population)
    {
        if (population <= 50) return Math.Clamp(population, 1, 50);
        if (population <= 200) return Math.Clamp((int)Math.Round(population / 10d) * 10, 50, 200);
        if (population <= 800) return Math.Clamp((int)Math.Round(population / 25d) * 25, 200, 800);
        if (population <= 3000) return Math.Clamp((int)Math.Round(population / 50d) * 50, 800, 3000);
        return Math.Clamp((int)Math.Round(population / 100d) * 100, 3000, 10000);
    }

    private LookupProfileHint? FindBestLookupProfile(LookupRuleData data, RealVehicleQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Make) && string.IsNullOrWhiteSpace(query.Model))
        {
            return null;
        }

        var normalizedModel = NormalizeKey(query.Model);
        var normalizedMake = NormalizeKey(query.Make);
        var normalizedCombined = NormalizeKey(string.Join(' ', new[] { query.Make, query.Model }.Where(x => !string.IsNullOrWhiteSpace(x))));

        return data.Profiles
            .Select(profile => new { profile, score = ScoreLookupProfile(profile, normalizedMake, normalizedModel, normalizedCombined, query.Year) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.profile)
            .FirstOrDefault();
    }

    private static int ScoreLookupProfile(LookupProfileHint profile, string? normalizedMake, string? normalizedModel, string? normalizedCombined, int? year)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(normalizedMake) && string.Equals(NormalizeKey(profile.Brand), normalizedMake, StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(normalizedModel) && profile.ModelAliases.Contains(normalizedModel!))
        {
            score += 130;
        }

        if (!string.IsNullOrWhiteSpace(normalizedCombined) && profile.Aliases.Contains(normalizedCombined!))
        {
            score += 150;
        }

        if (!string.IsNullOrWhiteSpace(normalizedModel))
        {
            foreach (var alias in profile.Aliases)
            {
                if (alias.Contains(normalizedModel!, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 110);
                }
            }
        }

        if (year.HasValue)
        {
            if (profile.YearMin.HasValue && year.Value < profile.YearMin.Value) score -= 30;
            if (profile.YearMax.HasValue && year.Value > profile.YearMax.Value) score -= 20;
            if ((!profile.YearMin.HasValue || year.Value >= profile.YearMin.Value) && (!profile.YearMax.HasValue || year.Value <= profile.YearMax.Value)) score += 20;
        }

        return score;
    }

    private static string InferSegmentKey(string? bodyStyle)
    {
        var normalized = NormalizeKey(bodyStyle);
        if (string.IsNullOrWhiteSpace(normalized)) return "sedan";
        if (normalized.Contains("truck")) return "truck";
        if (normalized.Contains("trailer")) return "trailer";
        if (normalized.Contains("convertible")) return "convertible";
        if (normalized.Contains("coupe")) return "coupe";
        if (normalized.Contains("hatch")) return "hatchback";
        if (normalized.Contains("wagon")) return "wagon";
        if (normalized.Contains("van")) return normalized.Contains("mini") ? "minivan" : "commercial_van";
        if (normalized.Contains("suv")) return "suv";
        if (normalized.Contains("cross")) return "crossover";
        return "sedan";
    }

    private LookupRuleData GetLookupRuleData()
    {
        lock (RuleDataGate)
        {
            _lookupRuleData ??= LoadLookupRuleData();
            return _lookupRuleData;
        }
    }

    private static LookupRuleData LoadLookupRuleData()
    {
        var data = new LookupRuleData();

        try
        {
            var pricingPath = ResolveDataFilePath("pricing-rules.json");
            if (!string.IsNullOrWhiteSpace(pricingPath) && File.Exists(pricingPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pricingPath));
                if (doc.RootElement.TryGetProperty("brandTierMultipliers", out var tiers) && tiers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tiers.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var multiplier))
                        {
                            data.BrandTierMultipliers[prop.Name] = multiplier;
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in segments.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var baseValue = prop.Value.TryGetProperty("baseValue", out var baseValueEl) && baseValueEl.TryGetDouble(out var bv) ? bv : 25000d;
                        var basePopulation = prop.Value.TryGetProperty("basePopulation", out var popEl) && popEl.TryGetInt32(out var bp) ? bp : 2500;
                        data.Segments[prop.Name] = new LookupSegmentRule { BaseValue = baseValue, BasePopulation = basePopulation };
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            var profilesPath = ResolveDataFilePath("vehicle-profiles.json");
            if (!string.IsNullOrWhiteSpace(profilesPath) && File.Exists(profilesPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profilesPath));
                if (doc.RootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var profile in profiles.EnumerateArray())
                    {
                        var hint = new LookupProfileHint
                        {
                            Brand = profile.TryGetProperty("brand", out var brandEl) ? brandEl.GetString() : null,
                            Segment = profile.TryGetProperty("segment", out var segmentEl) ? segmentEl.GetString() : null,
                            BrandTier = profile.TryGetProperty("brandTier", out var tierEl) ? tierEl.GetString() : null,
                            YearMin = profile.TryGetProperty("yearMin", out var yMinEl) && yMinEl.TryGetInt32(out var yMin) ? yMin : null,
                            YearMax = profile.TryGetProperty("yearMax", out var yMaxEl) && yMaxEl.TryGetInt32(out var yMax) ? yMax : null
                        };

                        if (profile.TryGetProperty("aliases", out var aliasesEl) && aliasesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var aliasEl in aliasesEl.EnumerateArray())
                            {
                                var alias = NormalizeKey(aliasEl.GetString());
                                if (!string.IsNullOrWhiteSpace(alias)) hint.Aliases.Add(alias!);
                            }
                        }

                        if (profile.TryGetProperty("modelAliases", out var modelAliasesEl) && modelAliasesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var aliasEl in modelAliasesEl.EnumerateArray())
                            {
                                var alias = NormalizeKey(aliasEl.GetString());
                                if (!string.IsNullOrWhiteSpace(alias)) hint.ModelAliases.Add(alias!);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(hint.Brand) && !string.IsNullOrWhiteSpace(hint.BrandTier) && !data.BrandToTier.ContainsKey(hint.Brand!))
                        {
                            data.BrandToTier[hint.Brand!] = hint.BrandTier!;
                        }

                        if (hint.Aliases.Count > 0 || hint.ModelAliases.Count > 0)
                        {
                            data.Profiles.Add(hint);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        if (data.BrandTierMultipliers.Count == 0)
        {
            data.BrandTierMultipliers["economy"] = 0.92;
            data.BrandTierMultipliers["mainstream"] = 1.0;
            data.BrandTierMultipliers["premium"] = 1.2;
            data.BrandTierMultipliers["luxury"] = 1.4;
            data.BrandTierMultipliers["exotic"] = 2.2;
        }

        if (data.Segments.Count == 0)
        {
            data.Segments["sedan"] = new LookupSegmentRule { BaseValue = 28500, BasePopulation = 2750 };
            data.Segments["suv"] = new LookupSegmentRule { BaseValue = 35500, BasePopulation = 1800 };
            data.Segments["truck"] = new LookupSegmentRule { BaseValue = 42000, BasePopulation = 900 };
            data.Segments["coupe"] = new LookupSegmentRule { BaseValue = 36000, BasePopulation = 900 };
            data.Segments["hatchback"] = new LookupSegmentRule { BaseValue = 24000, BasePopulation = 2600 };
            data.Segments["wagon"] = new LookupSegmentRule { BaseValue = 30000, BasePopulation = 1200 };
            data.Segments["commercial_van"] = new LookupSegmentRule { BaseValue = 44500, BasePopulation = 650 };
            data.Segments["minivan"] = new LookupSegmentRule { BaseValue = 37000, BasePopulation = 900 };
            data.Segments["convertible"] = new LookupSegmentRule { BaseValue = 42000, BasePopulation = 500 };
            data.Segments["trailer"] = new LookupSegmentRule { BaseValue = 18000, BasePopulation = 350 };
        }

        return data;
    }

    private static string? ResolveDataFilePath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static int? EstimatePopulationBandFromValue(double? value)
    {
        if (!value.HasValue || value.Value <= 0) return null;
        if (value.Value >= 250000) return 30;
        if (value.Value >= 120000) return 120;
        if (value.Value >= 70000) return 450;
        if (value.Value >= 35000) return 1200;
        if (value.Value >= 18000) return 3200;
        return 6500;
    }

    private CancellationToken CreateLinkedSourceTimeoutToken(CancellationToken cancellationToken, int seconds)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(seconds));
        return linked.Token;
    }

    private TrustedLookupCandidate? BuildWikipediaSearchFallbackCandidate(string title, string url, RealVehicleQuery query)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var score = 28;
        if (!string.IsNullOrWhiteSpace(query.Make) && title.Contains(query.Make, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }

        if (!string.IsNullOrWhiteSpace(query.Model) && title.Contains(query.Model, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (query.Year.HasValue && title.Contains(query.Year.Value.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        if (!string.IsNullOrWhiteSpace(query.BodyStyle) && title.Contains(query.BodyStyle, StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        if (score < 60)
        {
            return null;
        }

        return new TrustedLookupCandidate
        {
            SourceName = "Wikipedia",
            SourceUrl = url,
            EstimatedValue = null,
            Score = Math.Min(score, 92),
            VerificationStatus = score >= 78 ? "Likely" : "Fallback",
            Evidence = $"Wikipedia search matched '{title}' before page verification completed."
        };
    }

    private List<TrustedLookupCandidate> QueryWikipediaFirstManualCandidates(RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        var wikipedia = GetTrustedSources()
            .FirstOrDefault(x => string.Equals(x.Domain, "wikipedia.org", StringComparison.OrdinalIgnoreCase));

        if (wikipedia == null)
        {
            return new List<TrustedLookupCandidate>();
        }

        var results = new List<TrustedLookupCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var wikiSearchToken = CreateLinkedSourceTimeoutToken(cancellationToken, 3);
        var wikiUrls = SearchWikipediaUrls(query, progress, wikiSearchToken).Take(3).ToList();
        foreach (var url in wikiUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(url))
            {
                continue;
            }

            string title = url;
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    title = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Replace('_', ' ')?.Trim('/') ?? url);
                }
            }
            catch
            {
            }

            var fallback = BuildWikipediaSearchFallbackCandidate(title, url, query);
            if (fallback != null)
            {
                results.Add(fallback);
            }
        }

        foreach (var url in wikiUrls.Take(2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = TryBuildTrustedLookupCandidate(wikipedia, url, query, progress, CreateLinkedSourceTimeoutToken(cancellationToken, 4));
            if (candidate == null)
            {
                continue;
            }

            if (candidate.Score >= 60)
            {
                results.Add(candidate);
            }

            if (candidate.Score >= 70)
            {
                break;
            }
        }

        return results
            .GroupBy(x => x.SourceUrl ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SourceUrl?.Length ?? int.MaxValue)
            .ToList();
    }

    private RealVehicleQuery? ResolveVehicleIdentity(RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Make) || !query.Year.HasValue)
        {
            return query;
        }

        try
        {
            var url = $"https://vpic.nhtsa.dot.gov/api/vehicles/GetMakesForManufacturerAndYear/{Uri.EscapeDataString(query.Make) ?? string.Empty}?year={query.Year.Value}&format=json";
            progress?.Invoke($"Checking NHTSA make/year match: {query.Make} {query.Year.Value}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = SendWithPolicies(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return query;
            }

            using var stream = response.Content.ReadAsStream(cancellationToken);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return query;
            }

            foreach (var element in results.EnumerateArray())
            {
                if (!element.TryGetProperty("MakeName", out var makeNameElement))
                {
                    continue;
                }

                var makeName = makeNameElement.GetString();
                if (string.IsNullOrWhiteSpace(makeName))
                {
                    continue;
                }

                if (query.SearchTitle.Contains(makeName, StringComparison.OrdinalIgnoreCase))
                {
                    query.Make = makeName;
                    return query;
                }
            }
        }
        catch
        {
            return query;
        }

        return query;
    }

    private RealVehiclePriceResult? QueryMarketSearch(RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        var candidates = QueryTrustedLookupCandidates(query, progress, cancellationToken);
        var best = candidates
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.EstimatedValue.HasValue ? 1 : 0)
            .FirstOrDefault();

        if (best == null)
        {
            return null;
        }

        if (best.EstimatedValue.HasValue)
        {
            return new RealVehiclePriceResult
            {
                EstimatedValue = RoundValue(best.EstimatedValue.Value),
                Source = best.SourceName,
                Confidence = Math.Clamp(best.Score / 100d, 0.45, 0.95),
                Evidence = best.Evidence,
                SourceUrl = best.SourceUrl,
                SourceName = best.SourceName,
                VerificationStatus = best.VerificationStatus,
                ConfidenceScore = best.Score
            };
        }

        var fallbackValue = EstimateHeuristicValueFromIdentity(query);
        if (!fallbackValue.HasValue)
        {
            return null;
        }

        return new RealVehiclePriceResult
        {
            EstimatedValue = RoundValue(fallbackValue.Value),
            Source = best.SourceName + " identity fallback",
            Confidence = Math.Clamp(best.Score / 100d, 0.20, 0.45),
            Evidence = string.IsNullOrWhiteSpace(best.Evidence)
                ? "Identity matched but no direct market price was found, so a conservative heuristic value was used."
                : best.Evidence + " | No direct market price found; used a conservative heuristic value fallback.",
            SourceUrl = best.SourceUrl,
            SourceName = best.SourceName,
            VerificationStatus = "Likely",
            ConfidenceScore = Math.Max(25, best.Score - 20)
        };
    }

    private List<TrustedLookupCandidate> QueryTrustedLookupCandidates(RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        var results = new List<TrustedLookupCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var prioritizedSources = GetTrustedSources().ToList();
        var sourcesToCheck = _bulkAutofillMode
            ? prioritizedSources.Take(5).ToList()
            : prioritizedSources;
        foreach (var source in sourcesToCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceToken = CreateLinkedSourceTimeoutToken(cancellationToken, string.Equals(source.Domain, "wikipedia.org", StringComparison.OrdinalIgnoreCase) ? 4 : 3);

            var discoveredUrls = DiscoverTrustedSourceUrls(source, query, progress, sourceToken)
                .Where(url => UrlMatchesTrustedSource(url, source))
                .Take(_bulkAutofillMode ? 2 : (string.Equals(source.Domain, "wikipedia.org", StringComparison.OrdinalIgnoreCase) ? 3 : 4))
                .ToList();

            if (discoveredUrls.Count == 0)
            {
                progress?.Invoke($"No candidate pages found for {source.DisplayName}.");
            }

            foreach (var url in discoveredUrls)
            {
                if (!seenUrls.Add(url))
                {
                    continue;
                }

                var candidate = TryBuildTrustedLookupCandidate(source, url, query, progress, sourceToken);
                if (candidate != null && candidate.Score >= 16)
                {
                    results.Add(candidate);
                }
            }

            if (!_bulkAutofillMode && results.Any(x => string.Equals(x.SourceName, "Wikipedia", StringComparison.OrdinalIgnoreCase) && x.Score >= 70))
            {
                break;
            }

            if (_bulkAutofillMode && results.Any(x => x.EstimatedValue.HasValue))
            {
                break;
            }
        }

        return results
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.EstimatedValue.HasValue ? 1 : 0)
            .ToList();
    }

    private List<string> DiscoverTrustedSourceUrls(TrustedSourceDefinition source, RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
            {
                urls.Add(url);
            }
        }

        if (string.Equals(source.Domain, "wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var url in SearchWikipediaUrls(query, progress, cancellationToken))
            {
                AddUrl(url);
            }

            if (urls.Count > 0)
            {
                return urls;
            }
        }

        var terms = BuildTrustedSourceSearchTerms(query, source).ToList();
        if (_bulkAutofillMode && terms.Count > 1)
        {
            terms = terms.Take(1).ToList();
        }

        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var foundAny = false;
            foreach (var url in SearchDuckDuckGoForUrls(term, source, progress, cancellationToken))
            {
                AddUrl(url);
                foundAny = true;
            }

            if (!_bulkAutofillMode || !foundAny)
            {
                foreach (var url in SearchBingForUrls(term, source, progress, cancellationToken))
                {
                    AddUrl(url);
                }
            }
        }

        return urls;
    }

    private List<string> SearchWikipediaUrls(RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        var search = string.Join(' ', new[] { query.Year?.ToString(), query.Make, query.Model, query.BodyStyle }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(search))
        {
            return urls;
        }

        try
        {
            progress?.Invoke("Searching Wikipedia...");
            var apiUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch="
                + Uri.EscapeDataString(search)
                + "&utf8=&format=json&srlimit=3";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            using var response = SendWithPolicies(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return urls;
            }

            using var stream = response.Content.ReadAsStream(cancellationToken);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("search", out var searchElement) ||
                searchElement.ValueKind != JsonValueKind.Array)
            {
                return urls;
            }

            foreach (var item in searchElement.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleElement))
                {
                    continue;
                }

                var title = titleElement.GetString();
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                urls.Add("https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')));
            }
        }
        catch
        {
        }

        return urls;
    }

    private List<string> SearchDuckDuckGoForUrls(string term, TrustedSourceDefinition source, Action<string>? progress, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        try
        {
            progress?.Invoke($"Searching {source.DisplayName} via DuckDuckGo...");
            var searchUrl = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(term);
            var html = GetStringWithPolicies(searchUrl, cancellationToken);
            foreach (var url in ExtractDuckDuckGoResultUrls(html))
            {
                if (UrlMatchesTrustedSource(url, source))
                {
                    urls.Add(url);
                }
            }
        }
        catch
        {
        }

        return urls;
    }

    private List<string> SearchBingForUrls(string term, TrustedSourceDefinition source, Action<string>? progress, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        try
        {
            progress?.Invoke($"Searching {source.DisplayName} via Bing...");
            var searchUrl = "https://www.bing.com/search?q=" + Uri.EscapeDataString(term);
            var html = GetStringWithPolicies(searchUrl, cancellationToken);
            foreach (var url in ExtractExternalResultUrls(html))
            {
                if (UrlMatchesTrustedSource(url, source))
                {
                    urls.Add(url);
                }
            }
        }
        catch
        {
        }

        return urls;
    }

    private TrustedLookupCandidate? TryBuildTrustedLookupCandidate(TrustedSourceDefinition source, string url, RealVehicleQuery query, Action<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Invoke($"Checking {source.DisplayName} page...");
            var html = GetStringWithPolicies(url, cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var title = ExtractHtmlTitle(html);
            var text = FlattenHtmlToText(html);
            var score = ScoreTrustedPage(source, url, title, text, query);
            if (score < 18)
            {
                return null;
            }

            var values = ExtractMarketValuesFromPage(html, text, query);
            double? estimatedValue = values.Count == 0 ? null : RoundValue(values.OrderBy(x => x).ElementAt(values.Count / 2));

            var status = score >= 85
                ? "Verified"
                : score >= 60
                    ? "Likely"
                    : "Fallback";

            var evidenceBuilder = new StringBuilder();
            evidenceBuilder.Append(source.DisplayName);
            if (!string.IsNullOrWhiteSpace(title))
            {
                evidenceBuilder.Append(": " );
                evidenceBuilder.Append(title.Trim());
            }

            if (estimatedValue.HasValue)
            {
                evidenceBuilder.Append($" | Price signal: ${estimatedValue.Value:N0}");
            }

            return new TrustedLookupCandidate
            {
                SourceName = source.DisplayName,
                SourceUrl = url,
                EstimatedValue = estimatedValue,
                Score = score,
                VerificationStatus = status,
                Evidence = evidenceBuilder.ToString()
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> BuildTrustedSourceSearchTerms(RealVehicleQuery query, TrustedSourceDefinition source)
    {
        var baseTerms = new List<string>();
        var core = string.Join(' ', new[] { query.Year?.ToString(), query.Make, query.Model, query.BodyStyle }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(core))
        {
            baseTerms.Add(core);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTitle))
        {
            baseTerms.Add(query.SearchTitle);
        }

        foreach (var term in baseTerms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return $"site:{source.Domain} {term}";
            if (source.SupportsPricing)
            {
                yield return $"site:{source.Domain} {term} price";
                yield return $"site:{source.Domain} {term} value";
            }
        }
    }

    private static bool UrlMatchesTrustedSource(string url, TrustedSourceDefinition source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.Substring(4)
            : uri.Host;

        return host.Equals(source.Domain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + source.Domain, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreTrustedPage(TrustedSourceDefinition source, string url, string? title, string text, RealVehicleQuery query)
    {
        var score = source.TrustWeight;
        var titleText = title ?? string.Empty;

        score += ScoreTextMatch(titleText, query, 3);
        score += ScoreTextMatch(url, query, 2);
        score += ScoreTextMatch(text, query, 1);

        if (source.SupportsPricing && Regex.IsMatch(text, @"\$(\d{1,3}(?:,\d{3})+|\d{4,6})(?:\.\d{2})?"))
        {
            score += 10;
        }

        return Math.Min(score, 100);
    }

    private static int ScoreTextMatch(string haystack, RealVehicleQuery query, int weightMultiplier)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return 0;
        }

        var score = 0;
        if (!string.IsNullOrWhiteSpace(query.Make) && haystack.Contains(query.Make, StringComparison.OrdinalIgnoreCase))
        {
            score += 12 * weightMultiplier;
        }

        if (!string.IsNullOrWhiteSpace(query.Model) && haystack.Contains(query.Model, StringComparison.OrdinalIgnoreCase))
        {
            score += 14 * weightMultiplier;
        }

        if (query.Year.HasValue && haystack.Contains(query.Year.Value.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            score += 7 * weightMultiplier;
        }

        if (!string.IsNullOrWhiteSpace(query.BodyStyle) && haystack.Contains(query.BodyStyle, StringComparison.OrdinalIgnoreCase))
        {
            score += 5 * weightMultiplier;
        }

        return score;
    }

    private static string? ExtractHtmlTitle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = Regex.Match(html, @"<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(match.Groups["title"].Value);
        value = Regex.Replace(value, @"\s+", " " ).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FlattenHtmlToText(string html)
    {
        var decoded = WebUtility.HtmlDecode(html ?? string.Empty);
        var withoutScripts = Regex.Replace(decoded, @"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutScripts = Regex.Replace(withoutScripts, @"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var flattened = Regex.Replace(withoutScripts, "<[^>]+>", " " );
        flattened = Regex.Replace(flattened, @"\s+", " " ).Trim();
        return flattened;
    }

    private static List<double> ExtractMarketValuesFromPage(string html, string text, RealVehicleQuery query)
    {
        var values = new List<double>();
        var content = string.Join(' ', new[] { html ?? string.Empty, text ?? string.Empty });
        foreach (Match match in Regex.Matches(content, @"\$(\d{1,3}(?:,\d{3})+|\d{4,6})(?:\.\d{2})?"))
        {
            if (TryParseUsd(match.Value, out var amount) && IsPlausibleMarketValue(amount, query))
            {
                values.Add(amount);
            }
        }

        return values;
    }

    private static double? EstimateHeuristicValueFromIdentity(RealVehicleQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Make) && string.IsNullOrWhiteSpace(query.Model))
        {
            return null;
        }

        var currentYear = DateTime.UtcNow.Year;
        var age = query.Year.HasValue ? Math.Max(0, currentYear - query.Year.Value) : 10;

        double baseline = 22000;
        if (!string.IsNullOrWhiteSpace(query.BodyStyle))
        {
            if (query.BodyStyle.Contains("truck", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 32000;
            }
            else if (query.BodyStyle.Contains("suv", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 28000;
            }
            else if (query.BodyStyle.Contains("van", StringComparison.OrdinalIgnoreCase) || query.BodyStyle.Contains("wagon", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 24000;
            }
            else if (query.BodyStyle.Contains("coupe", StringComparison.OrdinalIgnoreCase) || query.BodyStyle.Contains("convertible", StringComparison.OrdinalIgnoreCase) || query.BodyStyle.Contains("sports", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 30000;
            }
            else if (query.BodyStyle.Contains("hatch", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 18000;
            }
            else if (query.BodyStyle.Contains("trailer", StringComparison.OrdinalIgnoreCase))
            {
                baseline = 14000;
            }
        }

        var value = baseline * Math.Pow(0.88, Math.Min(age, 20));
        return Math.Clamp(Math.Round(value / 500d) * 500d, 3500d, 120000d);
    }

    private static IReadOnlyList<TrustedSourceDefinition> GetTrustedSources()
        => new[]
        {
            new TrustedSourceDefinition("Wikipedia", "wikipedia.org", trustWeight: 34, supportsPricing: false),
            new TrustedSourceDefinition("NHTSA", "nhtsa.gov", trustWeight: 32, supportsPricing: false),
            new TrustedSourceDefinition("Kelley Blue Book", "kbb.com", trustWeight: 30, supportsPricing: true),
            new TrustedSourceDefinition("Edmunds", "edmunds.com", trustWeight: 28, supportsPricing: true),
            new TrustedSourceDefinition("Cars.com", "cars.com", trustWeight: 26, supportsPricing: true),
            new TrustedSourceDefinition("Car and Driver", "caranddriver.com", trustWeight: 24, supportsPricing: false),
            new TrustedSourceDefinition("MotorTrend", "motortrend.com", trustWeight: 24, supportsPricing: false)
        };

    private static IEnumerable<string> ExtractExternalResultUrls(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, "href=[\"'](?<url>[^\"'#>]+)[\"']", RegexOptions.IgnoreCase))
        {
            var candidate = NormalizeSearchResultUrl(match.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static IEnumerable<string> ExtractDuckDuckGoResultUrls(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, @"href=[""'](?<url>[^""'#>]+)[""']", RegexOptions.IgnoreCase))
        {
            var candidate = NormalizeSearchResultUrl(match.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            results.Add(candidate);
        }

        return results;
    }

    private static string? PickBestExternalResultUrl(IEnumerable<string> urls, RealVehicleQuery query)
    {
        return urls
            .Select(url => new { Url = url, Score = ScoreExternalResultUrl(url, query) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Url.Length)
            .Select(x => x.Url)
            .FirstOrDefault();
    }

    private static int ScoreExternalResultUrl(string url, RealVehicleQuery query)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return 0;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
        var score = 0;

        if (host.Contains("kbb.", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("edmunds.", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("cars.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("caranddriver.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("autoblog.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("motortrend.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("nhtsa.", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace(query.Make) && url.Contains(query.Make, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(query.Model) && url.Contains(query.Model, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (query.Year.HasValue && url.Contains(query.Year.Value.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(query.BodyStyle) && url.Contains(query.BodyStyle, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return score;
    }



    private static string? NormalizeSearchResultUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var candidate = rawUrl.Trim();
        if (candidate.StartsWith("//"))
        {
            candidate = "https:" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (host.Contains("bing.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            var uddg = GetQueryParameter(uri, "uddg") ?? GetQueryParameter(uri, "url") ?? GetQueryParameter(uri, "target");
            if (!string.IsNullOrWhiteSpace(uddg) && Uri.TryCreate(WebUtility.UrlDecode(uddg), UriKind.Absolute, out var redirected))
            {
                return IsSearchEngineHost(redirected.Host) ? null : redirected.AbsoluteUri;
            }

            return null;
        }

        return IsSearchEngineHost(host) ? null : uri.AbsoluteUri;
    }

    private static bool IsSearchEngineHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        return host.Contains("bing.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("google.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("duckduckgo.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("search.yahoo.", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetQueryParameter(Uri uri, string key)
    {
        if (uri == null || string.IsNullOrWhiteSpace(uri.Query) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 0)
            {
                continue;
            }

            if (!string.Equals(WebUtility.UrlDecode(parts[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        return null;
    }

    private static bool IsPlausibleMarketValue(double amount, RealVehicleQuery query)
    {
        if (string.Equals(query.BodyStyle, "Trailer", StringComparison.OrdinalIgnoreCase))
        {
            return amount is >= 500 and <= 250000;
        }

        return amount is >= 1500 and <= 1500000;
    }

    private static bool TryParseUsd(string text, out double amount)
    {
        var cleaned = text.Replace("$", string.Empty).Replace(",", string.Empty).Trim();
        return double.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private static string? GuessModel(string title, string? make)
    {
        var normalized = Regex.Replace(title, @"[^a-zA-Z0-9 ]", " ");
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!string.IsNullOrWhiteSpace(make))
        {
            tokens.RemoveAll(t => string.Equals(t, make, StringComparison.OrdinalIgnoreCase));
        }

        tokens.RemoveAll(t => Regex.IsMatch(t, @"^(19|20)\d{2}$"));
        tokens.RemoveAll(t => t.Length < 2);
        var banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mod", "config", "edition", "custom", "factory", "sport", "base", "sedan", "wagon", "coupe", "truck", "trailer", "utility"
        };
        tokens.RemoveAll(t => banned.Contains(t));
        return tokens.Count == 0 ? null : string.Join(' ', tokens.Take(3));
    }

    private static bool LooksLikeRealVehicle(string value)
        => Regex.IsMatch(value, @"\b(19\d{2}|20\d{2})\b") || value.Any(char.IsDigit);

    private static bool ContainsAny(string value, params string[] parts)
        => parts.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value.ToLowerInvariant(), @"\s+", " ").Trim();

    private static string BuildFamilyCacheKey(int? year, string? make, string? model, string? bodyStyle)
        => NormalizeKey(string.Join(' ', new[] { year?.ToString(), make, model, bodyStyle }.Where(x => !string.IsNullOrWhiteSpace(x))));

    private static double RoundValue(double value)
    {
        if (value < 20000) return Math.Round(value / 250d) * 250d;
        if (value < 100000) return Math.Round(value / 500d) * 500d;
        return Math.Round(value / 1000d) * 1000d;
    }

    private Dictionary<string, PricingCacheEntry> GetCache()
    {
        lock (GlobalCacheGate)
        {
            _cache ??= LoadCache();
            return _cache;
        }
    }

    private static string BuildIdentityCacheKey(string query, IReadOnlyList<string> candidateBrands)
    {
        var brands = (candidateBrands ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return query.Trim() + "||" + string.Join("|", brands);
    }

    private static RealVehicleIdentityResult CloneIdentityResult(RealVehicleIdentityResult source)
        => new()
        {
            Brand = source.Brand,
            Model = source.Model,
            ConfidenceScore = source.ConfidenceScore,
            Evidence = source.Evidence
        };

    private Dictionary<string, PricingCacheEntry> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return new Dictionary<string, PricingCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<Dictionary<string, PricingCacheEntry>>(json) ?? new Dictionary<string, PricingCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, PricingCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCache(bool force = false)
    {
        try
        {
            Dictionary<string, PricingCacheEntry>? snapshot;
            lock (GlobalCacheGate)
            {
                if (_cache == null)
                {
                    return;
                }

                if (!force && _pendingCacheWrites < 10)
                {
                    return;
                }

                snapshot = _cache;
                _pendingCacheWrites = 0;
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // keep non-blocking
        }
    }

    private static HttpResponseMessage SendWithPolicies(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        WaitForHostWindow(host, cancellationToken);
        var response = Http.Send(request, cancellationToken);
        ApplyHostResponsePolicies(host, response);
        return response;
    }

    private static string GetStringWithPolicies(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = SendWithPolicies(request, cancellationToken);
        if ((int)response.StatusCode == 429)
        {
            throw new HttpRequestException("Rate limited", null, response.StatusCode);
        }

        var html = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        if (LooksRateLimitedHtml(html))
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            lock (GlobalCacheGate)
            {
                HostCooldowns[host] = DateTimeOffset.UtcNow.Add(SearchEngineCooldown);
            }
            throw new HttpRequestException("Rate limited content");
        }

        return html;
    }

    private static void WaitForHostWindow(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? wait = null;
            lock (GlobalCacheGate)
            {
                if (HostCooldowns.TryGetValue(host, out var cooldownUntil))
                {
                    var cooldown = cooldownUntil - DateTimeOffset.UtcNow;
                    if (cooldown > TimeSpan.Zero)
                    {
                        wait = cooldown > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : cooldown;
                    }
                    else
                    {
                        HostCooldowns.Remove(host);
                    }
                }

                if (wait == null && HostLastRequestUtc.TryGetValue(host, out var lastRequestUtc))
                {
                    var minGap = host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase) || host.Contains("bing.com", StringComparison.OrdinalIgnoreCase)
                        ? (_bulkAutofillMode ? TimeSpan.FromMilliseconds(1200) : TimeSpan.FromMilliseconds(650))
                        : TimeSpan.FromMilliseconds(200);
                    var since = DateTimeOffset.UtcNow - lastRequestUtc;
                    if (since < minGap)
                    {
                        wait = minGap - since;
                    }
                }

                if (wait == null)
                {
                    HostLastRequestUtc[host] = DateTimeOffset.UtcNow;
                    return;
                }
            }

            if (wait.Value > TimeSpan.Zero)
            {
                Task.Delay(wait.Value, cancellationToken).GetAwaiter().GetResult();
            }
        }
    }

    private static void ApplyHostResponsePolicies(string host, HttpResponseMessage response)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        lock (GlobalCacheGate)
        {
            HostLastRequestUtc[host] = DateTimeOffset.UtcNow;
            if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
            {
                HostCooldowns[host] = DateTimeOffset.UtcNow.Add(SearchEngineCooldown);
            }
        }
    }

    private static bool LooksRateLimitedHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
               || html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
               || html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
               || html.Contains("verify you are human", StringComparison.OrdinalIgnoreCase)
               || html.Contains("detected unusual", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient BuildHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_lookupTimeoutSeconds, 3, 30))
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BeamNGMarketplaceFixer/1.0 (+https://local.app)");
        return client;
    }

    private sealed class PricingCacheEntry
    {
        public double EstimatedValue { get; set; }
        public string Source { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string? Evidence { get; set; }
        public string? SourceUrl { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }

        public RealVehiclePriceResult ToResult() => new()
        {
            EstimatedValue = EstimatedValue,
            Source = Source,
            Confidence = Confidence,
            Evidence = Evidence,
            SourceUrl = SourceUrl
        };

        public static PricingCacheEntry FromResult(RealVehiclePriceResult result, TimeSpan ttl) => new()
        {
            EstimatedValue = result.EstimatedValue,
            Source = result.Source,
            Confidence = result.Confidence,
            Evidence = result.Evidence,
            SourceUrl = result.SourceUrl,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(ttl)
        };
    }
}

public sealed class RealVehiclePriceResult
{
    public double EstimatedValue { get; set; }
    public string Source { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? Evidence { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceName { get; set; }
    public string? VerificationStatus { get; set; }
    public int ConfidenceScore { get; set; }
}

public sealed class RealVehicleQuery
{
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Trim { get; set; }
    public int? Year { get; set; }
    public string? BodyStyle { get; set; }
    public string SearchTitle { get; set; } = string.Empty;
    public string CacheKey { get; set; } = string.Empty;
}


public sealed class RealVehicleIdentityResult
{
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public int ConfidenceScore { get; set; }
    public string? Evidence { get; set; }
}

public sealed class InternetLookupResult
{
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Country { get; set; }
    public string? Type { get; set; }
    public string? BodyStyle { get; set; }
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public double? EstimatedValue { get; set; }
    public int? Population { get; set; }
    public string? InsuranceClass { get; set; }
    public string? Evidence { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceName { get; set; }
    public string? VerificationStatus { get; set; }
    public int ConfidenceScore { get; set; }
}


internal sealed class LookupRecommendation
{
    public double? RecommendedValue { get; set; }
    public int? RecommendedPopulation { get; set; }
}

internal sealed class LookupRuleData
{
    public Dictionary<string, double> BrandTierMultipliers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LookupSegmentRule> Segments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LookupProfileHint> Profiles { get; } = new();
    public Dictionary<string, string> BrandToTier { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class LookupSegmentRule
{
    public double BaseValue { get; set; }
    public int BasePopulation { get; set; }
}

internal sealed class LookupProfileHint
{
    public string? Brand { get; set; }
    public string? Segment { get; set; }
    public string? BrandTier { get; set; }
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ModelAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
}


public sealed class TrustedSourceDefinition
{
    public TrustedSourceDefinition(string displayName, string domain, int trustWeight, bool supportsPricing)
    {
        DisplayName = displayName;
        Domain = domain;
        TrustWeight = trustWeight;
        SupportsPricing = supportsPricing;
    }

    public string DisplayName { get; }
    public string Domain { get; }
    public int TrustWeight { get; }
    public bool SupportsPricing { get; }
}

public sealed class TrustedLookupCandidate
{
    public string SourceName { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public double? EstimatedValue { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "Fallback";
    public int Score { get; set; }
}
