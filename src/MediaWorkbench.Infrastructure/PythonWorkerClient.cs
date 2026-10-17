using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>One in-flight operation, one owned worker tree, no shell or network listener.</summary>
public sealed class PythonWorkerClient : IWorkerClient
{
    private readonly Process _process;
    private readonly ProcessTreeJob _job;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _stderrPump;
    private readonly Queue<string> _diagnostics = new();
    private bool _disposed;
    public WorkerEvent Handshake { get; private set; } = null!;

    private PythonWorkerClient(Process process, ProcessTreeJob job)
    {
        _process = process;
        _job = job;
        _stderrPump = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                lock (_diagnostics)
                {
                    _diagnostics.Enqueue(line.Length > 2048 ? line[..2048] : line);
                    while (_diagnostics.Count > 64) _diagnostics.Dequeue();
                }
        });
    }

    public static async Task<PythonWorkerClient> StartAsync(string pythonExecutable, string root,
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var worker = Path.Combine(root, "workers", "python", "media_worker.py");
        if (!File.Exists(worker)) throw new FileNotFoundException("Choose the scaffold root containing workers/python/media_worker.py", worker);
        if (!Path.IsPathFullyQualified(pythonExecutable) || !File.Exists(pythonExecutable))
            throw new FileNotFoundException("Use an absolute path to an existing Python executable.", pythonExecutable);
        var start = new ProcessStartInfo(pythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.Environment["PYTHONUTF8"] = "1";
        var job = new ProcessTreeJob();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("Python did not start.");
        }
        catch { job.Dispose(); throw; }
        try
        {
            // The worker starts no children before its first request, so assignment here covers its whole tree.
            job.Assign(process);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            job.Dispose();
            throw;
        }
        var client = new PythonWorkerClient(process, job);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token)
                ?? throw new IOException("Worker exited before the handshake.");
            var hello = Parse(line);
            if (hello.Protocol != Wire.Version || hello.Kind != "hello" || hello.Id is not null)
                throw new InvalidDataException("Worker handshake is incompatible with protocol 1.");
            client.Handshake = hello;
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<WorkerEvent> ExecuteAsync(string operation, object payload,
        Action<WorkerEvent>? onEvent = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        CancellationTokenSource? grace = null;
        bool started = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            await SendAsync(new { protocol = Wire.Version, id, operation, payload });
            started = true;
            while (true)
            {
                var pending = _process.StandardOutput.ReadLineAsync();
                string? line;
                try { line = await pending.WaitAsync(grace?.Token ?? cancellationToken); }
                catch (OperationCanceledException) when (grace is null && cancellationToken.IsCancellationRequested)
                {
                    await SendAsync(new { protocol = Wire.Version, id, operation = "cancel", payload = new { } });
                    grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    // Reuse the pending read. Never create a second concurrent StreamReader read.
                    line = await pending.WaitAsync(grace.Token);
                }
                if (line is null) throw new IOException($"Worker exited during {operation}. {DiagnosticTail()}");
                var message = Parse(line);
                if (message.Protocol != Wire.Version || message.Id != id ||
                    message.Kind is not ("progress" or "result" or "error"))
                    throw new InvalidDataException("Unexpected worker event, job id, or protocol.");
                onEvent?.Invoke(message);
                if (message.IsTerminal) return message;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopOwnedProcess();
            throw new OperationCanceledException("Worker stopped after cancellation deadline.", cancellationToken);
        }
        catch
        {
            // A partly consumed or invalid transport is never returned to the pool.
            if (started) StopOwnedProcess();
            throw;
        }
        finally { grace?.Dispose(); _gate.Release(); }
    }

    private async Task SendAsync(object message)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, Wire.Json));
        await _process.StandardInput.FlushAsync();
    }

    private static WorkerEvent Parse(string line)
    {
        // This post-read bound is a scaffold guard, not a production streaming size limit.
        if (line.Length > 1_048_576) throw new InvalidDataException("Worker event exceeds 1 MiB.");
        return JsonSerializer.Deserialize<WorkerEvent>(line, Wire.Json)
            ?? throw new InvalidDataException("Empty worker event.");
    }

    private string DiagnosticTail() { lock (_diagnostics) return string.Join(" | ", _diagnostics.TakeLast(4)); }
    private void StopOwnedProcess()
    {
        // The job also reaches descendants whose worker parent already exited.
        _job.Terminate();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Caller must cancel/await any active request before disposal.
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { StopOwnedProcess(); await _process.WaitForExitAsync(); }
            // Closing the job ends any descendant that outlived the worker before draining diagnostics.
            _job.Dispose();
            await _stderrPump;
        }
        finally { _job.Dispose(); _process.Dispose(); _gate.Dispose(); }
    }
}
