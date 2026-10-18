using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

static class StorageChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root, string output, CancellationToken token)
    {
        var layout = JsonSerializer.Deserialize<StorageLayout>(File.ReadAllText(Path.Combine(root, "profiles", "storage-layout.json")), Wire.Json)!;
        string folder = Path.Combine(output, "native-storage-café-森林");
        Dictionary<string, int> files = new()
        {
            [$"runtimes/{layout.RuntimeProfileId}/python/python.exe"] = 10,
            [$"models/{layout.ModelIds[0]}/weights.bin"] = 100,
            [$"models/{layout.ModelIds[1]}/weights.bin"] = 6,
            ["models/imported/" + new string('c', 32) + "/model.safetensors"] = 9,
            ["cache/wheel.bin"] = 40, ["bootstrap/runtime.zip"] = 10,
            ["artifacts/image.png"] = 5, ["jobs/record.json"] = 4, ["logs/inference.log"] = 3,
            ["library/index.sqlite"] = 2, ["unmanaged/keep.bin"] = 1000
        };
        foreach (var file in files)
        {
            string path = Path.Combine(folder, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[file.Value], token);
        }
        // A nonexistent interpreter proves desktop/MCP inventory does not start a Python worker.
        var service = new WorkbenchService(root, Path.Combine(output, "no-python.exe"));
        var snapshot = await service.GetStorageAsync(folder, token);
        check(snapshot.InstallationBytes == 166 && snapshot.FreeBytes > 0 && snapshot.TotalBytes >= snapshot.FreeBytes,
            "Rust measures the supported installation and available disk space without Python");
        var items = snapshot.Items.ToDictionary(item => item.Id);
        check(items.Count == 8 && items["images"].Bytes == 5 && items["library"].Bytes == 2 && items["model"].Files == 2 && items["imported"].Bytes == 9,
            "Native storage inventory preserves per-area sizes/counts and Unicode paths");
        check(snapshot.Items.Where(item => item.Clearable).Select(item => item.Id).SequenceEqual(["caches", "logs"]),
            "Native inventory never offers images or job records for clearing");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string absent = Path.Combine(output, "cancelled-native-storage");
        bool stopped = false;
        try { await service.GetStorageAsync(absent, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped && !Directory.Exists(absent), "Precancelled native storage requests do not create a data folder");
        bool refused = false;
        try { NativeCore.GetStorageSnapshot(folder, layout with { RuntimeProfileId = "../escape" }, token); }
        catch (InvalidDataException) { refused = true; }
        check(refused && File.Exists(Path.Combine(folder, "artifacts", "image.png")), "Native storage rejects escaping profile IDs without changing images");
        await using var worker = await PythonWorkerClient.StartAsync(Path.Combine(root, "runtime", "python", "python.exe"), root, token);
        var result = await worker.ExecuteAsync("storage.summary", new { dataRoot = folder }, cancellationToken: token);
        check(result.Kind == "result" && result.Payload.GetProperty("installationBytes").GetInt64() == snapshot.InstallationBytes
            && result.Payload.GetProperty("items").GetArrayLength() == 8,
            "Worker clients use the same Rust storage inventory as the desktop");
    }
}
