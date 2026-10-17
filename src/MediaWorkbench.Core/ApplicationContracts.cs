namespace MediaWorkbench.Core;

/// <summary>CheckForUpdates defaults to on for settings saved before it existed.</summary>
public sealed record AppSettings(int SchemaVersion, string DataRoot, string LastSearch, bool CheckForUpdates = true)
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
/// <summary>Defaults match records written before guidance, size, and sampler were selectable.</summary>
public sealed record GenerationSettings(string Prompt, string NegativePrompt, long Seed, int Steps,
    double GuidanceScale = 7.5, int Width = 512, int Height = 512, string Sampler = "pndm",
    string? SourceImage = null, double Strength = 0.6, int Scale = 0)
{
    /// <summary>Upscale jobs carry a scale and a source picture instead of a prompt.</summary>
    public bool IsUpscale => Scale > 0;
    public static readonly (int Scale, string Label)[] UpscaleChoices = [(2, "2× larger"), (4, "4× larger")];
    /// <summary>How far image-to-image moves from the starting picture, in plain words.</summary>
    public static readonly (double Strength, string Label)[] ChangeLevels = [(0.35, "A little"), (0.6, "Moderately"), (0.8, "A lot")];
    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static readonly (int Width, int Height, string Label)[] Sizes = [(512, 512, "Square · 512 × 512 (fastest)"), (512, 768, "Portrait · 512 × 768 (slower)"), (768, 512, "Landscape · 768 × 512 (slower)")];
    public static readonly (string Id, string Label)[] Samplers = [("pndm", "Balanced (model default, PNDM)"), ("dpmpp-2m", "Crisp detail (DPM++ 2M Karras)"), ("euler-a", "Softer and more varied (Euler ancestral)")];
    public string Summary => (SourceImage is null ? "" : $"From your picture, changed {ChangeLevels.MinBy(level => Math.Abs(level.Strength - Strength)).Label.ToLowerInvariant()} · ")
        + $"{Width} × {Height} · seed {Seed} · {Steps} steps · prompt strength {GuidanceScale:0.##}" + Sampler switch
    {
        "dpmpp-2m" => " · crisp detail sampling", "euler-a" => " · softer sampling", _ => ""
    };
}
public sealed record GeneratedImage(string Id, string ImagePath, string ImageSha256, GenerationSettings Settings,
    string CompletedUtc, double Seconds, string Gpu, string Kind = "generate", int Width = 0, int Height = 0)
{
    public bool CanReuseSettings => Settings.IsUpscale ? !string.IsNullOrEmpty(Settings.SourceImage) : !string.IsNullOrWhiteSpace(Settings.Prompt);
    public string Description => Kind == "upscale"
        ? $"Upscaled {Settings.Scale}× to {Width} × {Height} · {Seconds:N1}s · {Gpu}"
        : $"{Settings.Prompt}\n{Settings.Summary} · {Seconds:N1}s · {Gpu}";
}
/// <summary>A generation that failed, was cancelled, or was interrupted, with its reason and its own child log.</summary>
public sealed record JobSummary(string Id, string Status, string Message, string StartedUtc, GenerationSettings? Settings, string? LogPath)
{
    public string Title => Status.Length > 0 && DateTimeOffset.TryParse(StartedUtc, out var started)
        ? $"{char.ToUpperInvariant(Status[0])}{Status[1..]} · {started.ToLocalTime():g}" : Status;
    public string Description => Settings is null ? Message
        : Settings.IsUpscale ? $"Upscale {Settings.Scale}×\n{Message}" : $"{Settings.Prompt}\n{Settings.Summary}\n{Message}";
    public bool CanRetry => Settings is not null && (Settings.IsUpscale ? !string.IsNullOrEmpty(Settings.SourceImage) : !string.IsNullOrWhiteSpace(Settings.Prompt));
    public bool HasLog => LogPath is not null;
}
/// <summary>A page of matching images and recent unfinished generations; the index is rebuilt from job records.</summary>
public sealed record LibrarySnapshot(IReadOnlyList<GeneratedImage> Images, int Recovered, IReadOnlyList<JobSummary> Problems,
    int Total = 0, int Offset = 0, bool HasMore = false);
public sealed record LibraryQuery(string Query = "", string Kind = "all", int Offset = 0, int Limit = 48);
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
/// <summary>A newer release with a verified-download installer and its release notes.</summary>
public sealed record UpdateInfo(string Version, string Notes, string InstallerUrl, string ChecksumUrl, string PageUrl);
public interface IUpdateService
{
    string CurrentVersion { get; }
    /// <summary>The newest applicable release, or null when this version is current. Throws when GitHub cannot be reached.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    /// <summary>Downloads the installer and verifies its published SHA-256; returns the verified file's path.</summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
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
    Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken, LibraryQuery? query = null);
    /// <summary>Enlarges a picture 2× or 4× with the verified upscaler; the result is a Library image.</summary>
    Task<GeneratedImage> UpscaleAsync(string dataRoot, string sourceImage, int scale, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Removes "caches", "logs", or the whole "installation"; images and job records are never removed.</summary>
    Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}
