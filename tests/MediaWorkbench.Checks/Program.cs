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

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
var offered = new UpdateInfo("0.3.0-alpha.1", "New things", "https://github.com/ianrastall/media-workbench/releases/download/v0.3.0-alpha.1/MediaWorkbench-0.3.0-alpha.1-win-x64-setup.exe", "", "");
var updates = new FakeUpdates(offered);
var vm = new MainViewModel(workbench, vmStore, updates);
Check(vm.DataRoot == savedRoot && vm.DataRootDraft == savedRoot, "View model loads the saved data folder as both active and draft");
vm.ModelReady = true;
vm.Images.Add(RecordingWorkbench.Image(savedRoot));
vm.DataRootDraft = Path.Combine(output, "typed-but-unsaved");
Check(vm.DataRoot == savedRoot && vm.ModelReady && vm.Images.Count == 1, "Typing a data folder neither applies it nor clears readiness and images");
using (var configuration = System.Text.Json.JsonDocument.Parse(vm.McpConfiguration))
{
    var configurationArgs = configuration.RootElement.GetProperty("mcpServers").GetProperty("media-workbench").GetProperty("args").EnumerateArray().Select(arg => arg.GetString()).ToArray();
    Check(configurationArgs.SequenceEqual(["mcp", "--data-root", savedRoot]), "MCP configuration uses the saved folder and defaults to inspection only");
}
vm.McpAllowSetup = vm.McpAllowInference = true;
Check(vm.McpConfiguration.Contains("--allow-setup") && vm.McpConfiguration.Contains("--allow-inference"), "MCP configuration includes permissions only when chosen");
await vm.RefreshInstallationCommand.ExecuteAsync(null);
await vm.InstallCommand.ExecuteAsync(null);
await vm.GenerateCommand.ExecuteAsync(null);
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.DataRoots.Count == 4 && workbench.DataRoots.All(r => r == savedRoot), "Status, install, generate, and library use the saved folder, not an unsaved draft");
Check(workbench.ProfileRoots.Count == 2 && workbench.ProfileRoots.All(r => r == savedRoot), "The hardware profile is read once per folder, and again after setup tests new settings");
Check(vm.HasSystemProfile && vm.SystemProfileText.StartsWith("This computer: NVIDIA GeForce GTX 1660 Ti with 6 GB of graphics memory · 64 GB of memory (45 GB free now) · Data folder on a hard disk with 276 GB free.")
    && vm.SystemProfileText.Contains("memory-saving settings (full precision") && !vm.HasRuntimeNote,
    "Create's setup card describes this computer and its tested settings in plain words");
Check(vm.SystemProfileDetails.Contains("Driver 610.60 · compute capability 7.5") && vm.SystemProfileDetails.Contains("• NVIDIA GeForce GTX 1660 Ti can make blank pictures at half precision")
    && vm.SystemProfileDetails.Contains("Models load faster from a solid-state drive"), "Diagnostics explains why these settings were chosen and what a hard disk means");
workbench.ProfileFailure = new InvalidOperationException("nvidia-smi timed out");
await vm.DiagnosticsCommand.ExecuteAsync(null);
Check(vm.SystemProfileDetails == "This computer's hardware could not be read: nvidia-smi timed out" && vm.DiagnosticsText.Length == 0 && workbench.ProfileRoots.Count == 3,
    "Diagnostics reads the hardware again, and a failed read is reported without failing diagnostics");
workbench.ProfileFailure = null;
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
vm.LibrarySearch = "forest 42";
vm.LibraryKindIndex = 2;
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1] == new LibraryQuery("forest 42", "upscale", 0, 48) && !vm.PreviousLibraryPageCommand.CanExecute(null)
    && vm.NextLibraryPageCommand.CanExecute(null) && vm.LibraryText.Contains("of 100"), "Library applies search and image type on its first page");
vm.LibrarySearch = "a draft search";
await vm.NextLibraryPageCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1] == new LibraryQuery("forest 42", "upscale", 48, 48) && vm.PreviousLibraryPageCommand.CanExecute(null),
    "Paging keeps the applied search until Search / refresh applies a new draft");
await vm.NextLibraryPageCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1].Offset == 96 && !vm.NextLibraryPageCommand.CanExecute(null), "The last Library page disables Older images");
await vm.PreviousLibraryPageCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1].Offset == 48, "Newer images returns to the previous Library page");
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1] == new LibraryQuery("a draft search", "upscale", 0, 48), "Applying another search resets Library to the first page");
Check(!vm.OpenJobLogCommand.CanExecute(problem), "A job without a log offers no log to open");
vm.RetryJobCommand.Execute(problem);
Check(vm.Prompt == "retry me" && vm.NegativePrompt == "blurry" && vm.Seed == 42 && vm.Steps == 7 && vm.SelectedPage == "create"
    && vm.GuidanceScale == 4.5 && vm.SizeIndex == 2 && vm.SamplerIndex == 2, "Using an unfinished job's settings loads them into Create");
await vm.GenerateCommand.ExecuteAsync(null);
Check(workbench.LastGeneration is { Width: 768, Height: 512, Sampler: "euler-a", GuidanceScale: 4.5, Seed: 42, Steps: 7 },
    "Generation sends the chosen size, sampler, and guidance");
