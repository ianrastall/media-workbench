using System.Text.Json;
using System.Text.RegularExpressions;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>Opt-in local MCP stdio host. Capabilities and the data folder are fixed by the launching user.</summary>
public sealed class McpServer(IWorkbenchService service, string dataRoot, bool allowSetup = false, bool allowInference = false)
{
    private readonly object _outputLock = new();
    private TextWriter _output = TextWriter.Null;
    private bool _initialized;
    private bool _ready;
    private Task? _active;
    private CancellationTokenSource? _cancellation;
    private JsonElement _activeId;
    private int _operationRunning;
    private const int MaximumMessage = 1_048_576;
    private sealed record Tool(string Name, string Description, object InputSchema, object Annotations);

    private static object Schema(object properties, string[]? required = null) => new { type = "object", properties, required = required ?? [], additionalProperties = false };
    private static object Text(int maxLength = 2000, string? pattern = null) => pattern is null ? (object)new { type = "string", maxLength } : new { type = "string", maxLength, pattern };
    private static object Integer(long minimum, long maximum) => new { type = "integer", minimum, maximum };
    private static object Number(double minimum, double maximum) => new { type = "number", minimum, maximum };
    private static object IntegerChoice(int minimum, int maximum, params int[] choices) => new { type = "integer", minimum, maximum, @enum = choices };
    private static object Choice(params string[] choices) => new { type = "string", maxLength = 2000, @enum = choices };
    private static Tool Define(string name, string description, object schema, bool readOnly = true, bool network = false) =>
        new(name, description, schema, new { readOnlyHint = readOnly, destructiveHint = false, openWorldHint = network });

    private List<Tool> Tools()
    {
        List<Tool> tools = [
            Define("workbench_capabilities", "Read the supported pipelines and launch-time permissions. Short videos (2-3 seconds, Stable Diffusion 1.5 with AnimateDiff) need their own verified setup.", Schema(new { })),
            Define("workbench_health", "Inspect the native core, bundled worker, and detected GPU. Detection does not certify inference readiness.", Schema(new { })),
            Define("workbench_installation_plan", "Read the native installation plan for the bundled SD 1.5 profile; this does not install anything.", Schema(new { })),
            Define("workbench_model_status", "Read the saved folder's actual model readiness and recover interrupted work when idle.", Schema(new { })),
            Define("workbench_video_status", "Read whether short videos are set up and verified on this computer, and with which settings.", Schema(new { })),
            Define("workbench_storage", "Inspect storage usage in the selected data folder. No deletion is offered.", Schema(new { })),
            Define("workbench_models", "List the models in the selected data folder: the bundled Stable Diffusion 1.5 (id sd15-baseline) and SD 1.5-family models the user added, with their test status. Only models with status ready can generate; adding or removing models is done by the user in the app.", Schema(new { })),
            Define("workbench_library", "Search completed images and videos by prompt, seed or job ID and inspect recent unfinished jobs. Returns paths/settings, not image or video bytes.",
                Schema(new { query = Text(), kind = Choice("all", "generate", "upscale", "video"), offset = Integer(0, int.MaxValue), limit = Integer(1, 48) })),
            Define("workbench_search_models", "Search public Hugging Face metadata. Results do not imply runnable model support.",
                Schema(new { query = Text(200), task = Choice("", "text-to-image", "image-to-image", "text-to-video", "image-to-video") }, ["query"]), network: true),
            Define("workbench_model_details", "Read public model repository metadata and license information. This does not install a model.",
                Schema(new { repoId = Text(200) }, ["repoId"]), network: true)
        ];
        if (allowSetup) tools.Add(Define("workbench_install_model", "Install/verify only the bundled SD 1.5 and upscaler profile. Can download several GiB and run GPU verification. Review the model license first; readiness is reported only after verification.", Schema(new { }), false, true));
        if (allowSetup) tools.Add(Define("workbench_install_video", "Set up short videos: downloads the pinned AnimateDiff motion adapter (1.7 GB, Apache-2.0), verifies it, and makes a test video on the GPU, which can take several minutes. Needs a verified SD 1.5 installation.", Schema(new { }), false, true));
        if (allowInference)
        {
            tools.Add(Define("workbench_generate_image", "Generate a local SD 1.5 image using an already verified installation. Optional sourceJobId starts from a completed Library image; arbitrary input paths are not accepted. "
                + "With sourceJobId, changeLeft/changeTop/changeWidth/changeHeight (fractions 0-1 of the picture, all four together) change only that rectangle and keep the rest of the picture at its full size; strength 1 replaces the area completely. "
                + "Optional model is an id from workbench_models whose status is ready; the bundled sd15-baseline is the default.",
                Schema(new { prompt = Text(), negativePrompt = Text(), seed = Integer(0, uint.MaxValue), steps = Integer(1, 50), guidanceScale = Number(1, 20), width = IntegerChoice(512, 768, 512, 768), height = IntegerChoice(512, 768, 512, 768), sampler = Choice("pndm", "dpmpp-2m", "euler-a"), sourceJobId = Text(32, "^[a-f0-9]{32}$"), strength = Number(0.05, 1),
                    model = Text(32, "^(sd15-baseline|[a-f0-9]{32})$"),
                    changeLeft = Number(0, 1), changeTop = Number(0, 1), changeWidth = Number(0, 1), changeHeight = Number(0, 1) }, ["prompt"]), false));
            tools.Add(Define("workbench_generate_video", "Make a short video (16 frames = 2 seconds or 24 frames = 3 seconds at 8 frames a second) with the bundled SD 1.5 and the verified motion adapter. Returns the MP4 path and a poster frame; on 6 GB graphics cards this can take several minutes.",
                Schema(new { prompt = Text(), negativePrompt = Text(), seed = Integer(0, uint.MaxValue), steps = Integer(1, 50), guidanceScale = Number(1, 20), width = IntegerChoice(512, 768, 512, 768), height = IntegerChoice(512, 768, 512, 768), frames = IntegerChoice(16, 24, 16, 24) }, ["prompt"]), false));
            tools.Add(Define("workbench_upscale_image", "Upscale a completed Library image 2× or 4× with the verified Real-ESRGAN profile. The result is another Library image.",
                Schema(new { sourceJobId = Text(32, "^[a-f0-9]{32}$"), scale = IntegerChoice(2, 4, 2, 4) }, ["sourceJobId"]), false));
        }
        return tools;
    }

