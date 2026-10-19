using System.Security.Cryptography;
using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

static class PreferencesChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        string Area(string name) => Path.Combine(output, "preferences-" + name);
        string WriteLegacy(string state, AppSettings settings)
        {
            Directory.CreateDirectory(state);
            string file = Path.Combine(state, "settings.json");
            File.WriteAllText(file, JsonSerializer.Serialize(settings, Wire.Json));
            return file;
        }
        void Pointer(string state, object value)
        {
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, SettingsStore.PointerName), JsonSerializer.Serialize(value, Wire.Json));
        }

        string state = Area("profile"), root = Area("work café 森林");
        var store = new SettingsStore(state);
        store.RecordStartupError(new InvalidOperationException("Before folder choice"));
        check(store.Load().DataRoot.Length == 0 && !Directory.Exists(state), "Before choosing a folder, startup errors stay in memory and create no profile files");
        var original = new AppSettings(1, root, "watercolor", CheckForUpdates: false, KeepDownloadCaches: true, DataRootChosen: true);
        store.Choose(original);
        check(store.Folder == SettingsStore.Preferences(root) && store.Load() == original
            && File.ReadAllText(Path.Combine(SettingsStore.StartupLogs(root), "startup.log")).Contains("Before folder choice")
            && Directory.GetFiles(state).Select(Path.GetFileName).SequenceEqual([SettingsStore.PointerName]),
            "Preferences and buffered diagnostics use the working folder; the profile contains only the location pointer");
        store.Save(original with { LastSearch = "changed" });
        check(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(store.Folder, "settings.previous.json")), Wire.Json) == original,
            "Replacing preferences keeps the previous complete settings for recovery");
        check(new FileInfo(Path.Combine(state, SettingsStore.PointerName)).Length <= SettingsStore.MaximumPointerBytes,
            "The working-folder location pointer stays bounded");
        string lostState = Area("lost-pointer-profile");
        var recovering = new MainViewModel(new RecordingWorkbench(), new SettingsStore(lostState), new FakeUpdates(null));
        recovering.DataRootDraft = root;
        recovering.SaveSettingsCommand.Execute(null);
        check(!recovering.NeedsWorkingFolder && recovering.DataRoot == root && recovering.SearchQuery == "changed"
            && !recovering.CheckForUpdates && recovering.KeepDownloadCaches,
            "Choosing an existing working folder after losing the pointer restores its saved preferences in the UI");

        // Migration publishes a pointer only after usable preferences, then removes only its verified source files.
        string legacyState = Area("legacy-profile"), legacyRoot = Area("legacy-work");
        Directory.CreateDirectory(legacyRoot);
        var legacySettings = original with { DataRoot = legacyRoot, DataRootChosen = false };
        string legacyFile = WriteLegacy(legacyState, legacySettings);
        Directory.CreateDirectory(Path.Combine(legacyState, "Logs"));
        File.WriteAllText(Path.Combine(legacyState, "Logs", "startup.log"), "previous startup error");
        File.WriteAllText(Path.Combine(legacyState, "Logs", "startup.old.log"), "older error");
        File.WriteAllText(Path.Combine(legacyState, "keep.txt"), "user file");
        var migrated = new SettingsStore(legacyState);
        check(migrated.Load() == legacySettings with { DataRootChosen = true }
            && !File.Exists(legacyFile) && !Directory.Exists(Path.Combine(legacyState, "Logs"))
            && File.Exists(Path.Combine(legacyState, "keep.txt"))
            && File.ReadAllText(Path.Combine(SettingsStore.StartupLogs(legacyRoot), "legacy-startup.log")) == "previous startup error",
            "Legacy migration keeps preferences and diagnostics, removes verified old copies, and preserves unrelated files");

        string absentState = Area("absent-legacy"), absentRoot = Area("absent-work");
        string absentFile = WriteLegacy(absentState, original with { DataRoot = absentRoot });
        bool missing = false;
        try { new SettingsStore(absentState).Load(); } catch (WorkingFolderUnavailableException ex) { missing = ex.DataRoot == absentRoot; }
        check(missing && File.Exists(absentFile) && !Directory.Exists(absentRoot)
            && !File.Exists(Path.Combine(absentState, SettingsStore.PointerName)), "A missing legacy working folder blocks migration without creating it or deleting the old settings");
        string bomState = Area("bom-profile"), bomRoot = Area("bom-work");
        Directory.CreateDirectory(bomRoot);
        Directory.CreateDirectory(bomState);
        File.WriteAllText(Path.Combine(bomState, "settings.json"), JsonSerializer.Serialize(original with { DataRoot = bomRoot }, Wire.Json), System.Text.Encoding.Unicode);
        check(new SettingsStore(bomState).Load().DataRoot == bomRoot && !File.Exists(Path.Combine(bomState, "settings.json")),
            "Migration retains the old loader's support for BOM-encoded legacy preference files");
        string damagedLegacyState = Area("damaged-legacy-profile");
        Directory.CreateDirectory(damagedLegacyState);
        File.WriteAllText(Path.Combine(damagedLegacyState, "settings.json"), "{damaged");
        new SettingsStore(damagedLegacyState).Choose(original);
        var keptLegacy = new SettingsStore(damagedLegacyState);
        keptLegacy.Load();
        check(File.ReadAllText(Path.Combine(damagedLegacyState, "settings.json")) == "{damaged" && keptLegacy.MigrationNote.Contains("kept"),
            "Unreadable legacy settings are preserved with an explanation that survives restart");

        // A malformed authoritative pointer cannot be bypassed by a valid but stale legacy file.
        foreach (string body in new[] { "{broken", "null", "{}", new string('x', SettingsStore.MaximumPointerBytes + 1),
            "{\"schemaVersion\":99,\"dataRoot\":\"relative\"}",
            JsonSerializer.Serialize(new { schemaVersion = 1, dataRoot = Path.GetPathRoot(output)! + "bad\0folder" }, Wire.Json) })
        {
            string badState = Area("bad-pointer-" + Guid.NewGuid().ToString("N"));
            WriteLegacy(badState, original);
            string file = Path.Combine(badState, SettingsStore.PointerName);
            File.WriteAllText(file, body);
            var bad = new SettingsStore(badState);
            var service = new RecordingWorkbench();
            var vm = new MainViewModel(service, bad, new FakeUpdates(null));
            bad.RecordStartupError(new InvalidOperationException("No fallback log"));
            check(vm.NeedsWorkingFolder && vm.DataRoot.Length == 0 && service.DataRoots.Count == 0
                && File.ReadAllText(file) == body && !Directory.Exists(Path.Combine(badState, "Logs")),
                "An invalid pointer blocks stale legacy preferences and never creates a fallback startup log");
            bad.Choose(original);
            check(bad.Load().DataRoot == root && File.ReadAllText(Path.Combine(badState, "working-folder.previous.json")) == body,
                "An explicit folder choice repairs a broken pointer while preserving its original bytes");
        }

        // Failure at the final pointer replacement leaves the previous folder authoritative.
        string next = Area("next-work");
        var before = store.Load();
        using (File.Open(Path.Combine(state, SettingsStore.PointerName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool refused = false;
            try { store.Choose(before with { DataRoot = next }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
            check(refused && store.Folder == SettingsStore.Preferences(root)
                && new SettingsStore(state).Load().DataRoot == root
                && !Directory.EnumerateFiles(state, ".preferences-*.tmp").Any()
                && !Directory.EnumerateFiles(SettingsStore.Preferences(next), ".preferences-*.tmp").Any(),
                "A locked location pointer refuses the switch, keeps the previous folder active and removes staging files");
        }
        store.Choose(before with { DataRoot = next });
        check(new SettingsStore(state).Load().DataRoot == next, "Retrying a failed pointer switch succeeds after the file is released");

        // A crash after pointer publication but before cleanup resumes from the pointer's migration marker.
        string recoveryState = Area("recovery-profile"), recoveryRoot = Area("recovery-work");
        var recovery = original with { DataRoot = recoveryRoot };
        string recoverySource = WriteLegacy(recoveryState, recovery);
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(recoverySource)));
        Directory.CreateDirectory(SettingsStore.Preferences(recoveryRoot));
        File.WriteAllText(Path.Combine(SettingsStore.Preferences(recoveryRoot), "settings.json"), JsonSerializer.Serialize(recovery, Wire.Json));
        Pointer(recoveryState, new { schemaVersion = 1, dataRoot = recoveryRoot, migrateLegacy = true, legacySettingsSha256 = sourceHash });
        using (File.Open(recoverySource, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var interrupted = new SettingsStore(recoveryState);
            check(interrupted.Load() == recovery && File.Exists(recoverySource) && interrupted.MigrationNote.Contains("kept"),
                "A locked legacy source is kept with an explanation after the new preferences are committed");
        }
        check(new SettingsStore(recoveryState).Load() == recovery && !File.Exists(recoverySource),
            "Restart finishes an interrupted legacy cleanup without falling back to the source");
        WriteLegacy(recoveryState, recovery with { LastSearch = "changed by the older app" });
        Pointer(recoveryState, new { schemaVersion = 1, dataRoot = recoveryRoot, migrateLegacy = true, legacySettingsSha256 = sourceHash });
        var changedSource = new SettingsStore(recoveryState);
        check(changedSource.Load() == recovery && File.Exists(recoverySource) && changedSource.MigrationNote.Contains("changed"),
            "A legacy settings file changed after the migration checkpoint is preserved rather than deleted");

        // Corrupt destination preferences preserve their known root for explicit recovery, never loading the backup silently.
        string currentFile = Path.Combine(SettingsStore.Preferences(next), "settings.json");
        File.WriteAllText(currentFile, "{damaged");
        var broken = new SettingsStore(state);
        var blocked = new MainViewModel(new RecordingWorkbench(), broken, new FakeUpdates(null));
        check(blocked.NeedsWorkingFolder && blocked.DataRoot == next && blocked.DataRootDraft == next,
            "Corrupt working preferences keep the known folder visible but block work instead of loading a backup silently");
        blocked.SaveSettingsCommand.Execute(null);
        check(!blocked.NeedsWorkingFolder && File.ReadAllText(Path.Combine(SettingsStore.Preferences(next), "settings.previous.json")) == "{damaged",
            "Explicitly choosing the known folder repairs preferences and keeps the damaged file for recovery");

        string offline = next + ".offline";
        Directory.Move(next, offline);
        store.RecordStartupError(new InvalidOperationException("Drive disconnected"));
        bool autoRefused = false;
        try { store.Save(before with { DataRoot = next }); } catch (DirectoryNotFoundException) { autoRefused = true; }
        check(autoRefused && !Directory.Exists(next) && !Directory.Exists(Path.Combine(state, "Logs")),
            "Automatic preference saves and diagnostics cannot recreate a disconnected working folder or use a profile fallback");
        Directory.Move(offline, next);

        // The canonical settings win over a crashed write; only stale files with the exact app-owned name are swept.
        string stale = Path.Combine(SettingsStore.Preferences(next), ".preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(stale, "unfinished"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        string own = Path.Combine(SettingsStore.Preferences(next), ".preferences-my-notes.tmp"); File.WriteAllText(own, "keep");
        new SettingsStore(state).Load();
        check(!File.Exists(stale) && File.Exists(own), "Restart removes stale app-owned preference staging files and preserves other files");

        string elsewhere = Area("junction-destination"), link = Area("junction");
        Directory.CreateDirectory(elsewhere);
        var linkStart = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, elsewhere }) linkStart.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(linkStart)!)
        {
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Could not create the preference-containment test junction.");
        }
        try
        {
            bool refused = false;
            try { new SettingsStore(Area("junction-profile")).Choose(original with { DataRoot = Path.Combine(link, "new work") }); }
            catch (InvalidDataException) { refused = true; }
            check(refused && !Directory.EnumerateFileSystemEntries(elsewhere).Any(),
                "A linked parent is refused before choosing a folder can create files on another drive");
        }
        finally { Directory.Delete(link); }

        var logging = new SettingsStore(state); logging.Load();
        for (int index = 0; index < 150; index++) logging.RecordStartupError(new Exception(new string('e', 20000)));
        check(Directory.GetFiles(SettingsStore.StartupLogs(next), "startup*.log").All(file => new FileInfo(file).Length <= 1 << 20)
            && Directory.GetFiles(SettingsStore.StartupLogs(next), "startup*.log").Length == 2,
            "Repeated large startup errors are bounded to two current one-MiB logs inside the working folder");
    }
}
