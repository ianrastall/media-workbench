using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

/// <summary>Clean-up and drive-space rules: what each removes, what it must keep, crash leftovers, and the shared lock.</summary>
static class HousekeepingChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root, string output, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        string Touch(string folder, string relative, DateTime writtenUtc, string body = "x")
        {
            string path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
            File.SetLastWriteTimeUtc(path, writtenUtc);
            return path;
        }

        // Painted-area hand-overs: a crash leaves them; only old files with the app's own names go.
        string painted = Path.Combine(output, "painted");
        string stale = Touch(painted, new string('a', 32) + ".png", now.AddHours(-2));
        string fresh = Touch(painted, new string('b', 32) + ".png", now.AddMinutes(-1));
        string foreign = Touch(painted, "holiday.png", now.AddDays(-30));
        check(Housekeeping.SweepPaintedHandOvers(painted, Housekeeping.PaintedMaxAge, now) == 1 && !File.Exists(stale) && File.Exists(fresh) && File.Exists(foreign),
            "Startup sweeps painted-area files a crash left, but not one in use or any other file");
        File.Delete(fresh); File.Delete(foreign);
        Housekeeping.SweepPaintedHandOvers(painted, Housekeeping.PaintedMaxAge, now);
        check(!Directory.Exists(painted), "An emptied hand-over folder is removed too");

        // Update installers: finished once this version runs; a newer unused one waits a week.
        string updates = Path.Combine(output, "update");
        string current = Touch(updates, "MediaWorkbench-0.2.0-alpha.12-win-x64-setup.exe", now);
        string older = Touch(updates, "MediaWorkbench-0.2.0-alpha.9-win-x64-setup.exe", now.AddDays(-20));
        string partial = Touch(updates, "MediaWorkbench-0.3.0-win-x64-setup.exe.partial", now);
        string newer = Touch(updates, "MediaWorkbench-0.2.0-alpha.13-win-x64-setup.exe", now.AddHours(-1));
        string abandoned = Touch(updates, "MediaWorkbench-0.2.0-alpha.14-win-x64-setup.exe", now.AddDays(-8));
        string other = Touch(updates, "notes.txt", now.AddDays(-90));
        int removed = Housekeeping.RemoveFinishedInstallers(updates, "0.2.0-alpha.12", now);
        check(removed == 4 && !File.Exists(current) && !File.Exists(older) && !File.Exists(partial) && !File.Exists(abandoned)
            && File.Exists(newer) && File.Exists(other), "Installers for this version or older, interrupted downloads, and week-old unused installers are deleted");
        using (File.Open(newer, FileMode.Open, FileAccess.Read, FileShare.None))
            check(Housekeeping.RemoveFinishedInstallers(updates, "0.2.0-alpha.13", now) == 0 && File.Exists(newer),
                "An installer that is still running is left for the next start");

        // Native clean-up through the shipped DLL.
        var layout = JsonSerializer.Deserialize<StorageLayout>(File.ReadAllText(Path.Combine(root, "profiles", "storage-layout.json")), Wire.Json)!;
        string data = Path.Combine(output, "tidy café 森林");
        string Job(int number) => number.ToString("x32");
        for (int number = 1; number <= 52; number++)
        {
            Touch(data, $"jobs/{Job(number)}.json", now.AddMinutes(number - 100), $$"""{"id":"{{Job(number)}}","status":"cancelled"}""");
            Touch(data, $"artifacts/{Job(number)}/source.png", now);
            Touch(data, $"logs/generate-{Job(number)}.log", now);
        }
        Touch(data, $"jobs/{Job(99)}.json", now.AddDays(-400), $$"""{"id":"{{Job(99)}}","status":"succeeded"}""");
        Touch(data, $"artifacts/{Job(99)}/image.png", now.AddDays(-400));
        Touch(data, "logs/verify-old.log", now.AddDays(-31));
        Touch(data, "bootstrap/python.zip.partial", now.AddDays(-4));
        Touch(data, "bootstrap/uv.zip.partial", now);
        Touch(data, "cache/uv/wheel.whl", now);
        Touch(data, $"runtimes/{layout.RuntimeProfileId}/python/python.exe", now);
        Touch(data, "my notes.txt", now.AddDays(-400));
        Touch(data, ".removing/cache-1/leftover.whl", now);

        using (var owner = new FileStream(Path.Combine(data, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The data-folder lock is a Windows byte-range lock.");
            owner.Lock(0, 1); // The byte the Python worker locks; .NET uses LockFile like msvcrt.locking.
            bool busy = false;
            try { NativeCore.TidyDataFolder(data, layout, true, true, token); } catch (InvalidOperationException) { busy = true; }
            check(busy && File.Exists(Path.Combine(data, "cache/uv/wheel.whl")) && File.Exists(Path.Combine(data, "logs/verify-old.log")),
                "Clean-up waits for the data-folder owner and changes nothing");
            owner.Unlock(0, 1);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { NativeCore.TidyDataFolder(data, layout, true, true, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped && Directory.Exists(Path.Combine(data, ".removing")), "Cancelled clean-up removes nothing");
        }
        var service = new WorkbenchService(root, Path.Combine(output, "no-python.exe"));
        var report = await service.TidyAsync(data, installationVerified: false, removeDownloadCaches: true, token);
        check(report.JobRecords == 2 && !File.Exists(Path.Combine(data, $"jobs/{Job(1)}.json")) && !Directory.Exists(Path.Combine(data, $"artifacts/{Job(2)}"))
            && File.Exists(Path.Combine(data, $"jobs/{Job(3)}.json")) && File.Exists(Path.Combine(data, $"artifacts/{Job(3)}/source.png")) && File.Exists(Path.Combine(data, $"logs/generate-{Job(3)}.log")),
            "Unfinished jobs beyond the 50 Library lists go with their inputs and logs; listed ones keep theirs");
        check(File.Exists(Path.Combine(data, $"artifacts/{Job(99)}/image.png")) && File.Exists(Path.Combine(data, $"jobs/{Job(99)}.json"))
            && File.Exists(Path.Combine(data, "my notes.txt")) && !File.Exists(Path.Combine(data, "logs/verify-old.log")),
            "Images, finished records, and the user's own files are kept; month-old logs go");
        check(!File.Exists(Path.Combine(data, "bootstrap/python.zip.partial")) && File.Exists(Path.Combine(data, "bootstrap/uv.zip.partial"))
            && File.Exists(Path.Combine(data, "cache/uv/wheel.whl")) && !Directory.Exists(Path.Combine(data, ".removing")) && !report.DownloadCaches,
            "Stale downloads and an interrupted removal are cleared; caches stay until setup is verified");
        report = await service.TidyAsync(data, installationVerified: true, removeDownloadCaches: true, token);
        check(report.DownloadCaches && !Directory.Exists(Path.Combine(data, "cache")) && !Directory.Exists(Path.Combine(data, "bootstrap"))
            && File.Exists(Path.Combine(data, $"runtimes/{layout.RuntimeProfileId}/python/python.exe")), "A verified installation's download caches are removed, not the runtime");
        string missing = Path.Combine(output, "never-created");
        check((await service.TidyAsync(missing, true, true, token)).RemovedFiles == 0 && !Directory.Exists(missing), "Clean-up of a missing data folder creates nothing");

        var drive = await service.GetDriveAsync(Path.Combine(missing, "deeper"), token);
        check(drive.FreeBytes > 0 && drive.TotalBytes >= drive.FreeBytes && drive.DriveKind is "ssd" or "hdd" or "usb" or "removable" or "network" or "unknown" && !Directory.Exists(missing),
            "Drive information reads free space and type without creating the folder");
        check(new DriveStatus(4L << 30, 500L << 30, "hdd") is { IsLow: true, IsSlow: true } low && low.Warning.StartsWith("Only 4.00 GiB is free")
            && low.Warning.Contains("move the data folder in Settings") && low.SpeedNote.Contains("on a hard disk. Models load faster from a solid-state drive"),
            "Low space and a hard disk are explained in plain words");
        check(new DriveStatus(6L << 30, 500L << 30, "ssd") is { IsLow: false, IsSlow: false, Warning: "", SpeedNote: "" }, "A roomy SSD shows no warnings");

        // The view model: the setting is saved, startup tidies with the right permission, and warnings follow the drive.
        var store = new SettingsStore(Path.Combine(output, "housekeeping-settings"));
        string saved = Path.Combine(output, "housekeeping-data");
        store.Save(new AppSettings(1, saved, "image"));
        var workbench = new RecordingWorkbench { Drive = new(3L << 30, 100L << 30, "hdd"), TidyResult = new(9, 4L << 30, 2, 1, 1, true, 0) };
        var vm = new MainViewModel(workbench, store, new FakeUpdates(null));
        check(!vm.KeepDownloadCaches && store.Load().KeepDownloadCaches == false, "Download caches are removed by default");
        await vm.StartupMaintenanceAsync();
        check(workbench.Tidies.SequenceEqual([(true, true)]) && vm.Notice.StartsWith("Removed 4.00 GiB of setup downloads") && !vm.IsBusy,
            "Startup tidies a verified installation, removing its caches, and says how much it freed");
        check(vm.HasDriveWarning && vm.DriveWarning.StartsWith("Only 3.00 GiB") && vm.HasDriveSpeedNote, "Create and Settings warn about a nearly full hard disk");
        vm.KeepDownloadCaches = true;
        check(store.Load().KeepDownloadCaches && store.Load().DataRoot == saved, "Keeping download caches is saved with the other settings");
        workbench.InstallCleanedBytes = 0;
        await vm.InstallCommand.ExecuteAsync(null);
        check(workbench.LastKeepDownloadCaches == true, "Setup keeps its caches when Settings says so");
        await vm.StartupMaintenanceAsync();
        check(workbench.Tidies[^1] == (true, false), "Startup keeps caches when Settings says so");
        vm.KeepDownloadCaches = false;
        workbench.InstallCleanedBytes = 3L << 30;
        await vm.InstallCommand.ExecuteAsync(null);
        check(workbench.LastKeepDownloadCaches == false && vm.Notice.EndsWith("Removed 3.00 GiB of setup downloads that are no longer needed."),
            "After setup, the notice says how much it cleaned up");
        workbench.Drive = new(200L << 30, 500L << 30, "ssd");
        await vm.RefreshDriveAsync();
        check(!vm.HasDriveWarning && !vm.HasDriveSpeedNote, "Warnings clear when space is freed or the folder moves to an SSD");
        workbench.TidyFailure = new InvalidOperationException("Another operation is using this data folder.");
        string before = vm.Notice;
        await vm.StartupMaintenanceAsync();
        check(vm.Notice == before && vm.Status == "Ready", "A busy data folder skips the startup tidy quietly");
    }
}
