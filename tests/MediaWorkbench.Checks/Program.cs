using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

if (args.Length < 3) throw new ArgumentException("Checks require absolute Python path, scaffold root, and test output root; optional absolute ffprobe path.");
string python = args[0], root = Path.GetFullPath(args[1]);
string output = Path.Combine(Path.GetFullPath(args[2]), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
    checks++;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await using (var client = await PythonWorkerClient.StartAsync(python, root, deadline.Token))
{
    Check(client.Handshake.Protocol == 1 && client.Handshake.Kind == "hello", "C# validates real Python handshake");
    var health = await client.ExecuteAsync("health", new { }, cancellationToken: deadline.Token);
    Check(health.Kind == "result" && !health.Payload.GetProperty("generationAvailable").GetBoolean(), "Health does not claim generation availability");
    var catalog = await client.ExecuteAsync("catalog.list", new { }, cancellationToken: deadline.Token);
    Check(catalog.Kind == "result" && catalog.Payload.GetProperty("recipes")[0].GetProperty("runtimeProfileId").GetString() == "sd15-cu126-v1", "Catalog reports the pinned runtime profile");
    List<WorkerEvent> events = [];
    const string label = "Unicode: café — 森林; spaces & $ characters";
    var result = await client.ExecuteAsync("demo.run", new { label, outputDirectory = Path.Combine(output, "folder with spaces"), steps = 3, delayMs = 5 }, events.Add, deadline.Token);
    var artifact = result.Payload.GetProperty("artifacts")[0].GetProperty("path").GetString()!;
    Check(result.Kind == "result" && File.Exists(artifact) && File.ReadAllText(artifact).Contains(label), "Unicode and paths with spaces survive cross-language transport");
    Check(events.Count(e => e.IsTerminal) == 1 && events.Count(e => e.Kind == "progress") == 3, "Exactly one terminal result follows streamed progress");
    using var cancel = new CancellationTokenSource();
    var cancelled = await client.ExecuteAsync("demo.run", new { label, outputDirectory = output, steps = 100, delayMs = 30 },
        e => { if (e.Kind == "progress") cancel.Cancel(); }, cancel.Token);
    Check(cancelled.Kind == "error" && cancelled.ErrorCode == "cancelled", "Cooperative cancellation reaches a terminal outcome");
    var afterCancel = await client.ExecuteAsync("catalog.list", new { }, cancellationToken: deadline.Token);
    Check(afterCancel.Kind == "result", "Worker remains usable after cooperative cancellation");
    var unknown = await client.ExecuteAsync("generate.image", new { }, cancellationToken: deadline.Token);
    Check(unknown.ErrorCode == "invalid_input", "Generation rejects missing prompt before touching storage");
    var invalid = await client.ExecuteAsync("demo.run", new { outputDirectory = "relative", steps = 1 }, cancellationToken: deadline.Token);
    Check(invalid.ErrorCode == "invalid_input", "Invalid output location is rejected before writing");
}

var recipe = InstallationPlanner.ReadCatalog(root).Single();
Check(NativeCore.VerifyAbi() == 1, "Rust native core loads through C ABI 1");
Check(NativeCore.IsSafeRelativePath("unet/model.safetensors") && !NativeCore.IsSafeRelativePath("../escape") && !NativeCore.IsSafeRelativePath("NUL.txt"), "Native path validation rejects Windows device names and traversal");
var hashFile = Path.Combine(output, "hash café 森林.txt");
await File.WriteAllTextAsync(hashFile, "abc");
Check(NativeCore.Sha256File(hashFile) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Native streaming SHA-256 handles Unicode file paths");
var store = new SettingsStore(Path.Combine(output, "settings"));
var settings = new AppSettings(1, Path.Combine(output, "data"), "image");
store.Save(settings);
Check(store.Load() == settings, "Application settings survive a save/load round trip");
bool rootRejected = false;
try { store.Save(settings with { DataRoot = Path.GetPathRoot(output)! }); } catch (InvalidDataException) { rootRejected = true; }
Check(rootRejected && store.Load() == settings, "Invalid drive-root storage leaves saved settings unchanged");
var plan = new InstallationPlanner().Plan(recipe);
Check(plan.CanExecute && plan.Blockers.Length == 0, "Pinned experimental recipe permits local verification");
var draftPlan = new InstallationPlanner().Plan(recipe with { Status = "draft" });
Check(!draftPlan.CanExecute, "Draft recipe cannot become an executable installation");
var vmStore = new SettingsStore(Path.Combine(output, "vm-settings"));
string savedRoot = Path.Combine(output, "vm-data");
vmStore.Save(new AppSettings(1, savedRoot, "image"));
var workbench = new RecordingWorkbench();
var vm = new MainViewModel(workbench, vmStore);
Check(vm.DataRoot == savedRoot && vm.DataRootDraft == savedRoot, "View model loads the saved data folder as both active and draft");
vm.ModelReady = true;
vm.Images.Add(RecordingWorkbench.Image(savedRoot));
vm.DataRootDraft = Path.Combine(output, "typed-but-unsaved");
Check(vm.DataRoot == savedRoot && vm.ModelReady && vm.Images.Count == 1, "Typing a data folder neither applies it nor clears readiness and images");
await vm.RefreshInstallationCommand.ExecuteAsync(null);
await vm.InstallCommand.ExecuteAsync(null);
await vm.GenerateCommand.ExecuteAsync(null);
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.DataRoots.Count == 4 && workbench.DataRoots.All(r => r == savedRoot), "Status, install, generate, and library use the saved folder, not an unsaved draft");
foreach (var rejected in new[] { "relative-folder", Path.GetPathRoot(output)! })
{
    vm.DataRootDraft = rejected;
    vm.SaveSettingsCommand.Execute(null);
}
Check(vm.DataRoot == savedRoot && vmStore.Load().DataRoot == savedRoot && vm.ModelReady, "Rejected data folders leave the active folder and saved settings unchanged");
string nextRoot = Path.Combine(output, "vm-data-next");
vm.DataRootDraft = nextRoot;
vm.SaveSettingsCommand.Execute(null);
Check(vm.DataRoot == nextRoot && vm.DataRootDraft == nextRoot && vmStore.Load().DataRoot == nextRoot && !vm.ModelReady && vm.Images.Count == 0,
    "Saving applies the draft and clears state that belonged to the previous folder");
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.DataRoots[^1] == nextRoot, "Operations after saving use the newly saved folder");
var problem = vm.Problems.SingleOrDefault();
Check(vm.HasProblems && problem is { Status: "failed" } && vm.Images.Count == 1, "Library lists unfinished generations beside saved images");
Check(!vm.OpenJobLogCommand.CanExecute(problem), "A job without a log offers no log to open");
vm.RetryJobCommand.Execute(problem);
Check(vm.Prompt == "retry me" && vm.NegativePrompt == "blurry" && vm.Seed == 42 && vm.Steps == 7 && vm.SelectedPage == "create",
    "Using an unfinished job's settings loads them into Create");
