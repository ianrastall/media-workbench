using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

static class WorkingFolderChecks
{
    public static async Task RunAsync(Action<bool, string> check, string output)
    {
        // An unreadable or invalid preference file must never authorize a fallback folder or be overwritten at startup.
        foreach (string body in new[] { "{broken", "null", "{}", new string('x', 65537),
            "{\"schemaVersion\":99,\"dataRoot\":\"D:\\\\elsewhere\",\"lastSearch\":\"image\"}" })
        {
            string state = Path.Combine(output, "invalid-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(state);
            string file = Path.Combine(state, "settings.json");
            File.WriteAllText(file, body);
            var service = new RecordingWorkbench();
            var vm = new MainViewModel(service, new SettingsStore(state), new FakeUpdates(null));
            check(vm.Notice.Contains("Could not load settings"), "Invalid preferences explain why a working folder must be chosen");
            await vm.StartupMaintenanceAsync();
            vm.CheckForUpdates = false;
            vm.KeepDownloadCaches = true;
            await vm.InstallCommand.ExecuteAsync(null); // ExecuteAsync can bypass CanExecute; the action must also refuse.
            await vm.RefreshLibraryCommand.ExecuteAsync(null);
            await vm.GenerateCommand.ExecuteAsync(null);
            await vm.AddToQueueCommand.ExecuteAsync(null);
            check(vm.NeedsWorkingFolder && vm.DataRoot.Length == 0 && vm.SelectedPage == "settings"
                && service.DataRoots.Count == 0 && service.ProfileRoots.Count == 0
                && !Directory.Exists(Path.Combine(state, "Data")) && File.ReadAllText(file) == body,
                "Invalid preferences never start data operations, create a fallback or overwrite the original file");
        }

        // A folder saved by older versions as the unchosen default still requires an explicit decision.
        var legacy = new SettingsStore(Path.Combine(output, "legacy-default"));
        legacy.Save(new AppSettings(1, Path.Combine(legacy.Folder, "Data"), "image"));
        var legacyVm = new MainViewModel(new RecordingWorkbench(), legacy, new FakeUpdates(null));
        check(legacyVm.NeedsWorkingFolder && legacyVm.DataRoot.Length == 0, "An unchosen legacy default never silently becomes the working folder");

        // Losing a saved folder on disk stops maintenance and setup without recreating it.
        var store = new SettingsStore(Path.Combine(output, "disconnected-settings"));
        string folder = Path.Combine(output, "disconnected-working-folder");
        store.Save(new AppSettings(1, folder, "image", DataRootChosen: true));
        Directory.Delete(folder);
        var workbench = new RecordingWorkbench();
        var missing = new MainViewModel(workbench, store, new FakeUpdates(null));
        await missing.StartupMaintenanceAsync();
        await missing.InstallCommand.ExecuteAsync(null);
        check(missing.NeedsWorkingFolder && missing.DataRoot == folder && missing.DataRootDraft == folder
            && missing.Notice.Contains("Reconnect") && !Directory.Exists(folder) && workbench.DataRoots.Count == 0,
            "A missing saved folder stays selected for recovery, but no operation recreates it or uses another folder");
        Directory.CreateDirectory(folder);
        missing.SaveSettingsCommand.Execute(null);
        check(!missing.NeedsWorkingFolder && missing.InstallCommand.CanExecute(null), "Choosing the reconnected folder explicitly restores work");
        Directory.Delete(folder);
        await missing.InstallCommand.ExecuteAsync(null);
        check(missing.NeedsWorkingFolder && !Directory.Exists(folder) && workbench.DataRoots.Count == 0,
            "A folder disappearing after startup is refused before a worker starts");

        // With only one drive, confirmation is still mandatory; failed drive enumeration is not permission.
        var singleService = new RecordingWorkbench();
        singleService.DriveChoices.Clear();
        singleService.DriveChoices.Add(new(Path.GetPathRoot(output)!, "Only drive", 900L << 30, 2000L << 30, "ssd", true));
        var single = new MainViewModel(singleService, new SettingsStore(Path.Combine(output, "single-drive-settings")), new FakeUpdates(null));
        await single.StartupMaintenanceAsync();
        await single.InstallCommand.ExecuteAsync(null);
        check(single.NeedsWorkingFolder && single.DataRootDraft.Length > 0 && singleService.DataRoots.Count == 0,
            "A single roomy system drive is suggested but never chosen automatically");
        singleService.DriveChoices.Clear();
        var noDrives = new MainViewModel(singleService, new SettingsStore(Path.Combine(output, "no-drive-settings")), new FakeUpdates(null));
        await noDrives.StartupMaintenanceAsync();
        check(noDrives.NeedsWorkingFolder && noDrives.DataRoot.Length == 0 && !noDrives.InstallCommand.CanExecute(null),
            "An empty drive list cannot bypass the working-folder choice");
    }
}
