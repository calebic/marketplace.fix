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
    private static readonly string RenamerIgnorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNGMarketplaceConfigEditor",
        "mod-review-ignored.json");

    private static readonly string RenamerHistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNGMarketplaceConfigEditor",
        "mod-review-history.log");

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
        var affectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var renamedEntry in wizard.Entries.Where(x => x.HasQueuedRename).ToList())
        {
            if (TryApplyQueuedZipRename(renamedEntry, out var renamedPath))
            {
                renamedAny = true;
                affectedSources.Add(renamedPath);
                AppendRenameHistory($"RENAME {renamedEntry.OriginalSourcePath} -> {renamedPath}");
            }
        }

        foreach (var ignoredEntry in wizard.IgnoredEntries)
        {
            _renamerIgnoreStore.Ignore(ignoredEntry.Signature);
            ignored++;
            foreach (var item in Configs.Where(x => string.Equals(x.SourcePath, ignoredEntry.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                item.IgnoreFromRenamer = true;
                item.NotifyChanges();
            }
            SaveModMemorySnapshot();
            AppendRenameHistory($"IGNORE {ignoredEntry.SourcePath} :: {ignoredEntry.Reason}");
        }

        foreach (var retriedEntry in wizard.RetriedEntries)
        {
            ApplyReviewInputToSource(retriedEntry);
            retried++;
            affectedSources.Add(retriedEntry.SourcePath);
            AppendRenameHistory($"RETRY {retriedEntry.SourcePath} :: make='{retriedEntry.UserMake}' model='{retriedEntry.UserModel}' years='{retriedEntry.UserYearSummary}'");
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
        foreach (var item in matchingItems)
        {
            item.SourcePath = entry.SourcePath;
            item.ModName = Path.GetFileName(entry.SourcePath);
            item.SourceHintMake = string.IsNullOrWhiteSpace(entry.UserMake) ? null : entry.UserMake.Trim();
            item.SourceHintModel = string.IsNullOrWhiteSpace(entry.UserModel) ? null : entry.UserModel.Trim();
            item.SourceHintYearMin = entry.UserYearMin;
            item.SourceHintYearMax = entry.UserYearMax;
            item.IgnoreFromRenamer = false;
            item.NotifyChanges();
        }
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

            var reason = suspiciousItems
                .Select(x => x.ReviewReason ?? (x.HasMissing ? $"Missing: {x.MissingSummary}" : x.InferenceReason))
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
                Reason = string.Join("  •  ", reason),
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

        return result.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
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

            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamNGMarketplaceConfigEditor", "preview-cache");
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

            await AutoFillAffectedSourcesAsync(affectedSources);
            RefreshAllWorkspaceState();
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
            var dir = Path.GetDirectoryName(RenamerHistoryPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(RenamerHistoryPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }


    private async Task AutoFillAffectedSourcesAsync(HashSet<string> affectedSources)
    {
        var targets = Configs
            .Where(x => affectedSources.Contains(x.SourcePath))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var processedSources = 0;
        foreach (var sourceGroup in targets.GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var sourceItems = sourceGroup.ToList();
            var pendingWrites = new List<PendingConfigWrite>();
            var completedItems = new List<(VehicleConfigItem Item, VehicleInferenceResult Inference, string Detail, string Source)>();
            var modLabel = Path.GetFileName(sourceGroup.Key);
            UpdateAutoFillProgress(processedSources, affectedSources.Count, $"Retrying reviewed mods... {processedSources}/{affectedSources.Count}", $"Starting {modLabel}");

            foreach (var item in sourceItems)
            {
                var itemLabel = DescribeItem(item);
                SetItemAutoFillState(item, "Running", "Retrying", $"Retrying {itemLabel}");
                AppendScrapeLog($"RETRY START {itemLabel}");

                string latestDetail = "Retrying reviewed mod...";
                var inference = await Task.Run(() => _inferenceService.Infer(item, detail =>
                {
                    latestDetail = detail;
                }, CancellationToken.None));

                ApplyInferenceResultToItem(item, inference);
                var json = BuildJsonForItem(item);
                item.Json = json;
                var rendered = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                pendingWrites.Add(new PendingConfigWrite { Item = item, Json = rendered });

                var detail = BuildInferenceProgressDetail(inference);
                var source = string.IsNullOrWhiteSpace(inference.ValueSource) ? "Heuristic/local rules" : inference.ValueSource!;
                completedItems.Add((item, inference, detail, source));
                SetItemAutoFillState(item, "Running", "Queued", latestDetail);
                await Task.Yield();
            }

            WriteConfigBatch(pendingWrites, mirrorToVehicles: true);
            foreach (var completed in completedItems)
            {
                completed.Item.NotifyChanges();
                SetItemAutoFillState(completed.Item, "Completed", completed.Source, completed.Detail);
                AppendScrapeLog($"RETRY SUCCESS {DescribeItem(completed.Item)} :: Source={completed.Source} :: {completed.Detail}");
            }

            processedSources++;
            UpdateAutoFillProgress(processedSources, affectedSources.Count, $"Retrying reviewed mods... {processedSources}/{affectedSources.Count}", $"{modLabel} complete");
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
