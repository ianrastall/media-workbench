namespace MediaWorkbench.Core;

public sealed record AppSettings(int SchemaVersion, string DataRoot, string LastSearch)
{
    public static AppSettings Default(string stateRoot) => new(1, Path.Combine(stateRoot, "Data"), "stable-diffusion");
}
public sealed record HubModel(string Id, long Downloads, long Likes, string Task, string Library, string Access)
{
    public string Stats => $"{Downloads:N0} downloads · {Likes:N0} likes";
    public string Description => $"{Task} · {Library} · {Access}";
}
public sealed record ModelDetails(string Id, string Revision, string Task, string Library,
    string License, string Access, string[] Files);
public sealed record InstallationState(string Status, string Message);
public sealed record OperationProgress(string Stage, long Completed = 0, long Total = 0, string Unit = "");
public sealed record GenerationSettings(string Prompt, string NegativePrompt, long Seed, int Steps);
public sealed record GeneratedImage(string Id, string ImagePath, string ImageSha256, GenerationSettings Settings,
    string CompletedUtc, double Seconds, string Gpu)
{
    public string Description => $"{Settings.Prompt}\nSeed {Settings.Seed} · {Settings.Steps} steps · {Seconds:N1}s · {Gpu}";
}
/// <summary>A generation that failed, was cancelled, or was interrupted, with its reason and its own child log.</summary>
public sealed record JobSummary(string Id, string Status, string Message, string StartedUtc, GenerationSettings? Settings, string? LogPath)
{
    public string Title => Status.Length > 0 && DateTimeOffset.TryParse(StartedUtc, out var started)
        ? $"{char.ToUpperInvariant(Status[0])}{Status[1..]} · {started.ToLocalTime():g}" : Status;
    public string Description => Settings is null ? Message : $"{Settings.Prompt}\nSeed {Settings.Seed} · {Settings.Steps} steps\n{Message}";
    public bool CanRetry => Settings is not null && !string.IsNullOrWhiteSpace(Settings.Prompt);
    public bool HasLog => LogPath is not null;
}
/// <summary>Recent successful images and unfinished generations, plus how many jobs this listing marked interrupted.</summary>
public sealed record LibrarySnapshot(IReadOnlyList<GeneratedImage> Images, int Recovered, IReadOnlyList<JobSummary> Problems);
/// <summary>One part of the data folder; clearable parts are reproducible and can be removed from the app.</summary>
public sealed record StorageItem(string Id, string Label, long Bytes, long Files, bool Clearable, string Note)
{
    public string SizeText => $"{StorageSnapshot.Format(Bytes)} · {Files:N0} {(Files == 1 ? "file" : "files")}";
    public bool HasNote => Note.Length > 0;
}
public sealed record StorageSnapshot(long FreeBytes, long TotalBytes, long InstallationBytes, IReadOnlyList<StorageItem> Items)
{
    public static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N2} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N1} MiB" : $"{bytes / 1024.0:N0} KiB";
}
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
public interface IWorkbenchService
{
    Task<IReadOnlyList<HubModel>> SearchAsync(string query, string task, CancellationToken cancellationToken);
    Task<ModelDetails> GetDetailsAsync(string repoId, CancellationToken cancellationToken);
    Task<string> GetHealthAsync(CancellationToken cancellationToken);
    InstallationPlan GetBaselinePlan();
    Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken);
    Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken);
    Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Removes "caches", "logs", or the whole "installation"; images and job records are never removed.</summary>
    Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}
