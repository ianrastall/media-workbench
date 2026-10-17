using System.Runtime.InteropServices;

namespace MediaWorkbench_App.Services;

internal static class WindowsIdentity
{
    private static Mutex? _instanceMutex;
    internal const string AppId = "ianrastall.MediaWorkbench";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    internal static bool AcquireInstance()
    {
        _instanceMutex = new Mutex(true, @"Local\ianrastall.MediaWorkbench", out bool created);
        if (created) return true;
        var existing = FindWindow(null, "Media Workbench");
        if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
        return false;
    }
}