await vm.RefreshStorageCommand.ExecuteAsync(null);
Check(vm.StorageItems.Count == 2 && vm.StorageText.Contains("5.00 GiB") && workbench.DataRoots[^1] == nextRoot, "Storage is measured in the saved data folder");
vm.ModelReady = true;
await vm.ClearStorageCommand.ExecuteAsync("installation");
Check(workbench.Cleared.SequenceEqual(["installation"]) && !vm.ModelReady && vm.StorageText.Contains("use 0 KiB"), "Removing the installation clears readiness and shows the new sizes");
Check(!vm.ClearStorageCommand.CanExecute(null), "Clearing storage requires a named area");


async Task<string> Fixture(string name, string body)
{
    var folder = Path.Combine(output, name);
    Directory.CreateDirectory(Path.Combine(folder, "recipes"));
    Directory.CreateDirectory(Path.Combine(folder, "workers", "python"));
    await File.WriteAllTextAsync(Path.Combine(folder, "workers", "python", "media_worker.py"), body);
    return folder;
}
var incompatibleRoot = await Fixture("bad-handshake", "import json\nprint(json.dumps({'protocol':2,'id':None,'kind':'hello','payload':{}}),flush=True)\n");
bool incompatibleRejected = false;
try { await using var bad = await PythonWorkerClient.StartAsync(python, incompatibleRoot, deadline.Token); }
catch (InvalidDataException) { incompatibleRejected = true; }
Check(incompatibleRejected, "Incompatible worker handshake is rejected");

var crashRoot = await Fixture("crash", "import json,sys,os\nprint(json.dumps({'protocol':1,'id':None,'kind':'hello','payload':{}}),flush=True)\nsys.stdin.readline()\nos._exit(7)\n");
bool crashReported = false;
await using (var crash = await PythonWorkerClient.StartAsync(python, crashRoot, deadline.Token))
{
    try { await crash.ExecuteAsync("health", new { }, cancellationToken: deadline.Token); }
    catch (IOException) { crashReported = true; }
}
Check(crashReported, "Unexpected worker exit becomes a transport failure");

// A worker that dies after starting a child must not leave that child running; the job owns the whole tree.
var orphanRoot = await Fixture("orphan", "import json,sys,os,subprocess\nprint(json.dumps({'protocol':1,'id':None,'kind':'hello','payload':{}}),flush=True)\n" +
    "request=json.loads(sys.stdin.readline())\nchild=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\n" +
    "print(json.dumps({'protocol':1,'id':request['id'],'kind':'progress','payload':{'stage':'child','completed':child.pid}}),flush=True)\nos._exit(9)\n");
int orphanPid = 0;
await using (var orphan = await PythonWorkerClient.StartAsync(python, orphanRoot, deadline.Token))
{
    try { await orphan.ExecuteAsync("health", new { }, e => orphanPid = e.Payload.GetProperty("completed").GetInt32(), deadline.Token); }
    catch (IOException) { }
}
static bool Ended(int pid)
{
    try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.WaitForExit(5000); }
    catch (ArgumentException) { return true; }
}
Check(orphanPid > 0 && Ended(orphanPid), "Descendants of a failed worker are terminated with its job object");

if (args.Length > 3)
{
    var version = await NativeToolProbe.FfprobeVersionAsync(args[3], deadline.Token);
    Check(version.StartsWith("ffprobe version", StringComparison.Ordinal), "Read-only native ffprobe adapter invokes a real executable");
}
Console.WriteLine($"All {checks} C# checks passed. Test artifacts: {output}");