var legacy = System.Text.Json.JsonSerializer.Deserialize<GenerationSettings>("{\"prompt\":\"old\",\"negativePrompt\":\"\",\"seed\":1,\"steps\":20}", Wire.Json)!;
Check(legacy is { Width: 512, Height: 512, Sampler: "pndm", GuidanceScale: 7.5 }, "Older job records read with the original fixed settings");
await vm.RefreshStorageCommand.ExecuteAsync(null);
Check(vm.StorageItems.Count == 2 && vm.StorageText.Contains("5.00 GiB") && workbench.DataRoots[^1] == nextRoot, "Storage is measured in the saved data folder");
vm.ModelReady = true;
await vm.ClearStorageCommand.ExecuteAsync("installation");
Check(workbench.Cleared.SequenceEqual(["installation"]) && !vm.ModelReady && vm.StorageText.Contains("use 0 KiB"), "Removing the installation clears readiness and shows the new sizes");
Check(!vm.ClearStorageCommand.CanExecute(null), "Clearing storage requires a named area");

// Starting from a picture (image-to-image).
string picture = Path.Combine(output, "holiday photo.png");
await File.WriteAllBytesAsync(picture, [1, 2, 3]);
Check(!vm.UseAsStartingPicture(Path.Combine(output, "notes.txt")) && !vm.HasSourceImage, "Only picture files can start an image");
vm.SelectedPage = "library";
Check(vm.UseAsStartingPicture(picture) && vm.SourceImagePath == picture && vm.SelectedPage == "create", "Choosing a starting picture opens Create with it");
vm.ChangeIndex = 0;
await vm.GenerateCommand.ExecuteAsync(null);
Check(workbench.LastGeneration is { SourceImage: var sent, Strength: 0.35 } && sent == picture, "Generation sends the starting picture and how much to change it");
Check(workbench.LastGeneration!.Summary.StartsWith("From your picture, changed a little") && !legacy.Summary.Contains("picture"), "Summaries say when an image started from a picture");
vm.ClearStartingPictureCommand.Execute(null);
await vm.GenerateCommand.ExecuteAsync(null);
Check(!vm.HasSourceImage && workbench.LastGeneration!.SourceImage is null, "Removing the picture returns to description-only generation");
var reusable = RecordingWorkbench.Image(nextRoot) with { Settings = new("repeat this", "text", 123, 12, 6, 512, 768, "dpmpp-2m", picture, 0.8) };
vm.UseImageSettingsCommand.Execute(reusable);
Check(vm.Prompt == "repeat this" && vm.Seed == 123 && vm.Steps == 12 && vm.GuidanceScale == 6 && vm.SizeIndex == 1
    && vm.SamplerIndex == 1 && vm.SourceImagePath == picture && vm.ChangeIndex == 2 && vm.SelectedPage == "create",
    "A successful Library image's settings, including its private starting picture, can be reused");

// Changing only a painted part of the starting picture.
var cells = new MaskCanvas(64, 32);
Check(!cells.HasPaint, "A new painted area is empty");
cells.Stroke(0.1, 0.5, 0.4, 0.5, 0.05);
Check(cells.HasPaint && cells.Cells[16 * 64 + 12] == 255 && cells.Cells[0] == 0 && cells.Cells[16 * 64 + 50] == 0, "A brush stroke paints only along its path");
var decoded = MaskCanvas.DecodePng(cells.EncodePng());
Check(decoded.Width == 64 && decoded.Height == 32 && decoded.Cells.SequenceEqual(cells.Cells), "A painted area survives a PNG round trip");
bool damagedRejected = false;
try { byte[] damaged = cells.EncodePng(); damaged[^10] ^= 1; MaskCanvas.DecodePng(damaged); } catch (InvalidDataException) { damagedRejected = true; }
Check(damagedRejected, "A damaged painted-area PNG is rejected");
cells.Stroke(0.1, 0.5, 0.4, 0.5, 0.05, erase: true);
Check(!cells.HasPaint, "Erasing removes paint");
Check(MaskCanvas.ForPicture(4000, 3000) is { Width: 512, Height: 384 } && MaskCanvas.ForPicture(300, 200) is { Width: 300, Height: 200 },
    "A painted area keeps the picture's proportions at up to 512 cells a side");
var rectangle = MaskCanvas.ForPicture(100, 100);
rectangle.FillRectangle(0.5, 0.5, 0.5, 0.5);
Check(rectangle.Cells.ToArray().Count(cell => cell != 0) == 2500 && rectangle.Cells[99 * 100 + 99] == 255 && rectangle.Cells[0] == 0, "A rectangular change area covers exactly its fraction");
string otherPicture = Path.Combine(output, "another photo.png");
await File.WriteAllBytesAsync(otherPicture, [4, 5, 6]);
vm.UseAsStartingPicture(otherPicture);
Check(!vm.IsPainting && vm.IsChangingWholePicture && !vm.ClearPaintingCommand.CanExecute(null), "A starting picture changes as a whole until the user paints");
Check(vm.StartPainting(1600, 1200) && vm.IsPainting && vm.Mask is { Width: 512, Height: 384 } && !vm.IsChangingWholePicture && vm.PaintChangeIndex == 2,
    "Painting starts with an empty area shaped like the picture and replaces it completely by default");
