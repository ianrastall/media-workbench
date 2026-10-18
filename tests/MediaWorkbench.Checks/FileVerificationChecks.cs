using System.Security.Cryptography;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

static class FileVerificationChecks
{
    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => action(value);
    }

    public static async Task RunAsync(Action<bool, string> check, string output, CancellationToken token)
    {
        string folder = Path.Combine(output, "native-verification-café-森林");
        Directory.CreateDirectory(folder);
        const string relative = "weights.bin";
        string path = Path.Combine(folder, relative);
        byte[] contents = new byte[150000];
        RandomNumberGenerator.Fill(contents);
        await File.WriteAllBytesAsync(path, contents, token);
        string expected = Convert.ToHexStringLower(SHA256.HashData(contents));
        List<OperationProgress> reports = [];
        var progress = new InlineProgress(reports.Add);
        string hash = NativeCore.VerifyPinnedFile(folder, relative, contents.Length, expected, progress, token);
        check(hash == expected && reports.Count >= 4 && reports[^1].Completed == contents.Length
            && (await File.ReadAllBytesAsync(path, token)).SequenceEqual(contents),
            "C# calls the Rust pinned-file verifier with streaming byte progress and unchanged bytes");
        bool mismatch = false;
        try { NativeCore.VerifyPinnedFile(folder, relative, contents.Length, new string('0', 64), progress, token); }
        catch (InvalidDataException) { mismatch = true; }
        check(mismatch, "Native pinned-file verification rejects same-size checksum corruption");
        bool escaped = false;
        try { NativeCore.VerifyPinnedFile(folder, "../weights.bin", contents.Length, expected, progress, token); }
        catch (InvalidDataException) { escaped = true; }
        check(escaped, "Native pinned-file verification refuses escaping paths through the managed ABI");
        using var cancel = new CancellationTokenSource();
        bool stopped = false;
        var cancelDuringRead = new InlineProgress(value => { if (value.Completed > 0) cancel.Cancel(); });
        try { NativeCore.VerifyPinnedFile(folder, relative, contents.Length, expected, cancelDuringRead, cancel.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped && (await File.ReadAllBytesAsync(path, token)).SequenceEqual(contents),
            "C# cancellation stops native hashing inside a file and keeps the downloaded bytes");
        bool contained = false;
        var broken = new InlineProgress(_ => throw new InvalidOperationException("fixture progress failure"));
        try { NativeCore.VerifyPinnedFile(folder, relative, contents.Length, expected, broken, token); }
        catch (IOException ex) { contained = ex.InnerException is InvalidOperationException; }
        check(contained, "Managed verification progress failures are contained at the C ABI boundary");
    }
}
