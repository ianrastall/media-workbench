using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

/// <summary>
/// Picture jobs share one coordinator that keeps the model loaded; work that needs the data folder releases it,
/// reads do not, another folder gets its own, and it goes after the idle period.
/// </summary>
static class KeptModelChecks
{
    public static async Task RunAsync(Action<bool, string> check, string python, string output, CancellationToken token)
    {
        // A coordinator that answers every request with its process ID and whether it was asked to keep a model.
        string fixture = Path.Combine(output, "kept-model-worker");
        Directory.CreateDirectory(Path.Combine(fixture, "recipes"));
        Directory.CreateDirectory(Path.Combine(fixture, "workers", "python"));
        await File.WriteAllTextAsync(Path.Combine(fixture, "workers", "python", "media_worker.py"), """
            import json, os, sys
            print(json.dumps({"protocol": 1, "id": None, "kind": "hello", "payload": {}}), flush=True)
            for line in sys.stdin:
                request = json.loads(line)
                if request["operation"] == "cancel":
                    continue
                who = f"{os.getpid()}:{os.environ.get('MEDIA_WORKBENCH_KEEP_MODEL', '')}"
                payload = {"status": "ready", "message": who, "freeBytes": 0, "totalBytes": 0, "installationBytes": 0, "items": [],
                           "id": who, "imagePath": "", "imageSha256": "", "settings": {"prompt": "", "negativePrompt": "", "seed": 1, "steps": 1},
                           "completedUtc": "", "seconds": 0, "gpu": ""}
                print(json.dumps({"protocol": 1, "id": request["id"], "kind": "result", "payload": payload}), flush=True)
            """, token);
        string data = Path.Combine(output, "kept-model-data"), other = Path.Combine(output, "kept-model-other");
        var picture = new GenerationSettings("a house", "", 1, 4);
        await using var service = new WorkbenchService(fixture, python) { KeepLoadedFor = TimeSpan.FromSeconds(2) };
        string first = (await service.GenerateAsync(data, picture, new Progress<OperationProgress>(), token)).Id;
        string second = (await service.GenerateAsync(data, picture, new Progress<OperationProgress>(), token)).Id;
        check(first == second && first.EndsWith(":1") && service.KeptProcessId?.ToString() == first.Split(':')[0],
            "Pictures share one coordinator that is asked to keep the model loaded");
        string read = (await service.GetInstallationAsync(data, token)).Message;
        check(service.KeptProcessId?.ToString() == first.Split(':')[0] && read.EndsWith(':') && read != first,
            "Reads run in their own worker and leave the loaded model alone");
        await service.ClearStorageAsync(data, "logs", new Progress<OperationProgress>(), token);
        check(service.KeptProcessId is null, "Work that needs the data folder to itself releases the loaded model first");
        string third = (await service.GenerateAsync(data, picture, new Progress<OperationProgress>(), token)).Id;
        string elsewhere = (await service.GenerateAsync(other, picture, new Progress<OperationProgress>(), token)).Id;
        check(third != first && elsewhere != third, "A released model loads again, and another data folder gets its own coordinator");
        await Task.Delay(TimeSpan.FromSeconds(4), token);
        check(service.KeptProcessId is null, "The loaded model is released after the idle period");
        await service.GenerateAsync(data, picture, new Progress<OperationProgress>(), token);
        await service.ReleaseLoadedModelAsync();
        check(service.KeptProcessId is null, "The loaded model can be released on request");
    }
}
