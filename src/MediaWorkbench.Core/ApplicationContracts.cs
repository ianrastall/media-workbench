namespace MediaWorkbench.Core;

/// <summary>CheckForUpdates defaults to on for settings saved before it existed. Setup's download caches are
/// removed once it passes its test unless KeepDownloadCaches is set.</summary>
public sealed record AppSettings(int SchemaVersion, string DataRoot, string LastSearch, bool CheckForUpdates = true, bool KeepDownloadCaches = false)
{
    public static AppSettings Default(string stateRoot) => new(1, Path.Combine(stateRoot, "Data"), "stable-diffusion");
}
public sealed record HubModel(string Id, long Downloads, long Likes, string Task, string Library, string Access,
    string License = "Not specified", string Hint = "Load details to check model support.")
{
    public string Stats => $"{Downloads:N0} downloads · {Likes:N0} likes";
    public string Description => $"{Task} · {Library} · {Access}";
}
public sealed record ModelDetails(string Id, string Revision, string Task, string Library,
    string License, string Access, string[] Files);
public sealed record HubDownloadFile(string Path, long Bytes, string Sha256)
{
    public string Label => $"{Path} · {StorageSnapshot.Format(Bytes)}";
}
public sealed record HubFilePolicy(HubDownloadFile[] Files, string Blocker);
/// <summary>A pinned candidate, never an installation/readiness claim.</summary>
public sealed record HubDownloadPlan(ModelDetails Details, HubDownloadFile[] Files, HubDownloadFile? SelectedFile,
    bool CanInstall, string Message, string[] Warnings, bool OwnVae = false, bool HeaderChecked = false)
{
    public string Summary => string.Join("\n", new[] { Message,
        SelectedFile is { } file ? $"Download: {StorageSnapshot.Format(file.Bytes)}. Allow an extra 1 GiB free for testing and Windows." : "" }
        .Concat(Warnings).Where(text => text.Length > 0));
}
/// <summary>CleanedBytes: what setup removed after passing its test because nothing needs it any more.</summary>
public sealed record InstallationState(string Status, string Message, long CleanedBytes = 0);
public sealed record OperationProgress(string Stage, long Completed = 0, long Total = 0, string Unit = "");
/// <summary>Defaults match records written before guidance, size, and sampler were selectable.</summary>
public sealed record GenerationSettings(string Prompt, string NegativePrompt, long Seed, int Steps,
    double GuidanceScale = 7.5, int Width = 512, int Height = 512, string Sampler = "pndm",
    string? SourceImage = null, double Strength = 0.6, int Scale = 0, string? MaskImage = null, string? Model = null)
{
    /// <summary>Upscale jobs carry a scale and a source picture instead of a prompt.</summary>
    public bool IsUpscale => Scale > 0;
    public static readonly (int Scale, string Label)[] UpscaleChoices = [(2, "2× larger"), (4, "4× larger")];
    /// <summary>How far image-to-image moves from the starting picture, in plain words.</summary>
    public static readonly (double Strength, string Label)[] ChangeLevels = [(0.35, "A little"), (0.6, "Moderately"), (0.8, "A lot")];
    /// <summary>How far a painted area moves from what was there; the rest of the picture is kept.</summary>
    public static readonly (double Strength, string Label)[] PaintChangeLevels = [(0.5, "A little"), (0.75, "Moderately"), (1.0, "Completely")];
    /// <summary>Only the painted part of the starting picture changes.</summary>
    public bool IsPaintedChange => SourceImage is not null && MaskImage is not null;
    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static readonly (int Width, int Height, string Label)[] Sizes = [(512, 512, "Square · 512 × 512 (fastest)"), (512, 768, "Portrait · 512 × 768 (slower)"), (768, 512, "Landscape · 768 × 512 (slower)")];
    public static readonly (string Id, string Label)[] Samplers = [("pndm", "Balanced (model default, PNDM)"), ("dpmpp-2m", "Crisp detail (DPM++ 2M Karras)"), ("euler-a", "Softer and more varied (Euler ancestral)")];
    public string Summary => (SourceImage is null ? "" : IsPaintedChange
            ? $"Painted part of your picture changed {PaintChangeLevels.MinBy(level => Math.Abs(level.Strength - Strength)).Label.ToLowerInvariant()} · "
            : $"From your picture, changed {ChangeLevels.MinBy(level => Math.Abs(level.Strength - Strength)).Label.ToLowerInvariant()} · ")
        + $"{Width} × {Height} · seed {Seed} · {Steps} steps · prompt strength {GuidanceScale:0.##}" + Sampler switch
    {
        "dpmpp-2m" => " · crisp detail sampling", "euler-a" => " · softer sampling", _ => ""
    };
}
/// <summary>ModelName records which model made the image; null in records made before models could be added.</summary>
public sealed record GeneratedImage(string Id, string ImagePath, string ImageSha256, GenerationSettings Settings,
    string CompletedUtc, double Seconds, string Gpu, string Kind = "generate", int Width = 0, int Height = 0, string? ModelName = null)
{
    public bool CanReuseSettings => Settings.IsUpscale ? !string.IsNullOrEmpty(Settings.SourceImage) : !string.IsNullOrWhiteSpace(Settings.Prompt);
    /// <summary>Names an added model; images from the bundled model keep their familiar description.</summary>
    public string ModelLabel => Settings.Model is { } model && model != ModelChoice.BuiltInId && !string.IsNullOrEmpty(ModelName) ? $"Made with {ModelName} · " : "";
    public string Description => Kind == "upscale"
        ? $"Upscaled {Settings.Scale}× to {Width} × {Height} · {Seconds:N1}s · {Gpu}"
        : $"{Settings.Prompt}\n{ModelLabel}{Settings.Summary} · {Seconds:N1}s · {Gpu}";
}
/// <summary>The bundled Stable Diffusion 1.5 or a model the user added; only ready models can generate.</summary>
public sealed record ModelChoice(string Id, string Name, string Status, string Message, string Format = "", long Bytes = 0,
    bool BuiltIn = false, bool OwnVae = true, string VerifiedUtc = "", string SourceName = "")
{
    public const string BuiltInId = "sd15-baseline";
    public bool IsReady => Status == "ready";
    public bool NeedsTest => !BuiltIn && !IsReady;
    public string StatusText => Status switch
    {
        "ready" => "Ready", "needs_verification" => "Needs a new test", "failed" => "Did not pass its test", _ => Status
    };
    public string Summary => (Format == "diffusers" ? "Model folder" : "Single model file") + $" · {StorageSnapshot.Format(Bytes)} · {StatusText}"
        + (OwnVae ? "" : " · uses Stable Diffusion 1.5's picture decoder");
    public string Detail => IsReady ? (SourceName.Length > 0 ? "Added from " + SourceName : "") : Message;
}
/// <summary>What adding a file or folder would copy, found from its headers without loading it.</summary>
public sealed record ModelInspection(string Format, string SuggestedName, long Bytes, string Summary, bool OwnVae);
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
public sealed record ImageExportRequest(string JobId, string Destination);
/// <summary>One part of the data folder; clearable parts are reproducible and can be removed from the app.</summary>
public sealed record StorageLayout(string RuntimeProfileId, string[] ModelIds);

