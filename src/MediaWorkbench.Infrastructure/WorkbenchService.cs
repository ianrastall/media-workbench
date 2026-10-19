using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>
/// Runs worker operations. Picture, upscale and video jobs share one coordinator that keeps the picture model loaded
/// between pictures (and with it the data folder) until <see cref="KeepLoadedFor"/> passes without a job; any other
/// work that needs the data folder releases it first. Everything else runs in its own short-lived worker.
/// </summary>
public sealed class WorkbenchService(string applicationRoot, string pythonExecutable) : IWorkbenchService, IAsyncDisposable
{
    /// <summary>Operations that only read; they must not create a data folder that does not exist yet.</summary>
    private static readonly HashSet<string> ReadOnly = ["health", "system.profile", "catalog.search", "catalog.details", "models.inspect"];
    /// <summary>Jobs that run in the kept coordinator, one after another.</summary>
    private static readonly HashSet<string> Jobs = ["generate.image", "upscale.image", "generate.video"];
    /// <summary>Work that never needs the data folder to itself, so a kept model may stay loaded meanwhile: reads, and the
    /// queue of waiting pictures, which has its own folder and per-item atomic records.</summary>
    private static readonly HashSet<string> Reads = [.. ReadOnly, "catalog.plan", "installation.status", "video.status", "xl.status", "models.list", "library.list", "storage.summary",
        "queue.add", "queue.list", "queue.remove"];

    /// <summary>How long the picture model stays loaded after the last job. It holds system and graphics memory meanwhile.</summary>
    public TimeSpan KeepLoadedFor { get; init; } = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _keptGate = new(1, 1);
    private PythonWorkerClient? _kept;
    private string? _keptRoot;
    private long _keptUse;

    /// <summary>The kept coordinator's process ID, or null; for checks.</summary>
    public int? KeptProcessId => _kept?.ProcessId;

    /// <summary>Runs one worker operation; its temporary files stay inside <paramref name="dataRoot"/>.</summary>
    private async Task<JsonElement> Execute(string dataRoot, string operation, object payload, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        if (Jobs.Contains(operation)) return await ExecuteKept(dataRoot, operation, payload, token, progress);
        if (!Reads.Contains(operation)) await ReleaseLoadedModelAsync(); // Setup, imports, removals and clearing need the folder to themselves.
        await using var worker = await PythonWorkerClient.StartAsync(pythonExecutable, applicationRoot, dataRoot, token,
            createDataFolder: !ReadOnly.Contains(operation));
        return Interpret(await worker.ExecuteAsync(operation, payload, onEvent: Forward(progress), cancellationToken: token), token);
    }

    private async Task<JsonElement> ExecuteKept(string dataRoot, string operation, object payload, CancellationToken token, IProgress<OperationProgress>? progress)
    {
        await _keptGate.WaitAsync(token);
        try
        {
            Interlocked.Increment(ref _keptUse); // A pending idle release no longer applies.
            if (_kept is not null && _keptRoot != dataRoot) await DropKept();
            _kept ??= await PythonWorkerClient.StartAsync(pythonExecutable, applicationRoot, dataRoot, token, keepModelLoaded: true);
            _keptRoot = dataRoot;
            WorkerEvent response;
            try { response = await _kept.ExecuteAsync(operation, payload, onEvent: Forward(progress), cancellationToken: token); }
            catch
            {
                await DropKept(); // A stopped or confused transport is never reused.
                throw;
            }
            ScheduleIdleRelease();
            return Interpret(response, token);
        }
        finally { _keptGate.Release(); }
    }

    private void ScheduleIdleRelease()
    {
        long use = Interlocked.Read(ref _keptUse);
        _ = Task.Run(async () =>
        {
            await Task.Delay(KeepLoadedFor);
            await _keptGate.WaitAsync();
            try { if (Interlocked.Read(ref _keptUse) == use) await DropKept(); }
            finally { _keptGate.Release(); }
        });
    }

    private async Task DropKept()
    {
        var kept = _kept;
        _kept = null;
        _keptRoot = null;
        if (kept is not null) await kept.DisposeAsync(); // Ends the coordinator, its model, and the data-folder hold.
    }

