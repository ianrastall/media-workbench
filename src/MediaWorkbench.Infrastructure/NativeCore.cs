using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>ABI 1: caller-owned UTF-8 buffers. Rust allocations never cross into .NET.</summary>
public static unsafe partial class NativeCore
{
    private const string Library = "media_workbench_core";
    [LibraryImport(Library, EntryPoint = "mw_abi_version")]
    private static partial uint AbiVersion();
    [LibraryImport(Library, EntryPoint = "mw_validate_relative_path")]
    private static partial int ValidatePath(byte* input, nuint length);
    [LibraryImport(Library, EntryPoint = "mw_plan_installation")]
    private static partial int Plan(byte* input, nuint length, byte* output, nuint capacity, out nuint required);
    [LibraryImport(Library, EntryPoint = "mw_sha256_file")]
    private static partial int Hash(byte* input, nuint length, byte* output, nuint capacity, out nuint required);
    [LibraryImport(Library, EntryPoint = "mw_export_artifact")]
    private static partial int Export(byte* input, nuint length,
        delegate* unmanaged[Cdecl]<ulong, ulong, nint, int> progress, nint context,
        byte* output, nuint capacity, out nuint required);
    [LibraryImport(Library, EntryPoint = "mw_storage_summary")]
    private static partial int Storage(byte* input, nuint length,
        delegate* unmanaged[Cdecl]<nint, int> cancel, nint context,
        byte* output, nuint capacity, out nuint required);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStorageCancel(nint context)
    {
        try { return ((CancellationToken)GCHandle.FromIntPtr(context).Target!).IsCancellationRequested ? 1 : 0; }
        catch { return 1; } // No managed exception may unwind across the C ABI.
    }

    public static StorageSnapshot GetStorageSnapshot(string dataRoot, StorageLayout layout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyAbi();
        var input = JsonSerializer.SerializeToUtf8Bytes(new { dataRoot, layout.RuntimeProfileId, layout.ModelIds }, Wire.Json);
        var handle = GCHandle.Alloc(cancellationToken);
        try
        {
            fixed (byte* pointer = input)
            {
                int code = Storage(pointer, (nuint)input.Length, null, 0, null, 0, out nuint capacity);
                if (code != 2 || capacity > 1_048_576) throw new InvalidDataException($"Native storage inventory failed ({code}).");
                var output = new byte[(int)capacity];
                fixed (byte* result = output)
                {
                    code = Storage(pointer, (nuint)input.Length, &OnStorageCancel, GCHandle.ToIntPtr(handle), result, capacity, out nuint written);
                    if (code == 5) throw new OperationCanceledException("Storage measurement was cancelled.", cancellationToken);
                    if (code == 1) throw new InvalidDataException("The data folder or storage layout is invalid, or a managed path passes through a link or junction.");
                    if (code != 0 || written > capacity) throw new IOException($"The native storage inventory could not read the folder ({code}). Check folder access; no partial sizes were reported.");
                    return JsonSerializer.Deserialize<StorageSnapshot>(output.AsSpan(0, (int)written), Wire.Json)
                        ?? throw new InvalidDataException("The native storage inventory was empty.");
                }
            }
        }
        finally { handle.Free(); }
    }

    private sealed class ExportState(IProgress<OperationProgress> progress, CancellationToken cancellation)
    {
        public IProgress<OperationProgress> Progress { get; } = progress;
        public CancellationToken Cancellation { get; } = cancellation;
        public Exception? Failure { get; set; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnExportProgress(ulong completed, ulong total, nint context)
    {
        var state = (ExportState)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (state.Cancellation.IsCancellationRequested) return 1;
            state.Progress.Report(new("Saving a copy", (long)completed, (long)total, "bytes"));
            return state.Cancellation.IsCancellationRequested ? 1 : 0;
        }
        catch (Exception ex) { state.Failure = ex; return 1; } // Never unwind through Rust/C.
    }

    /// <summary>Rust owns the whole artifact export transaction; no Python or inference runtime is needed.</summary>
    public static string ExportArtifact(string dataRoot, ImageExportRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyAbi();
        var input = JsonSerializer.SerializeToUtf8Bytes(new { dataRoot, request.JobId, request.Destination }, Wire.Json);
        var output = new byte[64];
        var state = new ExportState(progress, cancellationToken);
        var handle = GCHandle.Alloc(state);
        try
        {
            fixed (byte* pointer = input)
            fixed (byte* result = output)
            {
                int code = Export(pointer, (nuint)input.Length, &OnExportProgress, GCHandle.ToIntPtr(handle), result, 64, out nuint written);
                if (state.Failure is { } failure) throw new IOException("Could not report export progress.", failure);
                if (code == 5) throw new OperationCanceledException("Saving the copy was cancelled; the destination was kept.", cancellationToken);
                if (code == 6) throw new InvalidDataException("This image changed since it was generated. The copy was not saved; the destination was kept.");
                if (code == 1) throw new InvalidDataException("Choose a completed Library image and a PNG destination outside the managed data folder. Its job record must be intact.");
                if (code != 0 || written != 64) throw new IOException($"The native export service could not save the copy ({code}). Check the original and destination folder access. The destination was kept.");
            }
            return Path.GetFullPath(request.Destination);
        }
        finally { handle.Free(); }
    }

    public static uint VerifyAbi()
    {
        uint version = AbiVersion();
        if (version != 1) throw new InvalidOperationException($"Native core ABI {version} is incompatible with ABI 1.");
        return version;
    }

    public static bool IsSafeRelativePath(string value)
    {
        VerifyAbi();
        var input = Encoding.UTF8.GetBytes(value);
        fixed (byte* pointer = input) return ValidatePath(pointer, (nuint)input.Length) == 0;
    }

    public static InstallationPlan PlanInstallation(ModelRecipe recipe)
    {
        VerifyAbi();
        var input = JsonSerializer.SerializeToUtf8Bytes(recipe, Wire.Json);
        fixed (byte* pointer = input)
        {
            int code = Plan(pointer, (nuint)input.Length, null, 0, out nuint required);
            if (code != 2 || required > 1_048_576) throw new InvalidDataException($"Native planning failed ({code}).");
            var output = new byte[(int)required];
            fixed (byte* result = output)
            {
                code = Plan(pointer, (nuint)input.Length, result, required, out nuint written);
                if (code != 0 || written > required) throw new InvalidDataException($"Native planning failed ({code}).");
                return JsonSerializer.Deserialize<InstallationPlan>(output.AsSpan(0, (int)written), Wire.Json)
                    ?? throw new InvalidDataException("Native plan was empty.");
            }
        }
    }

    public static string Sha256File(string path)
    {
        VerifyAbi();
        var input = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var output = new byte[64];
        fixed (byte* pointer = input)
        fixed (byte* result = output)
        {
            int code = Hash(pointer, (nuint)input.Length, result, (nuint)output.Length, out nuint written);
            if (code != 0 || written != 64) throw new IOException($"Native SHA-256 could not read the file ({code}).");
        }
        return Encoding.UTF8.GetString(output);
    }
}
