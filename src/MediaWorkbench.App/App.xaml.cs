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
                string hash = MediaWorkbench.Infrastructure.NativeCore.Sha256File(Path.Combine(root, "workers", "python", "media_worker.py"));
                string report = arguments[checkIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { success = true, health, plan, workerSha256 = hash }));
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
            File.AppendAllText(Path.Combine(folder, "startup.log"), $"{DateTimeOffset.Now:O}\n{exception}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
