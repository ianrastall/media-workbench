using System.Runtime.InteropServices;
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
