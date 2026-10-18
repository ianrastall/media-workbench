using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

public sealed class WorkbenchService(string applicationRoot, string pythonExecutable) : IWorkbenchService
{
    private async Task<JsonElement> Execute(string operation, object payload, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        await using var worker = await PythonWorkerClient.StartAsync(pythonExecutable, applicationRoot, token);
        var response = await worker.ExecuteAsync(operation, payload, onEvent: value =>
        {
            if (value.Kind == "progress" && progress is not null)
            {
                var update = JsonSerializer.Deserialize<OperationProgress>(value.Payload, Wire.Json);
                if (update is not null) progress.Report(update);
            }
        }, cancellationToken: token);
        if (response.Kind == "error")
        {
            if (response.ErrorCode == "cancelled") throw new OperationCanceledException(token);
            string message = response.Payload.TryGetProperty("message", out var detail) ? detail.GetString() ?? "Worker failed" : "Worker failed";
            throw new InvalidOperationException(message);
        }
        return response.Payload;
    }
    public async Task<IReadOnlyList<HubModel>> SearchAsync(string query, string task, CancellationToken cancellationToken)
    {
        var data = await Execute("catalog.search", new { query, task, limit = 30 }, cancellationToken);
        return JsonSerializer.Deserialize<HubModel[]>(data.GetProperty("models"), Wire.Json) ?? [];
    }
    public async Task<ModelDetails> GetDetailsAsync(string repoId, CancellationToken cancellationToken)
    {
        var data = await Execute("catalog.details", new { repoId }, cancellationToken);
        return JsonSerializer.Deserialize<ModelDetails>(data, Wire.Json) ?? throw new InvalidDataException("Empty model details.");
    }
    public async Task<HubDownloadPlan> PlanHubModelAsync(string dataRoot, string repoId, string revision, string file, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<HubDownloadPlan>(await Execute("catalog.plan", new { dataRoot, repoId, revision, file }, cancellationToken), Wire.Json)
            ?? throw new InvalidDataException("Empty model download plan.");
    public async Task<ModelChoice> DownloadModelAsync(string dataRoot, HubDownloadPlan plan, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!plan.CanInstall || plan.SelectedFile is not { } file) throw new InvalidOperationException("Check this model's details before downloading.");
        return JsonSerializer.Deserialize<ModelChoice>(await Execute("models.download", new { dataRoot, repoId = plan.Details.Id,
            revision = plan.Details.Revision, file = file.Path, file.Sha256, file.Bytes, licenseReviewed = true }, cancellationToken, progress), Wire.Json)!;
    }
    public async Task<string> GetHealthAsync(CancellationToken cancellationToken)
    {
        uint abi = NativeCore.VerifyAbi();
        var data = await Execute("health", new { }, cancellationToken);
        return $"Rust native core: ABI {abi}\nPython worker: {data.GetProperty("python").GetString()}\n" +
            $"GPU: {data.GetProperty("gpu").GetRawText()}\n\nDiscovery and the SD 1.5 installation workflow are available. Check the model state on Create for inference readiness.";
    }
    public InstallationPlan GetBaselinePlan() => new InstallationPlanner().Plan(InstallationPlanner.ReadCatalog(applicationRoot).Single(r => r.Id == "sd15-baseline"));
    public async Task<InstallationState> GetInstallationAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<InstallationState>(await Execute("installation.status", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<SystemProfile> GetSystemProfileAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<SystemProfile>(await Execute("system.profile", new { dataRoot }, cancellationToken), Wire.Json)
            ?? throw new InvalidDataException("Empty hardware profile.");
    public async Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool keepDownloadCaches = false)
    {
        var plan = GetBaselinePlan();
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("\n", plan.Blockers));
        return JsonSerializer.Deserialize<InstallationState>(await Execute("installation.run", new { dataRoot, keepDownloadCaches }, cancellationToken, progress), Wire.Json)!;
    }
    public async Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute("generate.image", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps,
            settings.GuidanceScale, settings.Width, settings.Height, settings.Sampler, sourceImage = settings.SourceImage ?? "", settings.Strength, maskImage = settings.MaskImage ?? "",
            model = settings.Model ?? "" }, cancellationToken, progress), Wire.Json)!;
    public async Task<AddonState> GetVideoAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute("video.status", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<AddonState> InstallVideoAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute("video.install", new { dataRoot }, cancellationToken, progress), Wire.Json)!;
    public async Task<AddonState> RemoveVideoAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute("video.remove", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<AddonState> InstallXlAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute("xl.install", new { dataRoot }, cancellationToken, progress), Wire.Json)!;
    public async Task<AddonState> RemoveXlAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AddonState>(await Execute("xl.remove", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<GeneratedImage> GenerateVideoAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute("generate.video", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps,
            settings.GuidanceScale, settings.Width, settings.Height, settings.Frames }, cancellationToken, progress), Wire.Json)!;
    public async Task<IReadOnlyList<ModelChoice>> GetModelsAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice[]>((await Execute("models.list", new { dataRoot }, cancellationToken)).GetProperty("models"), Wire.Json) ?? [];
    public async Task<ModelInspection> InspectModelAsync(string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelInspection>(await Execute("models.inspect", new { path }, cancellationToken), Wire.Json)!;
    public async Task<ModelChoice> ImportModelAsync(string dataRoot, string path, string name, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice>(await Execute("models.import", new { dataRoot, path, name }, cancellationToken, progress), Wire.Json)!;
    public async Task<ModelChoice> RetestModelAsync(string dataRoot, string modelId, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice>(await Execute("models.retest", new { dataRoot, modelId }, cancellationToken, progress), Wire.Json)!;
    public async Task<IReadOnlyList<ModelChoice>> RemoveModelAsync(string dataRoot, string modelId, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ModelChoice[]>((await Execute("models.remove", new { dataRoot, modelId }, cancellationToken)).GetProperty("models"), Wire.Json) ?? [];
    public async Task<GeneratedImage> UpscaleAsync(string dataRoot, string sourceImage, int scale, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute("upscale.image", new { dataRoot, sourceImage, scale }, cancellationToken, progress), Wire.Json)!;
    public async Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken, LibraryQuery? query = null)
    {
        query ??= new();
        var result = await Execute("library.list", new { dataRoot, query.Query, query.Kind, query.Offset, query.Limit }, cancellationToken);
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
        JsonSerializer.Deserialize<StorageSnapshot>(await Execute("storage.clear", new { dataRoot, area }, cancellationToken, progress), Wire.Json)!;
    public Task<DriveStatus> GetDriveAsync(string dataRoot, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.GetDriveStatus(dataRoot), cancellationToken);
    public Task<TidyReport> TidyAsync(string dataRoot, bool installationVerified, bool removeDownloadCaches, CancellationToken cancellationToken) =>
        Task.Run(() => NativeCore.TidyDataFolder(dataRoot, ReadLayout(), installationVerified, removeDownloadCaches, cancellationToken), cancellationToken);
}
