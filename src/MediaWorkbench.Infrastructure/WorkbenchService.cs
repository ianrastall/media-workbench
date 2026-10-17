using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

public sealed class WorkbenchService(string applicationRoot, string pythonExecutable) : IWorkbenchService
{
    private async Task<JsonElement> Execute(string operation, object payload, CancellationToken token)
    {
        await using var worker = await PythonWorkerClient.StartAsync(pythonExecutable, applicationRoot, token);
        var response = await worker.ExecuteAsync(operation, payload, cancellationToken: token);
        if (response.Kind == "error")
        {
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
            $"GPU: {data.GetProperty("gpu").GetRawText()}\n\nDiscovery is available. Model installation and image/video generation are the next milestones.";
    }
    public InstallationPlan GetBaselinePlan() => new InstallationPlanner().Plan(InstallationPlanner.ReadCatalog(applicationRoot).Single(r => r.Id == "sd15-baseline"));
}