    private void Send(object message)
    {
        lock (_outputLock)
        {
            _output.WriteLine(JsonSerializer.Serialize(message, Wire.Json));
            _output.Flush();
        }
    }
    private void Result(JsonElement id, object result) => Send(new { jsonrpc = "2.0", id, result });
    private void Error(JsonElement? id, int code, string message) => Send(new { jsonrpc = "2.0", id, error = new { code, message } });

    /// <summary>Bounded UTF-8 JSON lines; stdout contains MCP messages only. EOF cancels and joins owned work.</summary>
    public async Task RunAsync(Stream input, TextWriter output, CancellationToken cancellationToken)
    {
        _output = output;
        using var line = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        if (line.Length > 0) Dispatch(line.ToArray(), cancellationToken);
                        line.SetLength(0);
                    }
                    else
                    {
                        if (line.Length >= MaximumMessage) { Error(null, -32600, "Message exceeds 1 MiB; closing transport."); return; }
                        line.WriteByte(buffer[index]);
                    }
                }
            }
        }
        finally
        {
            _cancellation?.Cancel();
            if (_active is { } active) await active;
            _cancellation?.Dispose();
        }
    }

    private void Dispatch(byte[] message, CancellationToken hostToken)
    {
        JsonElement? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0"
                || !request.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String)
            { Error(null, -32600, "Expected a JSON-RPC 2.0 request."); return; }
            string method = methodValue.GetString()!;
            bool hasId = request.TryGetProperty("id", out var id);
            if (hasId)
            {
                if (!(id.ValueKind == JsonValueKind.String && id.GetString()!.Length <= 128) && !(id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)))
                { Error(null, -32600, "Request id must be a string or integer."); return; }
                requestId = id.Clone();
            }
            var parameters = request.TryGetProperty("params", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
            if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("params must be an object.");
            if (!hasId)
            {
                if (method == "notifications/initialized" && _initialized) _ready = true;
                if (method == "notifications/cancelled" && parameters.TryGetProperty("requestId", out var cancelled) && Volatile.Read(ref _operationRunning) != 0
                    && cancelled.GetRawText() == _activeId.GetRawText()) _cancellation?.Cancel();
                return; // Notifications never receive responses.
            }
            id = requestId!.Value;
            if (method == "ping") { Result(id, new { }); return; }
            if (method == "initialize")
            {
                if (_initialized) { Error(id, -32600, "Already initialized."); return; }
                string version = GetString(parameters, "protocolVersion", required: true);
                _initialized = true;
                Result(id, new { protocolVersion = version is "2025-11-25" or "2025-06-18" ? version : "2025-11-25",
                    capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "media-workbench", version = UpdateService.ApplicationVersion() },
                    instructions = "Local media workspace. Respect launch-time capabilities and model licensing. Read model status before inference. Discovery is not installation support. Video is unsupported." });
                return;
            }
            if (!_ready) { Error(id, -32002, "Initialize the session first."); return; }
            if (method == "tools/list") { Result(id, new { tools = Tools() }); return; }
            if (method != "tools/call") { Error(id, -32601, "Method not found."); return; }
            string name = GetString(parameters, "name", required: true);
            var tool = Tools().SingleOrDefault(value => value.Name == name) ?? throw new ArgumentException("Unknown or disabled tool.");
            var arguments = parameters.TryGetProperty("arguments", out var args) ? args.Clone() : JsonSerializer.SerializeToElement(new { });
            ValidateArguments(arguments, tool.InputSchema);
            if (Volatile.Read(ref _operationRunning) != 0) { Error(id, -32000, "Another tool operation is active. Cancel or wait for it."); return; }
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cancellation.CancelAfter(TimeSpan.FromHours(4));
            _activeId = id;
            JsonElement? progressToken = parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("progressToken", out var token)
                && token.ValueKind is JsonValueKind.String or JsonValueKind.Number ? token.Clone() : null;
            var operationToken = _cancellation.Token;
            Volatile.Write(ref _operationRunning, 1);
            _active = Task.Run(() => ExecuteAsync(id, name, arguments, progressToken, operationToken), CancellationToken.None);
        }
        catch (JsonException) { Error(null, -32700, "Invalid JSON."); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { if (requestId is { } id) Error(id, -32602, ex.Message); }
    }

    private static void ValidateArguments(JsonElement arguments, object inputSchema)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
        var schema = JsonSerializer.SerializeToElement(inputSchema, Wire.Json);
        var properties = schema.GetProperty("properties");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!arguments.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException("Missing argument: " + required.GetString());
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var rule)) throw new ArgumentException("Unexpected argument: " + property.Name);
            var value = property.Value;
            string type = rule.GetProperty("type").GetString()!;
            if (type == "string")
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > rule.GetProperty("maxLength").GetInt32()) throw new ArgumentException("Invalid string: " + property.Name);
                if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.GetString() == value.GetString())) throw new ArgumentException("Unsupported choice: " + property.Name);
                if (rule.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new ArgumentException("Invalid value: " + property.Name);
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)
                    || (type == "integer" && !value.TryGetInt64(out _)) || number < rule.GetProperty("minimum").GetDouble() || number > rule.GetProperty("maximum").GetDouble())
                    throw new ArgumentException("Invalid number: " + property.Name);
                if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.GetDouble() == number)) throw new ArgumentException("Unsupported choice: " + property.Name);
            }
        }
    }

    private static string GetString(JsonElement value, string name, string fallback = "", bool required = false)
    {
        if (!value.TryGetProperty(name, out var property)) return required ? throw new ArgumentException("Missing argument: " + name) : fallback;
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : throw new ArgumentException(name + " must be a string.");
    }
    private static int GetInt(JsonElement value, string name, int fallback) => value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
    private static double GetNumber(JsonElement value, string name, double fallback) => value.TryGetProperty(name, out var property) ? property.GetDouble() : fallback;

    private async Task<string?> SourceImage(JsonElement args, bool required, CancellationToken token)
    {
        string jobId = GetString(args, "sourceJobId", required: required);
        if (jobId.Length == 0) return null;
        var library = await service.GetLibraryAsync(dataRoot, token, new(jobId, Limit: 48));
        return library.Images.FirstOrDefault(image => image.Id == jobId)?.ImagePath
            ?? throw new InvalidDataException("That completed source image was not found in the selected data folder.");
    }

    private async Task<GeneratedImage> GenerateAsync(JsonElement args, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        string[] area = ["changeLeft", "changeTop", "changeWidth", "changeHeight"];
        int given = area.Count(name => args.TryGetProperty(name, out _));
        if (given is > 0 and < 4) throw new ArgumentException("Give all four of changeLeft, changeTop, changeWidth, and changeHeight, or none.");
        string? source = await SourceImage(args, false, cancellationToken);
        string? mask = null;
        if (given == 4)
        {
            if (source is null) throw new ArgumentException("A change area needs sourceJobId.");
            var (width, height) = PngSize(source);
            var canvas = MaskCanvas.ForPicture(width, height);
            canvas.FillRectangle(GetNumber(args, "changeLeft", 0), GetNumber(args, "changeTop", 0), GetNumber(args, "changeWidth", 0), GetNumber(args, "changeHeight", 0));
            string folder = Path.Combine(Path.GetTempPath(), "MediaWorkbench", "painted");
            Directory.CreateDirectory(folder);
            mask = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(mask, canvas.EncodePng(), cancellationToken);
        }
        try
        {
            return await service.GenerateAsync(dataRoot,
                new(GetString(args, "prompt"), GetString(args, "negativePrompt"), args.TryGetProperty("seed", out var seed) ? seed.GetInt64() : 20261017,
                    GetInt(args, "steps", 20), GetNumber(args, "guidanceScale", 7.5), GetInt(args, "width", 512), GetInt(args, "height", 512), GetString(args, "sampler", "pndm"),
                    source, GetNumber(args, "strength", mask is null ? 0.6 : 1.0), MaskImage: mask, Model: GetString(args, "model", ModelChoice.BuiltInId)), progress, cancellationToken);
        }
        finally
        {
            if (mask is not null) try { File.Delete(mask); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Pixel size from a Library PNG's header; Library images are app-written PNGs without orientation metadata.</summary>
    private static (int Width, int Height) PngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using (var stream = File.OpenRead(path)) stream.ReadExactly(header);
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R') throw new InvalidDataException("The source image is not a PNG.");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]));
    }

    private async Task ExecuteAsync(JsonElement id, string name, JsonElement args, JsonElement? progressToken, CancellationToken cancellationToken)
    {
        var progress = new DirectProgress(update =>
        {
            if (progressToken is not { } token) return;
            // Stages have different units/totals; use a monotonically increasing notification count.
            Send(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = Interlocked.Increment(ref _progressSequence), message = update.Stage } });
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            object result = name switch
            {
                "workbench_models" => new { models = await service.GetModelsAsync(dataRoot, cancellationToken) },
                "workbench_capabilities" => new { dataRoot, allowSetup, allowInference, model = ModelChoice.BuiltInId, addedModelsSupported = true, imageGenerationSupported = true, partialImageChangesSupported = true, upscalingSupported = true, videoGenerationSupported = true, videoNeedsSetup = true, requiresVerifiedInstallation = true },
                "workbench_health" => new { summary = await service.GetHealthAsync(cancellationToken) },
                "workbench_installation_plan" => service.GetBaselinePlan(),
                "workbench_model_status" => await service.GetInstallationAsync(dataRoot, cancellationToken),
                "workbench_video_status" => await service.GetVideoAsync(dataRoot, cancellationToken),
                "workbench_install_video" when allowSetup => await service.InstallVideoAsync(dataRoot, progress, cancellationToken),
                "workbench_generate_video" when allowInference => await service.GenerateVideoAsync(dataRoot, new(GetString(args, "prompt"), GetString(args, "negativePrompt"),
                    args.TryGetProperty("seed", out var videoSeed) ? videoSeed.GetInt64() : 20261018, GetInt(args, "steps", 20), GetNumber(args, "guidanceScale", 7.5),
                    GetInt(args, "width", 512), GetInt(args, "height", 512), "ddim", Frames: GetInt(args, "frames", 16)), progress, cancellationToken),
                "workbench_storage" => await service.GetStorageAsync(dataRoot, cancellationToken),
                "workbench_library" => await service.GetLibraryAsync(dataRoot, cancellationToken, new(GetString(args, "query"), GetString(args, "kind", "all"), GetInt(args, "offset", 0), GetInt(args, "limit", 48))),
                "workbench_search_models" => new { models = await service.SearchAsync(GetString(args, "query"), GetString(args, "task"), cancellationToken) },
                "workbench_model_details" => await service.GetDetailsAsync(GetString(args, "repoId"), cancellationToken),
                "workbench_install_model" when allowSetup => await service.InstallAsync(dataRoot, progress, cancellationToken),
                "workbench_generate_image" when allowInference => await GenerateAsync(args, progress, cancellationToken),
                "workbench_upscale_image" when allowInference => await service.UpscaleAsync(dataRoot, (await SourceImage(args, true, cancellationToken))!, GetInt(args, "scale", 4), progress, cancellationToken),
                _ => throw new InvalidOperationException("Tool is disabled.")
            };
            Volatile.Write(ref _operationRunning, 0); // A client may issue its next call as soon as it receives this response.
            Result(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, Wire.Json) } }, structuredContent = result, isError = false });
        }
        catch (Exception ex)
        {
            string message = ex is OperationCanceledException ? "Operation cancelled. Check model status/Library for any interrupted work." : ex.Message;
            Volatile.Write(ref _operationRunning, 0);
            Result(id, new { content = new[] { new { type = "text", text = message } }, isError = true });
        }
    }
    private long _progressSequence;
    private sealed class DirectProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => report(value); }
}
