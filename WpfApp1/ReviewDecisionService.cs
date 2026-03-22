using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1;

public sealed class ReviewAssessment
{
    public bool ShouldHold { get; init; }
    public string Category { get; init; } = "None";
    public int Priority { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? ConflictSummary { get; init; }
}

public static class ReviewDecisionService
{
    public static ReviewAssessment Analyze(VehicleConfigItem item, VehicleInferenceResult inference, bool holdWeakMatchesForReview)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (inference == null) throw new ArgumentNullException(nameof(inference));

        if (item.IsMapMod || inference.IsMapMod)
        {
            return new ReviewAssessment
            {
                ShouldHold = false,
                Category = "Map",
                Priority = 0,
                Summary = "Map content detected; vehicle review is not needed."
            };
        }

        var reasons = new List<string>();
        var conflictReasons = new List<string>();
        var priority = 0;
        var category = "None";

        void PromoteCategory(string next)
        {
            var rank = CategoryRank(next);
            if (rank > CategoryRank(category))
            {
                category = next;
            }
        }

        var missingBrand = string.IsNullOrWhiteSpace(inference.Brand);
        var missingModel = string.IsNullOrWhiteSpace(inference.Model);
        var missingIdentity = missingBrand || missingModel;
        if (missingIdentity)
        {
            PromoteCategory("Missing identity");
            priority += missingBrand && missingModel ? 42 : 30;
            reasons.Add(missingBrand && missingModel
                ? "Both make and model are still missing after inference."
                : missingBrand
                    ? "Make is still missing after inference."
                    : "Model is still missing after inference.");
        }

        var confidence = inference.ConfidenceScore;
        if (confidence < 45)
        {
            PromoteCategory("Weak evidence");
            priority += 32;
            reasons.Add($"Confidence stayed very low at {confidence}.");
        }
        else if (confidence < 60)
        {
            PromoteCategory("Weak evidence");
            priority += 22;
            reasons.Add($"Confidence stayed low at {confidence}.");
        }
        else if (confidence < 75)
        {
            priority += 10;
            reasons.Add($"Confidence is only moderate at {confidence}.");
        }

        if (string.Equals(inference.ConfidenceTier, "Fallback", StringComparison.OrdinalIgnoreCase))
        {
            PromoteCategory("Weak evidence");
            priority += 14;
            reasons.Add("The match is relying on fallback evidence instead of a strong verified identity.");
        }

        if (inference.IsSuspicious)
        {
            PromoteCategory("Identity conflict");
            priority += 24;
            if (!string.IsNullOrWhiteSpace(inference.SuspicionReason))
            {
                reasons.Add(inference.SuspicionReason!);
            }
        }

        if (BrandsConflict(item.VehicleInfoBrand, inference.Brand))
        {
            PromoteCategory("Identity conflict");
            priority += 30;
            var text = $"vehicle info points to {item.VehicleInfoBrand}, but the selected match points to {inference.Brand}.";
            conflictReasons.Add(text);
            reasons.Add(text);
        }

        if (BrandsConflict(item.SourceHintMake, inference.Brand))
        {
            PromoteCategory("Identity conflict");
            priority += 32;
            var text = $"your review hint points to {item.SourceHintMake}, but the current match points to {inference.Brand}.";
            conflictReasons.Add(text);
            reasons.Add(text);
        }

        if (TextConflict(item.SourceHintModel, inference.Model))
        {
            PromoteCategory("Identity conflict");
            priority += 18;
            var text = $"your review hint model ({item.SourceHintModel}) does not line up with the current match ({inference.Model}).";
            conflictReasons.Add(text);
            reasons.Add(text);
        }

        if (YearConflict(item.SourceHintYearMin, item.SourceHintYearMax, inference.YearMin, inference.YearMax))
        {
            PromoteCategory("Year conflict");
            priority += 16;
            var text = $"the inferred years ({FormatYearRange(inference.YearMin, inference.YearMax)}) do not line up with your review hint years ({FormatYearRange(item.SourceHintYearMin, item.SourceHintYearMax)}).";
            conflictReasons.Add(text);
            reasons.Add(text);
        }
        else if (YearConflict(item.VehicleInfoYearMin, item.VehicleInfoYearMax, inference.YearMin, inference.YearMax))
        {
            PromoteCategory("Year conflict");
            priority += 14;
            var text = $"vehicle metadata years ({FormatYearRange(item.VehicleInfoYearMin, item.VehicleInfoYearMax)}) disagree with the current match years ({FormatYearRange(inference.YearMin, inference.YearMax)}).";
            conflictReasons.Add(text);
            reasons.Add(text);
        }

