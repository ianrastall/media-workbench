using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

static class McpChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root, string output, CancellationToken token)
    {
        var location = new DirectoryInfo(AppContext.BaseDirectory);
        string cli = Path.Combine(location.Parent!.Parent!.FullName, "MediaWorkbench.Cli", location.Name, "MediaWorkbench.Cli.exe");
        async Task Session(bool allowSetup, bool allowInference)
        {
            var info = new ProcessStartInfo(cli) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            foreach (string argument in new[] { "mcp", "--root", root, "--data-root", Path.Combine(output, "mcp-data") }) info.ArgumentList.Add(argument);
            if (allowSetup) info.ArgumentList.Add("--allow-setup");
            if (allowInference) info.ArgumentList.Add("--allow-inference");
            using var process = Process.Start(info)!;
            try
            {
                async Task Send(object value) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Wire.Json)); await process.StandardInput.FlushAsync(token); }
                async Task<JsonElement> Read()
                {
                    string line = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token) ?? throw new IOException("MCP host exited without a response.");
                    return JsonDocument.Parse(line).RootElement.Clone();
                }
                async Task<JsonElement> Call(string id, string name, object args)
                {
                    await Send(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments = args } });
                    return await Read();
                }
                await Send(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
                check((await Read()).GetProperty("error").GetProperty("code").GetInt32() == -32002, "MCP requires initialization before exposing tools");
                await Send(new { jsonrpc = "2.0", id = 2, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "checks", version = "1" } } });
                check((await Read()).GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "MCP initializes over real UTF-8 stdio");
                await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
                await Send(new { jsonrpc = "2.0", id = 3, method = "tools/list" });
                var tools = (await Read()).GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
                check(tools.Contains("workbench_install_model") == allowSetup && tools.Contains("workbench_generate_image") == allowInference && tools.Contains("workbench_upscale_image") == allowInference,
                    "MCP advertises setup and inference only when granted at launch");
                var capabilities = (await Call("caps", "workbench_capabilities", new { })).GetProperty("result").GetProperty("structuredContent");
                check(!capabilities.GetProperty("videoGenerationSupported").GetBoolean() && capabilities.GetProperty("allowSetup").GetBoolean() == allowSetup,
                    "MCP reports unsupported video and actual launch permissions");
                var storage = (await Call("storage", "workbench_storage", new { })).GetProperty("result");
                check(!storage.GetProperty("isError").GetBoolean() && storage.GetProperty("structuredContent").GetProperty("items").GetArrayLength() == 7,
                    "MCP inspects the scoped data folder through the Rust storage service");
                var escape = await Call("escape", "workbench_library", new { dataRoot = output });
                check(escape.GetProperty("error").GetProperty("code").GetInt32() == -32602, "An LLM cannot override the MCP data-folder scope");
                if (!allowSetup)
                    check((await Call("setup", "workbench_install_model", new { })).GetProperty("error").GetProperty("code").GetInt32() == -32602,
                        "A disabled setup tool cannot be called by guessing its name");
                var library = (await Call("library", "workbench_library", new { query = "café", kind = "upscale", limit = 2 })).GetProperty("result");
                check(!library.GetProperty("isError").GetBoolean() && library.GetProperty("structuredContent").GetProperty("total").GetInt32() == 0,
                    "MCP Library search uses the real worker and selected data folder");
                var bad = await Call("bad", "workbench_library", new { offset = -1 });
                check(bad.GetProperty("error").GetProperty("code").GetInt32() == -32602, "MCP validates numeric arguments before execution");
                if (allowInference)
                {
                    var generation = (await Call("generate", "workbench_generate_image", new { prompt = "a house", seed = 4294967295L })).GetProperty("result");
                    check(generation.GetProperty("isError").GetBoolean() && generation.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("Install / verify"),
                        "MCP inference still requires actual verified installation state");
                }
                await process.StandardInput.WriteLineAsync("{broken");
                await process.StandardInput.FlushAsync(token);
                check((await Read()).GetProperty("error").GetProperty("code").GetInt32() == -32700, "Malformed MCP JSON produces a protocol error without ending the session");
                await Send(new { jsonrpc = "2.0", id = "ping", method = "ping" });
                check((await Read()).GetProperty("id").GetString() == "ping", "MCP remains usable after a rejected request");
                process.StandardInput.Close();
                await process.WaitForExitAsync(token);
                check(process.ExitCode == 0, "MCP exits cleanly when the client closes stdin");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
        await Session(false, false);
        await Session(true, true);
        await ControlledSession(check, output, token);
    }

    private static async Task ControlledSession(Action<bool, string> check, string root, CancellationToken token)
    {
        using var input = new ChannelInput();
        using var output = new ChannelOutput();
        var workbench = new RecordingWorkbench { HealthGate = new TaskCompletionSource().Task, HealthStarted = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var running = new McpServer(workbench, root, allowInference: true).RunAsync(input, output, token);
        void Send(object value) => input.Messages.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Wire.Json) + "\n"));
        async Task<JsonElement> Read() => JsonDocument.Parse(await output.Messages.Reader.ReadAsync(token)).RootElement.Clone();
        Send(new { jsonrpc = "2.0", id = "init", method = "initialize", @params = new { protocolVersion = "2025-11-25" } });
        await Read();
        Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        Send(new { jsonrpc = "2.0", id = "active", method = "tools/call", @params = new { name = "workbench_health", arguments = new { } } });
        await workbench.HealthStarted.Task.WaitAsync(token);
        Send(new { jsonrpc = "2.0", id = "busy", method = "tools/call", @params = new { name = "workbench_library", arguments = new { } } });
        check((await Read()).GetProperty("error").GetProperty("code").GetInt32() == -32000, "MCP rejects overlapping operations while a tool is active");
        Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = "active" } });
        var cancelled = await Read();
        check(cancelled.GetProperty("id").GetString() == "active" && cancelled.GetProperty("result").GetProperty("isError").GetBoolean(),
            "MCP cancellation reaches the active service token without a second notification response");
        Send(new { jsonrpc = "2.0", id = "generate", method = "tools/call", @params = new { name = "workbench_generate_image",
            arguments = new { prompt = "café forest", seed = 4294967295L, steps = 5, width = 768, height = 512, guidanceScale = 4.5, sampler = "euler-a" } } });
        var generated = await Read();
        check(!generated.GetProperty("result").GetProperty("isError").GetBoolean()
            && workbench.LastGeneration is { Prompt: "café forest", Seed: 4294967295L, Steps: 5, Width: 768, Height: 512, GuidanceScale: 4.5, Sampler: "euler-a" }
            && workbench.DataRoots[^1] == root, "MCP passes generation settings and large seeds to the scoped service without alteration");
        workbench.HealthStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new { jsonrpc = "2.0", id = "disconnect", method = "tools/call", @params = new { name = "workbench_health", arguments = new { } } });
        await workbench.HealthStarted.Task.WaitAsync(token);
        input.Messages.Writer.Complete();
        await running.WaitAsync(token);
        check((await Read()).GetProperty("result").GetProperty("isError").GetBoolean(), "MCP client EOF cancels and joins unfinished service work");

        using var oversizedInput = new MemoryStream(new byte[1_048_577].Select(_ => (byte)' ').ToArray());
        using var oversizedOutput = new StringWriter();
        await new McpServer(workbench, root).RunAsync(oversizedInput, oversizedOutput, token);
        check(JsonDocument.Parse(oversizedOutput.ToString()).RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32600,
            "MCP bounds messages before parsing and closes an oversized transport");
    }

    private sealed class ChannelOutput : TextWriter
    {
        public Channel<string> Messages { get; } = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => Messages.Writer.TryWrite(value!);
    }
    private sealed class ChannelInput : Stream
    {
        public Channel<byte[]> Messages { get; } = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = [];
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _pending.Length)
            {
                if (!await Messages.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _pending = await Messages.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