var beforeEmpty = workbench.LastGeneration;
await vm.GenerateCommand.ExecuteAsync(null);
Check(ReferenceEquals(beforeEmpty, workbench.LastGeneration) && vm.Notice.StartsWith("Paint over"), "Nothing is generated until some part is painted");
vm.BrushIndex = 2;
vm.PaintStroke(0.2, 0.2, 0.8, 0.8);
await vm.GenerateCommand.ExecuteAsync(null);
Check(workbench.LastGeneration is { SourceImage: var paintedSource, Strength: 1.0, MaskImage: { } sentMask } && paintedSource == otherPicture && !File.Exists(sentMask)
    && workbench.LastMaskBytes is { } sentBytes && MaskCanvas.DecodePng(sentBytes).Cells.SequenceEqual(vm.Mask!.Cells),
    "Generation hands over the painted area and then removes its temporary copy");
Check(workbench.LastGeneration!.Summary.StartsWith("Painted part of your picture changed completely"), "Summaries say when only a painted part changed");
string storedMask = Path.Combine(output, "stored mask.png");
await File.WriteAllBytesAsync(storedMask, vm.Mask!.EncodePng());
vm.UseAsStartingPicture(picture);
Check(!vm.IsPainting && vm.Mask is null, "Choosing a different picture discards the painted area");
vm.UseImageSettingsCommand.Execute(reusable with { Settings = reusable.Settings with { SourceImage = otherPicture, MaskImage = storedMask, Strength = 0.75 } });
Check(vm.IsPainting && vm.SourceImagePath == otherPicture && vm.PaintChangeIndex == 1 && vm.Mask!.Cells.SequenceEqual(MaskCanvas.DecodePng(File.ReadAllBytes(storedMask)).Cells),
    "Reusing a painted change's settings restores its picture, painted area, and change level");
vm.UseImageSettingsCommand.Execute(reusable with { Settings = reusable.Settings with { SourceImage = otherPicture } });
Check(!vm.IsPainting && vm.ChangeIndex == 2, "Reusing a whole-picture change on the same picture stops painting");
vm.StartPainting(1600, 1200);
vm.StopPaintingCommand.Execute(null);
Check(!vm.IsPainting && vm.Mask is null && vm.IsChangingWholePicture, "Change the whole picture leaves painting");
vm.ClearStartingPictureCommand.Execute(null);

// Upscaling from Tools.
Check(vm.UseForUpscale(picture) && vm.SelectedPage == "tools" && vm.UpscaleSourcePath == picture, "Choosing a picture to enlarge opens Tools with it");
vm.ModelReady = false;  // Opening Tools refreshed the (fake) status; force the not-installed case.
Check(!vm.UpscaleCommand.CanExecute(null), "Upscaling waits for a verified installation");
vm.ModelReady = true;
vm.UpscaleIndex = 0;
await vm.UpscaleCommand.ExecuteAsync(null);
Check(workbench.LastUpscale == (picture, 2) && vm.Images[0].Kind == "upscale" && vm.UpscaleDescription.StartsWith("Upscaled 2× to 2048 × 2048"),
    "Make it bigger sends the picture and scale and adds the result to Library");
var upscaled = vm.Images[0];
vm.UpscaleIndex = 1;
vm.SelectedPage = "home";
vm.UseImageSettingsCommand.Execute(upscaled);
Check(vm.SelectedPage == "tools" && vm.UpscaleIndex == 0 && vm.UpscaleSourcePath == picture, "Reusing an upscaled image restores Tools and its original scale");
vm.RetryJobCommand.Execute(new JobSummary("a", "failed", "GPU busy", "", upscaled.Settings, null));
Check(vm.SelectedPage == "tools" && vm.Notice.Contains("Make it bigger"), "An unfinished upscale can restore its settings too");

// Adding a model the user already downloaded, and choosing it.
Check(!vm.HasModelChoice && vm.SelectedModelId == ModelChoice.BuiltInId && vm.ModelChoices.Count == 1, "The model chooser stays hidden while only the bundled model exists");
await vm.GenerateCommand.ExecuteAsync(null);
Check(workbench.LastGeneration?.Model == ModelChoice.BuiltInId, "Generation names the bundled model by default");
await vm.ChooseModelToImportAsync(Path.Combine(output, "classic.ckpt"));
Check(!vm.HasImportCandidate && vm.ImportText.Contains("hidden code") && !vm.ImportModelCommand.CanExecute(null), "A refused model explains why and cannot be added");
string modelFile = Path.Combine(output, "dreamy style.safetensors");
await File.WriteAllBytesAsync(modelFile, [0]);
await vm.ChooseModelToImportAsync(modelFile);
Check(vm.HasImportCandidate && vm.ImportName == "dreamy style" && vm.ImportModelCommand.CanExecute(null), "A recognized model is offered for adding with a suggested name");
vm.ImportName = "Dreamy";
await vm.ImportModelCommand.ExecuteAsync(null);
string addedId = new('e', 32);
Check(workbench.LastImport == (modelFile, "Dreamy") && vm.HasModelChoice && vm.SelectedModelId == addedId && !vm.HasImportCandidate && vm.Notice.Contains("passed its test"),
    "Adding a model tests it, then shows the chooser with the new model selected");
