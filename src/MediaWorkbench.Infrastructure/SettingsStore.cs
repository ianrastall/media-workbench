using System.Security.Cryptography;
using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>Preferences live with the work. An authoritative profile pointer is published last;
/// an unavailable or invalid destination never falls back to legacy preferences.</summary>
public sealed class SettingsStore(string stateRoot) : ISettingsStore
{
    public const string PointerName = "working-folder.json";
    public const int MaximumSettingsBytes = 65536;
    public const int MaximumPointerBytes = 8192;
    private readonly object _gate = new();
    private string? _workingRoot;
    private readonly StartupLog _startupLog = new();
    private readonly Action<string> _deleteLegacy = File.Delete;
    // Allows checks to reproduce a source reappearing during deletion without a timing-dependent watcher.
    internal SettingsStore(string stateRoot, Action<string> deleteLegacy) : this(stateRoot) => _deleteLegacy = deleteLegacy;
    private sealed record FolderPointer(int SchemaVersion, string DataRoot, bool MigrateLegacy = false, string? LegacySettingsSha256 = null);

    public string Folder => _workingRoot is { } root ? Preferences(root) : stateRoot;
    public string LocationFolder => stateRoot;
    public string MigrationNote { get; private set; } = "";
    public static string Preferences(string root) => Path.Combine(root, "preferences");
    public static string StartupLogs(string root) => Path.Combine(Preferences(root), "Logs");
    private string PointerFile => Path.Combine(stateRoot, PointerName);
    private string LegacySettings => Path.Combine(stateRoot, "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            MigrationNote = "";
            _workingRoot = null;
            SweepTemporary(stateRoot);
            var pointer = ReadPointer();
            if (pointer is not null)
            {
                try
                {
                    RequireExistingRoot(pointer.DataRoot);
                    var saved = ReadSettings(Path.Combine(Preferences(pointer.DataRoot), "settings.json"));
                    if (!SameRoot(saved.DataRoot, pointer.DataRoot)) throw new InvalidDataException("Preferences name a different working folder than the location pointer.");
                    _workingRoot = pointer.DataRoot;
                    SweepTemporary(Folder);
                    SweepTemporary(StartupLogs(pointer.DataRoot));
                    FinishLegacyMigration(pointer);
                    _startupLog.Flush(_workingRoot);
                    return saved with { DataRootChosen = true };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                { throw new WorkingFolderUnavailableException(pointer.DataRoot, ex); }
            }

            byte[]? legacy = ReadOptional(LegacySettings, MaximumSettingsBytes);
            if (legacy is null) return AppSettings.Default();
            var settings = DeserializeSettings(legacy);
            bool chosen = settings.DataRootChosen || !SameRoot(settings.DataRoot, Path.Combine(stateRoot, "Data"));
            if (!chosen) return settings with { DataRootChosen = false };
            try
            {
                RequireExistingRoot(settings.DataRoot);
                SaveCore(settings with { DataRootChosen = true }, new(1, settings.DataRoot, true, Hash(legacy)));
                return settings with { DataRootChosen = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { throw new WorkingFolderUnavailableException(settings.DataRoot, ex); }
        }
    }

    public void Save(AppSettings settings) => Save(settings, explicitChoice: false);
    public AppSettings Choose(AppSettings settings) => Save(settings, explicitChoice: true);

    private AppSettings Save(AppSettings settings, bool explicitChoice)
    {
        lock (_gate)
        {
            MigrationNote = "";
            Validate(settings);
            FolderPointer? previous;
            bool repair = false;
            try { previous = ReadPointer(); }
            catch (Exception ex) when (explicitChoice && (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException))
            { previous = null; repair = true; }
            if (!explicitChoice && previous is not null) RequireExistingRoot(previous.DataRoot);
            if (explicitChoice && (previous is null || !SameRoot(previous.DataRoot, settings.DataRoot)))
            {
                // A working folder carries its own preferences. Explicitly choosing it restores those when valid;
                // a damaged file is preserved by AtomicWrite's recovery copy instead of being loaded silently.
                try
                {
                    var own = ReadOptional(Path.Combine(Preferences(settings.DataRoot), "settings.json"), MaximumSettingsBytes);
                    if (own is not null)
                    {
                        var restored = DeserializeSettings(own);
                        if (SameRoot(restored.DataRoot, settings.DataRoot))
                        {
                            settings = restored with { DataRoot = settings.DataRoot, DataRootChosen = true };
                            MigrationNote = "This working folder's saved preferences were restored.";
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
            }
            string? legacyHash = null;
            if (previous is null && explicitChoice)
            {
                try
                {
                    byte[]? legacy = ReadOptional(LegacySettings, MaximumSettingsBytes);
                    if (legacy is not null) { DeserializeSettings(legacy); legacyHash = Hash(legacy); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                { MigrationNote = "The previous preferences could not be read and were kept for recovery in " + stateRoot + "."; }
            }
            settings = settings with { DataRootChosen = true };
            SaveCore(settings, new(1, settings.DataRoot,
                (explicitChoice && previous is null) || previous is { MigrateLegacy: true }, legacyHash ?? previous?.LegacySettingsSha256), repair);
            if (repair) MigrationNote = (MigrationNote.Length == 0 ? "" : MigrationNote + " ")
                + "The previous location file was kept for recovery in " + stateRoot + ".";
            return settings;
        }
    }

    private void SaveCore(AppSettings settings, FolderPointer pointer, bool repairPointer = false)
    {
        Validate(settings);
        byte[] settingsBytes = JsonBytes(settings, MaximumSettingsBytes), pointerBytes = JsonBytes(pointer, MaximumPointerBytes);
        RefuseLinks(settings.DataRoot);
        Directory.CreateDirectory(settings.DataRoot); // Only explicit Save/Choose or an already-existing migration root.
        string folder = Preferences(settings.DataRoot);
        RefuseLinks(folder);
        Directory.CreateDirectory(folder);
        AtomicWrite(Path.Combine(folder, "settings.json"), settingsBytes, Path.Combine(folder, "settings.previous.json"));
        // Publish last. A failed switch leaves the old pointer and old folder usable.
        if (repairPointer) AtomicWrite(PointerFile, pointerBytes, Path.Combine(stateRoot, "working-folder.previous.json"));
        else
        {
            byte[]? existing = ReadOptional(PointerFile, MaximumPointerBytes);
            if (existing is null || !existing.AsSpan().SequenceEqual(pointerBytes)) AtomicWrite(PointerFile, pointerBytes);
        }
        _workingRoot = settings.DataRoot;
        FinishLegacyMigration(pointer);
        _startupLog.Flush(_workingRoot);
    }

    private FolderPointer? ReadPointer()
    {
        byte[]? bytes = ReadOptional(PointerFile, MaximumPointerBytes);
        if (bytes is null) return null;
        var result = Deserialize<FolderPointer>(bytes) ?? throw new InvalidDataException("The working-folder pointer is empty.");
        if (result.SchemaVersion != 1) throw new InvalidDataException("The working-folder pointer has an unsupported version.");
        ValidateRoot(result.DataRoot);
        if (result.LegacySettingsSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The working-folder migration record is invalid.");
        return result;
    }

    private void FinishLegacyMigration(FolderPointer pointer)
    {
        try
        {
            if (!pointer.MigrateLegacy)
            {
                // An older app or another writer may recreate a source after migration finished.
                // Never authorize it, delete it, or silently ignore it on subsequent loads/saves.
                RequireLegacyFilesAbsent();
                return;
            }
            string logs = Path.Combine(stateRoot, "Logs");
            foreach (string name in new[] { "startup.old.log", "startup.log" })
            {
                string source = Path.Combine(logs, name);
                RefuseLinks(source);
                var snapshot = new FileInfo(source);
                long oldLength = snapshot.Exists ? snapshot.Length : 0;
                DateTime oldWrite = snapshot.Exists ? snapshot.LastWriteTimeUtc : default;
                byte[]? content = StartupLog.ReadTail(source);
                if (content is null) continue;
                string destination = Path.Combine(StartupLogs(pointer.DataRoot), "legacy-" + name);
                var existing = ReadOptional(destination, StartupLog.MaximumBytes);
                if (existing is null) AtomicWrite(destination, content);
                else if (!existing.AsSpan().SequenceEqual(content))
                    throw new IOException("Previous startup logs differ from the migrated copy; the originals were kept.");
                var current = new FileInfo(source);
                if (!current.Exists || current.Length != oldLength || current.LastWriteTimeUtc != oldWrite)
                    throw new IOException("A previous startup log changed during migration and was kept.");
                _deleteLegacy(source);
                RequireAbsent(source);
            }
            if (pointer.LegacySettingsSha256 is { } expected)
            {
                byte[]? old = ReadOptional(LegacySettings, MaximumSettingsBytes);
                if (old is not null)
                {
                    if (Hash(old) != expected) throw new IOException("Previous preferences changed during migration and were kept.");
                    _deleteLegacy(LegacySettings);
                    RequireAbsent(LegacySettings);
                }
            }
            else if (ReadOptional(LegacySettings, MaximumSettingsBytes) is not null)
                throw new IOException("Previous preferences could not be verified and were kept for recovery.");
            if (Directory.Exists(logs) && !Directory.EnumerateFileSystemEntries(logs).Any()) Directory.Delete(logs);
            RequireLegacyFilesAbsent();
            AtomicWrite(PointerFile, JsonBytes(pointer with { MigrateLegacy = false, LegacySettingsSha256 = null }, MaximumPointerBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { MigrationNote = "Preferences now use your working folder. Some previous preferences or startup logs were kept in " + stateRoot + ": " + ex.Message; }
    }

    private void RequireLegacyFilesAbsent()
    {
        RequireAbsent(LegacySettings);
        RequireAbsent(Path.Combine(stateRoot, "Logs", "startup.log"));
        RequireAbsent(Path.Combine(stateRoot, "Logs", "startup.old.log"));
    }

    private static void RequireAbsent(string file)
    {
        RefuseLinks(file);
        // File.Exists hides access errors and directories; only a confirmed missing path counts as gone.
        try { File.GetAttributes(file); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("An old preferences or startup log file is still present. It was kept for recovery. Close older copies of Media Workbench before checking it.");
    }

    /// <summary>Never writes a profile fallback log. With no usable choice, retain one bounded error in memory.</summary>
    public void RecordStartupError(Exception exception)
    {
        lock (_gate)
        {
            _startupLog.Record(exception);
            try { _startupLog.Flush(_workingRoot ?? ReadPointer()?.DataRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
    }

    internal static void AtomicWrite(string file, byte[] bytes, string? backup = null)
    {
        RefuseLinks(file);
        if (backup is not null) RefuseLinks(backup);
        string folder = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException("The saved preferences could not be verified.");
            if (backup is not null && File.Exists(file)) File.Replace(temporary, file, backup);
            else File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void SweepTemporary(string folder)
    {
        try
        {
            RefuseLinks(folder);
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.EnumerateFiles(folder, ".preferences-*.tmp"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParseExact(name[".preferences-".Length..], "N", out _) || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10)) continue;
                try { RefuseLinks(file); File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    internal static byte[]? ReadOptional(string file, int maximum)
    {
        RefuseLinks(file);
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum) throw new InvalidDataException("The preferences or location file is unexpectedly large.");
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static AppSettings ReadSettings(string file) => DeserializeSettings(ReadOptional(file, MaximumSettingsBytes)
        ?? throw new InvalidDataException("The working folder's preferences are missing."));
    private static AppSettings DeserializeSettings(byte[] bytes)
    {
        var result = Deserialize<AppSettings>(bytes) ?? throw new InvalidDataException("Settings file is empty.");
        Validate(result);
        return result;
    }
    private static T? Deserialize<T>(byte[] bytes)
    {
        // Match the old File.ReadAllText loader's support for UTF-8/UTF-16 BOMs in edited legacy files.
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), Wire.Json);
    }
    private static byte[] JsonBytes<T>(T value, int maximum)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = true });
        if (bytes.Length > maximum) throw new InvalidDataException("The preferences or location file is unexpectedly large.");
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool SameRoot(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    internal static void RequireExistingRoot(string root)
    {
        ValidateRoot(root); RefuseLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Reconnect the drive that holds your working folder.");
    }
    internal static void RefuseLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"The working folder and preferences must not pass through a link or junction ({current}).");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void ValidateRoot(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("Choose an absolute working folder.");
            if (SameRoot(root, Path.GetPathRoot(root)!)) throw new InvalidDataException("Choose a folder inside a drive, rather than the entire drive.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new InvalidDataException("The saved working-folder path is invalid.", ex); }
    }
    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Unsupported settings version.");
        ValidateRoot(settings.DataRoot);
        if (settings.LastSearch is null || settings.LastSearch.Length > 200) throw new InvalidDataException("Search is too long.");
        if (settings.ExcludedDrives is { } excluded && (excluded.Length > 64 || excluded.Any(root => root is null || root.Length > 260 || !Path.IsPathFullyQualified(root))))
            throw new InvalidDataException("The list of drives not to use is invalid.");
    }
}

public sealed class WorkingFolderUnavailableException(string dataRoot, Exception inner) : IOException(
    $"Could not read preferences in your working folder ({dataRoot}). Reconnect its drive or choose the folder again in Settings. Nothing will be saved elsewhere. Details: {inner.Message}", inner)
{
    public string DataRoot { get; } = dataRoot;
}
