using System.Text;
using System.IO;

namespace WpfApp1;

internal static class AppPaths
{
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNGMarketplaceConfigEditor");

    public static string CacheRoot => Path.Combine(AppDataRoot, "Cache");
    public static string PreviewCacheRoot => Path.Combine(AppDataRoot, "preview-cache");
    public static string SettingsPath => Path.Combine(AppDataRoot, "settings.json");
    public static string ModMemoryPath => Path.Combine(AppDataRoot, "mod-memory.json");
    public static string ReviewIgnorePath => Path.Combine(AppDataRoot, "mod-review-ignored.json");
    public static string ReviewHistoryPath => Path.Combine(AppDataRoot, "mod-review-history.log");
    public static string ScrapeLogPath => Path.Combine(AppDataRoot, "scrape.log");
    public static string CrashLogPath => Path.Combine(AppDataRoot, "ui-crash.log");
    public static string StateLogPath => Path.Combine(AppDataRoot, "state.log");
    public static string PricingCachePath => Path.Combine(CacheRoot, "pricing-cache.json");

    public static void EnsureParentDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static void AppendStateLog(string scope, string message)
    {
        try
        {
            EnsureParentDirectory(StateLogPath);
            File.AppendAllText(StateLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{scope}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }
}