await vm.GenerateCommand.ExecuteAsync(null);
Check(workbench.LastGeneration?.Model == addedId, "Generation sends the chosen model");
var madeWithAdded = RecordingWorkbench.Image(nextRoot) with { Settings = new("lighthouse", "", 5, 20, Model: addedId), ModelName = "Dreamy" };
Check(madeWithAdded.Description.Contains("Made with Dreamy") && !RecordingWorkbench.Image(nextRoot).Description.Contains("Made with"),
    "Library descriptions name an added model and leave bundled images unchanged");
var timed = System.Text.Json.JsonSerializer.Deserialize<GeneratedImage>("""
{"id":"a","imagePath":"x.png","imageSha256":"","settings":{"prompt":"house","negativePrompt":"","seed":1,"steps":20},"completedUtc":"","seconds":14.12,"gpu":"Test GPU",
 "loadSeconds":48.4,"startupSeconds":6.2,"loadNote":"Loading the model took 48 seconds. The data folder is on a hard disk."}
""", Wire.Json)!;
Check(timed.SavedNotice == "Image saved in 14.1 seconds of inference, after 48.4 seconds loading the model. Loading the model took 48 seconds. The data folder is on a hard disk."
    && RecordingWorkbench.Image(nextRoot).SavedNotice == "Image saved in 1.0 seconds of inference." && vm.Notice == RecordingWorkbench.Image(nextRoot).SavedNotice,
    "Create reports model loading apart from inference, with the plain reason when loading was slow, and older records keep their notice");
// Short videos: chosen in Create, set up once, then made with the shared description and Advanced options.
vm.CreateKindIndex = 1;
Check(vm.IsVideoMode && vm.NeedsVideoSetup && !vm.GenerateVideoCommand.CanExecute(null) && vm.PromptHeader == "Describe the video" && vm.ShowsShape,
    "Choosing A short video asks for the video setup first");
await vm.InstallVideoCommand.ExecuteAsync(null);
Check(vm.VideoReady && !vm.NeedsVideoSetup && vm.CanMakeVideo && vm.VideoSettingsText.Contains("memory-saving") && workbench.VideoInstalls == 1,
    "Setting up videos tests them once and enables Make video");
vm.VideoLengthIndex = 1;
vm.SizeIndex = 2;
await vm.GenerateVideoCommand.ExecuteAsync(null);
Check(workbench.LastVideo is { Frames: 24, Width: 768, Height: 512, Sampler: "ddim", Prompt: var videoPrompt } && videoPrompt == vm.Prompt
    && vm.HasLatestVideo && vm.VideoPreviewPath.EndsWith("video.mp4") && vm.Notice.StartsWith("Video saved in"),
    "Make video sends the chosen length and shape and shows the new video");
var clip = RecordingWorkbench.VideoClip(nextRoot);
Check(clip.IsVideo && !clip.IsPicture && clip.Description.Contains("Short video, 2 seconds (16 frames)") && !RecordingWorkbench.Image(nextRoot).IsVideo,
    "Library describes a video by its length and keeps pictures as pictures");
vm.CreateKindIndex = 0;
vm.SizeIndex = 0;
vm.UseImageSettingsCommand.Execute(clip);
Check(vm.IsVideoMode && vm.VideoLengthIndex == 0 && vm.Prompt == "waves at dusk" && vm.SelectedPage == "create" && vm.Notice.Contains("this video's settings"),
    "Use settings on a video returns to video mode with its length");
vm.LibraryKindIndex = 3;
await vm.RefreshLibraryCommand.ExecuteAsync(null);
Check(workbench.LibraryQueries[^1].Kind == "video", "Library can show only videos");
vm.LibraryKindIndex = 0;
await vm.RemoveVideoCommand.ExecuteAsync(null);
Check(!vm.VideoReady && vm.NeedsVideoSetup && vm.Notice.Contains("Videos you made were kept"), "Removing the video setup keeps videos and asks for setup again");
vm.CreateKindIndex = 0;
vm.ModelIndex = 0;
vm.UseImageSettingsCommand.Execute(madeWithAdded);
Check(vm.SelectedModelId == addedId && vm.SelectedPage == "create", "Use settings restores the model that made the image");
vm.UseImageSettingsCommand.Execute(madeWithAdded with { Settings = madeWithAdded.Settings with { Model = new string('f', 32) } });
Check(vm.SelectedModelId == ModelChoice.BuiltInId && vm.Notice.Contains("no longer added"), "Use settings falls back to the bundled model, and says so, when its model was removed");
workbench.Models.Add(new(new string('d', 32), "Needs work", "needs_verification", "This model's copied files changed. Test it again.", "diffusers", 1L << 30));
await vm.RefreshStorageCommand.ExecuteAsync(null);
Check(vm.AddedModels.Count == 2 && vm.ModelChoices.Count == 2 && vm.HasAddedModels && vm.StorageText.Contains("added 2 models"),
    "Settings lists every added model and counts them, while Create offers only tested models");
var untested = vm.AddedModels.Single(model => model.NeedsTest);
await vm.RetestModelCommand.ExecuteAsync(untested);
Check(workbench.Retested.SequenceEqual([untested.Id]) && vm.ModelChoices.Count == 3, "Testing an added model again makes it available in Create");
vm.ModelIndex = vm.ModelChoices.ToList().FindIndex(model => model.Id == addedId);
await vm.RemoveModelCommand.ExecuteAsync(vm.AddedModels.Single(model => model.Id == addedId));
Check(vm.AddedModels.Count == 1 && vm.SelectedModelId == ModelChoice.BuiltInId && vm.Notice.Contains("images and your original file were kept"),
    "Removing the chosen model returns Create to the bundled model");
