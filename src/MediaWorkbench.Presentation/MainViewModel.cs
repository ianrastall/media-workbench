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
        CheckMoveCommand = new AsyncRelayCommand(CheckMoveAsync, () => !IsBusy && IsDraftDifferent);
        MoveDataCommand = new AsyncRelayCommand(MoveDataAsync, () => !IsBusy && _movePlan is { CanMove: true } && _moveTarget == DraftFolder() && _moveSource == DataRoot);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        OpenModelCommand = new RelayCommand(OpenModel, () => SelectedModel is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && !NeedsDriveChoice);
        UseDriveCommand = new RelayCommand<DriveChoice?>(UseDrive, drive => drive is not null && !IsBusy);
        KeepDataDriveCommand = new RelayCommand(KeepDataDrive, () => !IsBusy);
        RemoveOldCopyCommand = new AsyncRelayCommand(RemoveOldCopyAsync, () => !IsBusy && HasOldCopy);
        ToggleDriveCommand = new RelayCommand<DriveChoice?>(ToggleDrive, drive => drive is not null && !IsBusy);
        RefreshInstallationCommand = new AsyncRelayCommand(RefreshInstallationAsync, () => !IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy && ModelReady);
        AddToQueueCommand = new AsyncRelayCommand(AddToQueueAsync, () => !IsBusy && ModelReady);
        RunQueueCommand = new AsyncRelayCommand(RunQueueAsync, () => !IsBusy && ModelReady && HasQueue);
        RemoveQueuedCommand = new AsyncRelayCommand<QueuedPicture?>(RemoveQueuedAsync, item => !IsBusy && item is not null);
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
            _pendingMove = saved.PendingMove;
            _movedFrom = saved.MovedFrom;
            _excludedDrives = [.. saved.ExcludedDrives ?? []];
            ShowOldCopy();
            // Earlier versions did not record the choice; a folder other than the default was chosen on purpose.
            _dataRootChosen = saved.DataRootChosen || !string.Equals(saved.DataRoot, DefaultDataRoot, StringComparison.OrdinalIgnoreCase);
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
    /// <summary>How many pictures one Generate makes, with consecutive seeds; after the first, each reuses the loaded model.</summary>
    public static readonly int[] PictureCounts = [1, 2, 4];
    public IAsyncRelayCommand AddToQueueCommand { get; }
    public IAsyncRelayCommand RunQueueCommand { get; }
    public IAsyncRelayCommand<QueuedPicture?> RemoveQueuedCommand { get; }
    /// <summary>Pictures waiting to be made, oldest first; they run only when the user selects Run queue.</summary>
    public ObservableCollection<QueuedPicture> Queue { get; } = [];
    public bool HasQueue => Queue.Count > 0;
    public string QueueText => Queue.Count switch { 0 => "No pictures are waiting", 1 => "1 picture waiting", int count => $"{count} pictures waiting" };
    public string[] PictureCountLabels { get; } = ["1 picture", "2 versions", "4 versions"];
    [ObservableProperty] public partial int PictureCountIndex { get; set; }
    /// <summary>Index into <see cref="GenerationSettings.Samplers"/>.</summary>
    [ObservableProperty] public partial int SamplerIndex { get; set; }
    /// <summary>The shapes the chosen model and mode offer: Stable Diffusion XL pictures are larger.</summary>
    public ObservableCollection<string> SizeLabels { get; } = [.. GenerationSettings.Sizes.Select(size => size.Label)];
    public (int Width, int Height, string Label)[] ActiveSizes =>
        IsPictureMode && ModelChoices.ElementAtOrDefault(Math.Max(0, ModelIndex))?.UsesXlSizes == true ? GenerationSettings.XlSizes : GenerationSettings.Sizes;
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
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(DetailsCommand), nameof(DiagnosticsCommand), nameof(PlanCommand), nameof(SaveSettingsCommand), nameof(CheckMoveCommand), nameof(MoveDataCommand), nameof(RemoveOldCopyCommand), nameof(AddToQueueCommand), nameof(RunQueueCommand), nameof(RemoveQueuedCommand), nameof(CancelCommand), nameof(InstallCommand), nameof(RefreshInstallationCommand), nameof(GenerateCommand), nameof(RefreshLibraryCommand), nameof(RetryJobCommand), nameof(RefreshStorageCommand), nameof(ClearStorageCommand), nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand), nameof(UpscaleCommand), nameof(PreviousLibraryPageCommand), nameof(NextLibraryPageCommand), nameof(UseImageSettingsCommand), nameof(ExportImageCommand), nameof(ImportModelCommand), nameof(RetestModelCommand), nameof(RemoveModelCommand), nameof(CheckHubFileCommand), nameof(DownloadModelCommand), nameof(InstallVideoCommand), nameof(GenerateVideoCommand), nameof(RemoveVideoCommand), nameof(InstallXlCommand), nameof(RemoveXlCommand))]
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
    public IAsyncRelayCommand CheckMoveCommand { get; }
    public IRelayCommand<DriveChoice?> UseDriveCommand { get; }
    public IRelayCommand KeepDataDriveCommand { get; }
    public IAsyncRelayCommand RemoveOldCopyCommand { get; }
    public IRelayCommand<DriveChoice?> ToggleDriveCommand { get; }
    private List<string> _excludedDrives = [];
    /// <summary>The data folder is on a drive the user chose never to use.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExcludedDriveWarning))] public partial string ExcludedDriveWarning { get; private set; } = "";
    public bool HasExcludedDriveWarning => ExcludedDriveWarning.Length > 0;

    /// <summary>The excluded drive holding <paramref name="folder"/>, or null. Drives are identified by their root, such as D:\.</summary>
    private string? ExcludedRoot(string folder)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        return root is not null && _excludedDrives.Contains(root, StringComparer.OrdinalIgnoreCase) ? root : null;
    }

    private static string ExcludedMessage(string root) =>
        $"You chose never to use {root} for Media Workbench. Choose a folder on another drive, or select Allow this drive under Drives on this computer.";

    private void ShowExcludedDrive() =>
        ExcludedDriveWarning = ExcludedRoot(DataRoot) is { } root
            ? $"The data folder is on {root}, which you chose never to use. Select Move my data there… with a folder on another drive to move it."
            : "";

    /// <summary>Marks a drive as never to be used for Media Workbench, or allows it again.</summary>
    private void ToggleDrive(DriveChoice? drive)
    {
        if (drive is null) return;
        var previous = _excludedDrives.ToList();
        if (drive.Excluded) _excludedDrives.RemoveAll(root => string.Equals(root, drive.Root, StringComparison.OrdinalIgnoreCase));
        else _excludedDrives.Add(drive.Root);
        try { _settings.Save(CurrentSettings()); }
        catch (Exception ex) { _excludedDrives = previous; Notice = "Could not save the drive choice: " + ex.Message; return; }
        int index = Drives.IndexOf(drive);
        if (index >= 0) Drives[index] = drive with { Excluded = !drive.Excluded };
        ShowExcludedDrive();
        Notice = drive.Excluded ? $"{drive.Root} can be used again." : $"Media Workbench will not put its data on {drive.Root}.";
    }
    private string? _movedFrom;
    /// <summary>After a move, the old data folder is kept until the user removes it here.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOldCopy))] public partial string OldCopyText { get; private set; } = "";
    public bool HasOldCopy => OldCopyText.Length > 0;
    /// <summary>Drives the data folder could go on, for Settings.</summary>
    public ObservableCollection<DriveChoice> Drives { get; } = [];
    public bool HasDrives => Drives.Count > 0;
    private bool _dataRootChosen;
    private string DefaultDataRoot => AppSettings.Default(_settings.Folder).DataRoot;
    /// <summary>
    /// Before setup's first large download: the data folder is still the default on Windows' drive and the user never chose
    /// it. Setup waits until they keep it or choose another drive; nothing is decided for them.
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsDriveChoice))] public partial string DriveChoiceText { get; private set; } = "";
    public bool NeedsDriveChoice => DriveChoiceText.Length > 0;
    public IAsyncRelayCommand MoveDataCommand { get; }
    private DataMovePlan? _movePlan;
    private string? _moveTarget, _moveSource, _pendingMove;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMovePlan))] public partial string MovePlanText { get; private set; } = "";
    public bool HasMovePlan => MovePlanText.Length > 0;
    /// <summary>A different folder is typed in Settings, so the current data can be moved there.</summary>
    public bool IsDraftDifferent => DraftFolder() is { } draft && !string.Equals(draft, DataRoot, StringComparison.OrdinalIgnoreCase);
    private string? DraftFolder() => Path.IsPathFullyQualified(DataRootDraft) ? Path.GetFullPath(DataRootDraft) : null;
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
    /// <summary>Every place the app keeps files, with the drive each is really on.</summary>
    public ObservableCollection<StorageLocation> Locations { get; } = [];
    [ObservableProperty] public partial string LocationsText { get; set; } = "";
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

    private IProgress<OperationProgress> ObserveProgress(string prefix) => new Progress<OperationProgress>(value =>
        ((IProgress<OperationProgress>)ObserveProgress()).Report(value with { Stage = prefix + " · " + value.Stage }));

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
        try { ShowQueue(await _service.GetQueueAsync(DataRoot, token)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Record("The queue could not be read: " + ex.Message); }
        try { await UpdateDriveChoice(token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { DriveChoiceText = ""; } // Drives unreadable: never block setup on the question.
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
            var found = await _service.InspectModelAsync(DataRoot, path, token);
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

    /// <summary>The picture Create describes now (without its seed), and whether a painted area goes with it.</summary>
    private (GenerationSettings Settings, bool Painted) CurrentPicture()
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
        return (new(Prompt, NegativePrompt, (long)Seed, (int)Steps, Math.Round(GuidanceScale, 2), size.Width, size.Height, sampler.Id, source,
            change.Strength, Model: SelectedModelId), painted);
    }

    private static long NextSeed(double seed, int index) => ((long)seed + index) % (1L + uint.MaxValue);

    private Task GenerateAsync() => RunAsync("Preparing image generation…", async token =>
    {
        var (picture, painted) = CurrentPicture();
        int count = PictureCounts[Math.Clamp(PictureCountIndex, 0, PictureCounts.Length - 1)];
        // The worker copies the painted area into each job; this temporary file is only the hand-over.
        string? mask = painted ? await WorkFolders.WritePaintedAsync(DataRoot, Mask!.EncodePng(), token) : null; // A crash leaves it; the next start sweeps it.
        int made = 0;
        List<string> problems = [];
        try
        {
            for (int index = 0; index < count; index++)
            {
                // Consecutive seeds: each version is reproducible on its own with Use settings.
                long seed = NextSeed(Seed, index);
                var progress = count == 1 ? ObserveProgress() : ObserveProgress($"Picture {index + 1} of {count}");
                GeneratedImage image;
                try
                {
                    image = await _service.GenerateAsync(DataRoot, picture with { Seed = seed, MaskImage = mask }, progress, token);
                }
                catch (InvalidOperationException ex) when (count > 1)
                {
                    // One rejected version (for example by the safety checker) does not stop the others.
                    problems.Add($"Seed {seed}: {ex.Message}");
                    Record(problems[^1]);
                    continue;
                }
                made++;
                PreviewPath = image.ImagePath;
                LatestImage = image;
                ImageDescription = image.Description;
                await ShowNewImageAsync(image, token);
                Notice = count == 1 ? image.SavedNotice : $"Picture {index + 1} of {count} saved. " + image.SavedNotice;
                Record("Saved " + image.ImagePath);
            }
        }
        finally
        {
            if (mask is not null) try { File.Delete(mask); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _ = RefreshDriveAsync();
        }
        if (count > 1 && problems.Count > 0)
        {
            if (made == 0) throw new InvalidOperationException(string.Join(" ", problems));
            Notice = $"Made {made} of {count} versions; the others were not saved. " + string.Join(" ", problems);
        }
        else if (count > 1) Notice = $"Made {count} versions with seeds {(long)Seed} to {((long)Seed + count - 1) % (1L + uint.MaxValue)}; see Library for all of them. " + LatestImage!.SavedNotice;
    }, longRunning: true);
    /// <summary>Saves the picture Create describes (each version, with How many) to make later.</summary>
    private Task AddToQueueAsync() => RunAsync("Adding to the queue…", async token =>
    {
        var (picture, painted) = CurrentPicture();
        int count = PictureCounts[Math.Clamp(PictureCountIndex, 0, PictureCounts.Length - 1)];
        string? mask = painted ? await WorkFolders.WritePaintedAsync(DataRoot, Mask!.EncodePng(), token) : null;
        try
        {
            for (int index = 0; index < count; index++)
                await _service.QueuePictureAsync(DataRoot, picture with { Seed = NextSeed(Seed, index), MaskImage = mask }, token);
        }
        finally
        {
            if (mask is not null) try { File.Delete(mask); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        ShowQueue(await _service.GetQueueAsync(DataRoot, token));
        Notice = $"Added {(count == 1 ? "the picture" : $"{count} versions")} to the queue ({QueueText}). Select Run queue to make them; they wait until you do, even after closing the app.";
        Record(Notice);
    });

    /// <summary>Makes the waiting pictures oldest first. Cancel stops the current one, which stays waiting with the rest.</summary>
    private Task RunQueueAsync() => RunAsync("Making the waiting pictures…", async token =>
    {
        var items = await _service.GetQueueAsync(DataRoot, token);
        ShowQueue(items);
        int made = 0;
        List<string> problems = [];
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            try
            {
                var image = await _service.GenerateAsync(DataRoot, item.Settings, ObserveProgress($"Queue {index + 1} of {items.Count}"), token);
                made++;
                PreviewPath = image.ImagePath;
                LatestImage = image;
                ImageDescription = image.Description;
                await ShowNewImageAsync(image, token);
                Notice = $"Queue {index + 1} of {items.Count} saved. " + image.SavedNotice;
            }
            catch (InvalidOperationException ex)
            {
                // Its failed job is listed in Library with Retry; the rest of the queue still runs.
                problems.Add($"“{Shorten(item.Title)}”: {ex.Message}");
                Record(problems[^1]);
            }
            ShowQueue(await _service.RemoveQueuedAsync(DataRoot, item.Id, token));
        }
        _ = RefreshDriveAsync();
        Notice = $"Made {made} of {items.Count} waiting pictures." + (problems.Count == 0 ? " See Library for all of them." : " Not made: " + string.Join(" ", problems));
    }, longRunning: true);

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";

    private Task RemoveQueuedAsync(QueuedPicture? item) => RunAsync("Removing the waiting picture…", async token =>
    {
        if (item is null) return;
        ShowQueue(await _service.RemoveQueuedAsync(DataRoot, item.Id, token));
        Notice = "Removed the waiting picture. " + QueueText + ".";
    });

    private void ShowQueue(IReadOnlyList<QueuedPicture> items)
    {
        Queue.Clear();
        foreach (var item in items) Queue.Add(item);
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueText));
        RunQueueCommand.NotifyCanExecuteChanged();
    }

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
        Locations.Clear(); LocationsText = "";
        ShowQueue([]);
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
        await UpdateLocations(token);
        Drives.Clear();
        foreach (var drive in await _service.GetDrivesAsync(token)) Drives.Add(drive with { Excluded = ExcludedRoot(drive.Root) is not null });
        ShowExcludedDrive();
        OnPropertyChanged(nameof(HasDrives));
    });

    /// <summary>Preferences and the startup log together stay below this (settings.json plus two rotated 1 MiB logs).</summary>
    public const string ProfileFootprint = "about 2 MB at most";

    /// <summary>
    /// Lists where the app keeps files and which drive each is really on (mounted folders included), and says in
    /// one sentence which drive holds the work and what, if anything, stays elsewhere.
    /// </summary>
    private async Task UpdateLocations(CancellationToken token)
    {
        string root = DataRoot;
        async Task<DriveStatus?> Drive(string path)
        {
            try { return await _service.GetDriveAsync(path, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }
        static string Name(DriveStatus? drive) => drive is { Volume.Length: > 0 } ? drive.Volume : "a drive Windows did not name";
        static bool Known(DriveStatus? drive) => drive is { Volume.Length: > 0 };
        static bool Same(DriveStatus? left, DriveStatus? right) =>
            Known(left) && Known(right) && string.Equals(left!.Volume, right!.Volume, StringComparison.OrdinalIgnoreCase);
        // Only drives Windows named are compared; an unnamed one is neither claimed the same nor different.
        static bool Different(DriveStatus? left, DriveStatus? right) => Known(left) && Known(right) && !Same(left, right);
        var data = await Drive(root);
        var preferences = await Drive(_settings.Folder);
        string appFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var app = await Drive(appFolder);
        if (root != DataRoot) return;
        string where = data is null ? "The data folder's drive could not be read; it may be disconnected."
            : $"On {Name(data)}: {data.KindText}, {StorageSnapshot.Format(data.FreeBytes)} free.";
        string Elsewhere(DriveStatus? drive, string note) => Same(drive, data) ? "Same drive as the data folder. " + note : $"On {Name(drive)}. " + note;
        Locations.Clear();
        Locations.Add(new("Pictures, videos, models and setup", root, where));
        Locations.Add(new("Temporary work and update downloads", WorkFolders.Temporary(root),
            "Same drive as the data folder. A job's temporary files are removed when it ends; Media Workbench never falls back to Windows' temporary folder."));
        Locations.Add(new("Download and graphics-card caches", Path.Combine(root, "cache"),
            "Same drive as the data folder. Setup's downloads are removed once it passes its test, unless you keep them below."));
        Locations.Add(new("Preferences and startup log", _settings.Folder, Elsewhere(preferences, $"Small: {ProfileFootprint}.")));
        Locations.Add(new("Media Workbench itself", appFolder, Elsewhere(app, "Updates replace the app here.")));
        var apart = new List<string>();
        if (Different(preferences, data)) apart.Add($"preferences and the startup log ({ProfileFootprint}) are on {Name(preferences)}");
        if (Different(app, data)) apart.Add($"the app itself is on {Name(app)}");
        LocationsText = data is null ? where
            : $"Your pictures, videos, models, caches and temporary work all stay on {Name(data)}." +
              (apart.Count > 0 ? " Only " + string.Join(", and ", apart) + "."
                : Same(preferences, data) && Same(app, data) ? " So does everything else Media Workbench keeps." : "");
    }
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
        var models = await _service.SearchAsync(DataRoot, SearchQuery, TaskFilter ?? "", token);
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
        DiagnosticsText = await _service.GetHealthAsync(DataRoot, token);
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
        bool chosen = _dataRootChosen;
        try
        {
            if (!Path.IsPathFullyQualified(DataRootDraft)) throw new InvalidDataException("Choose an absolute data folder.");
            if (ExcludedRoot(DataRootDraft) is { } excluded) throw new InvalidDataException(ExcludedMessage(excluded));
            _dataRootChosen = true; // Saving a folder is choosing where Media Workbench works.
            _movedFrom = null; // An old copy relates only to the folder it was moved to.
            var value = CurrentSettings(Path.GetFullPath(DataRootDraft));
            _settings.Save(value);
            DataRoot = DataRootDraft = value.DataRoot;
            DriveChoiceText = "";
            ShowOldCopy();
            ShowExcludedDrive();
            Notice = "Settings saved. Models, generation, and Library now use this data folder.";
            Record("Storage settings saved");
        }
        catch (Exception ex) { _dataRootChosen = chosen; Notice = $"Settings were not saved; still using {DataRoot}. {ex.Message}"; }
    }

    private AppSettings CurrentSettings(string? dataRoot = null) =>
        new(1, dataRoot ?? DataRoot, SearchQuery, CheckForUpdates, KeepDownloadCaches, _pendingMove, _dataRootChosen, _movedFrom,
            _excludedDrives.Count == 0 ? null : [.. _excludedDrives]);

    private void ShowOldCopy()
    {
        OldCopyText = _movedFrom is { } old && Directory.Exists(old)
            ? $"Your previous data folder is still at {old}. Once pictures and models work from the new folder, remove the old copy to free its space. " +
              "Only files the new folder holds unchanged are deleted; anything else stays."
            : "";
        RemoveOldCopyCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Deletes the old folder's files that the new data folder holds unchanged, after the user confirmed.</summary>
    private Task RemoveOldCopyAsync() => RunAsync("Removing the old copy…", async token =>
    {
        if (_movedFrom is not { } old) return;
        var removal = await _service.RemoveMovedDataAsync(old, DataRoot, token);
        _movedFrom = null;
        _settings.Save(CurrentSettings());
        ShowOldCopy();
        Notice = $"Removed {removal.RemovedFiles:N0} files ({StorageSnapshot.Format(removal.RemovedBytes)}) from the old folder." +
            (removal.FolderRemoved ? " The old folder is gone."
                : $" {removal.KeptFiles:N0} {(removal.KeptFiles == 1 ? "file was" : "files were")} kept in {old} because the new folder has no identical copy; check them in File Explorer.");
        Record(Notice);
    }, longRunning: true);

    /// <summary>Puts a drive's suggested folder in the data folder box; saving or moving is still the user's choice.</summary>
    private void UseDrive(DriveChoice? drive)
    {
        if (drive is null) return;
        if (drive.Excluded) { Notice = ExcludedMessage(drive.Root); return; }
        DataRootDraft = drive.Folder;
        Notice = $"Select Save settings to start using {drive.Folder}, or Move my data there… to bring what you already have.";
    }

    /// <summary>Keeps the current data folder for setup, after the question in Create.</summary>
    private void KeepDataDrive()
    {
        _dataRootChosen = true;
        if (ExcludedRoot(DataRoot) is { } root) _excludedDrives.RemoveAll(item => string.Equals(item, root, StringComparison.OrdinalIgnoreCase));
        ShowExcludedDrive();
        try { _settings.Save(CurrentSettings()); } catch (Exception ex) { Notice = "Could not save the choice: " + ex.Message; }
        DriveChoiceText = "";
        InstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Asks before setup only when the default folder on Windows' drive was never chosen and another drive has more room.</summary>
    private async Task UpdateDriveChoice(CancellationToken token)
    {
        if (_dataRootChosen || ModelReady || !string.Equals(DataRoot, DefaultDataRoot, StringComparison.OrdinalIgnoreCase)) { DriveChoiceText = ""; return; }
        var drives = await _service.GetDrivesAsync(token);
        var current = drives.FirstOrDefault(drive => DataRoot.StartsWith(drive.Root, StringComparison.OrdinalIgnoreCase));
        var roomier = drives.Where(drive => drive != current && drive.DriveKind != "network" && ExcludedRoot(drive.Root) is null && drive.FreeBytes > (current?.FreeBytes ?? 0))
            .OrderByDescending(drive => drive.FreeBytes).FirstOrDefault();
        DriveChoiceText = current is null || roomier is null ? ""
            : $"Setup will download about 5 GB and needs about 20 GB in the data folder, which is on {current.Root} ({current.Detail}). " +
              $"{roomier.Root} has {StorageSnapshot.Format(roomier.FreeBytes)} free. Choose where Media Workbench works in Settings, or keep {current.Root}.";
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnDataRootDraftChanged(string value) => ClearMovePlan();

    private void ClearMovePlan()
    {
        _movePlan = null; _moveTarget = _moveSource = null; MovePlanText = "";
        OnPropertyChanged(nameof(IsDraftDifferent));
        CheckMoveCommand.NotifyCanExecuteChanged(); MoveDataCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Checks what moving the current data to the typed folder would copy, and whether it can start.</summary>
    private Task CheckMoveAsync() => RunAsync("Checking the new location…", async token =>
    {
        string source = DataRoot, target = DraftFolder() ?? throw new InvalidDataException("Choose an absolute data folder.");
        if (ExcludedRoot(target) is { } excluded) { Notice = ExcludedMessage(excluded); return; }
        var plan = await _service.PlanDataMoveAsync(source, target, token);
        if (source != DataRoot || target != DraftFolder()) return; // The choice changed meanwhile.
        _movePlan = plan; _moveTarget = target; _moveSource = source;
        MovePlanText = plan.Summary;
        MoveDataCommand.NotifyCanExecuteChanged();
    });

    /// <summary>
    /// Copies the data folder to the checked new folder and verifies every file, then switches to it. The current folder is
    /// kept unchanged. A crash part-way leaves only a marked half-made copy, removed at the next start.
    /// </summary>
    private Task MoveDataAsync() => RunAsync("Moving your data…", async token =>
    {
        if (_movePlan is not { CanMove: true } plan || _moveTarget is not { } target || _moveSource != DataRoot) return;
        string source = DataRoot;
        _pendingMove = target;
        _settings.Save(CurrentSettings());
        try { await _service.MoveDataAsync(source, target, ObserveProgress(), token); }
        catch
        {
            _pendingMove = null;
            try { _settings.Save(CurrentSettings()); } catch (Exception) { } // The next start removes the leftover either way.
            throw;
        }
        _pendingMove = null;
        _dataRootChosen = true;
        _movedFrom = source;
        _settings.Save(CurrentSettings(target));
        DataRoot = DataRootDraft = target;
        ClearMovePlan();
        ShowOldCopy();
        ShowExcludedDrive();
        Notice = $"Moved {StorageSnapshot.Format(plan.Bytes)} to {target} and checked every file. Media Workbench now uses the new folder. " +
            $"The old folder ({source}) was not changed: once you have checked the new one, select Remove the old copy in Settings. " +
            "If you use the local MCP host, update its --data-root.";
        Record("Data folder moved to " + target);
        await UpdateInstallation(token);
    }, longRunning: true);

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        try { _settings.Save(CurrentSettings() with { CheckForUpdates = value }); }
        catch (Exception ex) { Notice = $"Could not save the update preference: {ex.Message}"; }
    }

    partial void OnKeepDownloadCachesChanged(bool value)
    {
        if (_loading) return;
        try { _settings.Save(CurrentSettings() with { KeepDownloadCaches = value }); }
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
    /// Once per start: removes this app's own leftovers in the data folder's temp folder and, from earlier versions,
    /// in %TEMP% (painted-area hand-overs from a crash,
    /// installers of an update that has finished), then tidies the saved data folder under its lock.
    /// Download caches go only when setup is verified and Settings does not keep them. Quiet unless it frees space.
    /// </summary>
    public async Task StartupMaintenanceAsync()
    {
        if (_pendingMove is { } interrupted)
        {
            // A move stopped by a crash or power loss: the data folder was never switched; remove the half-made copy.
            try
            {
                await _service.AbandonDataMoveAsync(DataRoot, interrupted, CancellationToken.None);
                _pendingMove = null;
                _settings.Save(CurrentSettings());
                Notice = "Moving the data folder did not finish last time, so Media Workbench kept using the current folder and removed the partial copy.";
            }
            catch (Exception ex) { Record("Could not remove an interrupted move's partial copy: " + ex.Message); }
        }
        await Task.Run(() =>
        {
            // Earlier versions kept these in Windows' temporary folder; only this app's own file names are touched.
            foreach (var (painted, updates) in new[] { (WorkFolders.LegacyPainted, WorkFolders.LegacyUpdates), (WorkFolders.Painted(DataRoot), WorkFolders.Updates(DataRoot)) })
            {
                Housekeeping.SweepPaintedHandOvers(painted, Housekeeping.PaintedMaxAge, DateTime.UtcNow);
                Housekeeping.RemoveFinishedInstallers(updates, AppVersion, DateTime.UtcNow);
            }
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
        string installer = await _updates.DownloadAsync(_update, DataRoot, ObserveProgress(), token);
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
