using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

/// <summary>Several versions from one Generate: consecutive seeds, and one refused version does not stop the rest.</summary>
static class BatchChecks
{
    public static async Task RunAsync(Action<bool, string> check, string output)
    {
        var store = new SettingsStore(Path.Combine(output, "batch-settings"));
        store.Save(new AppSettings(1, Path.Combine(output, "batch-data"), "query"));
        var workbench = new RecordingWorkbench();
        var vm = new MainViewModel(workbench, store, new FakeUpdates(null)) { ModelReady = true, Prompt = "a lighthouse", Seed = 10 };
        await vm.GenerateCommand.ExecuteAsync(null);
        check(workbench.Generations.Count == 1 && vm.Notice == vm.LatestImage!.SavedNotice, "One picture is still the default");
        workbench.Generations.Clear();
        vm.PictureCountIndex = 2;
        await vm.GenerateCommand.ExecuteAsync(null);
        check(workbench.Generations.Select(item => item.Seed).SequenceEqual([10L, 11, 12, 13]) && vm.Notice.StartsWith("Made 4 versions with seeds 10 to 13"),
            "Four versions use consecutive seeds and say which");
        workbench.Generations.Clear();
        workbench.GenerationFailure = seed => seed == 11 ? new InvalidOperationException("The model safety checker rejected the result.") : null;
        await vm.GenerateCommand.ExecuteAsync(null);
        check(workbench.Generations.Count == 4 && vm.Notice.StartsWith("Made 3 of 4 versions") && vm.Notice.Contains("Seed 11: The model safety checker"),
            "A refused version is reported and the others are still made");
        workbench.GenerationFailure = _ => new InvalidOperationException("Install / verify Stable Diffusion 1.5 before generating.");
        await vm.GenerateCommand.ExecuteAsync(null);
        check(vm.Notice.Contains("Install / verify") && vm.Status == "Operation could not finish", "When every version fails, the reason is shown as a failure");
        workbench.GenerationFailure = null;
        workbench.Generations.Clear();
        vm.Seed = 4294967295;
        vm.PictureCountIndex = 1;
        await vm.GenerateCommand.ExecuteAsync(null);
        check(workbench.Generations.Select(item => item.Seed).SequenceEqual([4294967295L, 0]), "Seeds wrap around after the largest one");
    }
}