Check(!vm.RemoveModelCommand.CanExecute(RecordingWorkbench.BuiltIn) && !vm.RetestModelCommand.CanExecute(null), "The bundled model cannot be removed here");
workbench.Models.Clear();
await vm.RefreshStorageCommand.ExecuteAsync(null);
vm.LibrarySearch = "matching only";
vm.LibraryKindIndex = 2;
await vm.RefreshLibraryCommand.ExecuteAsync(null);
await vm.NextLibraryPageCommand.ExecuteAsync(null);
vm.SelectedPage = "create";
var generationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
workbench.GenerationGate = generationGate.Task;
var inFlight = vm.GenerateCommand.ExecuteAsync(null);
vm.SelectedPage = "library";
int requestsBeforeCompletion = workbench.LibraryQueries.Count;
generationGate.SetResult();
await inFlight;
workbench.GenerationGate = null;
Check(workbench.LibraryQueries.Count == requestsBeforeCompletion + 1 && workbench.LibraryQueries[^1] == new LibraryQuery("matching only", "upscale", 0, 48),
    "Opening Library during inference refreshes the applied filters on completion, instead of mixing in another image type");
var exportRequest = new ImageExportRequest(vm.LatestImage!.Id, Path.Combine(output, "saved copy.png"));
vm.DataRootDraft = Path.Combine(output, "another-unsaved-folder");
await vm.ExportImageCommand.ExecuteAsync(exportRequest);
Check(workbench.LastExport == exportRequest && workbench.DataRoots[^1] == nextRoot && vm.Notice.Contains("saved copy.png"),
    "Saving a copy uses the saved data folder and reports the export destination");
Check(vm.LatestImage?.ImagePath == vm.PreviewPath && vm.LatestUpscale?.ImagePath == vm.UpscalePreviewPath,
    "Latest results retain their job identity for export");
Check(!vm.ExportImageCommand.CanExecute(null), "Export requires a chosen image and destination");
vm.OpenImageCommand.Execute(Path.Combine(output, "not-an-image.cmd"));
Check(vm.Notice.Contains("PNG image is no longer available"), "Open image refuses a path that is not a PNG");

// Real coordinator + SQLite + typed C# transport, independent of the view-model fake.
string libraryRoot = Path.Combine(output, "indexed-library"), jobId = new string('b', 32);
Directory.CreateDirectory(Path.Combine(libraryRoot, "jobs"));
Directory.CreateDirectory(Path.Combine(libraryRoot, "artifacts", jobId));
await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "artifacts", jobId, "image.png"), [1]);
await File.WriteAllTextAsync(Path.Combine(libraryRoot, "jobs", jobId + ".json"), System.Text.Json.JsonSerializer.Serialize(new
{
    id = jobId, status = "succeeded", kind = "generate", imagePath = jobId + "/image.png", imageSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 })),
    settings = new GenerationSettings("Café forest", "", 987, 5), completedUtc = "2026-10-17T12:00:00Z", seconds = 1, gpu = "Test GPU"
}, Wire.Json));
var realService = new WorkbenchService(root, python);
var indexed = await realService.GetLibraryAsync(libraryRoot, deadline.Token, new("CAFÉ", Limit: 1));
Check(indexed.Total == 1 && indexed.Images.Single().Settings.Seed == 987 && indexed.Offset == 0 && !indexed.HasMore,
    "Library search and page metadata survive the real C#/Python/SQLite boundary");
Check((await realService.GetLibraryAsync(libraryRoot, deadline.Token, new("CAFÉ", "upscale"))).Total == 0,
    "The real Library filters upscaled and generated images independently");
string exportTarget = Path.Combine(output, "export café.png");
Check(await realService.ExportImageAsync(libraryRoot, new(jobId, exportTarget), new Progress<OperationProgress>(), deadline.Token) == exportTarget
    && File.ReadAllBytes(exportTarget).SequenceEqual(new byte[] { 1 }), "Export streams and verifies an image directly through the C#/Rust ABI");
bool nativeExportBlocked = false;
try { await realService.ExportImageAsync(libraryRoot, new(jobId, Path.Combine(libraryRoot, "protected.png")), new Progress<OperationProgress>(), deadline.Token); }
catch (InvalidDataException) { nativeExportBlocked = true; }
Check(nativeExportBlocked && !File.Exists(Path.Combine(libraryRoot, "protected.png")), "Native export rejects destinations in the managed data folder without writing them");

// Updates: check, then ask. Nothing is downloaded or run until the user chooses Update now.
await vm.CheckForUpdatesQuietlyAsync();
Check(vm.HasUpdate && vm.UpdateText.Contains("0.3.0-alpha.1") && vm.UpdateNotes == "New things", "A newer release is offered with its notes");
string? launched = null;
vm.InstallerReady += path => launched = path;
Check(launched is null, "Finding an update does not download or start it");
await vm.InstallUpdateCommand.ExecuteAsync(null);
Check(launched?.EndsWith("MediaWorkbench-0.3.0-alpha.1-win-x64-setup.exe") == true, "Update now hands the verified installer to the shell");
vm.CheckForUpdates = false;
int checksBefore = updates.Checks;
await vm.CheckForUpdatesQuietlyAsync();
Check(updates.Checks == checksBefore && !vmStore.Load().CheckForUpdates, "Turning off startup checks is saved and stops the quiet check");
updates.Offline = true;
await vm.CheckForUpdatesCommand.ExecuteAsync(null);
Check(vm.UpdateText.Contains("offline") && !vm.IsBusy, "An offline manual check says so and keeps working");

