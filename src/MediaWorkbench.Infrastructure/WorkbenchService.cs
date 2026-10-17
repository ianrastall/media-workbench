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
    public async Task<InstallationState> InstallAsync(string dataRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var plan = GetBaselinePlan();
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("\n", plan.Blockers));
        return JsonSerializer.Deserialize<InstallationState>(await Execute("installation.run", new { dataRoot }, cancellationToken, progress), Wire.Json)!;
    }
    public async Task<GeneratedImage> GenerateAsync(string dataRoot, GenerationSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<GeneratedImage>(await Execute("generate.image", new { dataRoot, settings.Prompt, settings.NegativePrompt, settings.Seed, settings.Steps }, cancellationToken, progress), Wire.Json)!;
    public async Task<LibrarySnapshot> GetLibraryAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var result = await Execute("library.list", new { dataRoot }, cancellationToken);
        var images = JsonSerializer.Deserialize<GeneratedImage[]>(result.GetProperty("artifacts"), Wire.Json) ?? [];
        var problems = result.TryGetProperty("problems", out var listed) ? JsonSerializer.Deserialize<JobSummary[]>(listed, Wire.Json) ?? [] : [];
        return new(images, result.TryGetProperty("recovered", out var recovered) ? recovered.GetInt32() : 0, problems);
    }
    public async Task<StorageSnapshot> GetStorageAsync(string dataRoot, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<StorageSnapshot>(await Execute("storage.summary", new { dataRoot }, cancellationToken), Wire.Json)!;
    public async Task<StorageSnapshot> ClearStorageAsync(string dataRoot, string area, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<StorageSnapshot>(await Execute("storage.clear", new { dataRoot, area }, cancellationToken, progress), Wire.Json)!;
}
