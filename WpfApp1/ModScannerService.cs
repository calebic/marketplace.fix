using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace WpfApp1;

public enum ModContentKind
{
    Unknown,
    Vehicle,
    Map,
    Mixed
}

public static class ModContentKindExtensions
{
    public static string ToDisplayLabel(this ModContentKind kind) => kind switch
    {
        ModContentKind.Vehicle => "Vehicle",
        ModContentKind.Map => "Map",
        ModContentKind.Mixed => "Mixed",
        _ => "Unknown"
    };
}

public sealed class ModScanRecord
{
    public string SourcePath { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool IsZip { get; init; }
    public ModContentKind Kind { get; set; }
    public int VehicleConfigCount { get; set; }
    public int VehicleModelCount { get; set; }
    public int MapAssetCount { get; set; }
    public int WarningCount { get; set; }
}

public sealed class ModScanBatchResult
{
    public List<VehicleConfigItem> Items { get; } = new();
    public List<ModScanRecord> Mods { get; } = new();
    public int Errors { get; set; }
}

public static class ModScannerService
{
    private const string DefaultInsuranceClass = "dailyDriver";

    public static ModScanBatchResult Scan(string modsPath, CancellationToken token)
    {
        var result = new ModScanBatchResult();

        foreach (var modDir in EnumerateFolderMods(modsPath))
        {
            token.ThrowIfCancellationRequested();
            ScanFolderMod(modDir, result, token);
        }

        foreach (var zipPath in Directory.EnumerateFiles(modsPath, "*.zip"))
        {
            token.ThrowIfCancellationRequested();
            ScanZipMod(zipPath, result, token);
        }

        result.Mods.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));
        return result;
    }

    private static IEnumerable<string> EnumerateFolderMods(string modsPath)
    {
        foreach (var modDir in Directory.EnumerateDirectories(modsPath))
        {
            var name = Path.GetFileName(modDir);
            if (string.Equals(name, "unpacked", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return modDir;
        }

        var unpackedRoot = Path.Combine(modsPath, "unpacked");
        if (!Directory.Exists(unpackedRoot))
        {
            yield break;
        }

        foreach (var modDir in Directory.EnumerateDirectories(unpackedRoot))
        {
            yield return modDir;
        }
    }

    private static void ScanFolderMod(string modDir, ModScanBatchResult result, CancellationToken token)
    {
        var record = new ModScanRecord
        {
            SourcePath = modDir,
            Label = Path.GetFileName(modDir),
            IsZip = false,
            Kind = ModContentKind.Unknown
        };

        try
        {
            var items = new List<VehicleConfigItem>();
            foreach (var filePath in Directory.EnumerateFiles(modDir, "info_*.json", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (!ConfigJsonService.IsVehicleInfoPath(filePath))
                {
                    continue;
                }

                try
                {
                    var jsonText = File.ReadAllText(filePath);
                    if (ConfigJsonService.TryCreateConfigItem(record.Label, modDir, filePath, false, jsonText, DefaultInsuranceClass, out var item))
                    {
                        items.Add(item);
                    }
                    else
                    {
                        result.Errors++;
                        record.WarningCount++;
                    }
                }
                catch
                {
                    result.Errors++;
                    record.WarningCount++;
                }
            }

            FinalizeRecord(record, items, DetectFolderMapAssetCount(modDir));
            foreach (var item in items)
            {
                item.SourceCategory = record.Kind.ToDisplayLabel();
                result.Items.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result.Errors++;
            record.WarningCount++;
        }

        result.Mods.Add(record);
    }

    private static void ScanZipMod(string zipPath, ModScanBatchResult result, CancellationToken token)
    {
        var record = new ModScanRecord
        {
            SourcePath = zipPath,
            Label = Path.GetFileNameWithoutExtension(zipPath),
            IsZip = true,
            Kind = ModContentKind.Unknown
        };

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var scanContext = new ArchiveScanContext(archive);
            var items = new List<VehicleConfigItem>();
            var mapAssetCount = 0;

            foreach (var entry in scanContext.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(entry.FullName) && LooksLikeMapEntry(entry.FullName))
                {
                    mapAssetCount++;
                }

                if (!ConfigJsonService.IsVehicleInfoEntry(entry.FullName))
                {
                    continue;
                }

                try
                {
                    var jsonText = scanContext.ReadText(entry.FullName);
                    if (string.IsNullOrWhiteSpace(jsonText))
                    {
                        result.Errors++;
                        record.WarningCount++;
                        continue;
                    }

                    if (ConfigJsonService.TryCreateConfigItem(record.Label, zipPath, entry.FullName, true, jsonText, DefaultInsuranceClass, out var item, scanContext))
                    {
                        items.Add(item);
                    }
                    else
                    {
                        result.Errors++;
                        record.WarningCount++;
                    }
                }
                catch
                {
                    result.Errors++;
                    record.WarningCount++;
                }
            }

            FinalizeRecord(record, items, mapAssetCount);
            foreach (var item in items)
            {
                item.SourceCategory = record.Kind.ToDisplayLabel();
                result.Items.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result.Errors++;
            record.WarningCount++;
        }

        result.Mods.Add(record);
    }

    private static void FinalizeRecord(ModScanRecord record, List<VehicleConfigItem> items, int mapAssetCount)
    {
        record.VehicleConfigCount = items.Count;
        record.VehicleModelCount = items.Select(x => x.ModelKey).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        record.MapAssetCount = mapAssetCount;

        var hasVehicle = record.VehicleConfigCount > 0;
        var hasMap = mapAssetCount > 0 || items.Any(x => x.IsMapMod || string.Equals(x.ContentCategory, "Map", StringComparison.OrdinalIgnoreCase));

        record.Kind = hasVehicle && hasMap
            ? ModContentKind.Mixed
            : hasMap
                ? ModContentKind.Map
                : hasVehicle
                    ? ModContentKind.Vehicle
                    : ModContentKind.Unknown;

        foreach (var item in items)
        {
            if (record.Kind == ModContentKind.Map)
            {
                item.IsMapMod = true;
                item.ContentCategory = "Map";
            }
        }
    }

    private static int DetectFolderMapAssetCount(string modDir)
    {
        try
        {
            var count = 0;
            if (Directory.Exists(Path.Combine(modDir, "levels")))
            {
                count += Directory.EnumerateFiles(Path.Combine(modDir, "levels"), "*", SearchOption.AllDirectories)
                    .Count(path => LooksLikeMapEntry(path.Replace('\\', '/')));
            }

            foreach (var marker in Directory.EnumerateFiles(modDir, "main.level.json", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrWhiteSpace(marker))
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool LooksLikeMapEntry(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.Contains("/levels/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("levels/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".mis", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".ter", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".forest4.json", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".items.json", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("main.level.json", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