Check(UpdateService.CompareVersions("0.2.0-alpha.10", "0.2.0-alpha.2") > 0 && UpdateService.CompareVersions("0.2.0", "0.2.0-alpha.9") > 0
    && UpdateService.CompareVersions("0.2.0-alpha.2", "0.2.0-beta.1") < 0 && UpdateService.CompareVersions("1.0.0", "0.10.0") > 0
    && UpdateService.CompareVersions("0.2.0-alpha.2", "0.2.0-alpha.2") == 0, "Release versions order by semantic-version precedence");
const string prefix = "https://github.com/ianrastall/media-workbench/releases/download/";
string Release(string version, bool prerelease, bool draft = false, string host = prefix, bool checksum = true) =>
    $$"""{"tag_name":"v{{version}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"body":"Notes {{version}}","html_url":"https://github.com/ianrastall/media-workbench/releases/tag/v{{version}}","assets":[{"name":"MediaWorkbench-{{version}}-win-x64-setup.exe","browser_download_url":"{{host}}v{{version}}/MediaWorkbench-{{version}}-win-x64-setup.exe"}{{(checksum ? $$""",{"name":"MediaWorkbench-{{version}}-win-x64-setup.exe.sha256","browser_download_url":"{{host}}v{{version}}/MediaWorkbench-{{version}}-win-x64-setup.exe.sha256"}""" : "")}}]}""";
using (var releases = System.Text.Json.JsonDocument.Parse("[" + string.Join(",", Release("0.4.0", false, draft: true), Release("0.3.1-alpha.1", true, host: "https://example.invalid/"),
    Release("0.3.0-alpha.3", true, checksum: false), Release("0.3.0-alpha.2", true), Release("0.2.0-alpha.2", true), Release("0.1.0", false)) + "]"))
{
    Check(UpdateService.SelectUpdate(releases.RootElement, "0.2.0-alpha.2")?.Version == "0.3.0-alpha.2",
        "Update selection skips drafts, foreign download hosts, and releases without a checksum");
    Check(UpdateService.SelectUpdate(releases.RootElement, "0.3.0-alpha.2") is null, "The newest usable release is not offered to itself");
    Check(UpdateService.SelectUpdate(releases.RootElement, "0.0.9")?.Version == "0.1.0", "Stable installs are offered only stable releases");
}
byte[] installerBytes = "fake installer"u8.ToArray();
string installerName = "MediaWorkbench-0.3.0-alpha.2-win-x64-setup.exe", installerUrl = prefix + "v0.3.0-alpha.2/" + installerName;
async Task<Exception?> TryDownload(string checksumLine)
{
    var http = new HttpClient(new StubHttp(new() { [installerUrl] = installerBytes, [installerUrl + ".sha256"] = System.Text.Encoding.ASCII.GetBytes(checksumLine) }));
    try { await new UpdateService(http, "0.2.0-alpha.2").DownloadAsync(new("0.3.0-alpha.2", "", installerUrl, installerUrl + ".sha256", ""), new Progress<OperationProgress>(), deadline.Token); return null; }
    catch (Exception ex) { return ex; }
}
string goodHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(installerBytes));
string downloaded = Path.Combine(Path.GetTempPath(), "MediaWorkbench-update", installerName);
Check(await TryDownload($"{goodHash}  {installerName}") is null && File.ReadAllBytes(downloaded).SequenceEqual(installerBytes), "A download matching its published checksum is kept");
File.Delete(downloaded);
Check(await TryDownload($"{new string('0', 64)}  {installerName}") is InvalidDataException && !File.Exists(downloaded) && !File.Exists(downloaded + ".partial"),
    "A download that fails its checksum is discarded");
await HousekeepingChecks.RunAsync(Check, root, output, deadline.Token);


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
using var mcpDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await StorageChecks.RunAsync(Check, root, output, mcpDeadline.Token);
await FileVerificationChecks.RunAsync(Check, output, mcpDeadline.Token);
await McpChecks.RunAsync(Check, root, output, mcpDeadline.Token);
// The hardware profile as the Python worker sends it.
const string profileJson = """
{"hardware": {"gpu": {"name": "NVIDIA GeForce RTX 3060", "vramMiB": 12288, "freeVramMiB": 11500, "computeCapability": "8.6", "driver": "610.60"},
  "gpuProblem": null, "ramMiB": 7987, "availableRamMiB": 3100, "drive": {"path": "C:\\Data", "freeBytes": 53687091200, "totalBytes": 512110190592, "kind": "ssd"}},
 "recommended": {"runtime": "fp16-gpu", "blocked": null, "reasons": ["12 GB of graphics memory holds the whole model, so nothing waits in system memory."], "warnings": [],
  "settings": {"id": "fp16-gpu", "label": "Fast", "description": "half precision, the whole model stays on the graphics card", "precision": "float16", "offload": false}},
 "verified": {"id": "fp32-offload", "label": "Memory-saving", "description": "full precision, parts of the model wait in system memory until needed", "precision": "float32", "offload": true},
 "note": "This computer can use fast settings. Install / verify again in Create to test them."}
""";
var wired = System.Text.Json.JsonSerializer.Deserialize<SystemProfile>(profileJson, Wire.Json)!;
Check(wired.CanMakePictures && wired.Hardware.Gpu!.ComputeCapability == "8.6" && wired.Recommended.Settings is { Precision: "float16", Offload: false } && wired.Verified!.Id == "fp32-offload",
    "The worker's hardware profile reads into typed records");
