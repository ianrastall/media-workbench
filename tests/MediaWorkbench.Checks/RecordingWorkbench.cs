using MediaWorkbench.Core;

/// <summary>Records which data folder each operation received; performs no work.</summary>
sealed class RecordingWorkbench : IWorkbenchService
{
    public List<string> DataRoots { get; } = [];

    public Task<IReadOnlyList<HubModel>> SearchAsync(string dataRoot, string query, string task, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HubModel>>([]);
    public Task<ModelDetails> GetDetailsAsync(string dataRoot, string repoId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public HubDownloadPlan HubPlan { get; set; } = new(new("owner/model", new string('a', 40), "text-to-image", "diffusers", "openrail", "Public", []),
        [new("model.safetensors", 2L << 30, new string('b', 64))], new("model.safetensors", 2L << 30, new string('b', 64)), true, "Can download and test.", [], true, true);
    public Task? HubPlanGate { get; set; }
    public (string Root, string Repo, string Revision, string File)? LastHubCheck { get; private set; }
    public (string Root, HubDownloadPlan Plan)? LastHubDownload { get; private set; }
    public async Task<HubDownloadPlan> PlanHubModelAsync(string dataRoot, string repoId, string revision, string file, CancellationToken cancellationToken)
    {
        LastHubCheck = (dataRoot, repoId, revision, file);
        if (HubPlanGate is { } gate) await gate.WaitAsync(cancellationToken);
        return HubPlan;
    }
    public Task<ModelChoice> DownloadModelAsync(string dataRoot, HubDownloadPlan plan, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastHubDownload = (dataRoot, plan);
        var model = new ModelChoice(new string('d', 32), "Downloaded model", "ready", "Ready", "single-file", Bytes: 2L << 30);
        Models.Add(model);
        return Task.FromResult(model);
    }
    public Task? HealthGate { get; set; }
    public TaskCompletionSource? HealthStarted { get; set; }
    public async Task<string> GetHealthAsync(string dataRoot, CancellationToken cancellationToken)
    {
        HealthStarted?.TrySetResult();
        if (HealthGate is { } gate) await gate.WaitAsync(cancellationToken);
        return "";
    }
    public InstallationPlan GetBaselinePlan() => throw new NotSupportedException();
    public string InstallationStatus { get; set; } = "ready";
    public List<string> SetupCalls { get; } = [];
    public IReadOnlyList<ModelSetupPlan>? SetupPlansOverride { get; set; }
    public Task<IReadOnlyList<ModelSetupPlan>> GetSetupPlansAsync(string dataRoot, CancellationToken cancellationToken)
    {
        SetupCalls.Add("plan");
        bool ready = InstallationStatus == "ready";
        return Task.FromResult(SetupPlansOverride ?? new ModelSetupPlan[] {
            new(ModelChoice.BuiltInId, ready, !ready, 5L << 30, 20L << 30, 100L << 30, "", []),
            new(ModelChoice.XlId, ready && Xl.IsReady, !ready, (ready ? 7L : 12L) << 30, 29L << 30, 100L << 30, "", []) });
    }
    public Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken) =>
        Record(dataRoot, new InstallationState(InstallationStatus, InstallationStatus == "ready" ? "Ready" : "Not installed"));
    /// <summary>A GTX 1660 Ti like the development machine's; reads are counted, not recorded as data-folder operations.</summary>
    public SystemProfile Profile { get; set; } = GtxProfile;
    public List<string> ProfileRoots { get; } = [];
    public Exception? ProfileFailure { get; set; }
    public Task<SystemProfile> GetSystemProfileAsync(string dataRoot, CancellationToken cancellationToken)
    {
        ProfileRoots.Add(dataRoot);
        return ProfileFailure is { } failure ? Task.FromException<SystemProfile>(failure) : Task.FromResult(Profile);
    }
    public static readonly RuntimeSettings MemorySaving = new("fp32-offload", "Memory-saving", "full precision, parts of the model wait in system memory until needed");
    public static readonly SystemProfile GtxProfile = new(
        new(new("NVIDIA GeForce GTX 1660 Ti", 6144, 4991, "7.5", "610.60"), null, 65198, 45716, new(@"D:\data", 296512593920, 2000381014016, "hdd")),
        new("fp32-offload", null, ["NVIDIA GeForce GTX 1660 Ti can make blank pictures at half precision, so full precision is used."], [], MemorySaving),
        MemorySaving);
    public bool? LastKeepDownloadCaches { get; private set; }
    public long InstallCleanedBytes { get; set; }
    public Task? InstallGate { get; set; }
    public string BaseInstallResult { get; set; } = "ready";
    public async Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool keepDownloadCaches = false)
    {
        SetupCalls.Add("base");
        if (InstallGate is { } gate) await gate.WaitAsync(cancellationToken);
        InstallationStatus = BaseInstallResult;
        LastKeepDownloadCaches = keepDownloadCaches;
        return await Record(dataRoot, new InstallationState(BaseInstallResult, BaseInstallResult, InstallCleanedBytes));
    }
    public GenerationSettings? LastGeneration { get; private set; }
    public byte[]? LastMaskBytes { get; private set; }
    public Task? GenerationGate { get; set; }
    /// <summary>Every picture asked for, and an optional failure by seed (like the worker's safety-checker refusal).</summary>
    public List<GenerationSettings> Generations { get; } = [];
    public Func<long, Exception?>? GenerationFailure { get; set; }
    public async Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastGeneration = settings;
        Generations.Add(settings);
        if (GenerationFailure?.Invoke(settings.Seed) is { } failure) throw failure;
        // The view model deletes its temporary painted-area file after the call, so keep its bytes.
        LastMaskBytes = settings.MaskImage is { } mask && File.Exists(mask) ? File.ReadAllBytes(mask) : null;
        if (GenerationGate is { } gate) await gate.WaitAsync(cancellationToken);
        return await Record(dataRoot, Image(dataRoot));
    }
    /// <summary>Video status reads are counted separately so data-folder counts above stay about pictures.</summary>
    public List<string> VideoRoots { get; } = [];
    public AddonState Video { get; set; } = new("not_installed", "Videos are not set up yet.");
    public int VideoInstalls { get; private set; }
    public GenerationSettings? LastVideo { get; private set; }
    public Task<AddonState> GetVideoAsync(string dataRoot, CancellationToken cancellationToken)
    {
        VideoRoots.Add(dataRoot);
        return Task.FromResult(Video);
    }
    public Task<AddonState> InstallVideoAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        VideoInstalls++;
        VideoRoots.Add(dataRoot);
        Video = new("ready", "Videos are ready on Test GPU with memory-saving settings.",
            new("video-fp32-sequential", "Memory-saving", "full precision, the model streams to the graphics card layer by layer"));
        return Task.FromResult(Video);
    }
    /// <summary>Stable Diffusion XL's row, as the worker lists it after Stable Diffusion 1.5.</summary>
    public ModelChoice Xl { get; set; } = new(ModelChoice.XlId, "Stable Diffusion XL", "not_installed", "Stable Diffusion XL is not set up.", "bundled-xl", 6_938_451_574, BuiltIn: true);
    public int XlInstalls { get; private set; }
    public Task? XlInstallGate { get; set; }
    public Exception? XlInstallFailure { get; set; }
    public async Task<AddonState> InstallXlAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        SetupCalls.Add("xl");
        XlInstalls++;
        if (XlInstallGate is { } gate) await gate.WaitAsync(cancellationToken);
        if (XlInstallFailure is { } failure) throw failure;
        Xl = Xl with { Status = "ready", Message = "Ready" };
        return new AddonState("ready", "Stable Diffusion XL is ready on Test GPU with memory-saving settings.");
    }
    public Task<AddonState> RemoveXlAsync(string dataRoot, CancellationToken cancellationToken)
    {
        Xl = Xl with { Status = "not_installed", Message = "Stable Diffusion XL is not set up." };
        return Task.FromResult(new AddonState("not_installed", "Stable Diffusion XL is not set up."));
    }
    public Task<AddonState> RemoveVideoAsync(string dataRoot, CancellationToken cancellationToken)
    {
        VideoRoots.Add(dataRoot);
        Video = new("not_installed", "Videos are not set up yet.");
        return Task.FromResult(Video);
    }
    public Task<GeneratedImage> GenerateVideoAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastVideo = settings;
        VideoRoots.Add(dataRoot);
        return Task.FromResult(VideoClip(dataRoot) with { Settings = settings with { Fps = 8 } });
    }
    public static GeneratedImage VideoClip(string dataRoot) => Image(dataRoot) with
    {
        Kind = "video", VideoPath = Path.Combine(dataRoot, "artifacts", "video.mp4"), Frames = 16, Fps = 8, Seconds = 300,
        Settings = new("waves at dusk", "", 1, 20, Frames: 16, Fps: 8, Sampler: "ddim"),
    };
    public List<LibraryQuery> LibraryQueries { get; } = [];
    /// <summary>When set, Library lists this image instead of a fresh placeholder.</summary>
    public GeneratedImage? LibraryImage { get; set; }
    public ImageExportRequest? LastExport { get; private set; }
    public Task<string> ExportImageAsync(string dataRoot, ImageExportRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastExport = request;
        return Record(dataRoot, request.Destination);
    }
    public Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken, LibraryQuery? query = null)
    {
        query ??= new();
        LibraryQueries.Add(query);
        return Record(dataRoot, new LibrarySnapshot([LibraryImage ?? Image(dataRoot)], 0,
            [new JobSummary(new string('a', 32), "failed", "CUDA out of memory", "2026-10-17T00:00:00+00:00", new("retry me", "blurry", 42, 7, 4.5, 768, 512, "euler-a"), null)],
            100, query.Offset, query.Offset + query.Limit < 100));
    }

    public List<string> Cleared { get; } = [];
    public (string Source, int Scale)? LastUpscale { get; private set; }
    public Task<GeneratedImage> UpscaleAsync(string dataRoot, string sourceImage, int scale, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastUpscale = (sourceImage, scale);
        return Record(dataRoot, Image(dataRoot) with { Kind = "upscale", Width = 2048, Height = 2048, Settings = new("", "", 0, 0, Scale: scale, SourceImage: sourceImage) });
    }
    public Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken) => Record(dataRoot, Storage(5L << 30));
    public Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        Cleared.Add(area);
        return Record(dataRoot, Storage(0));
    }
    private static StorageSnapshot Storage(long caches) => new(100L << 30, 500L << 30, caches,
        [new("caches", "Download caches", caches, 3, true, "Kept for repairs."), new("images", "Images", 1L << 20, 1, false, "")]);

    /// <summary>Models the user added, as the fake lists them after the bundled model.</summary>
    public List<ModelChoice> Models { get; } = [];
    public List<string> ModelRoots { get; } = [];
    public (string Path, string Name)? LastImport { get; private set; }
    public List<string> Retested { get; } = [];
    public static readonly ModelChoice BuiltIn = new(ModelChoice.BuiltInId, "Stable Diffusion 1.5", "ready", "Ready", "bundled", BuiltIn: true);
    public Task<IReadOnlyList<ModelChoice>> GetModelsAsync(string dataRoot, CancellationToken cancellationToken)
    {
        ModelRoots.Add(dataRoot);
        return Task.FromResult<IReadOnlyList<ModelChoice>>([BuiltIn, Xl, .. Models]);
    }
    public Task<ModelInspection> InspectModelAsync(string dataRoot, string path, CancellationToken cancellationToken) => path.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)
        ? throw new InvalidOperationException(".ckpt files can run hidden code when they are opened.")
        : Task.FromResult(new ModelInspection("single-file", "dreamy style", 2L << 30, "Stable Diffusion 1.5-family model file · 2.00 GiB.", true));
    public Task<ModelChoice> ImportModelAsync(string dataRoot, string path, string name, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastImport = (path, name);
        var added = new ModelChoice(new string('e', 32), name, "ready", "Ready", "single-file", 2L << 30, SourceName: Path.GetFileName(path));
        Models.Add(added);
        return Record(dataRoot, added);
    }
    public Task<ModelChoice> RetestModelAsync(string dataRoot, string modelId, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        Retested.Add(modelId);
        int index = Models.FindIndex(model => model.Id == modelId);
        Models[index] = Models[index] with { Status = "ready", Message = "Ready" };
        return Record(dataRoot, Models[index]);
    }
    public Task<IReadOnlyList<ModelChoice>> RemoveModelAsync(string dataRoot, string modelId, CancellationToken cancellationToken)
    {
        Models.RemoveAll(model => model.Id == modelId);
        return Record<IReadOnlyList<ModelChoice>>(dataRoot, [BuiltIn, .. Models]);
    }

    /// <summary>Drive reads and clean-ups are not data-folder operations the other checks count.</summary>
    public DriveStatus Drive { get; set; } = new(100L << 30, 500L << 30, "ssd");
    /// <summary>Optional per-folder answer, for checks of which drive each location is on.</summary>
    public Func<string, DriveStatus>? DriveFor { get; set; }
    public Task<DriveStatus> GetDriveAsync(string dataRoot, CancellationToken cancellationToken) => Task.FromResult(DriveFor?.Invoke(dataRoot) ?? Drive);
    public List<DriveChoice> DriveChoices { get; } = [new(@"C:\", "Windows", 40L << 30, 250L << 30, "ssd", true), new(@"D:\", "Data", 800L << 30, 2000L << 30, "hdd", false)];
    public Task<IReadOnlyList<DriveChoice>> GetDrivesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DriveChoice>>(DriveChoices);
    /// <summary>Waiting pictures, kept in memory like the worker keeps them on disk.</summary>
    public List<QueuedPicture> Waiting { get; } = [];
    public Task<QueuedPicture> QueuePictureAsync(string dataRoot, GenerationSettings settings, CancellationToken cancellationToken)
    {
        var item = new QueuedPicture(Guid.NewGuid().ToString("N"), DateTime.UtcNow.ToString("o"), settings);
        Waiting.Add(item);
        return Task.FromResult(item);
    }
    public Task<IReadOnlyList<QueuedPicture>> GetQueueAsync(string dataRoot, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueuedPicture>>([.. Waiting]);
    public Task<IReadOnlyList<QueuedPicture>> RemoveQueuedAsync(string dataRoot, string id, CancellationToken cancellationToken)
    {
        Waiting.RemoveAll(item => item.Id == id);
        return GetQueueAsync(dataRoot, cancellationToken);
    }
    public DataMovePlan MovePlan { get; set; } = new(3, 3L << 30, 100L << 30, 4L << 30, false, false, "");
    public Exception? MoveFailure { get; set; }
    public List<(string Source, string Destination)> Moves { get; } = [];
    public List<(string Source, string Destination)> Abandoned { get; } = [];
    public Task<DataMovePlan> PlanDataMoveAsync(string dataRoot, string destination, CancellationToken cancellationToken) => Task.FromResult(MovePlan);
    public Task? MoveGate { get; set; }
    public async Task MoveDataAsync(string dataRoot, string destination, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        Moves.Add((dataRoot, destination));
        if (MoveGate is { } gate) await gate.WaitAsync(cancellationToken);
        if (MoveFailure is { } failure) throw failure;
    }
    public MovedDataRemoval Removal { get; set; } = new(3, 3L << 30, 0, true);
    public List<(string Old, string Current)> Removals { get; } = [];
    public Task<MovedDataRemoval> RemoveMovedDataAsync(string oldRoot, string dataRoot, CancellationToken cancellationToken)
    {
        Removals.Add((oldRoot, dataRoot));
        return Task.FromResult(Removal);
    }
    public Task AbandonDataMoveAsync(string dataRoot, string destination, CancellationToken cancellationToken)
    {
        Abandoned.Add((dataRoot, destination));
        return Task.CompletedTask;
    }
    public List<(bool Verified, bool RemoveCaches)> Tidies { get; } = [];
    public TidyReport TidyResult { get; set; } = new(0, 0, 0, 0, 0, false, 0);
    public Exception? TidyFailure { get; set; }
    public Task<TidyReport> TidyAsync(string dataRoot, bool installationVerified, bool removeDownloadCaches, CancellationToken cancellationToken)
    {
        Tidies.Add((installationVerified, removeDownloadCaches));
        return TidyFailure is { } failure ? Task.FromException<TidyReport>(failure) : Task.FromResult(TidyResult);
    }

    public static GeneratedImage Image(string dataRoot) => new(Guid.NewGuid().ToString("N"),
        Path.Combine(dataRoot, "artifacts", "image.png"), "", new("house", "", 1, 1), "", 1, "Test GPU");
    private Task<T> Record<T>(string dataRoot, T value)
    {
        DataRoots.Add(dataRoot);
        return Task.FromResult(value);
    }
}
