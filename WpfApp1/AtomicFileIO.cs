using System.Text;
using System.IO;

namespace WpfApp1;

internal static class AtomicFileIO
{
    public static string BackupPathFor(string path) => path + ".bak";

    public static void WriteAllTextAtomic(string path, string contents, Encoding? encoding = null)
    {
        AppPaths.EnsureParentDirectory(path);
        var directory = Path.GetDirectoryName(path) ?? AppPaths.AppDataRoot;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));
        var backupPath = BackupPathFor(path);
        var targetEncoding = encoding ?? new UTF8Encoding(false);

        try
        {
            File.WriteAllText(tempPath, contents, targetEncoding);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    public static string? ReadAllTextWithBackup(string path, Encoding? encoding = null)
    {
        var targetEncoding = encoding ?? Encoding.UTF8;
        foreach (var candidate in new[] { path, BackupPathFor(path) })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate, targetEncoding);
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
