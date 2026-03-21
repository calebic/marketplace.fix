using System.IO;
using System.Text.Json;

namespace WpfApp1;

public sealed class RenamerIgnoreStore
{
    private readonly string _path;
    private readonly HashSet<string> _ignored;

    public RenamerIgnoreStore(string path)
    {
        _path = path;
        _ignored = Load(path);
    }

    public bool IsIgnored(string signature) => !string.IsNullOrWhiteSpace(signature) && _ignored.Contains(signature);

    public void Ignore(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return;
        }

        _ignored.Add(signature);
        Save();
    }

    public void Unignore(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return;
        }

        if (_ignored.Remove(signature))
        {
            Save();
        }
    }

    private static HashSet<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_ignored.OrderBy(x => x).ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }
}
