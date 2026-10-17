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
}
