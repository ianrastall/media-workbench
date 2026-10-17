using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace MediaWorkbench_App.Services;

internal static class WindowsIdentity
{
    private static Mutex? _instanceMutex;
    internal const string AppId = "ianrastall.MediaWorkbench";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    /// <summary>Returns true for the first instance; a later launch brings that instance's own window forward.</summary>
    internal static bool AcquireInstance()
    {
        // The installer's AppMutex looks for this mutex to close a running copy.
        _instanceMutex = new Mutex(true, @"Local\ianrastall.MediaWorkbench", out _);
        var primary = AppInstance.FindOrRegisterForKey("main");
        if (primary.IsCurrent) return true;
        try
        {
            // Matched by process, not by window title: an Explorer window named "Media Workbench" is not us.
            using var process = Process.GetProcessById((int)primary.ProcessId);
            var window = process.MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, IsIconic(window) ? 9 : 5); // SW_RESTORE only when minimized, so a maximized window stays maximized.
                SetForegroundWindow(window);
            }
        }
        catch (ArgumentException) { } // The first instance exited meanwhile.
        return false;
    }
}
