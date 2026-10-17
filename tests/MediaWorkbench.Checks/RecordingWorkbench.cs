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
    public Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        Record(dataRoot, Image(dataRoot));
    public Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken) =>
        Record(dataRoot, new LibrarySnapshot([Image(dataRoot)], 0,
            [new JobSummary(new string('a', 32), "failed", "CUDA out of memory", "2026-10-17T00:00:00+00:00", new("retry me", "blurry", 42, 7), null)]));

    public static GeneratedImage Image(string dataRoot) => new(Guid.NewGuid().ToString("N"),
        Path.Combine(dataRoot, "artifacts", "image.png"), "", new("house", "", 1, 1), "", 1, "Test GPU");
    private Task<T> Record<T>(string dataRoot, T value)
    {
        DataRoots.Add(dataRoot);
        return Task.FromResult(value);
    }
}
