using System.Text.RegularExpressions;

namespace MediaWorkbench.Infrastructure;

/// <summary>
/// Removes the desktop app's own temporary files from %TEMP%: painted-area hand-over files a crash left
/// behind and update installers that are no longer needed. Only names this app makes are touched, and a
/// file another program holds open is left for the next start.
/// </summary>
public static partial class Housekeeping
{
    public static string PaintedFolder => Path.Combine(Path.GetTempPath(), "MediaWorkbench", "painted");
    public static string UpdateFolder => Path.Combine(Path.GetTempPath(), "MediaWorkbench-update");
    /// <summary>A hand-over is copied into its job within seconds; anything older was left by a crash.</summary>
    public static readonly TimeSpan PaintedMaxAge = TimeSpan.FromHours(1);
    /// <summary>A newer installer that never ran (the update failed or was interrupted) is downloaded again when needed.</summary>
    public static readonly TimeSpan UnusedInstallerMaxAge = TimeSpan.FromDays(7);

    /// <summary>Deletes painted-area hand-over files older than <paramref name="maxAge"/>; returns how many.</summary>
    public static int SweepPaintedHandOvers(string folder, TimeSpan maxAge, DateTime nowUtc)
    {
        int removed = 0;
        foreach (var file in Files(folder))
            if (HandOver().IsMatch(file.Name) && nowUtc - file.LastWriteTimeUtc > maxAge && Delete(file)) removed++;
        RemoveIfEmpty(folder);
        return removed;
    }

    /// <summary>
    /// Deletes downloaded installers for this version or older (the update finished: this version is running),
    /// interrupted downloads, and newer installers unused for a week. Returns how many files were removed.
    /// </summary>
    public static int RemoveFinishedInstallers(string folder, string currentVersion, DateTime nowUtc)
    {
        int removed = 0;
        foreach (var file in Files(folder))
        {
            var match = Installer().Match(file.Name);
            if (!match.Success) continue;
            bool finished = match.Groups[2].Success // A ".partial" download never completed.
                || UpdateService.CompareVersions(match.Groups[1].Value, currentVersion) <= 0
                || nowUtc - file.LastWriteTimeUtc > UnusedInstallerMaxAge;
            if (finished && Delete(file)) removed++;
        }
        RemoveIfEmpty(folder);
        return removed;
    }

    private static IEnumerable<FileInfo> Files(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return [];
            return directory.EnumerateFiles().Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool Delete(FileInfo file)
    {
        try { file.Delete(); return true; }
        catch (IOException) { return false; } // Still running or open elsewhere; the next start tries again.
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.png$")]
    private static partial Regex HandOver();
    [GeneratedRegex("^MediaWorkbench-(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?)-win-x64-setup\\.exe(\\.partial)?$")]
    private static partial Regex Installer();
}
