using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfApp1;

public sealed class ArchiveScanContext
{
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly Dictionary<string, JsonNode?> _parsedJsonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _licensePlateCache = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveScanContext(ZipArchive archive)
    {
        _entries = archive.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.FullName))
            .GroupBy(e => NormalizeArchivePath(e.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<ZipArchiveEntry> Entries => _entries.Values;

    public bool Contains(string path) => _entries.ContainsKey(NormalizeArchivePath(path));

    public string? ReadText(string path)
    {
        if (!_entries.TryGetValue(NormalizeArchivePath(path), out var entry))
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public JsonNode? ReadJson(string path)
    {
        var normalized = NormalizeArchivePath(path);
        if (_parsedJsonCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var parsed = ConfigJsonService.ParseJson(ReadText(path) ?? string.Empty);
        _parsedJsonCache[normalized] = parsed;
        return parsed;
    }

    public string? ReadLicensePlate(string path)
    {
        var normalized = NormalizeArchivePath(path);
        if (_licensePlateCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var jsonText = ReadText(path);
        var value = jsonText == null ? null : ConfigJsonService.ReadString(ConfigJsonService.ParseJson(jsonText), "licenseName");
        _licensePlateCache[normalized] = value;
        return value;
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
}

public static class ConfigJsonService
{
    public static JsonNode? ParseJson(string jsonText)
    {
        var options = new JsonNodeOptions();
        var documentOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            return JsonNode.Parse(jsonText, nodeOptions: options, documentOptions: documentOptions);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsVehicleInfoPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var idx = normalized.IndexOf("\\vehicles\\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        return fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVehicleInfoEntry(string entryName)
    {
        if (!entryName.StartsWith("vehicles/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(entryName);
        return fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase)
               && entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryCreateConfigItem(
        string modName,
        string sourcePath,
        string infoPath,
        bool isZip,
        string jsonText,
        string defaultInsuranceClass,
        out VehicleConfigItem item,
        ArchiveScanContext? archiveScanContext = null)
    {
        item = new VehicleConfigItem();

        if (!TryExtractModelAndConfig(infoPath, isZip, out var modelKey, out var configKey))
        {
            return false;
        }

        var root = ParseJson(jsonText);
        if (root == null)
        {
            return false;
        }

        var vehicleInfoPath = BuildVehicleInfoPath(infoPath, isZip);
        var vehicleRoot = TryLoadVehicleInfoRoot(sourcePath, vehicleInfoPath, isZip, archiveScanContext);

        var configPcPath = BuildConfigPcPath(infoPath, configKey);
        var configPcExists = ConfigPcExists(sourcePath, configPcPath, isZip, archiveScanContext);
        var currentLicensePlate = TryReadLicensePlate(sourcePath, configPcPath, isZip, archiveScanContext);

        item = new VehicleConfigItem
        {
            ModName = modName,
            SourcePath = sourcePath,
            InfoPath = infoPath,
            IsZip = isZip,
            ModelKey = modelKey,
            ConfigKey = configKey,
            ConfigPcPath = configPcPath,
            HasConfigPc = configPcExists,
            CurrentLicensePlate = currentLicensePlate,
            Json = root,
            VehicleInfoJson = vehicleRoot,
            VehicleInfoPath = vehicleInfoPath
        };

        UpdateItemFromJson(item, root, defaultInsuranceClass, vehicleRoot);
        item.NotifyChanges();
        return true;
    }

    public static void UpdateItemFromJson(VehicleConfigItem item, JsonNode root, string defaultInsuranceClass, JsonNode? vehicleRoot = null)
    {
        if (vehicleRoot != null)
        {
            item.VehicleInfoJson = vehicleRoot;
        }

        var effectiveVehicleRoot = vehicleRoot ?? item.VehicleInfoJson;
        item.VehicleInfoName = ReadString(effectiveVehicleRoot, "Name");
        item.VehicleInfoBrand = ReadStringOrAggregate(effectiveVehicleRoot, "Brand");
        item.VehicleInfoCountry = ReadStringOrAggregate(effectiveVehicleRoot, "Country");
        item.VehicleInfoType = ReadStringOrAggregate(effectiveVehicleRoot, "Type");
        item.VehicleInfoBodyStyle = ReadStringOrAggregate(effectiveVehicleRoot, "Body Style");

        var vehicleYears = ReadYears(effectiveVehicleRoot);
        item.VehicleInfoYearMin = vehicleYears.min;
        item.VehicleInfoYearMax = vehicleYears.max;

        item.VehicleName = ReadString(root, "Name") ?? item.VehicleInfoName ?? ReadString(root, "Configuration") ?? item.ConfigKey;
        item.Brand = ReadStringOrAggregate(root, "Brand") ?? item.VehicleInfoBrand;
        item.Country = ReadStringOrAggregate(root, "Country") ?? item.VehicleInfoCountry;
        item.Type = ReadStringOrAggregate(root, "Type") ?? item.VehicleInfoType;
        item.BodyStyle = ReadStringOrAggregate(root, "Body Style") ?? item.VehicleInfoBodyStyle;
        item.ConfigType = ReadStringOrAggregate(root, "Config Type");
        item.Configuration = ReadString(root, "Configuration");
        item.InsuranceClass = ReadStringOrAggregate(root, "InsuranceClass") ?? defaultInsuranceClass;

        var years = ReadYears(root);
        item.YearMin = years.min ?? vehicleYears.min;
        item.YearMax = years.max ?? vehicleYears.max;

        item.Value = ReadDouble(root, "Value");
        item.Population = ReadInt(root, "Population");
    }

    public static string BuildVehicleInfoPath(string configInfoPath, bool isZip)
    {
        if (isZip)
        {
            var normalized = configInfoPath.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(0, slash + 1) + "info.json" : "info.json";
        }

        var dir = Path.GetDirectoryName(configInfoPath) ?? string.Empty;
        return Path.Combine(dir, "info.json");
    }

    public static string BuildConfigPcPath(string configInfoPath, string configKey)
    {
        var dir = Path.GetDirectoryName(configInfoPath) ?? string.Empty;
        return string.IsNullOrWhiteSpace(dir)
            ? $"{configKey}.pc"
            : Path.Combine(dir, $"{configKey}.pc").Replace('\\', '/');
    }

    public static JsonNode? TryLoadVehicleInfoRoot(string sourcePath, string vehicleInfoPath, bool isZip, ArchiveScanContext? archiveScanContext = null)
    {
        try
        {
            if (!isZip)
            {
                return File.Exists(vehicleInfoPath) ? ParseJson(File.ReadAllText(vehicleInfoPath)) : null;
            }

            return archiveScanContext?.ReadJson(vehicleInfoPath);
        }
        catch
        {
            return null;
        }
    }

    public static bool ConfigPcExists(string sourcePath, string configPcPath, bool isZip, ArchiveScanContext? archiveScanContext = null)
    {
        try
        {
            if (!isZip)
            {
                return File.Exists(configPcPath.Replace('/', Path.DirectorySeparatorChar));
            }

            return archiveScanContext?.Contains(configPcPath) ?? false;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryReadLicensePlate(string sourcePath, string configPcPath, bool isZip, ArchiveScanContext? archiveScanContext = null)
    {
        try
        {
            if (!isZip)
            {
                var physicalPath = configPcPath.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(physicalPath))
                {
                    return null;
                }

                return ReadString(ParseJson(File.ReadAllText(physicalPath)), "licenseName");
            }

            return archiveScanContext?.ReadLicensePlate(configPcPath);
        }
        catch
        {
            return null;
        }
    }

    public static (int? min, int? max) ReadYears(JsonNode? root)
    {
        if (root == null)
        {
            return (null, null);
        }

        var years = root["Years"] as JsonObject ?? root["aggregates"]?["Years"] as JsonObject;
        if (years == null)
        {
            return (null, null);
        }

        var min = ReadIntFromNode(years["min"]);
        var max = ReadIntFromNode(years["max"]);
        return (min, max);
    }

    public static string? ReadString(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        if (root[key] is JsonValue val && val.TryGetValue<string>(out var result))
        {
            return result;
        }

        return null;
    }

    public static string? ReadStringOrAggregate(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        var direct = ReadString(root, key);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (root["aggregates"]?[key] is JsonObject agg)
        {
            var values = agg
                .Where(kvp => kvp.Value is JsonValue v && v.TryGetValue<bool>(out var enabled) && enabled)
                .Select(kvp => kvp.Key)
                .ToList();

            if (values.Count > 0)
            {
                return string.Join(", ", values);
            }
        }

        return null;
    }

    public static double? ReadDouble(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        if (root[key] is JsonValue val && val.TryGetValue<double>(out var result))
        {
            return result;
        }

        return null;
    }

    public static int? ReadInt(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        return ReadIntFromNode(root[key]);
    }

    public static int? ReadIntFromNode(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue<int>(out var result))
            {
                return result;
            }

            if (val.TryGetValue<double>(out var doubleResult))
            {
                return (int)doubleResult;
            }
        }

        return null;
    }

    private static bool TryExtractModelAndConfig(string path, bool isZip, out string modelKey, out string configKey)
    {
        modelKey = string.Empty;
        configKey = string.Empty;

        var normalized = isZip ? path.Replace('\\', '/') : path.Replace('/', '\\');
        var marker = isZip ? "vehicles/" : "\\vehicles\\";
        var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var relative = normalized.Substring(idx + marker.Length);
        var parts = isZip
            ? relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            : relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        modelKey = parts[0];
        var fileName = parts[^1];
        if (!fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        configKey = baseName.Length > 5 ? baseName.Substring(5) : baseName;
        return true;
    }
}
