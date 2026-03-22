using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.IO.Compression;

namespace WpfApp1;

public partial class MainWindow
{
    private static readonly string RenamerIgnorePath = AppPaths.ReviewIgnorePath;

    private static readonly string RenamerHistoryPath = AppPaths.ReviewHistoryPath;

    private readonly RenamerIgnoreStore _renamerIgnoreStore = new(RenamerIgnorePath);

    private async Task<RenamerReviewOutcome> RunRenamerWizardIfNeededAsync()
    {
        var entries = BuildRenamerEntries();
        if (entries.Count == 0)
        {
            return default;
        }

        var wizard = new RenamerWizardWindow(entries)
        {
            Owner = this
        };

        wizard.ShowDialog();

        var reviewed = wizard.ReviewedEntries.Count;
        var retried = 0;
        var ignored = 0;
        var renamedAny = false;
        var persistenceDirty = false;
        var affectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var renamedEntry in wizard.Entries.Where(x => x.HasQueuedRename).ToList())
        {
            if (TryApplyQueuedZipRename(renamedEntry, out var renamedPath))
            {
                renamedAny = true;
                persistenceDirty = true;
                affectedSources.Add(renamedPath);
                AppendRenameHistory($"RENAME {renamedEntry.OriginalSourcePath} -> {renamedPath}");
            }
        }