/// <summary>Free space and type of the drive holding the data folder. Kind: ssd, hdd, usb, removable, network, or unknown.</summary>
public sealed record DriveStatus(long FreeBytes, long TotalBytes, string DriveKind)
{
    /// <summary>Below this, Create and Settings warn before a save can fail.</summary>
    public const long LowSpaceBytes = 5L << 30;
    public bool IsLow => FreeBytes < LowSpaceBytes;
    /// <summary>Drives that load the 4 GB model noticeably slower than a solid-state drive.</summary>
    public bool IsSlow => DriveKind is "hdd" or "usb" or "network";
    public string KindText => DriveKind switch
    {
        "ssd" => "a solid-state drive", "hdd" => "a hard disk", "usb" => "a USB drive",
        "removable" => "a removable drive", "network" => "a network drive", _ => "a drive of unknown type"
    };
    public string Warning => IsLow
        ? $"Only {StorageSnapshot.Format(FreeBytes)} is free on the drive that holds the data folder. New pictures and upscales need room to save, " +
          "and Media Workbench stops before writing when there is not enough. Free up space on that drive, or move the data folder in Settings."
        : "";
    public string SpeedNote => IsSlow ? $"The data folder is on {KindText}. Models load faster from a solid-state drive; you can move the data folder in Settings." : "";
}

