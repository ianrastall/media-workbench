namespace MediaWorkbench.Infrastructure;

/// <summary>
/// Where the app's temporary work goes: always under the saved data folder's <c>temp</c> folder, never
/// Windows' temporary folder or another drive. When the data folder cannot be used, the operation stops
/// with a plain explanation instead of writing somewhere the user did not choose.
/// </summary>
public static class WorkFolders
{
    /// <summary>The owner file inside a scratch folder; while it is open, clean-up leaves the folder alone.</summary>
    public const string OwnerFile = ".owner";

    public static string Temporary(string dataRoot) => Path.Combine(dataRoot, "temp");
    /// <summary>Painted-area hand-overs from Create and MCP; copied into the job within seconds.</summary>
    public static string Painted(string dataRoot) => Path.Combine(Temporary(dataRoot), "painted");
    /// <summary>Downloaded update installers until the new version starts.</summary>
    public static string Updates(string dataRoot) => Path.Combine(Temporary(dataRoot), "updates");
    /// <summary>One subfolder per running worker, used as its TEMP/TMP.</summary>
    public static string Work(string dataRoot) => Path.Combine(Temporary(dataRoot), "work");

    /// <summary>Earlier versions used Windows' temporary folder; these are only swept, never written.</summary>
    public static string LegacyPainted => Path.Combine(Path.GetTempPath(), "MediaWorkbench", "painted");
    public static string LegacyUpdates => Path.Combine(Path.GetTempPath(), "MediaWorkbench-update");

    /// <summary>
    /// Creates <paramref name="folder"/> inside <paramref name="dataRoot"/> after checking that neither the data
    /// folder nor anything below it on the way is a link or junction that could send the writes elsewhere.
    /// </summary>
    public static string Prepare(string dataRoot, string folder)
    {
        if (!Path.IsPathFullyQualified(dataRoot)) throw new InvalidDataException("Choose an absolute data folder.");
        string root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(folder);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Temporary work must stay inside the data folder.");
        try
        {
            Directory.CreateDirectory(root);
            for (string? current = target; current is not null; current = Path.GetDirectoryName(current))
                RefuseLink(current);
            Directory.CreateDirectory(target);
            RefuseLink(target); // Recheck: nothing may have replaced the folder with a link meanwhile.
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(dataRoot, ex);
        }
    }

    /// <summary>Writes a painted-area hand-over PNG into the data folder; the caller deletes it after the job copies it.</summary>
    public static async Task<string> WritePaintedAsync(string dataRoot, byte[] png, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Prepare(dataRoot, Painted(dataRoot)), Guid.NewGuid().ToString("N") + ".png");
        try { await File.WriteAllBytesAsync(path, png, cancellationToken); }
        catch (IOException ex)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw Unavailable(dataRoot, ex);
        }
        return path;
    }

    /// <summary>The plain reason shown when the data folder's drive is missing, full, or read-only.</summary>
    public static IOException Unavailable(string dataRoot, Exception? inner = null) => new(
        $"Media Workbench could not use its data folder ({dataRoot}). If it is on a removable or network drive, reconnect it; " +
        "otherwise free some space or choose another folder in Settings. Nothing was written anywhere else." +
        (inner is null ? "" : " Details: " + inner.Message), inner);

    private static void RefuseLink(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The data folder must not pass through a link or junction ({path}).");
    }
}

/// <summary>
/// A worker's own temporary folder under the data folder. The open owner file marks it as in use; disposing
/// removes the folder, and the data-folder clean-up removes any that a crash left behind.
/// </summary>
public sealed class ScratchFolder : IDisposable
{
    /// <summary>Tells the Python processes to use exactly this folder, with no fallback to another one.</summary>
    public const string Variable = "MEDIA_WORKBENCH_TEMP";
    private readonly FileStream? _owner;
    public string Path { get; }

    private ScratchFolder(string path, FileStream? owner) { Path = path; _owner = owner; }

    /// <summary>
    /// With <paramref name="createDataFolder"/> false and no data folder yet (for example, reading this computer's
    /// hardware for a folder not created yet), nothing is created: the folder is named but missing, so a temporary
    /// write fails instead of landing somewhere else.
    /// </summary>
    public static ScratchFolder Create(string dataRoot, bool createDataFolder = true)
    {
        string planned = System.IO.Path.Combine(WorkFolders.Work(dataRoot), Guid.NewGuid().ToString("N"));
        if (!createDataFolder && !Directory.Exists(dataRoot)) return new ScratchFolder(System.IO.Path.GetFullPath(planned), null);
        string path = WorkFolders.Prepare(dataRoot, planned);
        try
        {
            // FileShare.None: while this is open, Windows refuses to delete it, and clean-up skips the folder.
            var owner = new FileStream(System.IO.Path.Combine(path, WorkFolders.OwnerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new ScratchFolder(path, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw WorkFolders.Unavailable(dataRoot, ex);
        }
    }

    /// <summary>Points TEMP and TMP (and TMPDIR for portable tools) of a child process at this folder.</summary>
    public void Apply(IDictionary<string, string?> environment)
    {
        foreach (string name in (string[])["TEMP", "TMP", "TMPDIR", Variable]) environment[name] = Path;
    }

    public void Dispose()
    {
        _owner?.Dispose();
        try
        {
            var directory = new DirectoryInfo(Path);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            directory.Delete(recursive: true);
            // Leave no empty folders behind; a folder another worker is using is not empty, so it stays.
            string work = System.IO.Path.GetDirectoryName(Path)!;
            Directory.Delete(work);
            Directory.Delete(System.IO.Path.GetDirectoryName(work)!);
        }
        catch (IOException) { } // A file still open elsewhere; the data-folder clean-up removes it later.
        catch (UnauthorizedAccessException) { }
    }
}
