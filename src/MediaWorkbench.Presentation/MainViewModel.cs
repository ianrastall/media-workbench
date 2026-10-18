using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

namespace MediaWorkbench.Presentation;

public partial class MainViewModel : ObservableObject
{
    private readonly IWorkbenchService _service;
    private readonly ISettingsStore _settings;
    private readonly IUpdateService _updates;
    private bool _loading = true;
    private CancellationTokenSource? _operation;

    public MainViewModel(IWorkbenchService service, ISettingsStore settings, IUpdateService updates)
    {
        _service = service;
        _settings = settings;
        _updates = updates;
        AppVersion = updates.CurrentVersion;
        UpdateText = $"Media Workbench {AppVersion}.";
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => !IsBusy && HasUpdate);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        DetailsCommand = new AsyncRelayCommand(LoadDetailsAsync, () => !IsBusy && SelectedModel is not null);
        CheckHubFileCommand = new AsyncRelayCommand(CheckHubFileAsync, () => !IsBusy && _hubPlan is not null && SelectedHubFile is not null);
        DownloadModelCommand = new AsyncRelayCommand(DownloadHubModelAsync, () => !IsBusy && CanDownloadHubModel);
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
        PreviousLibraryPageCommand = new AsyncRelayCommand(() => LoadLibraryPageAsync(Math.Max(0, _libraryOffset - LibraryPageSize)), () => !IsBusy && _libraryOffset > 0);
        NextLibraryPageCommand = new AsyncRelayCommand(() => LoadLibraryPageAsync(_libraryOffset + LibraryPageSize), () => !IsBusy && LibraryHasMore);
        UseImageSettingsCommand = new RelayCommand<GeneratedImage?>(UseImageSettings, image => !IsBusy && image?.CanReuseSettings == true);
        ExportImageCommand = new AsyncRelayCommand<ImageExportRequest?>(ExportImageAsync, request => !IsBusy && request is not null);
        OpenImageCommand = new RelayCommand<string?>(OpenImage, path => !string.IsNullOrEmpty(path));
        RetryJobCommand = new RelayCommand<JobSummary?>(RetryJob, job => !IsBusy && job?.CanRetry == true);
        OpenJobLogCommand = new RelayCommand<JobSummary?>(OpenJobLog, job => job?.HasLog == true);
        RefreshStorageCommand = new AsyncRelayCommand(RefreshStorageAsync, () => !IsBusy);
        UseAsStartingPictureCommand = new RelayCommand<string?>(path => UseAsStartingPicture(path));
        UseForUpscaleCommand = new RelayCommand<string?>(path => UseForUpscale(path));
        UpscaleCommand = new AsyncRelayCommand(UpscaleAsync, () => !IsBusy && ModelReady && HasUpscaleSource);
        GoToCreateCommand = new RelayCommand(() => SelectedPage = "create");
        ClearStartingPictureCommand = new RelayCommand(() => SourceImagePath = "");
        ClearPaintingCommand = new RelayCommand(() => { Mask?.Clear(); MaskVersion++; }, () => IsPainting);
        StopPaintingCommand = new RelayCommand(StopPainting, () => IsPainting);
        ClearStorageCommand = new AsyncRelayCommand<string?>(ClearStorageAsync, area => !IsBusy && !string.IsNullOrEmpty(area));
        ImportModelCommand = new AsyncRelayCommand(ImportModelAsync, () => !IsBusy && ModelReady && HasImportCandidate);
        CancelImportCommand = new RelayCommand(ClearImportCandidate);
        RetestModelCommand = new AsyncRelayCommand<ModelChoice?>(RetestModelAsync, model => !IsBusy && model is { BuiltIn: false });
        RemoveModelCommand = new AsyncRelayCommand<ModelChoice?>(RemoveModelAsync, model => !IsBusy && model is { BuiltIn: false });
        InstallVideoCommand = new AsyncRelayCommand(InstallVideoAsync, () => !IsBusy && ModelReady);
        InstallXlCommand = new AsyncRelayCommand(InstallXlAsync, () => !IsBusy && ModelReady);
        RemoveXlCommand = new AsyncRelayCommand(RemoveXlAsync, () => !IsBusy && XlReady);
        GenerateVideoCommand = new AsyncRelayCommand(GenerateVideoAsync, () => !IsBusy && ModelReady && VideoReady);
        RemoveVideoCommand = new AsyncRelayCommand(RemoveVideoAsync, () => !IsBusy);
        OpenVideoCommand = new RelayCommand<string?>(OpenVideo, path => !string.IsNullOrEmpty(path));
        ModelChoices.Add(BuiltInChoice);
        DataRootDraft = DataRoot;
        try
        {
            var saved = settings.Load();
            DataRoot = DataRootDraft = saved.DataRoot;
            SearchQuery = saved.LastSearch;
            CheckForUpdates = saved.CheckForUpdates;
            KeepDownloadCaches = saved.KeepDownloadCaches;
        }
        catch (Exception ex) { Notice = $"Could not load settings: {ex.Message}"; }
        _loading = false;
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
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(UpscaleCommand), nameof(ImportModelCommand), nameof(InstallVideoCommand), nameof(GenerateVideoCommand), nameof(InstallXlCommand)),
     NotifyPropertyChangedFor(nameof(NeedsSetup), nameof(NeedsVideoSetup), nameof(CanMakeVideo), nameof(CanMakePicture), nameof(NeedsXlSetup))] public partial bool ModelReady { get; set; }
    public bool NeedsSetup => !ModelReady;
    /// <summary>"This computer" for Create's setup card: hardware and the settings chosen from it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSystemProfile))] public partial string SystemProfileText { get; set; } = "";
    public bool HasSystemProfile => SystemProfileText.Length > 0;
    [ObservableProperty] public partial string SystemProfileDetails { get; set; } = "Run diagnostics to read this computer's graphics card, memory, and data folder drive.";
    /// <summary>Shown beside a ready installation when faster settings could be tested, or the graphics card changed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRuntimeNote))] public partial string RuntimeNote { get; set; } = "";
    public bool HasRuntimeNote => RuntimeNote.Length > 0;
    private string? _profileRoot;
    private static readonly ModelChoice BuiltInChoice = new(ModelChoice.BuiltInId, "Stable Diffusion 1.5", "ready", "", "bundled", BuiltIn: true);
    /// <summary>Models Create can use now: the bundled model, then added models that passed their test.</summary>
    public ObservableCollection<ModelChoice> ModelChoices { get; } = [];
    /// <summary>Every added model with its tested state, for Settings.</summary>
    public ObservableCollection<ModelChoice> AddedModels { get; } = [];
    /// <summary>Index into <see cref="ModelChoices"/>; the chooser is hidden while only the bundled model exists.</summary>
    [ObservableProperty] public partial int ModelIndex { get; set; }
    [ObservableProperty] public partial bool HasModelChoice { get; private set; }
    [ObservableProperty] public partial bool HasAddedModels { get; private set; }
    public string SelectedModelId => ModelChoices.ElementAtOrDefault(Math.Max(0, ModelIndex))?.Id ?? ModelChoice.BuiltInId;
    /// <summary>A file or folder the user chose to add, identified but not yet copied.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportCandidate), nameof(HasNoImportCandidate)), NotifyCanExecuteChangedFor(nameof(ImportModelCommand))]
    public partial string ImportPath { get; private set; } = "";
    public bool HasImportCandidate => ImportPath.Length > 0;
    public bool HasNoImportCandidate => !HasImportCandidate;
    [ObservableProperty] public partial string ImportName { get; set; } = "";
    [ObservableProperty] public partial string ImportText { get; set; } = "Choose a .safetensors file or a Diffusers model folder you downloaded.";
    [ObservableProperty] public partial string Prompt { get; set; } = "A small red ceramic house beside a green tree, watercolor illustration, daylight";
    [ObservableProperty] public partial string NegativePrompt { get; set; } = "";
    [ObservableProperty] public partial double Seed { get; set; } = 20261017;
    [ObservableProperty] public partial double Steps { get; set; } = 20;
    [ObservableProperty] public partial double GuidanceScale { get; set; } = 7.5;
    /// <summary>Index into <see cref="GenerationSettings.Sizes"/>.</summary>
    [ObservableProperty] public partial int SizeIndex { get; set; }
    /// <summary>Index into <see cref="GenerationSettings.Samplers"/>.</summary>
    [ObservableProperty] public partial int SamplerIndex { get; set; }
    /// <summary>The shapes the chosen model and mode offer: Stable Diffusion XL pictures are larger.</summary>
    public ObservableCollection<string> SizeLabels { get; } = [.. GenerationSettings.Sizes.Select(size => size.Label)];
    public (int Width, int Height, string Label)[] ActiveSizes => IsPictureMode && SelectedModelId == ModelChoice.XlId ? GenerationSettings.XlSizes : GenerationSettings.Sizes;
    private void ShowSizes()
    {
        int index = SizeIndex;
        var labels = ActiveSizes.Select(size => size.Label).ToArray();
        if (SizeLabels.SequenceEqual(labels)) return;
        SizeLabels.Clear();
        foreach (string label in labels) SizeLabels.Add(label);
        SizeIndex = Math.Clamp(index, 0, labels.Length - 1);
    }
    partial void OnModelIndexChanged(int value) => ShowSizes();
    partial void OnCreateKindIndexChanged(int value) => ShowSizes();
    /// <summary>Stable Diffusion XL's row from the model list; null until the list is read.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(XlReady), nameof(NeedsXlSetup), nameof(XlText), nameof(XlSettingsText))]
    public partial ModelChoice? XlChoice { get; private set; }
    public bool XlReady => XlChoice?.IsReady == true;
    public bool NeedsXlSetup => ModelReady && !XlReady;
    public string XlText => XlChoice is { IsReady: false, Message.Length: > 0 } xl ? xl.Message : "Stable Diffusion XL is not set up.";
    public string XlSettingsText => XlReady ? "Stable Diffusion XL is set up and tested; choose it under Model in Create."
        : "Stable Diffusion XL is not set up. Create offers it once Stable Diffusion 1.5 is ready.";
    public IAsyncRelayCommand InstallXlCommand { get; }
    public IAsyncRelayCommand RemoveXlCommand { get; }
    public IReadOnlyList<string> SamplerLabels { get; } = [.. GenerationSettings.Samplers.Select(sampler => sampler.Label)];
    /// <summary>An optional picture to start from (image-to-image); empty for a description only.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSourceImage), nameof(HasNoSourceImage), nameof(SourceImageName), nameof(IsChangingWholePicture), nameof(ShowsShape))]
    public partial string SourceImagePath { get; set; } = "";
    public bool HasSourceImage => SourceImagePath.Length > 0;
    public bool HasNoSourceImage => !HasSourceImage;
    public string SourceImageName => HasSourceImage ? Path.GetFileName(SourceImagePath) : "";
    /// <summary>Index into <see cref="GenerationSettings.ChangeLevels"/>.</summary>
    [ObservableProperty] public partial int ChangeIndex { get; set; } = 1;
    /// <summary>The painted part of the starting picture to change; null when the whole picture changes.</summary>
    public MaskCanvas? Mask { get; private set; }
    /// <summary>Whether Create changes only a painted part of the starting picture.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChangingWholePicture)), NotifyCanExecuteChangedFor(nameof(ClearPaintingCommand), nameof(StopPaintingCommand))]
    public partial bool IsPainting { get; private set; }
    public bool IsChangingWholePicture => HasSourceImage && !IsPainting;
    /// <summary>Increments whenever <see cref="Mask"/> changes so the view redraws it.</summary>
    [ObservableProperty] public partial int MaskVersion { get; private set; }
    /// <summary>Index into <see cref="MaskCanvas.BrushSizes"/>.</summary>
    [ObservableProperty] public partial int BrushIndex { get; set; } = 1;
    [ObservableProperty] public partial bool IsErasing { get; set; }
    public IReadOnlyList<string> BrushLabels { get; } = [.. MaskCanvas.BrushSizes.Select(brush => brush.Label)];
    /// <summary>Index into <see cref="GenerationSettings.PaintChangeLevels"/>; a painted area is replaced completely by default.</summary>
    [ObservableProperty] public partial int PaintChangeIndex { get; set; } = 2;
    public IReadOnlyList<string> PaintChangeLabels { get; } = [.. GenerationSettings.PaintChangeLevels.Select(level => level.Label)];
    /// <summary>The picture Tools will enlarge.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpscaleSource), nameof(UpscaleSourceName)), NotifyCanExecuteChangedFor(nameof(UpscaleCommand))]
    public partial string UpscaleSourcePath { get; set; } = "";
    public bool HasUpscaleSource => UpscaleSourcePath.Length > 0;
    public string UpscaleSourceName => HasUpscaleSource ? Path.GetFileName(UpscaleSourcePath) : "No picture chosen yet.";
    /// <summary>Index into <see cref="GenerationSettings.UpscaleChoices"/>; 4× by default.</summary>
    [ObservableProperty] public partial int UpscaleIndex { get; set; } = 1;
    public IReadOnlyList<string> UpscaleLabels { get; } = [.. GenerationSettings.UpscaleChoices.Select(choice => choice.Label)];
    [ObservableProperty] public partial string UpscalePreviewPath { get; set; } = "";
    [ObservableProperty] public partial string UpscaleDescription { get; set; } = "";
    public IReadOnlyList<string> ChangeLabels { get; } = [.. GenerationSettings.ChangeLevels.Select(level => level.Label)];
    [ObservableProperty] public partial string PreviewPath { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLatestImage))] public partial GeneratedImage? LatestImage { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLatestUpscale))] public partial GeneratedImage? LatestUpscale { get; set; }
    public bool HasLatestImage => LatestImage is not null;
    public bool HasLatestUpscale => LatestUpscale is not null;
    /// <summary>What Create makes: 0 a picture, 1 a short video.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVideoMode), nameof(IsPictureMode), nameof(NeedsVideoSetup), nameof(CanMakeVideo), nameof(CanMakePicture), nameof(PromptHeader), nameof(ShowsShape))]
    public partial int CreateKindIndex { get; set; }
    public string PromptHeader => IsVideoMode ? "Describe the video" : "Describe the picture";
    /// <summary>Pictures from a starting picture keep its proportions; videos and new pictures choose a shape.</summary>
    public bool ShowsShape => IsVideoMode || HasNoSourceImage;
    public IReadOnlyList<string> CreateKindLabels { get; } = ["A picture", "A short video"];
    public bool IsVideoMode => CreateKindIndex == 1;
    public bool IsPictureMode => !IsVideoMode;
    /// <summary>Whether the motion adapter is set up and passed its GPU test here.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsVideoSetup), nameof(CanMakeVideo)), NotifyCanExecuteChangedFor(nameof(GenerateVideoCommand))]
    public partial bool VideoReady { get; set; }
    [ObservableProperty] public partial string VideoText { get; set; } = "Videos are not set up yet.";
    /// <summary>Settings' one-line summary of the video setup.</summary>
    [ObservableProperty] public partial string VideoSettingsText { get; set; } = "Videos are not set up. Create sets them up when you choose A short video.";
    public bool NeedsVideoSetup => IsVideoMode && ModelReady && !VideoReady;
    public bool CanMakeVideo => IsVideoMode && ModelReady && VideoReady;
    public bool CanMakePicture => IsPictureMode;
    /// <summary>Index into <see cref="GenerationSettings.VideoLengths"/>.</summary>
    [ObservableProperty] public partial int VideoLengthIndex { get; set; }
    public IReadOnlyList<string> VideoLengthLabels { get; } = [.. GenerationSettings.VideoLengths.Select(length => length.Label)];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLatestVideo))] public partial GeneratedImage? LatestVideo { get; set; }
    public bool HasLatestVideo => LatestVideo is not null;
    /// <summary>The latest video's MP4, which Create plays on a loop.</summary>
    [ObservableProperty] public partial string VideoPreviewPath { get; set; } = "";
    [ObservableProperty] public partial string VideoDescription { get; set; } = "Your video will appear here.";
    public IAsyncRelayCommand InstallVideoCommand { get; }
    public IAsyncRelayCommand GenerateVideoCommand { get; }
    public IAsyncRelayCommand RemoveVideoCommand { get; }
    public IRelayCommand<string?> OpenVideoCommand { get; }    [ObservableProperty] public partial string ImageDescription { get; set; } = "Your generated image will appear here.";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string DetailsText { get; set; } = "Select a model to inspect its source, revision, and files.";
    [ObservableProperty] public partial string DiagnosticsText { get; set; } = "Run diagnostics to check the native core and Python worker.";
    [ObservableProperty] public partial string PlanText { get; set; } = "Preview the baseline installation plan to see its prerequisites.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(DetailsCommand), nameof(DiagnosticsCommand), nameof(PlanCommand), nameof(SaveSettingsCommand), nameof(CancelCommand), nameof(InstallCommand), nameof(RefreshInstallationCommand), nameof(GenerateCommand), nameof(RefreshLibraryCommand), nameof(RetryJobCommand), nameof(RefreshStorageCommand), nameof(ClearStorageCommand), nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand), nameof(UpscaleCommand), nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand), nameof(UseImageSettingsCommand), nameof(ExportImageCommand), nameof(ImportModelCommand), nameof(RetestModelCommand), nameof(RemoveModelCommand), nameof(CheckHubFileCommand), nameof(DownloadModelCommand), nameof(InstallVideoCommand), nameof(GenerateVideoCommand), nameof(RemoveVideoCommand), nameof(InstallXlCommand), nameof(RemoveXlCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetailsCommand), nameof(OpenModelCommand))]
    public partial HubModel? SelectedModel { get; set; }

    public ObservableCollection<HubModel> Models { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public ObservableCollection<GeneratedImage> Images { get; } = [];
    /// <summary>Recent failed, cancelled, and interrupted generations in the saved data folder.</summary>
    public ObservableCollection<JobSummary> Problems { get; } = [];
    [ObservableProperty] public partial bool HasProblems { get; set; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand DetailsCommand { get; }
    public IAsyncRelayCommand CheckHubFileCommand { get; }
    public IAsyncRelayCommand DownloadModelCommand { get; }
    public ObservableCollection<HubDownloadFile> HubFiles { get; } = [];
    private HubDownloadPlan? _hubPlan;
    private string? _hubRoot;
    [ObservableProperty] public partial string HubPlanText { get; set; } = "Check a model to see whether this computer can download and test it.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(CheckHubFileCommand))]
    public partial HubDownloadFile? SelectedHubFile { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    public partial bool HubLicenseReviewed { get; set; }
    public bool CanDownloadHubModel => _hubPlan is { CanInstall: true, HeaderChecked: true, SelectedFile: { } file }
        && file == SelectedHubFile && _hubRoot == DataRoot && _hubPlan.Details.Id == SelectedModel?.Id && HubLicenseReviewed;

    partial void OnSelectedModelChanged(HubModel? value) => ClearHubPlan();
    partial void OnSelectedHubFileChanged(HubDownloadFile? value)
    {
        HubLicenseReviewed = false;
        if (_hubPlan is { SelectedFile: { } file } && file != value)
            HubPlanText = "Check this file before downloading. A different file can contain a different kind of model.";
    }

    private void ClearHubPlan()
    {
        _hubPlan = null; _hubRoot = null;
        HubFiles.Clear(); SelectedHubFile = null; HubLicenseReviewed = false;
        HubPlanText = "Check a model to see whether this computer can download and test it.";
        DetailsText = "Select a model to inspect its source, revision, and files.";
        CheckHubFileCommand.NotifyCanExecuteChanged(); DownloadModelCommand.NotifyCanExecuteChanged();
    }
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
    public IAsyncRelayCommand PreviousLibraryPageCommand { get; }
    public IAsyncRelayCommand NextLibraryPageCommand { get; }
    public IRelayCommand<GeneratedImage?> UseImageSettingsCommand { get; }
    public IAsyncRelayCommand<ImageExportRequest?> ExportImageCommand { get; }
    public IRelayCommand<string?> OpenImageCommand { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(McpConfiguration))] public partial bool McpAllowSetup { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(McpConfiguration))] public partial bool McpAllowInference { get; set; }
    public string McpConfiguration
    {
        get
        {
            List<string> args = ["mcp", "--data-root", DataRoot];
            if (McpAllowSetup) args.Add("--allow-setup");
            if (McpAllowInference) args.Add("--allow-inference");
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["media-workbench"] = new { command = Path.Combine(AppContext.BaseDirectory, "MediaWorkbench.Cli.exe"), args }
                }
            }, new System.Text.Json.JsonSerializerOptions(Wire.Json) { WriteIndented = true });
        }
    }
    private const int LibraryPageSize = 48;
    private int _libraryOffset;
    private LibraryQuery _libraryQuery = new();
    [ObservableProperty] public partial string LibrarySearch { get; set; } = "";
    [ObservableProperty] public partial int LibraryKindIndex { get; set; }
    public IReadOnlyList<string> LibraryKindLabels { get; } = ["Everything", "Generated pictures", "Upscaled pictures", "Videos"];
    [ObservableProperty] public partial string LibraryText { get; set; } = "Open Library to browse your saved images.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextLibraryPageCommand))] public partial bool LibraryHasMore { get; set; }
    public IRelayCommand<JobSummary?> RetryJobCommand { get; }
    public IRelayCommand<JobSummary?> OpenJobLogCommand { get; }
    public IAsyncRelayCommand RefreshStorageCommand { get; }
    public IAsyncRelayCommand CheckForUpdatesCommand { get; }
    public IRelayCommand<string?> UseAsStartingPictureCommand { get; }
    public IRelayCommand<string?> UseForUpscaleCommand { get; }
    public IAsyncRelayCommand UpscaleCommand { get; }
    public IRelayCommand GoToCreateCommand { get; }
    public IRelayCommand ClearStartingPictureCommand { get; }
    public IRelayCommand ClearPaintingCommand { get; }
    public IRelayCommand StopPaintingCommand { get; }
    public IAsyncRelayCommand InstallUpdateCommand { get; }
    /// <summary>Raised with a checksum-verified installer path once the user chose to update; the shell runs it and exits.</summary>
    public event Action<string>? InstallerReady;
    public string AppVersion { get; }
    [ObservableProperty] public partial string UpdateText { get; set; }
    [ObservableProperty] public partial string UpdateNotes { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))] public partial bool HasUpdate { get; set; }
    [ObservableProperty] public partial bool CheckForUpdates { get; set; } = true;
    private UpdateInfo? _update;
    public IAsyncRelayCommand<string?> ClearStorageCommand { get; }
    public IAsyncRelayCommand ImportModelCommand { get; }
    public IRelayCommand CancelImportCommand { get; }
    public IAsyncRelayCommand<ModelChoice?> RetestModelCommand { get; }
    public IAsyncRelayCommand<ModelChoice?> RemoveModelCommand { get; }
    public ObservableCollection<StorageItem> StorageItems { get; } = [];
    [ObservableProperty] public partial string StorageText { get; set; } = "Open Settings to measure what the data folder uses.";
    /// <summary>Keep setup's download caches for repairs instead of removing them once setup passes its test.</summary>
    [ObservableProperty] public partial bool KeepDownloadCaches { get; set; }
    /// <summary>Shown in Create and Settings when the data folder's drive is nearly full.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDriveWarning))] public partial string DriveWarning { get; private set; } = "";
    public bool HasDriveWarning => DriveWarning.Length > 0;
    /// <summary>Shown where model loading speed matters when the data folder is on a hard disk, USB, or network drive.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDriveSpeedNote))] public partial string DriveSpeedNote { get; private set; } = "";
    public bool HasDriveSpeedNote => DriveSpeedNote.Length > 0;

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
        "create" => "Make pictures and short videos locally with Stable Diffusion 1.5, or pictures with a compatible model you already downloaded. The first setup downloads a private runtime and model, then tests your GPU.",
        "library" => "Images and their prompts, seeds, and runtime evidence are saved in your data folder.",
        "tools" => "Make a picture bigger and sharper, offline. Works on your own pictures and on images from Library.",
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
        InstallationText = state.Message;
        ModelReady = state.Status == "ready";
        await UpdateModels(token);
        await UpdateSystemProfile(token);
        await UpdateVideo(token);
    }

    /// <summary>Whether videos are set up here; they need a ready Stable Diffusion 1.5 first.</summary>
    private async Task UpdateVideo(CancellationToken token)
    {
        if (!ModelReady) { ShowVideo(new("needs_base", "Set up Stable Diffusion 1.5 first (Install / verify under A picture); videos use it.")); return; }
        try { ShowVideo(await _service.GetVideoAsync(DataRoot, token)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowVideo(new("unknown", "Video setup could not be read: " + ex.Message)); }
    }

    private void ShowVideo(AddonState state)
    {
        VideoReady = state.IsReady;
        VideoText = state.Message;
        VideoSettingsText = state.Settings is { } settings
            ? $"Videos are set up and tested with {settings.Label.ToLowerInvariant()} settings ({settings.Description})."
            : state.IsReady ? "Videos are set up and tested." : "Videos are not set up. Create sets them up when you choose A short video.";
    }

    /// <summary>Reads the hardware once per data folder in a session; setup and Diagnostics read it again.</summary>
    private async Task UpdateSystemProfile(CancellationToken token, bool refresh = false)
    {
        if (!refresh && _profileRoot == DataRoot) return;
        try
        {
            var profile = await _service.GetSystemProfileAsync(DataRoot, token);
            SystemProfileText = profile.Summary;
            SystemProfileDetails = profile.Details;
            RuntimeNote = profile.Note;
            _profileRoot = DataRoot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SystemProfileText = SystemProfileDetails = "This computer's hardware could not be read: " + ex.Message;
            RuntimeNote = "";
        }
    }

    /// <summary>Reloads added models, keeping the chosen model when it is still ready.</summary>
    private async Task UpdateModels(CancellationToken token, string? select = null)
    {
        ShowModels(await _service.GetModelsAsync(DataRoot, token), select);
    }

    private void ShowModels(IReadOnlyList<ModelChoice> models, string? select = null)
    {
        string chosen = select ?? SelectedModelId;
        AddedModels.Clear();
        foreach (var model in models.Where(model => !model.BuiltIn && !model.IsXl)) AddedModels.Add(model);
        HasAddedModels = AddedModels.Count > 0;
        XlChoice = models.FirstOrDefault(model => model.IsXl);
        RemoveXlCommand.NotifyCanExecuteChanged();
        ModelChoices.Clear();
        ModelChoices.Add(BuiltInChoice);
        if (XlChoice is { IsReady: true } xl) ModelChoices.Add(xl);
        foreach (var model in AddedModels.Where(model => model.IsReady)) ModelChoices.Add(model);
        ModelIndex = Math.Max(0, ModelChoices.ToList().FindIndex(model => model.Id == chosen));
        HasModelChoice = ModelChoices.Count > 1;
        ShowSizes();  // The same index can now name a different model.
    }

    /// <summary>Identifies a model file or folder the user chose, without copying it.</summary>
    public Task ChooseModelToImportAsync(string? path) => RunAsync("Reading the model…", async token =>
    {
        ClearImportCandidate();
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return;
        try
        {
            var found = await _service.InspectModelAsync(path, token);
            ImportName = found.SuggestedName;
            ImportText = found.Summary;
            ImportPath = path;
            Notice = "Give the model a name, then select Add and test. Testing makes a small picture on your GPU.";
        }
        catch (InvalidOperationException ex)
        {
            ImportText = ex.Message;
            Notice = "That model cannot be added: " + ex.Message;
        }
    });

    private void ClearImportCandidate()
    {
        ImportPath = "";
        ImportName = "";
        ImportText = "Choose a .safetensors file or a Diffusers model folder you downloaded.";
    }

    private Task ImportModelAsync() => RunAsync("Adding your model…", async token =>
    {
        if (!HasImportCandidate) return;
        if (!File.Exists(ImportPath) && !Directory.Exists(ImportPath)) throw new InvalidDataException("That model is no longer there. Choose it again.");
        var added = await _service.ImportModelAsync(DataRoot, ImportPath, ImportName, ObserveProgress(), token);
        ClearImportCandidate();
        await UpdateModels(token, added.IsReady ? added.Id : null);
        Notice = added.IsReady ? $"“{added.Name}” passed its test and is selected under Model. Your original file was not changed; you may delete it to save space."
            : $"“{added.Name}” was added but is not ready: {added.Message}";
        Record($"Added model {added.Name}");
    }, longRunning: true);

    private Task RetestModelAsync(ModelChoice? model) => model is null ? Task.CompletedTask : RunAsync($"Testing {model.Name}…", async token =>
    {
        try
        {
            var tested = await _service.RetestModelAsync(DataRoot, model.Id, ObserveProgress(), token);
            Notice = $"“{tested.Name}” passed its test and can be chosen in Create.";
        }
        finally { await UpdateModels(token); }
        Record($"Tested model {model.Name}");
    }, longRunning: true);

    private Task RemoveModelAsync(ModelChoice? model) => model is null ? Task.CompletedTask : RunAsync($"Removing {model.Name}…", async token =>
    {
        ShowModels(await _service.RemoveModelAsync(DataRoot, model.Id, token));
        ShowStorage(await _service.GetStorageAsync(DataRoot, token));
        Notice = $"Removed “{model.Name}” from the data folder. Your images and your original file were kept.";
        Record(Notice);
    });

    /// <summary>Chooses the model a reused image or job was made with; returns a note when it is no longer available.</summary>
    private string SelectModel(string? id)
    {
        int index = ModelChoices.ToList().FindIndex(model => model.Id == (id ?? ModelChoice.BuiltInId));
        ModelIndex = Math.Max(0, index);
        if (index >= 0) return "";
        var added = AddedModels.FirstOrDefault(model => model.Id == id);
        return added is null ? " The model that made it is no longer added, so Stable Diffusion 1.5 is selected."
            : $" “{added.Name}” needs a new test in Settings, so Stable Diffusion 1.5 is selected.";
    }
    private string _restoreNote = "";
    private Task RefreshInstallationAsync() => RunAsync("Checking model state…", UpdateInstallation);
    private Task InstallAsync() => RunAsync("Preparing Stable Diffusion 1.5…", async token =>
    {
        ModelReady = false;
        try
        {
            var state = await _service.InstallAsync(DataRoot, ObserveProgress(), token, KeepDownloadCaches);
            InstallationText = state.Message;
            ModelReady = state.Status == "ready";
            Notice = state.Message + (state.CleanedBytes > 0 ? $" Removed {StorageSnapshot.Format(state.CleanedBytes)} of setup downloads that are no longer needed." : "");
            Record(state.Message);
            await UpdateSystemProfile(token, refresh: true);  // The tested settings are now the ones in use.
        }
        finally
        {
            // A force-stopped worker leaves an interrupted attempt that the next check recovers.
            if (!ModelReady) InstallationText = "Setup did not finish. Check model status for details; install / verify resumes retained downloads.";
        }
    }, longRunning: true);
    /// <summary>The Advanced options shared by pictures and videos, checked before any work starts.</summary>
    private void CheckRefinement()
    {
        if (!double.IsFinite(Seed) || Seed != Math.Truncate(Seed) || Seed < 0 || Seed > uint.MaxValue ||
            !double.IsFinite(Steps) || Steps != Math.Truncate(Steps) || Steps < 1 || Steps > 50)
            throw new InvalidDataException("Use a whole-number seed (0–4294967295) and 1–50 steps.");
        if (!double.IsFinite(GuidanceScale) || GuidanceScale < 1 || GuidanceScale > 20)
            throw new InvalidDataException("Use a guidance value from 1 to 20.");
    }

    private Task InstallVideoAsync() => RunAsync("Setting up videos…", async token =>
    {
        VideoReady = false;
        try
        {
            var state = await _service.InstallVideoAsync(DataRoot, ObserveProgress(), token);
            ShowVideo(state);
            Notice = state.Message;
            Record(state.Message);
        }
        finally
        {
            if (!VideoReady) VideoText = "Video setup did not finish. Select Set up videos to try again; finished downloads are kept.";
            _ = RefreshDriveAsync();
        }
    }, longRunning: true);

    private Task InstallXlAsync() => RunAsync("Setting up Stable Diffusion XL…", async token =>
    {
        try
        {
            var state = await _service.InstallXlAsync(DataRoot, ObserveProgress(), token);
            Notice = state.Message + (state.IsReady ? " Choose it under Model." : "");
            Record(state.Message);
        }
        finally
        {
            await UpdateModels(token, ModelChoice.XlId);
            _ = RefreshDriveAsync();
        }
    }, longRunning: true);

    private Task RemoveXlAsync() => RunAsync("Removing Stable Diffusion XL…", async token =>
    {
        await _service.RemoveXlAsync(DataRoot, token);
        await UpdateModels(token);
        ShowStorage(await _service.GetStorageAsync(DataRoot, token));
        Notice = "Removed Stable Diffusion XL (about 6.9 GB). Pictures you made with it were kept; setting it up again downloads it again.";
        Record(Notice);
    });

    private Task RemoveVideoAsync() => RunAsync("Removing the video setup…", async token =>
    {
        ShowVideo(await _service.RemoveVideoAsync(DataRoot, token));
        ShowStorage(await _service.GetStorageAsync(DataRoot, token));
        Notice = "Removed the video setup (about 1.7 GB). Videos you made were kept; Set up videos in Create downloads it again.";
        Record(Notice);
    });

    private Task GenerateVideoAsync() => RunAsync("Preparing the video…", async token =>
    {
        CheckRefinement();
        var size = GenerationSettings.Sizes[Math.Clamp(SizeIndex, 0, GenerationSettings.Sizes.Length - 1)];
        int frames = GenerationSettings.VideoLengths[Math.Clamp(VideoLengthIndex, 0, GenerationSettings.VideoLengths.Length - 1)].Frames;
        var clip = await _service.GenerateVideoAsync(DataRoot, new(Prompt, NegativePrompt, (long)Seed, (int)Steps,
            Math.Round(GuidanceScale, 2), size.Width, size.Height, "ddim", Frames: frames), ObserveProgress(), token);
        LatestVideo = clip;
        VideoPreviewPath = clip.VideoPath ?? "";
        VideoDescription = clip.Description;
        await ShowNewImageAsync(clip, token);
        Notice = clip.SavedNotice;
        Record("Saved " + clip.VideoPath);
        _ = RefreshDriveAsync();
    }, longRunning: true);

    private Task GenerateAsync() => RunAsync("Preparing image generation…", async token =>
    {
        CheckRefinement();
        var size = ActiveSizes[Math.Clamp(SizeIndex, 0, ActiveSizes.Length - 1)];
        var sampler = GenerationSettings.Samplers[Math.Clamp(SamplerIndex, 0, GenerationSettings.Samplers.Length - 1)];
        string? source = HasSourceImage ? SourceImagePath : null;
        if (source is not null && !File.Exists(source)) throw new InvalidDataException("The starting picture is no longer there. Choose it again or remove it.");
        bool painted = source is not null && IsPainting;
        if (painted && Mask?.HasPaint != true) throw new InvalidDataException("Paint over the part of the picture to change, or select Change the whole picture.");
        var change = painted ? GenerationSettings.PaintChangeLevels[Math.Clamp(PaintChangeIndex, 0, GenerationSettings.PaintChangeLevels.Length - 1)]
            : GenerationSettings.ChangeLevels[Math.Clamp(ChangeIndex, 0, GenerationSettings.ChangeLevels.Length - 1)];
        // The worker copies the painted area into the job; this temporary file is only the hand-over.
        string? mask = painted ? WriteTemporaryMask(Mask!) : null;
        GeneratedImage image;
        try
        {
            image = await _service.GenerateAsync(DataRoot, new(Prompt, NegativePrompt, (long)Seed, (int)Steps,
                Math.Round(GuidanceScale, 2), size.Width, size.Height, sampler.Id, source, change.Strength, MaskImage: mask, Model: SelectedModelId), ObserveProgress(), token);
        }
        finally
        {
            if (mask is not null) try { File.Delete(mask); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        PreviewPath = image.ImagePath;
        LatestImage = image;
        ImageDescription = image.Description;
        await ShowNewImageAsync(image, token);
        Notice = image.SavedNotice;
        Record("Saved " + image.ImagePath);
        _ = RefreshDriveAsync();
    }, longRunning: true);
    private Task RefreshLibraryAsync() => LoadLibraryPageAsync(0, new(LibrarySearch,
        LibraryKindIndex switch { 1 => "generate", 2 => "upscale", 3 => "video", _ => "all" }, Limit: LibraryPageSize));

    private Task LoadLibraryPageAsync(int offset, LibraryQuery? query = null) => RunAsync("Loading saved images…",
        token => LoadLibraryAsync((query ?? _libraryQuery) with { Offset = offset }, token));

    private async Task LoadLibraryAsync(LibraryQuery requested, CancellationToken token)
    {
        var library = await _service.GetLibraryAsync(DataRoot, token, requested);
        _libraryQuery = requested;
        _libraryOffset = library.Offset;
        LibraryHasMore = library.HasMore;
        PreviousLibraryPageCommand.NotifyCanExecuteChanged();
        Images.Clear();
        foreach (var image in library.Images) Images.Add(image);
        Problems.Clear();
        foreach (var job in library.Problems) Problems.Add(job);
        HasProblems = Problems.Count > 0;
        LibraryText = library.Total == 0 ? "No saved images match. Try another search or image type."
            : $"{library.Offset + 1:N0}–{library.Offset + library.Images.Count:N0} of {library.Total:N0} matching images · newest first";
        Notice = LibraryText;
        if (library.Problems.Count > 0) Notice += $" {library.Problems.Count} recent generation(s) did not finish; see below the images.";
        if (library.Recovered > 0)
        {
            Notice += $" {library.Recovered} unfinished job(s) from an earlier session were marked interrupted.";
            Record($"Recorded {library.Recovered} interrupted job(s)");
        }
    }

    private async Task ShowNewImageAsync(GeneratedImage image, CancellationToken token)
    {
        // Navigation stays available during inference. If Library is open when it finishes,
        // refresh its applied filters instead of inserting an unrelated image into a filtered page.
        if (IsLibrary)
        {
            try { await LoadLibraryAsync(_libraryQuery with { Offset = 0 }, token); }
            catch (Exception ex) { LibraryText = $"Image saved. Refresh Library to load it: {ex.Message}"; }
        }
        else
        {
            Images.Insert(0, image);
            if (Images.Count > LibraryPageSize) Images.RemoveAt(Images.Count - 1);
        }
    }
    private static string WriteTemporaryMask(MaskCanvas mask)
    {
        string folder = Housekeeping.PaintedFolder; // A crash leaves the file; the next start sweeps it.
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, mask.EncodePng());
        return path;
    }

    /// <summary>Starts painting over the starting picture, given its displayed (EXIF-oriented) pixel size.</summary>
    public bool StartPainting(int pictureWidth, int pictureHeight)
    {
        if (!HasSourceImage || pictureWidth < 1 || pictureHeight < 1) { Notice = "Choose a starting picture first."; return false; }
        Mask = MaskCanvas.ForPicture(pictureWidth, pictureHeight);
        MaskVersion++;
        IsPainting = true;
        Notice = "Paint over the part to change, describe what should be there, and select Generate image. Everything else stays as it is.";
        return true;
    }

    /// <summary>Paints or erases a stroke between two points given as fractions (0–1) of the picture.</summary>
    public void PaintStroke(double x0, double y0, double x1, double y1)
    {
        if (!IsPainting || Mask is null) return;
        Mask.Stroke(x0, y0, x1, y1, MaskCanvas.BrushSizes[Math.Clamp(BrushIndex, 0, MaskCanvas.BrushSizes.Length - 1)].Radius, IsErasing);
        MaskVersion++;
    }

    private void StopPainting()
    {
        Mask = null;
        IsPainting = false;
        IsErasing = false;
        MaskVersion++;
    }

    partial void OnSourceImagePathChanged(string value)
    {
        if (IsPainting) StopPainting(); // A painted area belongs to the picture it was painted on.
    }

    partial void OnDataRootChanged(string value)
    {
        ClearHubPlan();
        ModelReady = false; Images.Clear(); Problems.Clear(); HasProblems = false; PreviewPath = "";
        UpscalePreviewPath = ""; SourceImagePath = ""; UpscaleSourcePath = "";
        LatestImage = null; LatestUpscale = null; LatestVideo = null; VideoPreviewPath = "";
        ShowVideo(new("unknown", "Videos are not set up yet."));
        _libraryOffset = 0; LibraryHasMore = false; LibrarySearch = ""; LibraryKindIndex = 0; _libraryQuery = new();
        PreviousLibraryPageCommand.NotifyCanExecuteChanged();
        LibraryText = "Open Library to browse images in the new data folder.";
        OnPropertyChanged(nameof(McpConfiguration));
        StorageItems.Clear(); StorageText = "Select Measure storage to see what the new data folder uses.";
        DriveWarning = DriveSpeedNote = "";
        if (!_loading) _ = RefreshDriveAsync();
        ShowModels([], ModelChoice.BuiltInId);
        ClearImportCandidate();
    }

    private void ShowStorage(StorageSnapshot storage)
    {
        StorageItems.Clear();
        foreach (var item in storage.Items) StorageItems.Add(item);
        StorageText = $"{StorageSnapshot.Format(storage.FreeBytes)} free of {StorageSnapshot.Format(storage.TotalBytes)} on this drive. " +
            $"The Stable Diffusion 1.5 installation and its caches use {StorageSnapshot.Format(storage.InstallationBytes)}." +
            (AddedModels.Count == 0 ? "" : $" You added {AddedModels.Count} {(AddedModels.Count == 1 ? "model" : "models")} of your own.");
    }
    private Task RefreshStorageAsync() => RunAsync("Measuring the data folder…", async token =>
    {
        await UpdateModels(token);
        ShowStorage(await _service.GetStorageAsync(DataRoot, token));
    });
    private Task ClearStorageAsync(string? area) => RunAsync(area == "installation" ? "Removing Stable Diffusion 1.5…" : "Clearing storage…", async token =>
    {
        if (area == "installation") ModelReady = false;
        var storage = await _service.ClearStorageAsync(DataRoot, area!, ObserveProgress(), token);
        if (area == "installation") { await UpdateModels(token); await UpdateVideo(token); }
        ShowStorage(storage);
        Notice = area == "installation"
            ? "Stable Diffusion 1.5 was removed. Your images, their records, and models you added were kept; Install / verify sets it up again."
            : "Storage cleared.";
        Record(Notice);
    }, longRunning: true);

    private void RetryJob(JobSummary? job)
    {
        if (job?.Settings is not { } settings || !job.CanRetry) return;
        if (ApplySettings(settings)) Notice = $"Loaded the settings of a {job.Status} job. Select {(settings.IsUpscale ? "Make it bigger" : settings.IsVideo ? "Make video" : "Generate image")} to try again.{_restoreNote}";
    }

    private void UseImageSettings(GeneratedImage? image)
    {
        if (image?.Settings is not { } settings || !image.CanReuseSettings) return;
        if (ApplySettings(settings)) Notice = $"Loaded this {(settings.IsVideo ? "video" : "image")}'s settings. You can repeat it or change them for a variation." + _restoreNote;
    }

    private Task ExportImageAsync(ImageExportRequest? request) => RunAsync("Saving an image copy…", async token =>
    {
        if (request is null) return;
        string destination = await _service.ExportImageAsync(DataRoot, request, ObserveProgress(), token);
        Notice = "Saved a copy to " + destination;
        Record(Notice);
    });

    private void OpenImage(string? path)
    {
        try
        {
            if (path is null || !Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            { Notice = "That PNG image is no longer available. Refresh Library."; return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private void OpenVideo(string? path)
    {
        try
        {
            if (path is null || !Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            { Notice = "That video is no longer available. Refresh Library."; return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private bool ApplySettings(GenerationSettings settings)
    {
        _restoreNote = "";
        if (settings.IsUpscale)
        {
            if (settings.SourceImage is not { } picture || !File.Exists(picture))
            {
                Notice = "The original picture for this upscale is no longer available. Choose a picture in Tools.";
                return false;
            }
            UpscaleSourcePath = picture;
            UpscaleIndex = settings.Scale == 2 ? 0 : 1;
            SelectedPage = "tools";
            return true;
        }
        CreateKindIndex = settings.IsVideo ? 1 : 0;
        if (settings.IsVideo) VideoLengthIndex = Math.Max(0, Array.FindIndex(GenerationSettings.VideoLengths, length => length.Frames == settings.Frames));
        Prompt = settings.Prompt;
        NegativePrompt = settings.NegativePrompt ?? "";
        Seed = settings.Seed;
        Steps = Math.Clamp(settings.Steps, 1, 50);
        GuidanceScale = Math.Clamp(settings.GuidanceScale, 1, 20);
        SamplerIndex = Math.Max(0, Array.FindIndex(GenerationSettings.Samplers, sampler => sampler.Id == settings.Sampler));
        _restoreNote = SelectModel(settings.Model);  // Before the shape: Stable Diffusion XL has its own sizes.
        SizeIndex = Math.Max(0, Array.FindIndex(ActiveSizes, size => size.Width == settings.Width && size.Height == settings.Height));
        if (IsPainting) StopPainting();
        SourceImagePath = settings.SourceImage is { } source && File.Exists(source) ? source : "";
        if (HasSourceImage && settings.MaskImage is { } painted && File.Exists(painted))
        {
            try
            {
                Mask = MaskCanvas.DecodePng(File.ReadAllBytes(painted));
                MaskVersion++;
                IsPainting = true;
                PaintChangeIndex = Array.IndexOf(GenerationSettings.PaintChangeLevels,
                    GenerationSettings.PaintChangeLevels.MinBy(level => Math.Abs(level.Strength - settings.Strength)));
            }
            catch (InvalidDataException) { Notice = "The painted area of that image could not be read; paint it again."; }
        }
        else ChangeIndex = NearestChange(settings.Strength);
        SelectedPage = "create";
        return true;
    }

    /// <summary>Chooses the picture to enlarge in Tools: one of the user's files, a Library image, or the latest result.</summary>
    public bool UseForUpscale(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !GenerationSettings.PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            Notice = "Choose a PNG, JPEG, WebP, or BMP picture.";
            return false;
        }
        UpscaleSourcePath = path;
        SelectedPage = "tools";
        Notice = "Choose how much larger, then select Make it bigger.";
        return true;
    }

    private Task UpscaleAsync() => RunAsync("Preparing the upscaler…", async token =>
    {
        if (!File.Exists(UpscaleSourcePath)) throw new InvalidDataException("That picture is no longer there. Choose it again.");
        int scale = GenerationSettings.UpscaleChoices[Math.Clamp(UpscaleIndex, 0, GenerationSettings.UpscaleChoices.Length - 1)].Scale;
        var image = await _service.UpscaleAsync(DataRoot, UpscaleSourcePath, scale, ObserveProgress(), token);
        UpscalePreviewPath = image.ImagePath;
        LatestUpscale = image;
        UpscaleDescription = image.Description;
        await ShowNewImageAsync(image, token);
        Notice = $"Saved a {image.Width} × {image.Height} picture to Library.";
        Record("Upscaled " + Path.GetFileName(UpscaleSourcePath));
        _ = RefreshDriveAsync();
    }, longRunning: true);

    private static int NearestChange(double strength) => Array.IndexOf(GenerationSettings.ChangeLevels,
        GenerationSettings.ChangeLevels.MinBy(level => Math.Abs(level.Strength - strength)));

    /// <summary>Starts the next image from a picture: one of the user's files, a Library image, or the current preview.</summary>
    public bool UseAsStartingPicture(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !GenerationSettings.PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            Notice = "Choose a PNG, JPEG, WebP, or BMP picture.";
            return false;
        }
        SourceImagePath = path;
        SelectedPage = "create";
        Notice = "Starting from your picture. Describe what it should become, choose how much to change it, and select Generate image.";
        return true;
    }

    private void OpenJobLog(JobSummary? job)
    {
        try
        {
            if (job?.LogPath is not { } path || !Path.IsPathFullyQualified(path) || !File.Exists(path)) { Notice = "That job's log is no longer available."; return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = ex.Message; }
    }
    partial void OnSelectedPageChanged(string value)
    {
        if ((value == "create" || value == "tools") && !IsBusy) RefreshInstallationCommand.Execute(null);
        if (value == "library" && !IsBusy) RefreshLibraryCommand.Execute(null);
        if (value == "settings" && !IsBusy) RefreshStorageCommand.Execute(null);
        if (value is "create" or "tools" or "settings") _ = RefreshDriveAsync();
    }

    private Task SearchAsync() => RunAsync("Searching Hugging Face…", async token =>
    {
        var models = await _service.SearchAsync(SearchQuery, TaskFilter ?? "", token);
        Models.Clear();
        SelectedModel = null;
        foreach (var model in models) Models.Add(model);
        Notice = models.Count == 0 ? "No models matched. Try another search or task filter." : $"Found {models.Count} models. Check a model to read its files and this computer's limits. Downloads currently support single-file Stable Diffusion 1.5 checkpoints.";
        Record($"Loaded {models.Count} model results");
    });

    private Task LoadDetailsAsync()
    {
        string? id = SelectedModel?.Id;
        return id is null ? Task.CompletedTask : LoadHubPlanAsync(id, "", "");
    }

    private Task CheckHubFileAsync() => _hubPlan is { } plan && SelectedHubFile is { } file
        ? LoadHubPlanAsync(plan.Details.Id, plan.Details.Revision, file.Path) : Task.CompletedTask;

    private Task LoadHubPlanAsync(string id, string revision, string file)
    {
        string folder = DataRoot;
        // An earlier successful check must never authorize a failed refresh.
        _hubPlan = null; HubLicenseReviewed = false;
        return RunAsync("Checking model files and this computer…", async token =>
        {
            var plan = await _service.PlanHubModelAsync(folder, id, revision, file, token);
            if (SelectedModel?.Id != id || DataRoot != folder) return;
            var model = plan.Details;
            DetailsText = $"{model.Id}\n\nTask: {model.Task}\nLibrary: {model.Library}\nAccess: {model.Access}\nLicense: {model.License}\nRevision: {model.Revision}\n\nRepository files (up to 200):\n{string.Join("\n", model.Files)}";
            HubFiles.Clear(); foreach (var candidate in plan.Files) HubFiles.Add(candidate);
            SelectedHubFile = HubFiles.FirstOrDefault(candidate => candidate == plan.SelectedFile);
            _hubPlan = plan; _hubRoot = folder;
            HubPlanText = plan.Summary;
            CheckHubFileCommand.NotifyCanExecuteChanged(); DownloadModelCommand.NotifyCanExecuteChanged();
            Record($"Inspected {model.Id}");
        });
    }

    private Task DownloadHubModelAsync() => RunAsync("Downloading and testing your model…", async token =>
    {
        if (!CanDownloadHubModel || _hubPlan is not { } plan) return;
        string folder = DataRoot;
        var added = await _service.DownloadModelAsync(folder, plan, ObserveProgress(), token);
        ClearHubPlan();
        await UpdateInstallation(token);
        await UpdateModels(token, added.IsReady ? added.Id : null);
        if (added.IsReady) SelectedPage = "create";
        Notice = added.IsReady ? $"“{added.Name}” passed its GPU test and is selected in Create. Its pinned source, model card, and license information are kept with the model."
            : $"“{added.Name}” is not ready: {added.Message}";
        Record($"Downloaded and tested {added.Name}");
    }, longRunning: true);

    private Task DiagnoseAsync() => RunAsync("Checking components…", async token =>
    {
        DiagnosticsText = await _service.GetHealthAsync(token);
        WorkerStatus = "Python worker connected";
        await UpdateSystemProfile(token, refresh: true);
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
            var value = new AppSettings(1, Path.GetFullPath(DataRootDraft), SearchQuery, CheckForUpdates, KeepDownloadCaches);
            _settings.Save(value);
            DataRoot = DataRootDraft = value.DataRoot;
            Notice = "Settings saved. Models, generation, and Library now use this data folder.";
            Record("Storage settings saved");
        }
        catch (Exception ex) { Notice = $"Settings were not saved; still using {DataRoot}. {ex.Message}"; }
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        try { _settings.Save(new AppSettings(1, DataRoot, SearchQuery, value, KeepDownloadCaches)); }
        catch (Exception ex) { Notice = $"Could not save the update preference: {ex.Message}"; }
    }

    partial void OnKeepDownloadCachesChanged(bool value)
    {
        if (_loading) return;
        try { _settings.Save(new AppSettings(1, DataRoot, SearchQuery, CheckForUpdates, value)); }
        catch (Exception ex) { Notice = $"Could not save the download cache preference: {ex.Message}"; }
    }

    /// <summary>Free space and drive type of the saved data folder, for the warnings in Create and Settings. Never throws.</summary>
    public async Task RefreshDriveAsync()
    {
        string root = DataRoot;
        try
        {
            var drive = await _service.GetDriveAsync(root, CancellationToken.None);
            if (root != DataRoot) return;
            DriveWarning = drive.Warning;
            DriveSpeedNote = drive.SpeedNote;
        }
        catch (Exception) { if (root == DataRoot) DriveWarning = DriveSpeedNote = ""; }
    }

    /// <summary>
    /// Once per start: removes this app's own leftovers in %TEMP% (painted-area hand-overs from a crash,
    /// installers of an update that has finished), then tidies the saved data folder under its lock.
    /// Download caches go only when setup is verified and Settings does not keep them. Quiet unless it frees space.
    /// </summary>
    public async Task StartupMaintenanceAsync()
    {
        await Task.Run(() =>
        {
            Housekeeping.SweepPaintedHandOvers(Housekeeping.PaintedFolder, Housekeeping.PaintedMaxAge, DateTime.UtcNow);
            Housekeeping.RemoveFinishedInstallers(Housekeeping.UpdateFolder, AppVersion, DateTime.UtcNow);
        });
        await RunAsync("Tidying the data folder…", async token =>
        {
            try
            {
                bool verified = (await _service.GetInstallationAsync(DataRoot, token)).Status == "ready";
                var report = await _service.TidyAsync(DataRoot, verified, verified && !KeepDownloadCaches, token);
                if (report.RemovedFiles == 0) return;
                Record($"Removed {report.RemovedFiles:N0} files no longer needed ({StorageSnapshot.Format(report.RemovedBytes)})");
                if (report.DownloadCaches && report.RemovedBytes >= 1L << 30)
                    Notice = $"Removed {StorageSnapshot.Format(report.RemovedBytes)} of setup downloads that are no longer needed. To keep them for repairs, see Settings.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record("Tidying skipped: " + ex.Message); // Another operation owns the folder, or it is unreadable; next start tries again.
            }
        }, longRunning: true);
        await RefreshDriveAsync();
    }

    private void ShowUpdate(UpdateInfo? update)
    {
        _update = update;
        HasUpdate = update is not null;
        UpdateNotes = update?.Notes ?? "";
        UpdateText = update is null ? $"Media Workbench {AppVersion} is up to date."
            : $"Media Workbench {update.Version} is available (you have {AppVersion}).";
    }

    /// <summary>The startup check: silent when offline or turned off, and never blocks other work.</summary>
    public async Task CheckForUpdatesQuietlyAsync()
    {
        if (!CheckForUpdates) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var update = await _updates.CheckAsync(deadline.Token);
            ShowUpdate(update);
            if (update is not null) Notice = $"Media Workbench {update.Version} is available. See Home to read what changed and update.";
        }
        catch (Exception) { } // Offline or GitHub unavailable: working offline is normal.
    }

    private Task CheckForUpdatesAsync() => RunAsync("Checking for updates…", async token =>
    {
        try { ShowUpdate(await _updates.CheckAsync(token)); }
        catch (HttpRequestException) { UpdateText = "Could not reach GitHub to check for updates. Everything else keeps working offline."; }
        Notice = UpdateText;
    });

    private Task InstallUpdateAsync() => RunAsync("Downloading the update…", async token =>
    {
        if (_update is null) return;
        string installer = await _updates.DownloadAsync(_update, ObserveProgress(), token);
        Record($"Verified the {_update.Version} installer; starting it");
        InstallerReady?.Invoke(installer);
    }, longRunning: true);

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
            Process.Start(new ProcessStartInfo(ModelSourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = ex.Message; }
    }
    public string ModelSourceUrl => SelectedModel is not { } model ? "" : "https://huggingface.co/"
        + string.Join('/', model.Id.Split('/').Select(Uri.EscapeDataString))
        + (_hubPlan is { } plan && plan.Details.Id == model.Id ? "/tree/" + Uri.EscapeDataString(plan.Details.Revision) : "");
    private void Record(string message)
    {
        Activity.Insert(0, $"{DateTime.Now:t}   {message}");
        while (Activity.Count > 30) Activity.RemoveAt(Activity.Count - 1);
    }
    public void CancelActiveWork() => _operation?.Cancel();
}