Check(wired.Summary == "This computer: NVIDIA GeForce RTX 3060 with 12 GB of graphics memory · 7.8 GB of memory (3 GB free now) · Data folder on a solid-state drive with 50 GB free.\n"
    + "Pictures are made with memory-saving settings (full precision, parts of the model wait in system memory until needed), tested on this computer.\n"
    + "This computer can use fast settings. Install / verify again in Create to test them.", "The setup card shows tested settings and says when faster ones can be tested");
var blockedProfile = wired with { Hardware = wired.Hardware with { Gpu = wired.Hardware.Gpu! with { Name = "NVIDIA GeForce GTX 1650", VramMiB = 4096 } },
    Recommended = new(null, "NVIDIA GeForce GTX 1650 has 4 GB of graphics memory; Stable Diffusion 1.5 needs a graphics card with at least 6 GB.", [], [], null), Verified = null, Note = "" };
Check(!blockedProfile.CanMakePictures && blockedProfile.Summary.EndsWith("\nNVIDIA GeForce GTX 1650 has 4 GB of graphics memory; Stable Diffusion 1.5 needs a graphics card with at least 6 GB.")
    && blockedProfile.Details.Contains("4 GB of graphics memory"), "A computer that cannot make pictures is told why in plain words");
var unread = wired with { Verified = null, Note = "", Recommended = wired.Recommended with { Warnings = ["This computer has 7.8 GB of memory."] },
    Hardware = wired.Hardware with { Gpu = null, GpuProblem = null, RamMiB = null, Drive = wired.Hardware.Drive! with { Kind = "network", FreeBytes = null } } };
Check(unread.Summary.Contains("No NVIDIA graphics card was found. · System memory could not be read · Data folder on a network drive.")
    && unread.Summary.Contains("Setup will test fast settings (half precision") && unread.Summary.Contains("This computer has 7.8 GB of memory.")
    && unread.DriveAdvice.Length > 0, "Missing readings, warnings, and a network drive are described plainly");
Check(SystemProfile.Size(6143.7) == "6 GB" && SystemProfile.Size(5939) == "5.8 GB" && SystemProfile.Size(1907729) == "1,863 GB" && SystemProfile.DriveKind("odd") == "a drive of unknown type",
    "Sizes and drive kinds use plain units and words");

var hubStore = new SettingsStore(Path.Combine(output, "hub-settings"));
var tensorLayout = new Dictionary<string, ModelTensor>
{
    ["model.diffusion_model.input_blocks.0.0.weight"] = new("F16", [320, 4, 3, 3]),
    ["model.diffusion_model.input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight"] = new("F16", [320, 768]),
    ["model.diffusion_model.out.2.weight"] = new("F16", [4, 320, 3, 3]),
    ["cond_stage_model.transformer.text_model.embeddings.token_embedding.weight"] = new("F16", [49408, 768]),
};
Check(NativeCore.CheckModelCompatibility("checkpoint", tensorLayout) is { Compatible: true, OwnVae: false, Message: "" },
    "C# calls the real native SD 1.5 policy and identifies the bundled VAE fallback");
tensorLayout["model.diffusion_model.out.2.weight"] = new("F8_E4M3", [4, 320, 3, 3]);
Check(NativeCore.CheckModelCompatibility("checkpoint", tensorLayout) is { Compatible: false, Message: var refusal }
    && refusal.Contains("compressed number format"), "Native compatibility gives a plain reason for unsupported weight types");
bool invalidTensorRefused = false;
try { NativeCore.CheckModelCompatibility("checkpoint", new Dictionary<string, ModelTensor> { ["bad"] = new("F16", [ulong.MaxValue]) }); }
catch (InvalidDataException) { invalidTensorRefused = true; }
Check(invalidTensorRefused, "The native model boundary refuses out-of-range tensor dimensions");
var nativeHub = NativeCore.PlanHubFiles("owner/model", new string('a', 40), "openrail", false,
    [new("model.safetensors", 100, new string('b', 64)), new("../escape.safetensors", 100, new string('b', 64)), new("unet/model.safetensors", 100, new string('b', 64))]);
Check(nativeHub.Blocker == "" && nativeHub.Files is [{ Path: "model.safetensors" }], "C# calls the real Rust Hub planner and excludes unsafe paths and individual model parts");
Check(NativeCore.PlanHubFiles("owner/model", "main", "openrail", true, []).Blocker.Length > 0,
    "The native Hub plan refuses protected or unpinned metadata without implying model readiness");