    /// <summary>Releases the kept picture model and the data folder it holds; the next picture loads it again.</summary>
    public async Task ReleaseLoadedModelAsync()
    {
        await _keptGate.WaitAsync();
        try
        {
            Interlocked.Increment(ref _keptUse);
            await DropKept();
        }
        finally { _keptGate.Release(); }
    }

    public async ValueTask DisposeAsync() => await ReleaseLoadedModelAsync();

    private static Action<WorkerEvent> Forward(IProgress<OperationProgress>? progress) => value =>
    {
        if (value.Kind == "progress" && progress is not null)
        {
            var update = JsonSerializer.Deserialize<OperationProgress>(value.Payload, Wire.Json);
            if (update is not null) progress.Report(update);
        }
    };

    private static JsonElement Interpret(WorkerEvent response, CancellationToken token)
    {
        if (response.Kind == "error")
        {
            if (response.ErrorCode == "cancelled") throw new OperationCanceledException(token);
            string message = response.Payload.TryGetProperty("message", out var detail) ? detail.GetString() ?? "Worker failed" : "Worker failed";
            throw new InvalidOperationException(message);
        }
        return response.Payload;
    }
    public async Task<IReadOnlyList<HubModel>> SearchAsync(string dataRoot, string query, string task, CancellationToken cancellationToken)
    {
        var data = await Execute(dataRoot, "catalog.search", new { query, task, limit = 30 }, cancellationToken);
        return JsonSerializer.Deserialize<HubModel[]>(data.GetProperty("models"), Wire.Json) ?? [];
    }
    public async Task<ModelDetails> GetDetailsAsync(string dataRoot, string repoId, CancellationToken cancellationToken)
    {
        var data = await Execute(dataRoot, "catalog.details", new { repoId }, cancellationToken);
        return JsonSerializer.Deserialize<ModelDetails>(data, Wire.Json) ?? throw new InvalidDataException("Empty model details.");
    }
    public async Task<HubDownloadPlan> PlanHubModelAsync(string dataRoot, string repoId, string revision, string file, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<HubDownloadPlan>(await Execute(dataRoot, "catalog.plan", new { dataRoot, repoId, revision, file }, cancellationToken), Wire.Json)
            ?? throw new InvalidDataException("Empty model download plan.");
    public async Task<ModelChoice> DownloadModelAsync(string dataRoot, HubDownloadPlan plan, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!plan.CanInstall || plan.SelectedFile is not { } file) throw new InvalidOperationException("Check this model's details before downloading.");
        return JsonSerializer.Deserialize<ModelChoice>(await Execute(dataRoot, "models.download", new { dataRoot, repoId = plan.Details.Id,
            revision = plan.Details.Revision, file = file.Path, file.Sha256, file.Bytes, licenseReviewed = true }, cancellationToken, progress), Wire.Json)!;
    }
    public async Task<string> GetHealthAsync(string dataRoot, CancellationToken cancellationToken)
    {
        uint abi = NativeCore.VerifyAbi();
        var data = await Execute(dataRoot, "health", new { }, cancellationToken);
        return $"Rust native core: ABI {abi}\nPython worker: {data.GetProperty("python").GetString()}\n" +
            $"GPU: {data.GetProperty("gpu").GetRawText()}\n\nDiscovery and the SD 1.5 installation workflow are available. Check the model state on Create for inference readiness.";
    }
    public InstallationPlan GetBaselinePlan() => new InstallationPlanner().Plan(InstallationPlanner.ReadCatalog(applicationRoot).Single(r => r.Id == "sd15-baseline"));
    public async Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<InstallationState>(await Execute(dataRoot, "installation.status", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<SystemProfile> GetSystemProfileAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<SystemProfile>(await Execute(dataRoot, "system.profile", new { dataRoot }, cancellationToken), Wire.Json)
            ?? throw new InvalidDataException("Empty hardware profile.");
    public async Task<IReadOnlyList<ModelSetupPlan>> GetSetupPlansAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelSetupPlan[]>((await Execute(dataRoot, "models.setup_plan", new { dataRoot }, cancellationToken)).GetProperty("plans"), Wire.Json)
            ?? throw new InvalidDataException("Empty model setup plans.");
    public async Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool keepDownloadCaches = false)
    {
        var plan = GetBaselinePlan();
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("\n", plan.Blockers));
        return JsonSerializer.Deserialize<InstallationState>(await Execute(dataRoot, "installation.run", new { dataRoot, keepDownloadCaches }, cancellationToken, progress), Wire.Json)!;
    }
    public async Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute(dataRoot, "generate.image", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps,
            settings.GuidanceScale, settings.Width, settings.Height, settings.Sampler, sourceImage = settings.SourceImage ?? "", settings.Strength, maskImage = settings.MaskImage ?? "",
            model = settings.Model ?? "" }, cancellationToken, progress), Wire.Json)!;
    public async Task<QueuedPicture> QueuePictureAsync(string dataRoot, GenerationSettings settings, CancellationToken cancellationToken) =>
        Queued(await Execute(dataRoot, "queue.add", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps,
            settings.GuidanceScale, settings.Width, settings.Height, settings.Sampler, sourceImage = settings.SourceImage ?? "", settings.Strength,
            maskImage = settings.MaskImage ?? "", model = settings.Model ?? "" }, cancellationToken));
    public async Task<IReadOnlyList<QueuedPicture>> GetQueueAsync(string dataRoot, CancellationToken cancellationToken) =>
        QueueItems(await Execute(dataRoot, "queue.list", new { dataRoot }, cancellationToken));
    public async Task<IReadOnlyList<QueuedPicture>> RemoveQueuedAsync(string dataRoot, string id, CancellationToken cancellationToken) =>
        QueueItems(await Execute(dataRoot, "queue.remove", new { dataRoot, id }, cancellationToken));
    private static QueuedPicture Queued(JsonElement item) => JsonSerializer.Deserialize<QueuedPicture>(item, Wire.Json)
        ?? throw new InvalidDataException("The worker returned an empty waiting picture.");
    private static IReadOnlyList<QueuedPicture> QueueItems(JsonElement data) => [.. data.GetProperty("items").EnumerateArray().Select(Queued)];
    public async Task<AddonState> GetVideoAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute(dataRoot, "video.status", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<AddonState> InstallVideoAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute(dataRoot, "video.install", new { dataRoot }, cancellationToken, progress), Wire.Json)!;
    public async Task<AddonState> RemoveVideoAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute(dataRoot, "video.remove", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<AddonState> InstallXlAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute(dataRoot, "xl.install", new { dataRoot }, cancellationToken, progress), Wire.Json)!;
    public async Task<AddonState> RemoveXlAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute(dataRoot, "xl.remove", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<GeneratedImage> GenerateVideoAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute(dataRoot, "generate.video", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps,
            settings.GuidanceScale, settings.Width, settings.Height, settings.Frames }, cancellationToken, progress), Wire.Json)!;
    public async Task<IReadOnlyList<ModelChoice>> GetModelsAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice[]>((await Execute(dataRoot, "models.list", new { dataRoot }, cancellationToken)).GetProperty("models"), Wire.Json) ?? [];
    public async Task<ModelInspection> InspectModelAsync(string dataRoot, string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelInspection>(await Execute(dataRoot, "models.inspect", new { path }, cancellationToken), Wire.Json)!;
    public async Task<ModelChoice> ImportModelAsync(string dataRoot, string path, string name, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice>(await Execute(dataRoot, "models.import", new { dataRoot, path, name }, cancellationToken, progress), Wire.Json)!;
    public async Task<ModelChoice> RetestModelAsync(string dataRoot, string modelId, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice>(await Execute(dataRoot, "models.retest", new { dataRoot, modelId }, cancellationToken, progress), Wire.Json)!;
    public async Task<IReadOnlyList<ModelChoice>> RemoveModelAsync(string dataRoot, string modelId, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice[]>((await Execute(dataRoot, "models.remove", new { dataRoot, modelId }, cancellationToken)).GetProperty("models"), Wire.Json) ?? [];
    public async Task<GeneratedImage> UpscaleAsync(string dataRoot, string sourceImage, int scale, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute(dataRoot, "upscale.image", new { dataRoot, sourceImage, scale }, cancellationToken, progress), Wire.Json)!;
    public async Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken, LibraryQuery? query = null)
    {
        query ??= new();
        var result = await Execute(dataRoot, "library.list", new { dataRoot, query.Query, query.Kind, query.Offset, query.Limit }, cancellationToken);
        var images = JsonSerializer.Deserialize<GeneratedImage[]>(result.GetProperty("artifacts"), Wire.Json) ?? [];
        var problems = result.TryGetProperty("problems", out var listed) ? JsonSerializer.Deserialize<JobSummary[]>(listed, Wire.Json) ?? [] : [];
        return new(images, result.TryGetProperty("recovered", out var recovered) ? recovered.GetInt32() : 0, problems,
            result.GetProperty("total").GetInt32(), result.GetProperty("offset").GetInt32(), result.GetProperty("hasMore").GetBoolean());
    }
    public Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            return NativeCore.GetStorageSnapshot(dataRoot, ReadLayout(), cancellationToken);
        }, cancellationToken);
    private StorageLayout ReadLayout() =>
        JsonSerializer.Deserialize<StorageLayout>(File.ReadAllText(Path.Combine(applicationRoot, "profiles", "storage-layout.json")), Wire.Json)
            ?? throw new InvalidDataException("The bundled storage layout is missing.");
    public Task<string> ExportImageAsync(string dataRoot, ImageExportRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.ExportArtifact(dataRoot, request, progress, cancellationToken), cancellationToken);
    public async Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<StorageSnapshot>(await Execute(dataRoot, "storage.clear", new { dataRoot, area }, cancellationToken, progress), Wire.Json)!;
    public Task<IReadOnlyList<DriveChoice>> GetDrivesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DriveChoice>>(() =>
        {
            string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            List<DriveChoice> drives = [];
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                    // Rust reports quota-aware free space and the drive's real type; nothing is created.
                    var status = NativeCore.GetDriveStatus(Path.Combine(drive.RootDirectory.FullName, "Media Workbench"));
                    drives.Add(new(drive.RootDirectory.FullName, drive.VolumeLabel, status.FreeBytes, status.TotalBytes, status.DriveKind,
                        string.Equals(drive.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { } // Not ready or not readable now.
            }
            return drives;
        }, cancellationToken);
    public Task<DataMovePlan> PlanDataMoveAsync(string dataRoot, string destination, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.PlanDataMove(dataRoot, destination), cancellationToken);
    public async Task MoveDataAsync(string dataRoot, string destination, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        await ReleaseLoadedModelAsync();
        await Task.Run(() => NativeCore.MoveDataFolder(dataRoot, destination, progress, cancellationToken), cancellationToken);
    }
    public Task<MovedDataRemoval> RemoveMovedDataAsync(string oldRoot, string dataRoot, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.RemoveMovedData(oldRoot, dataRoot, cancellationToken), cancellationToken);
    public Task AbandonDataMoveAsync(string dataRoot, string destination, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.MoveDataFolder(dataRoot, destination, new Progress<OperationProgress>(), cancellationToken, abandon: true), cancellationToken);
    public Task<DriveStatus> GetDriveAsync(string dataRoot, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.GetDriveStatus(dataRoot), cancellationToken);
    public async Task<TidyReport> TidyAsync(string dataRoot, bool installationVerified, bool removeDownloadCaches, CancellationToken cancellationToken)
    {
        await ReleaseLoadedModelAsync();
        return await TidyNative(dataRoot, installationVerified, removeDownloadCaches, cancellationToken);
    }
    private Task<TidyReport> TidyNative(string dataRoot, bool installationVerified, bool removeDownloadCaches, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.TidyDataFolder(dataRoot, ReadLayout(), installationVerified, removeDownloadCaches, cancellationToken), cancellationToken);
}
