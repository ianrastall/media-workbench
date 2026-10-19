using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace MediaWorkbench_App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(string? testStateRoot = null)
    {
        string stateRoot = testStateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWorkbench");
        string python = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
#if DEBUG
        // Development override only; a shipped build always runs its bundled interpreter.
        python = Environment.GetEnvironmentVariable("MEDIA_WORKBENCH_PYTHON") ?? python;
#endif
        string version = UpdateService.ApplicationVersion();
        ViewModel = new(new WorkbenchService(AppContext.BaseDirectory, python), new SettingsStore(stateRoot),
            new UpdateService(UpdateService.CreateClient(version), version));
        ViewModel.InstallerReady += RunInstallerAndExit;
        InitializeComponent();
        var shell = new ShellView(ViewModel) { WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this) };
        Host.Children.Add(shell);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(shell.TitleBarElement);
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = Math.Min((int)(1200 * scale), (int)(display.Width * .92));
        int height = Math.Min((int)(820 * scale), (int)(display.Height * .92));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(display.X + (display.Width - width) / 2, display.Y + (display.Height - height) / 2, width, height));
        Closed += (_, _) => ViewModel.CancelActiveWork();
        // The packaging smoke test opens the window briefly; it must not tidy the tester's data folder.
        if (testStateRoot is null)
        {
            _ = ViewModel.CheckForUpdatesQuietlyAsync();
            _ = ViewModel.StartupMaintenanceAsync();
        }
    }

    // The user chose Update: run the verified installer silently; it closes this copy and reopens the new one.
    private void RunInstallerAndExit(string installer)
    {
        Services.WindowsIdentity.ReleaseInstance();
        var start = new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = false };
        foreach (string argument in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/SP-" }) start.ArgumentList.Add(argument);
        System.Diagnostics.Process.Start(start);
        Application.Current.Exit();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
