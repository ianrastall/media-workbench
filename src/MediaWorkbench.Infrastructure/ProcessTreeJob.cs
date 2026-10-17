using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaWorkbench.Infrastructure;

/// <summary>
/// Windows Job Object owning one worker and every descendant it starts. Closing the handle, including the
/// operating system closing it after an abrupt crash of this process, terminates the whole tree.
/// </summary>
internal sealed unsafe partial class ProcessTreeJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private readonly SafeFileHandle? _handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, void* information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    public ProcessTreeJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        _handle = CreateJobObject(0, 0);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a worker job object.");
        var limits = new ExtendedLimits { LimitFlags = KillOnJobClose };
        if (!SetInformationJobObject(_handle, ExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimits)))
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, "Could not configure worker job ownership.");
        }
    }

    /// <summary>Assign before the process can receive work, so every later descendant inherits the job.</summary>
    public void Assign(Process process)
    {
        if (_handle is null) return;
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not place the worker in its job object.");
    }

    /// <summary>Ends the worker and descendants that may have outlived it, such as an orphaned inference child.</summary>
    public void Terminate()
    {
        if (_handle is { IsClosed: false }) TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle?.Dispose();
}
