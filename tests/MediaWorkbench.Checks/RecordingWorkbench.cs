using MediaWorkbench.Core;

/// <summary>Records which data folder each operation received; performs no work.</summary>
sealed class RecordingWorkbench : IWorkbenchService
{
    public List<string> DataRoots { get; } = [];

    public Task<IReadOnlyList<HubModel>> SearchAsync(string query, string task, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HubModel>>([]);
    public Task<ModelDetails> GetDetailsAsync(string repoId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task? HealthGate { get; set; }
    public TaskCompletionSource? HealthStarted { get; set; }
    public async Task<string> GetHealthAsync(CancellationToken cancellationToken)
    {
        HealthStarted?.TrySetResult();
        if (HealthGate is { } gate) await gate.WaitAsync(cancellationToken);
        return "";
    }
    public InstallationPlan GetBaselinePlan() => throw new NotSupportedException();
    public Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken) =>
        Record(dataRoot, new InstallationState("ready", "Ready"));
    public Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        Record(dataRoot, new InstallationState("ready", "Ready"));
    public GenerationSettings? LastGeneration { get; private set; }
    public byte[]? LastMaskBytes { get; private set; }
    public Task? GenerationGate { get; set; }
    public async Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastGeneration = settings;
        // The view model deletes its temporary painted-area file after the call, so keep its bytes.
        LastMaskBytes = settings.MaskImage is { } mask && File.Exists(mask) ? File.ReadAllBytes(mask) : null;
        if (GenerationGate is { } gate) await gate.WaitAsync(cancellationToken);
        return await Record(dataRoot, Image(dataRoot));
    }
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
        return Task.FromResult<IReadOnlyList<ModelChoice>>([BuiltIn, .. Models]);
    }
    public Task<ModelInspection> InspectModelAsync(string path, CancellationToken cancellationToken) => path.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)
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

    public static GeneratedImage Image(string dataRoot) => new(Guid.NewGuid().ToString("N"),
        Path.Combine(dataRoot, "artifacts", "image.png"), "", new("house", "", 1, 1), "", 1, "Test GPU");
    private Task<T> Record<T>(string dataRoot, T value)
    {
        DataRoots.Add(dataRoot);
        return Task.FromResult(value);
    }
}
