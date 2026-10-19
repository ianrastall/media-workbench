using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

public sealed class SettingsStore(string stateRoot) : ISettingsStore
{
    public string Folder => stateRoot;

    public AppSettings Load()
    {
        string file = Path.Combine(stateRoot, "settings.json");
        if (!File.Exists(file)) return AppSettings.Default();
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Settings file is unexpectedly large.");
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Wire.Json)
            ?? throw new InvalidDataException("Settings file is empty.");
        Validate(result);
        return result;
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        // Check existing parents before creating anything: a junction must not redirect the chosen folder.
        for (var folder = new DirectoryInfo(settings.DataRoot); folder is not null; folder = folder.Parent)
            if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Choose a working folder that does not pass through a link or junction ({folder.FullName}).");
        Directory.CreateDirectory(settings.DataRoot);
        // Choosing a folder must establish that it can be written before enabling downloads or saving the choice.
        using (var probe = new FileStream(Path.Combine(settings.DataRoot, $".write-check-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            probe.WriteByte(0);
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
        if (settings.ExcludedDrives is { } excluded && (excluded.Length > 64 || excluded.Any(root => root is null || root.Length > 260 || !Path.IsPathFullyQualified(root))))
            throw new InvalidDataException("The list of drives not to use is invalid.");
    }
}