string hubRoot = Path.Combine(output, "hub-data");
hubStore.Save(new(1, hubRoot, "model"));
var hubService = new RecordingWorkbench();
var hubVm = new MainViewModel(hubService, hubStore, updates);
var hubModel = new HubModel("owner/model", 1, 1, "text-to-image", "diffusers", "Public");
hubVm.SelectedModel = hubModel;
hubVm.DataRootDraft = Path.Combine(output, "unsaved-hub-folder");
await hubVm.DetailsCommand.ExecuteAsync(null);
Check(hubService.LastHubCheck is { Root: var checkedRoot, Revision: "", File: "" } && checkedRoot == hubRoot,
    "Discover checks this computer using the saved folder rather than an unsaved Settings draft");
Check(!hubVm.DownloadModelCommand.CanExecute(null) && hubVm.HubFiles.Count == 1 && hubVm.HubPlanText.Contains("2.00 GiB"),
    "A checked Hub file shows its download size and still requires reviewing the license");
hubVm.HubLicenseReviewed = true;
Check(hubVm.DownloadModelCommand.CanExecute(null) && hubVm.ModelSourceUrl.EndsWith("/tree/" + new string('a', 40)),
    "Download requires the checked file and the model-card link uses the pinned revision");
var alternateHubFile = new HubDownloadFile("alternate.safetensors", 3L << 30, new string('c', 64));
hubVm.SelectedHubFile = alternateHubFile;
hubVm.HubLicenseReviewed = true;
Check(!hubVm.DownloadModelCommand.CanExecute(null), "Changing a model file invalidates its old compatibility check");
hubService.HubPlan = hubService.HubPlan with { Files = [alternateHubFile], SelectedFile = alternateHubFile };
await hubVm.CheckHubFileCommand.ExecuteAsync(null);
Check(hubService.LastHubCheck is { Revision: var checkedRevision, File: "alternate.safetensors" } && checkedRevision == new string('a', 40)
    && !hubVm.HubLicenseReviewed, "A different file is checked at the reviewed immutable revision and requires a new license review");
hubVm.HubLicenseReviewed = true;
await hubVm.DownloadModelCommand.ExecuteAsync(null);
Check(hubService.LastHubDownload is { Root: var downloadedRoot, Plan.SelectedFile.Path: "alternate.safetensors" } && downloadedRoot == hubRoot
    && hubVm.SelectedPage == "create" && hubVm.ModelChoices[hubVm.ModelIndex].Id == new string('d', 32),
    "Discover downloads the exact checked file into the saved folder and selects a model only after it passes");
hubVm.SelectedModel = hubModel with { Id = "owner/other" };
Check(hubVm.HubFiles.Count == 0 && !hubVm.DownloadModelCommand.CanExecute(null), "Changing repository clears the previous download authorization");
hubVm.SelectedModel = hubModel;
hubService.HubPlan = hubService.HubPlan with { CanInstall = false, Message = "This computer needs more graphics memory." };
await hubVm.DetailsCommand.ExecuteAsync(null);
hubVm.HubLicenseReviewed = true;
Check(!hubVm.DownloadModelCommand.CanExecute(null) && hubVm.HubPlanText.Contains("more graphics memory"),
    "Discover displays a hardware refusal without enabling download");
hubService.HubPlan = hubService.HubPlan with { CanInstall = true };
await hubVm.DetailsCommand.ExecuteAsync(null);
hubVm.HubLicenseReviewed = true;
hubService.HubPlanGate = Task.FromException(new InvalidOperationException("Model details unavailable"));
await hubVm.DetailsCommand.ExecuteAsync(null);
Check(!hubVm.DownloadModelCommand.CanExecute(null), "A failed refresh cannot reuse an earlier successful compatibility check");
var hubGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
hubService.HubPlanGate = hubGate.Task;
var staleHubCheck = hubVm.DetailsCommand.ExecuteAsync(null);
hubVm.SelectedModel = hubModel with { Id = "owner/new-selection" };
hubGate.SetResult();
await staleHubCheck;
Check(hubVm.HubFiles.Count == 0 && !hubVm.DownloadModelCommand.CanExecute(null), "A late Hub response cannot authorize a different selected model");
hubService.HubPlanGate = null;
hubVm.SelectedModel = hubModel;
await hubVm.DetailsCommand.ExecuteAsync(null);
hubVm.HubLicenseReviewed = true;
hubVm.SaveSettingsCommand.Execute(null);
Check(!hubVm.DownloadModelCommand.CanExecute(null), "Saving a different data folder invalidates a Hub plan tied to the old folder");
await using (var hubWorker = await PythonWorkerClient.StartAsync(python, root, deadline.Token))
{
    string noCreate = Path.Combine(output, "invalid-hub-request");
    var invalidHub = await hubWorker.ExecuteAsync("catalog.plan", new { dataRoot = noCreate, repoId = "../outside" }, cancellationToken: deadline.Token);
    var unreviewedHub = await hubWorker.ExecuteAsync("models.download", new { dataRoot = noCreate, licenseReviewed = false }, cancellationToken: deadline.Token);
    Check(invalidHub.ErrorCode == "invalid_input" && unreviewedHub.ErrorCode == "invalid_input" && !Directory.Exists(noCreate),
        "The real worker routes Hub operations and rejects unsafe or unreviewed requests before networking or writes");
}

Console.WriteLine($"All {checks} C# checks including MCP passed. Test artifacts: {output}");
