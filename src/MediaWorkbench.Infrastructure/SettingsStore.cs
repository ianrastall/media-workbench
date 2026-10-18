using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

public sealed class SettingsStore(string stateRoot) : ISettingsStore
{
    public string Folder => stateRoot;

    public AppSettings Load()
    {
        string file = Path.Combine(stateRoot, "settings.json");
        if (!File.Exists(file)) return AppSettings.Default(stateRoot);
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Settings file is unexpectedly large.");
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Wire.Json)
            ?? throw new InvalidDataException("Settings file is empty.");
        Validate(result);
        return result;
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(settings.DataRoot);
        Directory.CreateDirectory(stateRoot);
        string temporary = Path.Combine(stateRoot, $"settings-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        File.Move(temporary, Path.Combine(stateRoot, "settings.json"), overwrite: true);
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Unsupported settings version.");
        if (string.IsNullOrWhiteSpace(settings.DataRoot) || !Path.IsPathFullyQualified(settings.DataRoot))
            throw new InvalidDataException("Choose an absolute data folder.");
        string path = Path.GetFullPath(settings.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        string drive = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
        if (string.Equals(path, drive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a folder inside a drive, rather than the entire drive.");
        if (settings.LastSearch is null || settings.LastSearch.Length > 200) throw new InvalidDataException("Search is too long.");
    }
}
