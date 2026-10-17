using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

namespace MediaWorkbench_App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IWorkbenchService _service;
    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _operation;

    public MainViewModel(IWorkbenchService service, ISettingsStore settings)
    {
        _service = service;
        _settings = settings;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        DetailsCommand = new AsyncRelayCommand(LoadDetailsAsync, () => !IsBusy && SelectedModel is not null);
        DiagnosticsCommand = new AsyncRelayCommand(DiagnoseAsync, () => !IsBusy);
        PlanCommand = new RelayCommand(ShowPlan, () => !IsBusy);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => !IsBusy);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        OpenModelCommand = new RelayCommand(OpenModel, () => SelectedModel is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy);
        RefreshInstallationCommand = new AsyncRelayCommand(RefreshInstallationAsync, () => !IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy && ModelReady);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, () => !IsBusy);
        DataRootDraft = DataRoot;
        try
        {
            var saved = settings.Load();
            DataRoot = DataRootDraft = saved.DataRoot;
            SearchQuery = saved.LastSearch;
        }
        catch (Exception ex) { Notice = $"Could not load settings: {ex.Message}"; }
        try { NativeStatus = $"Rust core · ABI {NativeCore.VerifyAbi()}"; }
        catch (Exception ex) { NativeStatus = "Native core unavailable"; Notice = ex.Message; }
        WorkerStatus = File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe"))
            ? "Python runtime bundled" : "Python runtime missing; run the build script";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsDiscover), nameof(IsCreate), nameof(IsLibrary), nameof(IsSettings), nameof(IsDiagnostics), nameof(IsPlanned), nameof(PageTitle), nameof(PageDescription))]
    public partial string SelectedPage { get; set; } = "home";
    [ObservableProperty] public partial string SearchQuery { get; set; } = "stable-diffusion";
    [ObservableProperty] public partial int TaskIndex { get; set; } = 1;
    public string TaskFilter => TaskIndex switch { 1 => "text-to-image", 2 => "image-to-image", 3 => "text-to-video", 4 => "image-to-video", _ => "" };
    /// <summary>The saved data folder every install, generation, and library operation uses. Changes only on load or save.</summary>
    [ObservableProperty] public partial string DataRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench", "Data");
    /// <summary>The Settings text box; it has no effect until Save settings applies it.</summary>
    [ObservableProperty] public partial string DataRootDraft { get; set; } = "";
    [ObservableProperty] public partial string NativeStatus { get; set; } = "Checking native core…";
    [ObservableProperty] public partial string WorkerStatus { get; set; } = "Checking worker…";
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string Notice { get; set; } = "Start in Create to install and verify Stable Diffusion 1.5. Requires an NVIDIA GPU with 6 GB VRAM and 20 GiB free space.";
    [ObservableProperty] public partial string InstallationText { get; set; } = "Check model status, or install / verify to prepare image generation.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateCommand))] public partial bool ModelReady { get; set; }
    [ObservableProperty] public partial string Prompt { get; set; } = "A small red ceramic house beside a green tree, watercolor illustration, daylight";
    [ObservableProperty] public partial string NegativePrompt { get; set; } = "";
    [ObservableProperty] public partial double Seed { get; set; } = 20261017;
    [ObservableProperty] public partial double Steps { get; set; } = 20;
    [ObservableProperty] public partial string PreviewPath { get; set; } = "";
    [ObservableProperty] public partial string ImageDescription { get; set; } = "Your generated image will appear here.";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string DetailsText { get; set; } = "Select a model to inspect its source, revision, and files.";
    [ObservableProperty] public partial string DiagnosticsText { get; set; } = "Run diagnostics to check the native core and Python worker.";
    [ObservableProperty] public partial string PlanText { get; set; } = "Preview the baseline installation plan to see its prerequisites.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(DetailsCommand), nameof(DiagnosticsCommand), nameof(PlanCommand), nameof(SaveSettingsCommand), nameof(CancelCommand), nameof(InstallCommand), nameof(RefreshInstallationCommand), nameof(GenerateCommand), nameof(RefreshLibraryCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetailsCommand), nameof(OpenModelCommand))]
    public partial HubModel? SelectedModel { get; set; }

    public ObservableCollection<HubModel> Models { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public ObservableCollection<GeneratedImage> Images { get; } = [];
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand DetailsCommand { get; }
    public IAsyncRelayCommand DiagnosticsCommand { get; }
    public IRelayCommand PlanCommand { get; }
    public IRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand OpenDataFolderCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand OpenModelCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RefreshInstallationCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand RefreshLibraryCommand { get; }

    public bool IsHome => SelectedPage == "home";
    public bool CanEdit => !IsBusy;
    public bool IsDiscover => SelectedPage == "discover";
    public bool IsCreate => SelectedPage == "create";
    public bool IsLibrary => SelectedPage == "library";
    public bool IsSettings => SelectedPage == "settings";
    public bool IsDiagnostics => SelectedPage == "diagnostics";
    public bool IsPlanned => SelectedPage == "tools";
    public string PageTitle => SelectedPage switch
    {
        "discover" => "Discover models", "create" => "Create", "library" => "Library",
        "tools" => "Tools", "settings" => "Settings", "diagnostics" => "Diagnostics", _ => "Your local media workspace"
    };
    public string PageDescription => SelectedPage switch
    {
        "discover" => "Search public Hugging Face models and inspect their metadata before choosing a local pipeline.",
        "create" => "Generate 512 × 512 images locally with Stable Diffusion 1.5. The first setup downloads a private runtime and model, then tests your GPU.",
        "library" => "Images and their prompts, seeds, and runtime evidence are saved in your data folder.",
        "tools" => "Focused native tools will add upscaling, frame processing, and export capabilities.",
        "settings" => "Choose where models, private environments, and media are stored.",
        "diagnostics" => "Check the components that power Media Workbench.",
        _ => "Discover local generation models, understand their requirements, and prepare a dependable creative setup."
    };

    private async Task RunAsync(string description, Func<CancellationToken, Task> action, bool longRunning = false)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(longRunning ? TimeSpan.FromHours(4) : TimeSpan.FromSeconds(45));
        _operation = cancellation;
        IsBusy = true;
        Status = description;
        ProgressIndeterminate = true;
        Record(description);
        try { await action(cancellation.Token); Status = "Ready"; }
        catch (OperationCanceledException) { Status = "Cancelled"; Record("Operation cancelled"); }
        catch (Exception ex) { Notice = ex.Message; Status = "Operation could not finish"; Record(ex.Message); }
        finally { _operation = null; IsBusy = false; ProgressIndeterminate = false; }
    }

    private IProgress<OperationProgress> ObserveProgress() => new Progress<OperationProgress>(value =>
    {
        Status = value.Total > 0 ? value.Unit == "bytes"
            ? $"{value.Stage} · {value.Completed / 1048576.0:N0}/{value.Total / 1048576.0:N0} MiB"
            : $"{value.Stage} · {value.Completed:N0}/{value.Total:N0} {value.Unit}" : value.Stage;
        ProgressIndeterminate = value.Total <= 0;
        ProgressPercent = value.Total > 0 ? Math.Clamp(100.0 * value.Completed / value.Total, 0, 100) : 0;
    });

    private async Task UpdateInstallation(CancellationToken token)
    {
        var state = await _service.GetInstallationAsync(DataRoot, token);
        InstallationText = $"{state.Status.Replace('_', ' ')} · {state.Message}";
        ModelReady = state.Status == "ready";
    }
    private Task RefreshInstallationAsync() => RunAsync("Checking model state…", UpdateInstallation);
    private Task InstallAsync() => RunAsync("Preparing Stable Diffusion 1.5…", async token =>
    {
        ModelReady = false;
        try
        {
            var state = await _service.InstallAsync(DataRoot, ObserveProgress(), token);
            InstallationText = state.Message;
            ModelReady = state.Status == "ready";
            Notice = state.Message;
            Record(state.Message);
        }
        finally
        {
            // A force-stopped worker leaves an interrupted attempt that the next check recovers.
            if (!ModelReady) InstallationText = "Setup did not finish. Check model status for details; install / verify resumes retained downloads.";
        }
    }, longRunning: true);
    private Task GenerateAsync() => RunAsync("Preparing image generation…", async token =>
    {
        if (!double.IsFinite(Seed) || Seed != Math.Truncate(Seed) || Seed < 0 || Seed > uint.MaxValue ||
            !double.IsFinite(Steps) || Steps != Math.Truncate(Steps) || Steps < 1 || Steps > 50)
            throw new InvalidDataException("Use a whole-number seed (0–4294967295) and 1–50 steps.");
        var image = await _service.GenerateAsync(DataRoot, new(Prompt, NegativePrompt, (long)Seed, (int)Steps), ObserveProgress(), token);
        PreviewPath = image.ImagePath;
        ImageDescription = image.Description;
        Images.Insert(0, image);
        Notice = $"Image saved in {image.Seconds:N1} seconds of inference.";
        Record("Saved " + image.ImagePath);
    }, longRunning: true);
    private Task RefreshLibraryAsync() => RunAsync("Loading saved images…", async token =>
    {
        var library = await _service.GetLibraryAsync(DataRoot, token);
        Images.Clear();
        foreach (var image in library.Images) Images.Add(image);
        Notice = $"Loaded {library.Images.Count} saved images (up to 200 recent jobs).";
        if (library.Recovered > 0)
        {
            Notice += $" {library.Recovered} unfinished job(s) from an earlier session were marked interrupted.";
            Record($"Recorded {library.Recovered} interrupted job(s)");
        }
    });
    partial void OnDataRootChanged(string value) { ModelReady = false; Images.Clear(); PreviewPath = ""; }
    partial void OnSelectedPageChanged(string value)
    {
        if (value == "create" && !IsBusy) RefreshInstallationCommand.Execute(null);
        if (value == "library" && !IsBusy) RefreshLibraryCommand.Execute(null);
    }

    private Task SearchAsync() => RunAsync("Searching Hugging Face…", async token =>
    {
        var models = await _service.SearchAsync(SearchQuery, TaskFilter ?? "", token);
        Models.Clear();
        SelectedModel = null;
        foreach (var model in models) Models.Add(model);
        Notice = models.Count == 0 ? "No models matched. Try another search or task filter." : $"Found {models.Count} public models. These results do not imply installation support or hardware compatibility.";
        Record($"Loaded {models.Count} model results");
    });

    private Task LoadDetailsAsync()
    {
        string? id = SelectedModel?.Id;
        return id is null ? Task.CompletedTask : RunAsync("Loading model details…", async token =>
        {
            var model = await _service.GetDetailsAsync(id, token);
            DetailsText = $"{model.Id}\n\nTask: {model.Task}\nLibrary: {model.Library}\nAccess: {model.Access}\nLicense: {model.License}\nRevision: {model.Revision}\n\nRepository files (up to 200):\n{string.Join("\n", model.Files)}";
            Record($"Inspected {model.Id}");
        });
    }

    private Task DiagnoseAsync() => RunAsync("Checking components…", async token =>
    {
        DiagnosticsText = await _service.GetHealthAsync(token);
        WorkerStatus = "Python worker connected";
        Record("Native core and worker responded");
    });

    private void ShowPlan()
    {
        try
        {
            var plan = _service.GetBaselinePlan();
            PlanText = $"Stable Diffusion 1.5 baseline\n\n{(plan.CanExecute ? "Recipe permits installation and local verification. Open Create to begin." : string.Join("\n", plan.Blockers))}\n\nStages:\n{string.Join("\n", plan.Steps.Select(s => "• " + s.Description))}";
            Record("Rust core prepared the baseline installation plan");
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private void SaveSettings()
    {
        try
        {
            if (!Path.IsPathFullyQualified(DataRootDraft)) throw new InvalidDataException("Choose an absolute data folder.");
            var value = new AppSettings(1, Path.GetFullPath(DataRootDraft), SearchQuery);
            _settings.Save(value);
            DataRoot = DataRootDraft = value.DataRoot;
            Notice = "Settings saved. Models, generation, and Library now use this data folder.";
            Record("Storage settings saved");
        }
        catch (Exception ex) { Notice = $"Settings were not saved; still using {DataRoot}. {ex.Message}"; }
    }

    private void OpenDataFolder()
    {
        try
        {
            if (!Directory.Exists(DataRoot)) { Notice = "Save a valid data folder first."; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(Path.GetFullPath(DataRoot));
            Process.Start(start);
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private void OpenModel()
    {
        if (SelectedModel is null) return;
        try
        {
            string id = string.Join('/', SelectedModel.Id.Split('/').Select(Uri.EscapeDataString));
            Process.Start(new ProcessStartInfo("https://huggingface.co/" + id) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = ex.Message; }
    }
    private void Record(string message)
    {
        Activity.Insert(0, $"{DateTime.Now:t}   {message}");
        while (Activity.Count > 30) Activity.RemoveAt(Activity.Count - 1);
    }
    public void CancelActiveWork() => _operation?.Cancel();
}