/// <summary>What one automatic clean-up removed. Skipped files were in use and are tried again next time.</summary>
public sealed record TidyReport(long RemovedFiles, long RemovedBytes, long Logs, long JobRecords, long PartialDownloads, bool DownloadCaches, long Skipped);

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
/// <summary>The NVIDIA graphics card inference uses, as its driver reports it; ComputeCapability is empty on old drivers.</summary>
public sealed record GpuDetails(string Name, double VramMiB, double? FreeVramMiB = null, string ComputeCapability = "", string Driver = "");
/// <summary>The drive holding the data folder (or its nearest existing parent). Kind: ssd, hdd, usb, removable, network, or unknown.</summary>
public sealed record DataDrive(string Path, long? FreeBytes, long? TotalBytes, string Kind = "unknown");
public sealed record HardwareDetails(GpuDetails? Gpu, string? GpuProblem, double? RamMiB, double? AvailableRamMiB, DataDrive? Drive);
/// <summary>One verified way of running models: precision and whether parts of the model wait in system memory.</summary>
public sealed record RuntimeSettings(string Id, string Label, string Description, string Precision = "float32", bool Offload = true);
/// <summary>The settings chosen for this computer, or the plain reason it cannot make pictures.</summary>
public sealed record RuntimeRecommendation(string? Runtime, string? Blocked, string[] Reasons, string[] Warnings, RuntimeSettings? Settings);
/// <summary>What this computer has and how it makes pictures, read without loading any AI runtime.</summary>
public sealed record SystemProfile(HardwareDetails Hardware, RuntimeRecommendation Recommended, RuntimeSettings? Verified, string Note = "")
{
    /// <summary>Sizes in the words people use for memory and drives ("6 GB", "7.8 GB", "276 GB").</summary>
    public static string Size(double mib)
    {
        double gib = mib / 1024;
        return gib >= 10 || Math.Abs(gib - Math.Round(gib)) < 0.05 ? $"{gib:N0} GB" : $"{gib:0.0} GB";
    }
    public static string DriveKind(string kind) => kind switch
    {
        "ssd" => "a solid-state drive", "hdd" => "a hard disk", "usb" => "a USB drive",
        "removable" => "a removable drive", "network" => "a network drive", _ => "a drive of unknown type"
    };
    public bool CanMakePictures => Recommended.Blocked is null && Recommended.Settings is not null;
    public string GpuLine => Hardware.Gpu is { } gpu ? $"{gpu.Name} with {Size(gpu.VramMiB)} of graphics memory"
        : Hardware.GpuProblem ?? "No NVIDIA graphics card was found.";
    public string MemoryLine => Hardware.RamMiB is { } ram ? $"{Size(ram)} of memory" + (Hardware.AvailableRamMiB is { } free ? $" ({Size(free)} free now)" : "")
        : "System memory could not be read";
    public string DriveLine => Hardware.Drive is not { } drive ? "The data folder's drive could not be read"
        : $"Data folder on {DriveKind(drive.Kind)}" + (drive.FreeBytes is { } free ? $" with {Size(free / 1048576.0)} free" : "");
    /// <summary>The settings in use (once tested) or the ones setup will test, in one sentence.</summary>
    public string SettingsLine => Verified is { } verified ? $"Pictures are made with {verified.Label.ToLowerInvariant()} settings ({verified.Description}), tested on this computer."
        : Recommended.Blocked ?? (Recommended.Settings is { } chosen ? $"Setup will test {chosen.Label.ToLowerInvariant()} settings ({chosen.Description})." : "");
    /// <summary>Plain advice when the drive is the likely reason something is slow.</summary>
    public string DriveAdvice => Hardware.Drive?.Kind switch
    {
        "hdd" or "usb" or "network" => "Models load faster from a solid-state drive; you can move the data folder in Settings.",
        _ => ""
    };
    /// <summary>The short version for Create's setup card (Create shows drive speed and space advice on its own).</summary>
    public string Summary => string.Join("\n", new[] { $"This computer: {GpuLine} · {MemoryLine} · {DriveLine}.", SettingsLine }
        .Concat(Recommended.Warnings).Append(Note).Where(line => line.Length > 0));
    /// <summary>Everything Diagnostics shows, including why these settings were chosen.</summary>
    public string Details
    {
        get
        {
            var lines = new List<string> { "Graphics card: " + GpuLine + "." };
            if (Hardware.Gpu is { } gpu)
                lines.Add($"   Driver {(gpu.Driver.Length > 0 ? gpu.Driver : "version unknown")} · compute capability {(gpu.ComputeCapability.Length > 0 ? gpu.ComputeCapability : "not reported")}"
                    + (gpu.FreeVramMiB is { } free ? $" · {Size(free)} free now" : ""));
            lines.Add("Memory: " + MemoryLine + ".");
            lines.Add(DriveLine + (Hardware.Drive is { } drive ? $" ({drive.Path})." : "."));
            lines.Add("");
            lines.Add(SettingsLine);
            if (Verified is not null && Recommended.Settings is { } best && best.Id != Verified.Id && Note.Length == 0)
                lines.Add($"Settings this computer could use: {best.Label.ToLowerInvariant()} ({best.Description}); they did not pass their test here.");
            lines.AddRange(Recommended.Reasons.Select(reason => "• " + reason));
            lines.AddRange(Recommended.Warnings.Select(warning => "Warning: " + warning));
            if (Note.Length > 0) lines.Add(Note);
            if (DriveAdvice.Length > 0) lines.Add(DriveAdvice);
            return string.Join("\n", lines);
        }
    }
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
    Task<HubDownloadPlan> PlanHubModelAsync(string dataRoot, string repoId, string revision, string file, CancellationToken cancellationToken);
    Task<ModelChoice> DownloadModelAsync(string dataRoot, HubDownloadPlan plan, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<string> GetHealthAsync(CancellationToken cancellationToken);
    InstallationPlan GetBaselinePlan();
    Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Reads the GPU, memory, and data-folder drive, and the settings chosen from them; creates nothing.</summary>
    Task<SystemProfile> GetSystemProfileAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Sets up and verifies the bundled model; afterwards removes the download caches unless keepDownloadCaches.</summary>
    Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool keepDownloadCaches = false);
    Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken, LibraryQuery? query = null);
    /// <summary>Copies a completed image outside the data folder after verifying its recorded checksum.</summary>
    Task<string> ExportImageAsync(string dataRoot, ImageExportRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    /// <summary>Enlarges a picture 2× or 4× with the verified upscaler; the result is a Library image.</summary>
    Task<GeneratedImage> UpscaleAsync(string dataRoot, string sourceImage, int scale, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>The bundled model first, then models the user added, each with its tested state.</summary>
    Task<IReadOnlyList<ModelChoice>> GetModelsAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Identifies a .safetensors file or Diffusers folder from its headers; throws with a plain reason when it cannot be added.</summary>
    Task<ModelInspection> InspectModelAsync(string path, CancellationToken cancellationToken);
    /// <summary>Copies the model into the data folder, verifies the copy, and tests it on the GPU; nothing is kept if a step fails.</summary>
    Task<ModelChoice> ImportModelAsync(string dataRoot, string path, string name, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    /// <summary>Verifies an added model's copied files and tests it on the GPU again.</summary>
    Task<ModelChoice> RetestModelAsync(string dataRoot, string modelId, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    /// <summary>Removes an added model's copy; images, job records, and the user's original files are kept.</summary>
    Task<IReadOnlyList<ModelChoice>> RemoveModelAsync(string dataRoot, string modelId, CancellationToken cancellationToken);
    /// <summary>Removes "caches", "logs", or the whole "installation"; images and job records are never removed.</summary>
    Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    /// <summary>Free space and drive type for the data folder; quick, and creates nothing.</summary>
    Task<DriveStatus> GetDriveAsync(string dataRoot, CancellationToken cancellationToken);
    /// <summary>Removes old logs, old unfinished-job records beyond the ones Library lists, stale downloads, and,
    /// when asked after a verified setup, the download caches. Throws when another operation holds the data folder.</summary>
    Task<TidyReport> TidyAsync(string dataRoot, bool installationVerified, bool removeDownloadCaches, CancellationToken cancellationToken);
}