        if (TextConflict(item.VehicleInfoBodyStyle, inference.BodyStyle))
        {
            PromoteCategory("Metadata conflict");
            priority += 10;
            reasons.Add($"vehicle metadata body style ({item.VehicleInfoBodyStyle}) does not line up with the inferred body style ({inference.BodyStyle}).");
        }

        var missingValue = !inference.Value.HasValue || inference.Value.Value <= 0;
        var missingPopulation = !inference.Population.HasValue || inference.Population.Value <= 0;
        if (missingValue || missingPopulation)
        {
            PromoteCategory(category == "None" ? "Value uncertainty" : category);
            priority += missingValue && missingPopulation ? 12 : 7;
            if (missingValue && missingPopulation)
            {
                reasons.Add("Price and population both stayed weak or missing.");
            }
            else if (missingValue)
            {
                reasons.Add("Price stayed weak or missing.");
            }
            else
            {
                reasons.Add("Population stayed weak or missing.");
            }
        }

        if (!string.IsNullOrWhiteSpace(item.ReviewReason) && item.ReviewReason!.Contains("Missing", StringComparison.OrdinalIgnoreCase))
        {
            priority += 4;
        }

        priority = Math.Max(0, Math.Min(100, priority));

        var identityConflict = string.Equals(category, "Identity conflict", StringComparison.OrdinalIgnoreCase)
            || conflictReasons.Count > 0;
        var onlyValueUncertainty = !identityConflict && !missingIdentity && !inference.IsSuspicious && confidence >= 78 && (missingValue || missingPopulation);

        bool shouldHold;
        if (!holdWeakMatchesForReview)
        {
            shouldHold = identityConflict || (missingIdentity && confidence < 70);
        }
        else if (item.HasSourceHints)
        {
            shouldHold = identityConflict
                || missingIdentity
                || confidence < 50
                || (inference.IsSuspicious && priority >= 45)
                || (string.Equals(inference.ConfidenceTier, "Fallback", StringComparison.OrdinalIgnoreCase) && priority >= 55);
        }
        else
        {
            shouldHold = identityConflict
                || missingIdentity
                || inference.IsSuspicious
                || confidence < 60
                || string.Equals(inference.ConfidenceTier, "Fallback", StringComparison.OrdinalIgnoreCase)
                || (!onlyValueUncertainty && (missingValue || missingPopulation) && confidence < 68);
        }

        if (onlyValueUncertainty)
        {
            shouldHold = false;
        }

        if (string.Equals(category, "None", StringComparison.OrdinalIgnoreCase) && shouldHold)
        {
            category = "Weak evidence";
        }

        var orderedReasons = reasons
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeSentence)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var summary = orderedReasons.Count == 0
            ? (shouldHold ? "Held for review because the match is still not trustworthy enough." : "Decision quality looks stable.")
            : string.Join(" ", orderedReasons);

        return new ReviewAssessment
        {
            ShouldHold = shouldHold,
            Category = category,
            Priority = priority,
            Summary = summary,
            ConflictSummary = conflictReasons.Count == 0
                ? null
                : string.Join(" ", conflictReasons.Select(NormalizeSentence).Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static int CategoryRank(string category) => category switch
    {
        "Identity conflict" => 6,
        "Missing identity" => 5,
        "Year conflict" => 4,
        "Metadata conflict" => 3,
        "Weak evidence" => 2,
        "Value uncertainty" => 1,
        _ => 0
    };

    private static bool BrandsConflict(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TextConflict(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeLoose(left);
        var normalizedRight = NormalizeLoose(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && !string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static bool YearConflict(int? expectedMin, int? expectedMax, int? actualMin, int? actualMax)
    {
        if ((!expectedMin.HasValue && !expectedMax.HasValue) || (!actualMin.HasValue && !actualMax.HasValue))
        {
            return false;
        }

        var leftMin = expectedMin ?? expectedMax ?? 0;
        var leftMax = expectedMax ?? expectedMin ?? 9999;
        var rightMin = actualMin ?? actualMax ?? 0;
        var rightMax = actualMax ?? actualMin ?? 9999;
        return rightMax < leftMin || rightMin > leftMax;
    }

    private static string FormatYearRange(int? min, int? max)
    {
        if (!min.HasValue && !max.HasValue) return "n/a";
        if (min.HasValue && max.HasValue && min.Value != max.Value) return $"{min}-{max}";
        return (min ?? max)?.ToString() ?? "n/a";
    }

    private static string NormalizeSentence(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }
}
