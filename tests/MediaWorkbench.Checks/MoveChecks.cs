using System.Security.Cryptography;
using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

/// <summary>Moving the data folder: a checked plan, a verified copy, an unchanged source, and recovery after a crash.</summary>
static class MoveChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root, string python, string output, CancellationToken token)
    {
        // The settings flow, against a scripted service.
        string oldRoot = Path.Combine(output, "move-old"), newRoot = Path.Combine(output, "move-new");
        var store = new SettingsStore(Path.Combine(output, "move-settings"));
        store.Save(new AppSettings(1, oldRoot, "query"));
        var workbench = new RecordingWorkbench();
        var vm = new MainViewModel(workbench, store, new FakeUpdates(null));
        check(!vm.CheckMoveCommand.CanExecute(null), "Moving is offered only when a different folder is typed");
        vm.DataRootDraft = newRoot;
        await vm.CheckMoveCommand.ExecuteAsync(null);
        check(vm.MovePlanText == workbench.MovePlan.Summary && vm.MoveDataCommand.CanExecute(null) && vm.DataRoot == oldRoot && workbench.Moves.Count == 0,
            "Checking a move explains it and changes nothing yet");
        vm.DataRootDraft = newRoot + "-other";
        check(!vm.MoveDataCommand.CanExecute(null) && !vm.HasMovePlan, "Typing another folder invalidates the checked move");
        vm.DataRootDraft = newRoot;
        workbench.MoveFailure = new IOException("The new drive was disconnected.");
        await vm.CheckMoveCommand.ExecuteAsync(null);
        await vm.MoveDataCommand.ExecuteAsync(null);
        check(vm.DataRoot == oldRoot && store.Load() is { DataRoot: var keptRoot, PendingMove: null } && keptRoot == oldRoot && vm.Notice.Contains("disconnected"),
            "A failed move keeps the current data folder and clears the pending move");
        workbench.MoveFailure = null;
        await vm.MoveDataCommand.ExecuteAsync(null);
        check(workbench.Moves[^1] == (oldRoot, newRoot) && vm.DataRoot == newRoot && store.Load() is { DataRoot: var movedRoot, PendingMove: null } && movedRoot == newRoot
            && vm.Notice.Contains("was not changed") && !vm.HasMovePlan, "A verified move switches to the new folder and says the old one is kept");
        workbench.MovePlan = workbench.MovePlan with { Blocker = "The new folder already has files in it. Choose an empty or new folder." };
        vm.DataRootDraft = oldRoot;
        await vm.CheckMoveCommand.ExecuteAsync(null);
        check(!vm.MoveDataCommand.CanExecute(null) && vm.MovePlanText.StartsWith("The new folder already has files"), "A blocked move shows why and cannot start");

        // A crash during a move: the saved pending move is cleaned up at the next start, and the folder never switched.
        store.Save(new AppSettings(1, oldRoot, "query", PendingMove: newRoot + "-crashed"));
        var restarted = new MainViewModel(workbench, store, new FakeUpdates(null));
        await restarted.StartupMaintenanceAsync();
        check(workbench.Abandoned.Contains((oldRoot, newRoot + "-crashed")) && store.Load() is { PendingMove: null, DataRoot: var stayed } && stayed == oldRoot
            && restarted.Notice.Contains("did not finish"), "An interrupted move's partial copy is removed at the next start and the data folder is unchanged");

        // Before setup's first download, the default folder on a fuller drive is a question, never a silent choice.
        string here = Path.GetPathRoot(output)!;
        var fresh = new SettingsStore(Path.Combine(output, "drive-question"));
        var asking = new RecordingWorkbench { InstallationStatus = "not_installed" };
        asking.DriveChoices.Clear();
        asking.DriveChoices.Add(new(here, "Windows", 30L << 30, 250L << 30, "ssd", true));
        asking.DriveChoices.Add(new(@"Z:\", "Big", 900L << 30, 2000L << 30, "hdd", false));
        var first = new MainViewModel(asking, fresh, new FakeUpdates(null));
        await first.RefreshInstallationCommand.ExecuteAsync(null);
        check(first.NeedsDriveChoice && first.DriveChoiceText.Contains(@"Z:\ has 900.00 GiB free") && !first.InstallCommand.CanExecute(null),
            "Setup asks where to work first when the default folder is on a drive with less room than another");
        first.UseDriveCommand.Execute(asking.DriveChoices[1]);
        check(first.DataRootDraft == @"Z:\Media Workbench" && first.DataRoot == AppSettings.Default(fresh.Folder).DataRoot,
            "Use this drive fills in a folder on that drive without saving or moving anything");
        first.DataRootDraft = first.DataRoot;
        first.KeepDataDriveCommand.Execute(null);
        check(!first.NeedsDriveChoice && first.InstallCommand.CanExecute(null) && fresh.Load().DataRootChosen, "Keeping the drive is remembered and lets setup start");
        var again = new MainViewModel(asking, fresh, new FakeUpdates(null));
        await again.RefreshInstallationCommand.ExecuteAsync(null);
        check(!again.NeedsDriveChoice, "A kept choice is not asked again");
        var older = new SettingsStore(Path.Combine(output, "drive-older"));
        older.Save(new AppSettings(1, Path.Combine(output, "picked-earlier"), "query"));
        var olderVm = new MainViewModel(asking, older, new FakeUpdates(null));
        await olderVm.RefreshInstallationCommand.ExecuteAsync(null);
        check(!olderVm.NeedsDriveChoice, "A folder chosen in an earlier version counts as chosen");
        var drives = await new WorkbenchService(root, python).GetDrivesAsync(token);
        check(drives.Any(drive => string.Equals(drive.Root, here, StringComparison.OrdinalIgnoreCase) && drive.FreeBytes > 0 && drive.TotalBytes >= drive.FreeBytes)
            && drives.Count(drive => drive.IsSystem) <= 1, "The real drive list includes the checks' drive with its free space");

        // The real Rust move, then the real Python Library and Rust export from the moved folder.
        string source = Path.Combine(output, "move-real café"), jobId = new string('c', 32);
        byte[] picture = [9, 8, 7];
        Directory.CreateDirectory(Path.Combine(source, "jobs"));
        Directory.CreateDirectory(Path.Combine(source, "artifacts", jobId));
        await File.WriteAllBytesAsync(Path.Combine(source, "artifacts", jobId, "image.png"), picture, token);
        await File.WriteAllTextAsync(Path.Combine(source, "jobs", jobId + ".json"), JsonSerializer.Serialize(new
        {
            id = jobId, status = "succeeded", kind = "generate", imagePath = jobId + "/image.png", imageSha256 = Convert.ToHexStringLower(SHA256.HashData(picture)),
            settings = new GenerationSettings("Moved forest", "", 77, 5), completedUtc = "2026-10-18T12:00:00Z", seconds = 1, gpu = "Test GPU"
        }, Wire.Json), token);
        var service = new WorkbenchService(root, python);
        check((await service.GetLibraryAsync(source, token)).Total == 1, "The folder to move has a Library image and index");
        string temp = Path.Combine(WorkFolders.Temporary(source), "work", "leftover.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await File.WriteAllBytesAsync(temp, [1], token);
        string destination = Path.Combine(output, "moved here", "data");
        var plan = await service.PlanDataMoveAsync(source, destination, token);
        check(plan is { CanMove: true, Files: 2 } && !Directory.Exists(destination), "The real plan counts the files to copy and creates nothing");
        long reported = 0;
        await service.MoveDataAsync(source, destination, new SynchronousProgress(value => reported = value.Completed), token);
        check(reported == plan.Bytes * 2 && File.ReadAllBytes(Path.Combine(destination, "artifacts", jobId, "image.png")).SequenceEqual(picture)
            && File.Exists(Path.Combine(source, "artifacts", jobId, "image.png")) && !Directory.Exists(WorkFolders.Temporary(destination))
            && !Directory.Exists(Path.Combine(destination, "library")), "The real move copies and verifies every byte, leaves the source, and skips temporary work and the index");
        var moved = await service.GetLibraryAsync(destination, token, new("moved"));
        check(moved.Total == 1 && moved.Images.Single().Settings.Seed == 77 && moved.Images.Single().ImagePath.StartsWith(destination, StringComparison.OrdinalIgnoreCase),
            "Library rebuilds its index in the moved folder and finds images there");
        string exported = Path.Combine(output, "exported after move.png");
        await service.ExportImageAsync(destination, new(jobId, exported), new Progress<OperationProgress>(), token);
        check(File.ReadAllBytes(exported).SequenceEqual(picture), "A moved image still passes its recorded checksum when saved as a copy");
        check(!(await service.PlanDataMoveAsync(source, destination, token)).CanMove, "Moving into a folder that already has files is refused");
    }

    /// <summary>Reports on the calling thread, so the last value is known when the move returns.</summary>
    private sealed class SynchronousProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
