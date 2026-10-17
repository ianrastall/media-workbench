using MediaWorkbench.Core;

/// <summary>Records which data folder each operation received; performs no work.</summary>
sealed class RecordingWorkbench : IWorkbenchService
{
    public List<string> DataRoots { get; } = [];

    public Task<IReadOnlyList<HubModel>> SearchAsync(string query, string task, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HubModel>>([]);
    public Task<ModelDetails> GetDetailsAsync(string repoId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult("");
    public InstallationPlan GetBaselinePlan() => throw new NotSupportedException();
    public Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken) =>
        Record(dataRoot, new InstallationState("ready", "Ready"));
    public Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        Record(dataRoot, new InstallationState("ready", "Ready"));
    public GenerationSettings? LastGeneration { get; private set; }
    public Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        LastGeneration = settings;
        return Record(dataRoot, Image(dataRoot));
    }
    public Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken) =>
        Record(dataRoot, new LibrarySnapshot([Image(dataRoot)], 0,
            [new JobSummary(new string('a', 32), "failed", "CUDA out of memory", "2026-10-17T00:00:00+00:00", new("retry me", "blurry", 42, 7, 4.5, 768, 512, "euler-a"), null)]));

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

    public static GeneratedImage Image(string dataRoot) => new(Guid.NewGuid().ToString("N"),
        Path.Combine(dataRoot, "artifacts", "image.png"), "", new("house", "", 1, 1), "", 1, "Test GPU");
    private Task<T> Record<T>(string dataRoot, T value)
    {
        DataRoots.Add(dataRoot);
        return Task.FromResult(value);
    }
}
