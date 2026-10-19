using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.Presentation;

public partial class MainViewModel
{
    public IRelayCommand GoToModelsCommand { get; }
    public IAsyncRelayCommand RefreshModelsCommand { get; }
    public IAsyncRelayCommand<string?> GetModelCommand { get; }
    [ObservableProperty] public partial string BaselineSetupText { get; private set; } = "Open Models to check this computer and the download size.";
    [ObservableProperty] public partial string XlSetupText { get; private set; } = "Open Models to check this computer and the download size.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GetModelCommand))]
    public partial bool BaselineSetupBlocked { get; private set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GetModelCommand))]
    public partial bool XlSetupBlocked { get; private set; }
    public string BaselineActionLabel => ModelReady ? "Ready" : "Get";
    public string XlActionLabel => XlReady && ModelReady ? "Ready" : "Get";
    public bool IsModels => SelectedPage is "models" or "discover";

    private Task RefreshModelsAsync() => RunAsync("Checking your models…", async token =>
    {
        await UpdateInstallation(token);
        ShowSetupPlans(await _service.GetSetupPlansAsync(DataRoot, token));
    });

    private void ShowSetupPlans(IReadOnlyList<ModelSetupPlan> plans)
    {
        var baseline = plans.Single(plan => plan.Model == ModelChoice.BuiltInId);
        var xl = plans.Single(plan => plan.Model == ModelChoice.XlId);
        ModelReady = baseline.Ready;
        BaselineSetupBlocked = baseline.Blocker.Length > 0;
        XlSetupBlocked = xl.Blocker.Length > 0;
        BaselineSetupText = baseline.Summary;
        XlSetupText = xl.Summary;
        if (!xl.Ready && XlChoice is { IsReady: true } previous)
            ShowModels(AddedModels.Append(previous with { Status = "needs_verification", Message = xl.Blocker }).ToArray());
    }

    /// <summary>One cancellable operation for all prerequisites; the existing workers still own verification.</summary>
    private Task GetModelAsync(string? model) => RunAsync("Preparing your model…", async token =>
    {
        if (model is not (ModelChoice.BuiltInId or ModelChoice.XlId)) throw new InvalidDataException("Choose a model from Models.");
        // Recheck immediately before writing: cached readiness, space and hardware may have changed.
        var plans = await _service.GetSetupPlansAsync(DataRoot, token);
        ShowSetupPlans(plans);
        var plan = plans.Single(item => item.Model == model);
        if (plan.Blocker.Length > 0) throw new InvalidOperationException(plan.Blocker);
        if (model == ModelChoice.XlId && !plan.Ready)
        {
            // Remove stale ready choices before any prerequisite test; a failure cannot leave XL selected.
            ShowModels(AddedModels.Append((XlChoice ?? new(ModelChoice.XlId, "Stable Diffusion XL", "not_installed", ""))
                with { Status = "needs_verification" }).ToArray());
        }
        if (plan.RequiresBase)
        {
            Status = model == ModelChoice.XlId ? "Step 1 of 2 · Preparing the shared picture setup…" : "Preparing the picture setup…";
            await PrepareBaseAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ModelReady) throw new InvalidOperationException("The shared picture setup did not pass its checks. Select Get to try again.");
        }
        else ModelReady = true; // The worker just checked the saved runtime, files and verification evidence.

        if (model == ModelChoice.XlId && !plan.Ready)
        {
            Status = plan.RequiresBase ? "Step 2 of 2 · Preparing Stable Diffusion XL…" : "Preparing Stable Diffusion XL…";
            var state = await _service.InstallXlAsync(DataRoot, ObserveProgress("Stable Diffusion XL"), token);
            if (!state.IsReady) throw new InvalidOperationException(state.Message);
        }
        token.ThrowIfCancellationRequested();
        await UpdateModels(token, model);
        ShowSetupPlans(await _service.GetSetupPlansAsync(DataRoot, token));
        if (!ModelReady || (model == ModelChoice.XlId && !XlReady))
            throw new InvalidOperationException("The model is not ready after setup. Check again in Models for details.");
        Notice = "Your model is ready. Open Create to describe a picture.";
        Record(Notice);
        _ = RefreshDriveAsync();
    }, longRunning: true);
}
