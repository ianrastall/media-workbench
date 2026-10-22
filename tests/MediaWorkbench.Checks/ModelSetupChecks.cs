using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

static class ModelSetupChecks
{
    public static async Task RunAsync(Action<bool, string> check, string output)
    {
        (MainViewModel Vm, RecordingWorkbench Service) Fixture()
        {
            string folder = Path.Combine(output, "model-setup-" + Guid.NewGuid().ToString("N"));
            var settings = new SettingsStore(folder + "-profile");
            settings.Save(new(1, folder, "image", DataRootChosen: true));
            var service = new RecordingWorkbench { InstallationStatus = "not_installed" };
            return (new(service, settings, new FakeUpdates(null)), service);
        }

        var (first, firstService) = Fixture();
        first.KeepDownloadCaches = true;
        await first.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(firstService.SetupCalls.SequenceEqual(["plan", "base", "xl", "plan"]) && firstService.LastKeepDownloadCaches == true
            && first.ModelReady && first.XlReady && first.SelectedModelId == ModelChoice.XlId,
            "One XL Get checks the full plan, prepares the shared setup, tests XL and selects only the ready model");
        check(first.XlSetupText == "Ready to use on this computer." && first.XlActionLabel == "Ready"
            && !first.GetModelCommand.CanExecute(ModelChoice.XlId), "Ready models show Ready instead of offering another download");

        var (reuse, reuseService) = Fixture();
        reuseService.InstallationStatus = "ready";
        await reuse.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(reuseService.SetupCalls.SequenceEqual(["plan", "xl", "plan"]), "XL Get reuses a verified shared setup");

        var (blocked, blockedService) = Fixture();
        blockedService.SetupPlansOverride = [
            new(ModelChoice.BuiltInId, false, true, 5L << 30, 20L << 30, 20L << 30, "", []),
            new(ModelChoice.XlId, false, true, 12L << 30, 29L << 30, 20L << 30, "Free up space first.", [])];
        await blocked.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(blockedService.SetupCalls.SequenceEqual(["plan"]) && blocked.Notice == "Free up space first." && !blocked.ModelReady,
            "A combined preflight blocker stops before any prerequisite downloads");
        check(!blocked.GetModelCommand.CanExecute(ModelChoice.XlId), "A blocked model shows its reason without offering Get again until it is checked");

        var (changedHardware, hardwareService) = Fixture();
        hardwareService.InstallationStatus = "ready";
        hardwareService.Xl = hardwareService.Xl with { Status = "ready" };
        hardwareService.SetupPlansOverride = [
            new(ModelChoice.BuiltInId, true, false, 0, 0, 100L << 30, "", []),
            new(ModelChoice.XlId, false, false, 0, 0, 100L << 30, "More memory is needed.", [])];
        await changedHardware.RefreshModelsCommand.ExecuteAsync(null);
        check(changedHardware.ModelReady && !changedHardware.XlReady && changedHardware.XlActionLabel == "Get"
            && !changedHardware.GetModelCommand.CanExecute(ModelChoice.XlId) && changedHardware.ModelChoices.All(model => !model.IsXl),
            "An XL-specific hardware blocker removes old ready evidence from the picker while keeping the smaller model available");

        var (failedBase, baseService) = Fixture();
        baseService.BaseInstallResult = "failed";
        await failedBase.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(baseService.SetupCalls.SequenceEqual(["plan", "base"]) && !failedBase.ModelReady && !failedBase.XlReady,
            "An unverified shared setup never starts XL or labels either model ready");

        var (cancelled, cancelService) = Fixture();
        var baseGate = new TaskCompletionSource();
        cancelService.InstallGate = baseGate.Task;
        var pending = cancelled.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(cancelled.IsBusy && !cancelled.GetModelCommand.CanExecute(ModelChoice.BuiltInId), "One Get owns the operation through prerequisite setup");
        cancelled.CancelCommand.Execute(null);
        await pending;
        check(cancelled.Status == "Cancelled" && cancelService.XlInstalls == 0 && !cancelled.XlReady && !cancelled.IsBusy,
            "Cancelling shared setup never starts XL and releases the operation for retry");
        cancelService.InstallGate = null;
        await cancelled.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(cancelled.XlReady, "Get can retry successfully after cancellation");

        var (failedXl, xlService) = Fixture();
        xlService.InstallationStatus = "ready";
        xlService.XlInstallFailure = new InvalidOperationException("GPU test failed");
        await failedXl.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        check(failedXl.ModelReady && !failedXl.XlReady && failedXl.SelectedModelId == ModelChoice.BuiltInId
            && failedXl.ModelChoices.All(model => !model.IsXl) && failedXl.Notice == "GPU test failed",
            "A failed XL test preserves the ready shared setup without exposing XL in Create");

        var (cancelXl, cancelXlService) = Fixture();
        cancelXlService.InstallationStatus = "ready";
        cancelXlService.XlInstallGate = new TaskCompletionSource().Task;
        var xlPending = cancelXl.GetModelCommand.ExecuteAsync(ModelChoice.XlId);
        cancelXl.CancelCommand.Execute(null);
        await xlPending;
        check(cancelXl.Status == "Cancelled" && cancelXl.ModelReady && !cancelXl.XlReady && cancelXl.SelectedModelId == ModelChoice.BuiltInId,
            "Cancelling XL keeps the verified prerequisite and never selects the unfinished model");

        var (single, singleService) = Fixture();
        await single.GetModelCommand.ExecuteAsync(ModelChoice.BuiltInId);
        check(single.ModelReady && singleService.XlInstalls == 0, "The smaller model's Get does not download XL");

        ModelSetupPlan Plan(string id, bool ready = false, string blocker = "") => new(id, ready, !ready, 0, 0, 100L << 30, blocker, []);
        var gtx = RecordingWorkbench.GtxProfile;
        var roomy = gtx with {
            Hardware = gtx.Hardware with { Gpu = new("NVIDIA RTX", 16384), RamMiB = 32768,
                Drive = new("working", 100L << 30, 200L << 30, "ssd") },
            Recommended = gtx.Recommended with { Settings = new("fp16", "Balanced", "", "float16") }
        };
        string Advice(SystemProfile? profile, bool smallReady = false, bool xlReady = false, string smallBlocker = "", string xlBlocker = "") =>
            PictureRecommendation.Describe(profile, [Plan(ModelChoice.BuiltInId, smallReady, smallBlocker), Plan(ModelChoice.XlId, xlReady, xlBlocker)]);
        check(Advice(gtx).StartsWith("Suggested starting model: Stable Diffusion 1.5.")
            && Advice(gtx).Contains("memory-saving settings can make it slow") && Advice(gtx).Contains("solid-state drive")
            && Advice(gtx).Contains("must pass before it is ready"), "Limited graphics memory and a hard disk get lighter-model advice without a speed or readiness claim");
        check(Advice(roomy).StartsWith("Suggested starting model: Stable Diffusion XL.")
            && Advice(roomy, true).StartsWith("Suggested starting model: Stable Diffusion 1.5.")
            && Advice(roomy, true, true).StartsWith("Suggested starting model: Stable Diffusion XL."),
            "Roomy hardware favors XL while an already verified smaller model avoids an unnecessary download");
        check(Advice(gtx, false, true).StartsWith("Suggested starting model: Stable Diffusion XL.")
            && Advice(gtx, false, true).Contains("memory-saving settings"), "An already verified XL is preferred over downloading another model, with honest slow-settings advice");
        foreach (var limited in new[] {
            roomy with { Hardware = roomy.Hardware with { RamMiB = 16384 } },
            roomy with { Hardware = roomy.Hardware with { Gpu = new("NVIDIA RTX", 8192) } },
            roomy with { Hardware = roomy.Hardware with { Drive = null } },
            roomy with { Hardware = roomy.Hardware with { Drive = roomy.Hardware.Drive! with { Kind = "unknown" } } },
            roomy with { Recommended = gtx.Recommended } })
            check(Advice(limited).StartsWith("Suggested starting model: Stable Diffusion 1.5."), "Lower memory, unknown drive or full precision keeps the lighter suggestion");
        check(Advice(roomy, true, true, xlBlocker: "Not enough memory").StartsWith("Suggested starting model: Stable Diffusion 1.5.")
            && Advice(roomy, true, true, "Free up space", "Free up space") == "Before choosing a model: Free up space",
            "Current setup blockers override old readiness and roomy hardware");
        check(Advice(null).Contains("hardware could not be read")
            && Advice(gtx with { Hardware = gtx.Hardware with { Drive = gtx.Hardware.Drive! with { FreeBytes = 1L << 30 } } }, true).Contains("low on space"),
            "Unknown hardware and low drive space are explained without inventing timing estimates");
        check(Advice(gtx, smallBlocker: "Unavailable").StartsWith("Suggested starting model: Stable Diffusion XL.")
            && !Advice(gtx, smallBlocker: "Unavailable").Contains("already passed"),
            "The only eligible uninstalled choice is never described as already verified");

        var (advised, advisedService) = Fixture();
        advisedService.InstallationStatus = "ready";
        advisedService.Xl = advisedService.Xl with { Status = "ready" };
        advisedService.Profile = roomy;
        advised.Steps = 31;
        await advised.RefreshModelsCommand.ExecuteAsync(null);
        check(advised.PictureRecommendationText.StartsWith("Suggested starting model: Stable Diffusion XL.")
            && advised.SelectedModelId == ModelChoice.BuiltInId && advised.Steps == 31 && advisedService.Generations.Count == 0,
            "Refreshing advice preserves the selected model and creative controls and never starts inference");
        advisedService.ProfileFailure = new IOException("Hardware unavailable");
        await advised.RefreshModelsCommand.ExecuteAsync(null);
        check(advised.PictureRecommendationText.Contains("hardware could not be read"), "A failed hardware refresh discards the previous recommendation");
    }
}
