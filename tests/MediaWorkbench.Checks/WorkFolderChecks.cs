using System.Diagnostics;
using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

/// <summary>Temporary work stays inside the chosen data folder, and a missing data folder stops work instead of falling back.</summary>
static class WorkFolderChecks
{
    public static async Task RunAsync(Action<bool, string> check, string python, string output, CancellationToken token)
    {
        string data = Path.Combine(output, "work-folders café");
        using (var scratch = ScratchFolder.Create(data))
        {
            check(Path.GetDirectoryName(scratch.Path) == WorkFolders.Work(data) && File.Exists(Path.Combine(scratch.Path, WorkFolders.OwnerFile)),
                "A worker's temporary folder is created inside the data folder's temp/work with its owner file");
            bool held = false;
            try { File.Delete(Path.Combine(scratch.Path, WorkFolders.OwnerFile)); } catch (IOException) { held = true; }
            check(held, "A running worker's owner file cannot be deleted, so clean-up leaves its folder alone");
            var environment = new Dictionary<string, string?>();
            scratch.Apply(environment);
            check(environment["TEMP"] == scratch.Path && environment["TMP"] == scratch.Path && environment[ScratchFolder.Variable] == scratch.Path,
                "Children are told to use the worker's temporary folder");
        }
        check(!Directory.Exists(WorkFolders.Temporary(data)) && Directory.Exists(data), "A finished worker leaves no temporary folders behind");

        string absent = Path.Combine(output, "not-created-yet");
        using (var readOnly = ScratchFolder.Create(absent, createDataFolder: false))
            check(readOnly.Path.StartsWith(WorkFolders.Work(absent), StringComparison.Ordinal) && !Directory.Exists(absent),
                "Read-only work names a temporary folder in a missing data folder without creating anything");

        // A real worker reports where its temporary files would go; nothing in it may point outside the data folder.
        string fixture = Path.Combine(output, "temp-report");
        Directory.CreateDirectory(Path.Combine(fixture, "workers", "python"));
        await File.WriteAllTextAsync(Path.Combine(fixture, "workers", "python", "media_worker.py"), """
            import json, os, sys, tempfile
            folder = os.environ.get("MEDIA_WORKBENCH_TEMP")
            if folder:
                tempfile.tempdir = folder
            print(json.dumps({"protocol": 1, "id": None, "kind": "hello", "payload": {}}), flush=True)
            request = json.loads(sys.stdin.readline())
            with tempfile.NamedTemporaryFile(delete=False) as handle:
                written = handle.name
            report = {"temp": os.environ.get("TEMP"), "tmp": os.environ.get("TMP"), "tempdir": tempfile.gettempdir(), "written": written}
            print(json.dumps({"protocol": 1, "id": request["id"], "kind": "result", "payload": report}), flush=True)
            sys.stdin.readline()
            """, token);
        string workerData = Path.Combine(output, "worker-data");
        JsonElement reported;
        await using (var worker = await PythonWorkerClient.StartAsync(python, fixture, workerData, token))
            reported = (await worker.ExecuteAsync("health", new { }, cancellationToken: token)).Payload.Clone();
        string work = WorkFolders.Work(workerData) + Path.DirectorySeparatorChar;
        check(new[] { "temp", "tmp", "tempdir", "written" }.All(name => reported.GetProperty(name).GetString()!.StartsWith(work, StringComparison.OrdinalIgnoreCase)),
            "A real Python worker's TEMP, TMP, and temporary files are inside the data folder");
        check(!File.Exists(reported.GetProperty("written").GetString()) && !Directory.Exists(WorkFolders.Temporary(workerData)),
            "Stopping the worker removes its temporary files");

        // A data folder on a drive that is not connected stops before anything starts or is written elsewhere.
        char? missing = Enumerable.Range('D', 23).Select(letter => (char)letter)
            .Cast<char?>().FirstOrDefault(letter => !Directory.Exists($"{letter}:\\"));
        if (missing is { } letter)
        {
            string unplugged = $"{letter}:\\Media Workbench data";
            IOException? refused = null;
            try { await using var _ = await PythonWorkerClient.StartAsync(python, fixture, unplugged, token); }
            catch (IOException ex) { refused = ex; }
            check(refused is not null && refused.Message.Contains("reconnect it") && refused.Message.Contains("Nothing was written anywhere else"),
                "A disconnected data folder stops the worker with a plain explanation instead of using Windows' temporary folder");
            refused = null;
            try { await WorkFolders.WritePaintedAsync(unplugged, [1, 2, 3], token); }
            catch (IOException ex) { refused = ex; }
            check(refused is not null, "A painted area is never handed over outside a disconnected data folder");
        }

        // Links or junctions inside the data folder could send temporary work to another drive.
        string linked = Path.Combine(output, "linked-data");
        string elsewhere = Path.Combine(output, "elsewhere");
        Directory.CreateDirectory(linked);
        Directory.CreateDirectory(elsewhere);
        if (await Junction(WorkFolders.Temporary(linked), elsewhere, token))
        {
            bool linkRefused = false;
            try { ScratchFolder.Create(linked).Dispose(); } catch (InvalidDataException) { linkRefused = true; }
            check(linkRefused && !Directory.EnumerateFileSystemEntries(elsewhere).Any(), "A junction in the data folder's temp folder is refused, not followed");
            Directory.Delete(WorkFolders.Temporary(linked));
        }
    }

    private static async Task<bool> Junction(string link, string target, CancellationToken token)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in (string[])["/d", "/c", "mklink", "/J", link, target]) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(token);
        return process.ExitCode == 0;
    }
}
