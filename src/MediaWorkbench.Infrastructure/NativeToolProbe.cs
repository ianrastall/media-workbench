using System.Diagnostics;

namespace MediaWorkbench.Infrastructure;

public static class NativeToolProbe
{
    // Narrow read-only ffprobe adapter. No downloads or arbitrary command strings.
    public static async Task<string> FfprobeVersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Supply an absolute ffprobe executable path.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ?? throw new IOException("ffprobe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var text = await stdout;
            var diagnostic = await stderr;
            if (process.ExitCode != 0) throw new IOException($"ffprobe exited {process.ExitCode}: {diagnostic}");
            return text.Split('\n')[0].Trim();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            // Observe both drain tasks before disposing their process streams.
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
    }
}
