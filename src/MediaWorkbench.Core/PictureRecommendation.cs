namespace MediaWorkbench.Core;

/// <summary>Advice only. Setup plans own readiness and eligibility; this never changes inference settings.</summary>
public static class PictureRecommendation
{
    public static string Describe(SystemProfile? profile, IReadOnlyList<ModelSetupPlan> plans)
    {
        var small = plans.FirstOrDefault(plan => plan.Model == ModelChoice.BuiltInId);
        var large = plans.FirstOrDefault(plan => plan.Model == ModelChoice.XlId);
        if (small is null || large is null) return "Select Check models to get a recommendation for this computer.";
        bool smallUsable = small.Blocker.Length == 0;
        bool largeUsable = large.Blocker.Length == 0;
        if (!smallUsable && !largeUsable)
            return "Before choosing a model: " + small.Blocker;
        if (profile is null)
            return "This computer's hardware could not be read. Check models again before choosing; each model still needs its setup checks.";

        // This is a conservative product preference, not a new runtime profile or a speed estimate.
        // Unknown hardware/drive and memory-saving graphics cards keep the lighter starting point.
        bool roomy = profile.Hardware.Gpu?.VramMiB >= 12288 && profile.Hardware.RamMiB >= 32768
            && profile.Hardware.Drive?.Kind == "ssd" && profile.Recommended.Settings?.Precision == "float16";
        bool smallReady = smallUsable && small.Ready;
        bool largeReady = largeUsable && large.Ready;
        bool chooseLarge = largeUsable && (!smallUsable || largeReady && !smallReady || roomy && (largeReady || !smallReady));
        string name = chooseLarge ? "Stable Diffusion XL" : "Stable Diffusion 1.5";
        bool ready = chooseLarge ? largeReady : smallReady;
        string reason = chooseLarge
            ? roomy ? "Your graphics memory, system memory and solid-state drive make it a suitable starting point for larger pictures."
                : largeReady ? "It has already passed its checks here, so you can start without another download. Larger pictures can take longer with memory-saving settings."
                    : "The smaller model is currently unavailable. XL makes larger pictures, but memory-saving settings can make it slow."
            : smallReady && !largeReady ? "It has already passed its checks here, so you can start without another download."
                : "Its smaller pictures need less memory and are a lighter starting point on this computer.";
        string memory = !roomy && !chooseLarge
            ? " XL remains an option when its checks allow it, but memory-saving settings can make it slow."
            : "";
        string drive = profile.DriveAdvice.Length > 0 ? " " + profile.DriveAdvice : "";
        string space = profile.Hardware.Drive?.FreeBytes is { } free && free < DriveStatus.LowSpaceBytes
            ? " The working drive is low on space; free up space before making pictures." : "";
        return $"Suggested starting model: {name}. {reason}{memory}{drive}{space} "
            + (ready ? "It is ready to choose in Create." : "Select Get below; files, software and a real test picture must pass before it is ready.");
    }
}
