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
        try
        {
            var saved = settings.Load();
            DataRoot = saved.DataRoot;
            SearchQuery = saved.LastSearch;
        }
        catch (Exception ex) { Notice = $"Could not load settings: {ex.Message}"; }
        try { NativeStatus = $"Rust core · ABI {NativeCore.VerifyAbi()}"; }
        catch (Exception ex) { NativeStatus = "Native core unavailable"; Notice = ex.Message; }
        WorkerStatus = File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe"))
            ? "Python runtime bundled" : "Python runtime missing; run the build script";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsDiscover), nameof(IsSettings), nameof(IsDiagnostics), nameof(IsPlanned), nameof(PageTitle), nameof(PageDescription))]
    public partial string SelectedPage { get; set; } = "home";
    [ObservableProperty] public partial string SearchQuery { get; set; } = "stable-diffusion";
    [ObservableProperty] public partial int TaskIndex { get; set; } = 1;
    public string TaskFilter => TaskIndex switch { 1 => "text-to-image", 2 => "image-to-image", 3 => "text-to-video", 4 => "image-to-video", _ => "" };
    [ObservableProperty] public partial string DataRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench", "Data");
    [ObservableProperty] public partial string NativeStatus { get; set; } = "Checking native core…";
    [ObservableProperty] public partial string WorkerStatus { get; set; } = "Checking worker…";
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string Notice { get; set; } = "This first alpha supports model discovery and diagnostics. Installation and generation are in development.";
    [ObservableProperty] public partial string DetailsText { get; set; } = "Select a model to inspect its source, revision, and files.";
    [ObservableProperty] public partial string DiagnosticsText { get; set; } = "Run diagnostics to check the native core and Python worker.";
    [ObservableProperty] public partial string PlanText { get; set; } = "Preview the baseline installation plan to see its prerequisites.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(DetailsCommand), nameof(DiagnosticsCommand), nameof(PlanCommand), nameof(SaveSettingsCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetailsCommand), nameof(OpenModelCommand))]
    public partial HubModel? SelectedModel { get; set; }

    public ObservableCollection<HubModel> Models { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand DetailsCommand { get; }
    public IAsyncRelayCommand DiagnosticsCommand { get; }
    public IRelayCommand PlanCommand { get; }
    public IRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand OpenDataFolderCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand OpenModelCommand { get; }

    public bool IsHome => SelectedPage == "home";
    public bool IsDiscover => SelectedPage == "discover";
    public bool IsSettings => SelectedPage == "settings";
    public bool IsDiagnostics => SelectedPage == "diagnostics";
    public bool IsPlanned => SelectedPage is "create" or "library" or "tools";
    public string PageTitle => SelectedPage switch
    {
        "discover" => "Discover models", "create" => "Create", "library" => "Library",
        "tools" => "Tools", "settings" => "Settings", "diagnostics" => "Diagnostics", _ => "Your local media workspace"
    };
    public string PageDescription => SelectedPage switch
    {
        "discover" => "Search public Hugging Face models and inspect their metadata before choosing a local pipeline.",
        "create" => "Image and video creation will use verified model recipes and isolated inference workers.",
        "library" => "Generated media and reproducible settings will appear here as generation support is added.",
        "tools" => "Focused native tools will add upscaling, frame processing, and export capabilities.",
        "settings" => "Choose where future models, environments, and media will be stored.",
        "diagnostics" => "Check the components that power Media Workbench.",
        _ => "Discover local generation models, understand their requirements, and prepare a dependable creative setup."
    };

    private async Task RunAsync(string description, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _operation = cancellation;
        IsBusy = true;
        Status = description;
        Record(description);
        try { await action(cancellation.Token); Status = "Ready"; }
        catch (OperationCanceledException) { Status = "Cancelled"; Record("Operation cancelled"); }
        catch (Exception ex) { Notice = ex.Message; Status = "Operation could not finish"; Record(ex.Message); }
        finally { _operation = null; IsBusy = false; }
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
            PlanText = $"Stable Diffusion 1.5 baseline\n\nPrerequisites:\n{string.Join("\n", plan.Blockers.Select(b => "• " + b))}\n\nPlanned stages:\n{string.Join("\n", plan.Steps.Select(s => "• " + s.Description))}";
            Record("Rust core prepared the baseline installation plan");
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private void SaveSettings()
    {
        try
        {
            if (!Path.IsPathFullyQualified(DataRoot)) throw new InvalidDataException("Choose an absolute data folder.");
            var value = new AppSettings(1, Path.GetFullPath(DataRoot), SearchQuery);
            _settings.Save(value);
            DataRoot = value.DataRoot;
            Notice = "Settings saved. Future model installs will use this data folder.";
            Record("Storage settings saved");
        }
        catch (Exception ex) { Notice = $"Settings were not fully applied: {ex.Message}"; }
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
