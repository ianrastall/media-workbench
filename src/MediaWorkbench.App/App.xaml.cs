using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MediaWorkbench_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Services.WindowsIdentity.Initialize();
        UnhandledException += (_, args) => WriteStartupError(args.Exception);
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int checkIndex = Array.IndexOf(arguments, "--self-test");
        int uiCheckIndex = Array.IndexOf(arguments, "--ui-self-test");
        if (checkIndex >= 0)
        {
            try
            {
                if (checkIndex + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[checkIndex + 1]))
                    throw new ArgumentException("--self-test requires an absolute report path.");
                string root = AppContext.BaseDirectory;
                var service = new MediaWorkbench.Infrastructure.WorkbenchService(root, Path.Combine(root, "runtime", "python", "python.exe"));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string health = await service.GetHealthAsync(deadline.Token);
                var plan = service.GetBaselinePlan();
                if (!plan.CanExecute) throw new InvalidDataException("Published baseline recipe does not permit local verification.");
                var hub = MediaWorkbench.Infrastructure.NativeCore.PlanHubFiles("owner/model", new string('a', 40), "openrail", false,
                    [new("model.safetensors", 100, new string('b', 64))]);
                if (hub.Blocker.Length != 0 || hub.Files.Length != 1)
                    throw new InvalidDataException("Published native Hub planning failed.");
                string hash = MediaWorkbench.Infrastructure.NativeCore.Sha256File(Path.Combine(root, "workers", "python", "media_worker.py"));
                MediaWorkbench.Infrastructure.NativeCore.VerifyPinnedFile(root, "workers/python/media_worker.py",
                    new FileInfo(Path.Combine(root, "workers", "python", "media_worker.py")).Length, hash,
                    new Progress<MediaWorkbench.Core.OperationProgress>(), deadline.Token);
                // Drive information and clean-up of a folder that does not exist create nothing.
                string untouched = Path.Combine(Path.GetTempPath(), "MediaWorkbench-tidy-smoke-" + Guid.NewGuid().ToString("N"));
                var drive = await service.GetDriveAsync(untouched, deadline.Token);
                var tidy = await service.TidyAsync(untouched, true, true, deadline.Token);
                if (drive.FreeBytes <= 0 || tidy.RemovedFiles != 0 || Directory.Exists(untouched))
                    throw new InvalidDataException("The bundled native drive and clean-up check failed.");
                string report = arguments[checkIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                string libraryCheck = Path.Combine(Path.GetTempPath(), "MediaWorkbench-library-smoke-" + Guid.NewGuid().ToString("N"));
                try
                {
                    // Hardware is read from the nearest existing folder without creating the data folder.
                    var profile = await service.GetSystemProfileAsync(libraryCheck, deadline.Token);
                    if (Directory.Exists(libraryCheck) || profile.Hardware.Drive is not { FreeBytes: > 0 } || profile.Hardware.RamMiB is not > 0 || profile.Summary.Length == 0)
                        throw new InvalidDataException("The bundled hardware profile check failed.");
                    var library = await service.GetLibraryAsync(libraryCheck, deadline.Token);
                    if (library.Total != 0 || library.Images.Count != 0) throw new InvalidDataException("The bundled Library index check failed.");
                    var storage = await service.GetStorageAsync(libraryCheck, deadline.Token);
                    if (storage.Items.Count != 8 || storage.FreeBytes <= 0 || storage.InstallationBytes != 0)
                        throw new InvalidDataException("The bundled native storage check failed.");
                    var models = await service.GetModelsAsync(libraryCheck, deadline.Token);
                    if (models.Count != 1 || !models[0].BuiltIn || models[0].IsReady)
                        throw new InvalidDataException("The bundled model listing check failed.");
                }
                finally { if (Directory.Exists(libraryCheck)) Directory.Delete(libraryCheck, recursive: true); }
                await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { success = true, health, plan, workerSha256 = hash, libraryIndex = true, nativeVerification = true, hardwareProfile = true, hubPlanning = true }));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                if (checkIndex + 1 < arguments.Length && Path.IsPathFullyQualified(arguments[checkIndex + 1]))
                {
                    string report = arguments[checkIndex + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { success = false, error = ex.Message }));
                }
            }
            Exit();
            return;
        }
        if (uiCheckIndex < 0 && !Services.WindowsIdentity.AcquireInstance()) { Exit(); return; }
        try
        {
            if (uiCheckIndex >= 0 && (uiCheckIndex + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[uiCheckIndex + 1])))
                throw new ArgumentException("--ui-self-test requires an absolute report path.");
            _window = new MainWindow();
            if (uiCheckIndex >= 0 && _window.Content is FrameworkElement root)
            {
                root.Loaded += async (_, _) =>
                {
                    await Task.Delay(300);
                    string report = arguments[uiCheckIndex + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    await File.WriteAllTextAsync(report, "{\"success\":true,\"mainWindowLoaded\":true}");
                    Exit();
                };
            }
            _window.Activate();
        }
        catch (Exception ex) { WriteStartupError(ex); Environment.ExitCode = 1; Exit(); }
    }

    private static void WriteStartupError(Exception exception)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench", "Logs");
            Directory.CreateDirectory(folder);
            string log = Path.Combine(folder, "startup.log");
            // Bounded: past 1 MiB the log becomes startup.old.log, replacing the previous one.
            if (File.Exists(log) && new FileInfo(log).Length > 1 << 20) File.Move(log, Path.Combine(folder, "startup.old.log"), overwrite: true);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O}\n{exception}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
