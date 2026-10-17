using System.Text.Json;

namespace MediaWorkbench.Core;

public static class Wire
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record WorkerEvent(int Protocol, string? Id, string Kind, JsonElement Payload)
{
    public bool IsTerminal => Kind is "result" or "error";
    public string? ErrorCode => Kind == "error" && Payload.TryGetProperty("code", out var code)
        ? code.GetString() : null;
}

public interface IWorkerClient : IAsyncDisposable
{
    WorkerEvent Handshake { get; }
    Task<WorkerEvent> ExecuteAsync(string operation, object payload,
        Action<WorkerEvent>? onEvent = null, CancellationToken cancellationToken = default);
}

public sealed record ModelSource(string Provider, string RepoId, string? Revision);
public sealed record ModelRecipe(int SchemaVersion, string Id, string Version, string DisplayName,
    string Status, ModelSource Source, string AdapterId, string? RuntimeProfileId,
    string[] Operations, int MinimumVramMiB, string[] Notes);
public sealed record PlanStep(string Id, string Description);
public sealed record InstallationPlan(string RecipeId, bool CanExecute, string[] Blockers,
    PlanStep[] Steps);

public interface IInstallationPlanner
{
    InstallationPlan Plan(ModelRecipe recipe);
}

public enum JobState { Queued, Preparing, Running, Finalizing, Cancelling, Succeeded, Failed, Cancelled, Interrupted }

public sealed class JobLifecycle
{
    public JobState State { get; private set; } = JobState.Queued;
    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Interrupted;

    public void MoveTo(JobState next)
    {
        bool allowed = !IsTerminal && (next switch
        {
            JobState.Preparing => State == JobState.Queued,
            JobState.Running => State == JobState.Preparing,
            JobState.Finalizing => State == JobState.Running,
            JobState.Succeeded => State == JobState.Finalizing,
            JobState.Cancelling => State != JobState.Cancelling,
            JobState.Cancelled => State == JobState.Cancelling,
            JobState.Failed or JobState.Interrupted => true,
            _ => false
        });
        if (!allowed) throw new InvalidOperationException($"Invalid job transition: {State} -> {next}");
        State = next;
    }
}
