using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

/// <summary>Several versions from one Generate: consecutive seeds, and one refused version does not stop the rest.</summary>
static class BatchChecks
{
    public static async Task RunAsync(Action<bool, string> check, string output, string root, string python)
    {
        await QueueAsync(check, output, root, python);
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

    /// <summary>Waiting pictures: saved without generating, kept across a restart, made only on Run queue.</summary>
    private static async Task QueueAsync(Action<bool, string> check, string output, string root, string python)
    {
        var store = new SettingsStore(Path.Combine(output, "queue-settings"));
        store.Save(new AppSettings(1, Path.Combine(output, "queue-data"), "query"));
        var workbench = new RecordingWorkbench();
        var vm = new MainViewModel(workbench, store, new FakeUpdates(null)) { ModelReady = true, Prompt = "a harbour at dawn", Seed = 30, PictureCountIndex = 1 };
        await vm.AddToQueueCommand.ExecuteAsync(null);
        check(workbench.Waiting.Select(item => item.Settings.Seed).SequenceEqual([30L, 31]) && workbench.Generations.Count == 0 && vm.Queue.Count == 2
            && vm.QueueText == "2 pictures waiting" && vm.RunQueueCommand.CanExecute(null), "Add to queue saves each version without generating");
        var restarted = new MainViewModel(workbench, store, new FakeUpdates(null));
        await restarted.RefreshInstallationCommand.ExecuteAsync(null);
        check(restarted.Queue.Count == 2 && workbench.Generations.Count == 0, "Waiting pictures are listed after a restart and do not start by themselves");
        workbench.GenerationFailure = seed => seed == 30 ? new InvalidOperationException("The model safety checker rejected the result.") : null;
        await restarted.RunQueueCommand.ExecuteAsync(null);
        check(workbench.Generations.Select(item => item.Seed).SequenceEqual([30L, 31]) && workbench.Waiting.Count == 0 && restarted.Queue.Count == 0
            && restarted.Notice.StartsWith("Made 1 of 2 waiting pictures") && restarted.Notice.Contains("safety checker"),
            "Run queue makes them oldest first, and a failed one does not stop the rest");
        workbench.GenerationFailure = null;
        workbench.Generations.Clear();
        await restarted.AddToQueueCommand.ExecuteAsync(null);
        var gate = new TaskCompletionSource();
        workbench.GenerationGate = gate.Task;
        var running = restarted.RunQueueCommand.ExecuteAsync(null);
        restarted.CancelCommand.Execute(null);
        await running;
        workbench.GenerationGate = null;
        check(workbench.Waiting.Count == 1 && restarted.Status == "Cancelled", "Cancel stops the current picture and leaves it waiting");
        await restarted.RemoveQueuedCommand.ExecuteAsync(restarted.Queue[0]);
        check(workbench.Waiting.Count == 0 && !restarted.HasQueue, "A waiting picture can be removed");

        // The real worker keeps its own copy of a starting picture for a waiting picture.
        string data = Path.Combine(output, "queue-real-data");
        string picture = Path.Combine(output, "queue-start.png");
        await File.WriteAllBytesAsync(picture, new MaskCanvas(64, 64).EncodePng());
        var service = new WorkbenchService(root, python);
        var waiting = await service.QueuePictureAsync(data, new GenerationSettings("a quiet street", "", 5, 12, SourceImage: picture, Strength: 0.6), CancellationToken.None);
        File.Delete(picture);
        var listed = await service.GetQueueAsync(data, CancellationToken.None);
        check(listed.Count == 1 && listed[0].Id == waiting.Id && File.Exists(listed[0].Settings.SourceImage) && !listed[0].MissingInputs
            && listed[0].Settings.SourceImage!.StartsWith(Path.Combine(data, "queue"), StringComparison.OrdinalIgnoreCase),
            "The real worker keeps a waiting picture's own copy of its starting picture");
        check((await service.RemoveQueuedAsync(data, waiting.Id, CancellationToken.None)).Count == 0 && !Directory.Exists(Path.Combine(data, "queue", waiting.Id)),
            "Removing a waiting picture removes its copies");
    }
}