        foreach (var ignoredEntry in wizard.IgnoredEntries)
        {
            _renamerIgnoreStore.Ignore(ignoredEntry.Signature);
            ignored++;
            persistenceDirty = true;
            foreach (var item in Configs.Where(x => string.Equals(x.SourcePath, ignoredEntry.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                item.IgnoreFromRenamer = true;
                item.LastAutoFillStatus = "Ignored";
                item.LastAutoFillSource = "Review input";
                item.LastAutoFillDetail = string.IsNullOrWhiteSpace(ignoredEntry.Reason)
                    ? "Ignored from future review/retry passes by user choice."
                    : ignoredEntry.Reason;
                item.DecisionOrigin = "Review input";
                item.LastDecisionUtc = DateTime.UtcNow;
                item.NotifyChanges();
            }

            AppendRenameHistory($"IGNORE {ignoredEntry.SourcePath} :: {ignoredEntry.Reason}");
        }

        foreach (var retriedEntry in wizard.RetriedEntries)
        {
            ApplyReviewInputToSource(retriedEntry);
            retried++;
            persistenceDirty = true;
            affectedSources.Add(retriedEntry.SourcePath);
            AppendRenameHistory($"RETRY {retriedEntry.SourcePath} :: make='{retriedEntry.UserMake}' model='{retriedEntry.UserModel}' years='{retriedEntry.UserYearSummary}'");
        }

        if (persistenceDirty)
        {
            SaveModMemorySnapshot();
        }

        var rescanned = false;
        if (renamedAny && retried == 0)
        {
            RefreshAllWorkspaceState();
        }

        if (retried > 0)
        {
            rescanned = await RescanAfterRenamerAsync(affectedSources);
        }

        return new RenamerReviewOutcome(reviewed, retried, ignored, rescanned);
    }

    private void ApplyReviewInputToSource(SourceReviewEntry entry)
    {
        var oldPath = string.IsNullOrWhiteSpace(entry.OriginalSourcePath) ? entry.SourcePath : entry.OriginalSourcePath;
        var matchingItems = Configs.Where(x =>
            string.Equals(x.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.SourcePath, oldPath, StringComparison.OrdinalIgnoreCase)).ToList();

        var reviewSummary = BuildReviewInputSummary(entry);
        foreach (var item in matchingItems)
        {
            item.SourcePath = entry.SourcePath;
            item.ModName = Path.GetFileName(entry.SourcePath);
            item.SourceHintMake = string.IsNullOrWhiteSpace(entry.UserMake) ? null : entry.UserMake.Trim();
            item.SourceHintModel = string.IsNullOrWhiteSpace(entry.UserModel) ? null : entry.UserModel.Trim();
            item.SourceHintYearMin = entry.UserYearMin;
            item.SourceHintYearMax = entry.UserYearMax;
            item.IgnoreFromRenamer = false;
            item.LastAutoFillStatus = "Queued for retry";
            item.LastAutoFillSource = "Review input";
            item.LastAutoFillDetail = reviewSummary;
            item.ReviewReason = reviewSummary;
            item.DecisionOrigin = "Review input";
            item.LastDecisionUtc = DateTime.UtcNow;
            item.NotifyChanges();
        }
    }


    private static string BuildReviewInputSummary(SourceReviewEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.UserMake))
        {
            parts.Add($"make={entry.UserMake.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(entry.UserModel))
        {
            parts.Add($"model={entry.UserModel.Trim()}");
        }

        if (entry.UserYearMin.HasValue || entry.UserYearMax.HasValue)
        {
            parts.Add($"years={entry.UserYearSummary}");
        }

        return parts.Count == 0
            ? "Queued for retry using the latest review decision."
            : $"Queued for retry with review input: {string.Join(", ", parts)}";
    }


    private bool TryApplyQueuedZipRename(SourceReviewEntry entry, out string renamedPath)
    {
        renamedPath = entry.SourcePath;
        if (!entry.IsZip || !entry.HasQueuedRename)
        {
            return false;
        }

        try
        {
            var sourceDir = Path.GetDirectoryName(entry.SourcePath);
            if (string.IsNullOrWhiteSpace(sourceDir) || !File.Exists(entry.SourcePath))
            {
                return false;
            }

            var safeName = SanitizeFileName(entry.PendingZipName.Trim());
            if (string.IsNullOrWhiteSpace(safeName))
            {
                return false;
            }

            var targetPath = Path.Combine(sourceDir, safeName + ".zip");
            if (string.Equals(targetPath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (File.Exists(targetPath))
            {
                return false;
            }

            File.Move(entry.SourcePath, targetPath);
            var oldPath = entry.SourcePath;
            entry.SourcePath = targetPath;
            entry.DisplayName = Path.GetFileName(targetPath);
            entry.PreviewImagePath = TryResolveModPreviewImage(targetPath, null, true);

            foreach (var item in Configs.Where(x => string.Equals(x.SourcePath, oldPath, StringComparison.OrdinalIgnoreCase)))
            {
                item.SourcePath = targetPath;
                item.ModName = Path.GetFileName(targetPath);
                item.NotifyChanges();
            }

            renamedPath = targetPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return cleaned;
    }

    private List<SourceReviewEntry> BuildRenamerEntries()
    {
        var grouped = Configs
            .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<SourceReviewEntry>();
        foreach (var group in grouped)
        {
            var first = group.First();
            var signature = CreateSourceSignature(first.SourcePath, first.IsZip);
            if (_renamerIgnoreStore.IsIgnored(signature))
            {
                foreach (var item in group)
                {
                    item.IgnoreFromRenamer = true;
                    item.NotifyChanges();
                }
                continue;
            }

            var suspiciousItems = group.Where(ShouldIncludeInSourceReview).ToList();
            if (suspiciousItems.Count == 0)
            {
                continue;
            }

            var aggregatedPriority = suspiciousItems.Max(x => x.ReviewPriority);
            var leadingItem = suspiciousItems
                .OrderByDescending(x => x.ReviewSortScore)
                .ThenByDescending(x => x.ReviewPriority)
                .ThenByDescending(x => x.IsSuspicious)
                .First();
            var category = leadingItem.ReviewCategory ?? string.Empty;

            var reason = suspiciousItems
                .Select(x => x.ReviewReason ?? x.ReviewConflictSummary ?? (x.HasMissing ? $"Missing: {x.MissingSummary}" : x.InferenceReason))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            var (makeHint, modelHint, minYear, maxYear) = BuildSuggestedReviewInput(suspiciousItems);

            var entry = new SourceReviewEntry
            {
                OriginalSourcePath = first.SourcePath,
                SourcePath = first.SourcePath,
                IsZip = first.IsZip,
                DisplayName = Path.GetFileName(first.SourcePath),
                DirectoryPath = Path.GetDirectoryName(first.SourcePath) ?? string.Empty,
                Extension = first.IsZip ? Path.GetExtension(first.SourcePath) : string.Empty,
                Reason = string.IsNullOrWhiteSpace(category)
                    ? string.Join("  •  ", reason)
                    : $"{category} (priority {aggregatedPriority})  •  {string.Join("  •  ", reason)}",
                ReviewCategory = category,
                ReviewPriority = aggregatedPriority,
                Signature = signature,
                PreviewImagePath = TryResolveModPreviewImage(first.SourcePath, first.InfoPath, first.IsZip),
                PendingZipName = first.IsZip ? Path.GetFileNameWithoutExtension(first.SourcePath) : string.Empty,
                UserMake = makeHint,
                UserModel = modelHint,
                UserYearMinText = minYear?.ToString() ?? string.Empty,
                UserYearMaxText = maxYear?.ToString() ?? string.Empty
            };

            result.Add(entry);
        }

        return result.OrderByDescending(x => x.ReviewPriority).ThenByDescending(x => !string.IsNullOrWhiteSpace(x.UserMake) || !string.IsNullOrWhiteSpace(x.UserModel)).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }


    private static string? TryResolveModPreviewImage(string modPath, string? infoPath, bool isZip)
    {
        try
        {
            if (!isZip)
            {
                if (!string.IsNullOrWhiteSpace(infoPath))
                {
                    var infoDir = Path.GetDirectoryName(infoPath);
                    var png = TryGetPreferredPreviewFromDirectory(infoDir);
                    if (!string.IsNullOrWhiteSpace(png))
                    {
                        return png;
                    }
                }

                if (Directory.Exists(modPath))
                {
                    return TryGetPreferredPreviewFromDirectory(modPath, recursive: true);
                }

                return null;
            }

            if (!File.Exists(modPath))
            {
                return null;
            }

            using var archive = ZipFile.OpenRead(modPath);

            static string NormalizeArchivePath(string value) => value.Replace('\\', '/').TrimStart('/');

            static bool IsPngEntry(ZipArchiveEntry entry) =>
                !string.IsNullOrWhiteSpace(entry.Name) &&
                entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith("/", StringComparison.OrdinalIgnoreCase);

            static int ScoreEntry(ZipArchiveEntry entry, string? normalizedInfo)
            {
                var full = NormalizeArchivePath(entry.FullName);
                var fileName = Path.GetFileName(full);
                var score = string.Equals(fileName, "default.png", StringComparison.OrdinalIgnoreCase) ? 500 : 200;
                if (full.Contains("/vehicles/", StringComparison.OrdinalIgnoreCase)) score += 50;
                if (!string.IsNullOrWhiteSpace(normalizedInfo))
                {
                    var infoDir = Path.GetDirectoryName(normalizedInfo!)?.Replace('\\', '/');
                    var parentDir = string.IsNullOrWhiteSpace(infoDir) ? null : Path.GetDirectoryName(infoDir)?.Replace('\\', '/');
                    if (!string.IsNullOrWhiteSpace(infoDir) && full.StartsWith(infoDir + "/", StringComparison.OrdinalIgnoreCase)) score += 250;
                    if (!string.IsNullOrWhiteSpace(parentDir) && full.StartsWith(parentDir + "/", StringComparison.OrdinalIgnoreCase)) score += 100;
                }
                return score;
            }

            var normalizedInfo = string.IsNullOrWhiteSpace(infoPath) ? null : NormalizeArchivePath(infoPath!);
            var entry = archive.Entries
                .Where(IsPngEntry)
                .OrderByDescending(e => ScoreEntry(e, normalizedInfo))
                .ThenBy(e => e.FullName.Length)
                .FirstOrDefault();

            if (entry == null)
            {
                return null;
            }

            var cacheDir = AppPaths.PreviewCacheRoot;
            Directory.CreateDirectory(cacheDir);
            var cacheName = $"{CreateSourceSignature(modPath, true)}_{Path.GetFileName(entry.FullName)}";
            var cachePath = Path.Combine(cacheDir, cacheName);
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length != entry.Length)
            {
                using var entryStream = entry.Open();
                using var output = File.Create(cachePath);
                entryStream.CopyTo(output);
            }

            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPreferredPreviewFromDirectory(string? directoryPath, bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directoryPath, "*.png", searchOption)
            .OrderByDescending(path => string.Equals(Path.GetFileName(path), "default.png", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private async Task<bool> RescanAfterRenamerAsync(HashSet<string> affectedSources)
    {
        if (affectedSources.Count == 0)
        {
            return false;
        }

        try
        {
            RealVehiclePricingService.SetBulkAutofillMode(true);
            StatusTextBlock.Text = "Retrying reviewed mods...";
            StatusDetailTextBlock.Text = "Re-running inference with your review input and refreshing the workspace.";
            BeginAutoFillProgress(affectedSources.Count, "Retrying reviewed mods...");

            _refreshCoordinator.BeginDeferredRefresh();
            try
            {
                await AutoFillAffectedSourcesAsync(affectedSources);
                _refreshCoordinator.RequestRefresh(persist: true);
            }
            finally
            {
                _refreshCoordinator.EndDeferredRefresh(defaultPersist: true);
            }
            EndAutoFillProgress($"Review retry complete for {affectedSources.Count} mod(s).");
            StatusTextBlock.Text = $"Review retry complete for {affectedSources.Count} mod(s).";
            StatusDetailTextBlock.Text = "Reviewed mods were refreshed with your latest input.";
            return true;
        }
        catch (Exception ex)
        {
            EndAutoFillProgress("Review retry failed.", ex.Message);
            StatusTextBlock.Text = "Review retry failed.";
            StatusDetailTextBlock.Text = ex.Message;
            return false;
        }
        finally
        {
            RealVehiclePricingService.SetBulkAutofillMode(false);
        }
    }

    private static string CreateSourceSignature(string path, bool isZip)
    {
        try
        {
            string raw;
            if (isZip)
            {
                var fileInfo = new FileInfo(path);
                raw = $"zip|{Path.GetFullPath(path).ToLowerInvariant()}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
            }
            else
            {
                var dirInfo = new DirectoryInfo(path);
                raw = $"dir|{Path.GetFullPath(path).ToLowerInvariant()}|{dirInfo.LastWriteTimeUtc.Ticks}";
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }
        catch
        {
            return path;
        }
    }

    private static void AppendRenameHistory(string line)
    {
        try
        {
            AppPaths.EnsureParentDirectory(RenamerHistoryPath);
            File.AppendAllText(RenamerHistoryPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppPaths.AppendStateLog("review-history-save", ex.Message);
        }
    }


    private async Task AutoFillAffectedSourcesAsync(HashSet<string> affectedSources)
    {
        var targets = Configs
            .Where(x => affectedSources.Contains(x.SourcePath) && !x.IgnoreFromRenamer)
            .OrderByDescending(x => x.HasSourceHints)
            .ThenByDescending(x => x.ReviewSortScore)
            .ThenByDescending(x => x.ReviewPriority)
            .ToList();

        if (targets.Count == 0)
        {
            AppendScrapeLog("RETRY skipped because no eligible reviewed configs remained after ignore filtering.");
            return;
        }

        var lastReportedProcessed = -1;
        var batchResult = await _autoFillWorkflowService.RunBatchAsync(
            targets,
            mirrorToVehicles: true,
            CancellationToken.None,
            progress =>
            {
                var force = progress.Processed >= progress.Total || progress.Processed >= lastReportedProcessed + 4;
                if (!ShouldPushAutoFillUiProgress(force))
                {
                    return;
                }

                lastReportedProcessed = progress.Processed;
                UpdateAutoFillProgress(progress.Processed, progress.Total, progress.Status.Replace("Auto-filling configs", "Retrying reviewed mods"), progress.Detail);
            },
            operationLabel: "Retrying reviewed mods");

        AppendScrapeLog($"RETRY processed={batchResult.Processed} saved={batchResult.Saved} failed={batchResult.Failed} local={batchResult.LocalOnlyMatches} online={batchResult.OnlineMatches}");
        if (batchResult.HeldForReview > 0)
        {
            AppendScrapeLog($"RETRY HELD {batchResult.HeldForReview} config(s) for further review.");
        }

        _configsView?.Refresh();
        if (_selected != null)
        {
            LoadForm(_selected);
            SetDirty(false);
        }
    }

    private static (string make, string model, int? yearMin, int? yearMax) BuildSuggestedReviewInput(IReadOnlyCollection<VehicleConfigItem> items)
    {
        string make = items
            .Select(x => x.SourceHintMake ?? x.Brand)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

        string model = items
            .Select(x => x.SourceHintModel ?? x.VehicleName ?? x.Configuration)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !LooksTooGenericModel(x!))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

        int? yearMin = items.Select(x => x.SourceHintYearMin ?? x.YearMin).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Min();
        int? yearMax = items.Select(x => x.SourceHintYearMax ?? x.YearMax).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
        if (yearMin == 0) yearMin = null;
        if (yearMax == 0) yearMax = null;
        return (make, model, yearMin, yearMax);
    }

    private static bool LooksTooGenericModel(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "base", "custom", "sport", "police", "drag", "drift", "factory", "config", "sedan", "wagon", "coupe"
        };

        return generic.Contains(normalized);
    }

    private static bool ShouldIncludeInSourceReview(VehicleConfigItem item)
    {
        if (item.IgnoreFromRenamer)
        {
            return false;
        }

        return item.NeedsReview;
    }

}
