using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

if (args.FirstOrDefault() == "mcp")
{
    try
    {
        string applicationRoot = AppContext.BaseDirectory;
        string? dataFolder = null;
        bool setup = false, inference = false;
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root" when index + 1 < args.Length: applicationRoot = Path.GetFullPath(args[++index]); break;
                case "--data-root" when index + 1 < args.Length: dataFolder = args[++index]; break;
                case "--allow-setup": setup = true; break;
                case "--allow-inference": inference = true; break;
                default: throw new ArgumentException("Unknown or incomplete MCP option: " + args[index]);
            }
        }
        if (dataFolder is null || !Path.IsPathFullyQualified(dataFolder)) throw new ArgumentException("MCP requires --data-root with an explicit absolute folder.");
        dataFolder = Path.GetFullPath(dataFolder);
        if (dataFolder.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(dataFolder)!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a data folder inside a drive, rather than the drive root.");
        if (!Directory.Exists(Path.Combine(applicationRoot, "recipes"))) throw new ArgumentException("The MCP application root must contain recipes.");
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        var service = new WorkbenchService(applicationRoot, Path.Combine(applicationRoot, "runtime", "python", "python.exe"));
        await new McpServer(service, dataFolder, setup, inference).RunAsync(Console.OpenStandardInput(), Console.Out, shutdown.Token);
        return 0;
    }
    catch (OperationCanceledException) { return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
}

if (args.Length < 3)
{
    Console.WriteLine("Usage: MediaWorkbench.Cli <absolute-python.exe> <application-root> health|catalog|demo|plan|install|status|generate|library|storage [absolute-data-directory] [prompt] [steps]");
    return 2;
}
try
{
    var root = Path.GetFullPath(args[1]);
    if (args[2] == "plan")
    {
        foreach (var recipe in InstallationPlanner.ReadCatalog(root))
            Console.WriteLine(JsonSerializer.Serialize(new InstallationPlanner().Plan(recipe), Wire.Json));
        return 0;
    }
    if (args[2] == "update-check")
    {
        // Optional: pretend to be an older version, and --download to fetch and verify the installer (never runs it).
        string version = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : UpdateService.ApplicationVersion();
        var updates = new UpdateService(UpdateService.CreateClient(version), version);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var update = await updates.CheckAsync(limit.Token);
        Console.WriteLine(update is null ? $"{version} is up to date." : $"{update.Version} is available: {update.PageUrl}");
        if (update is not null && args.Contains("--download"))
            Console.WriteLine("Verified installer: " + await updates.DownloadAsync(update, new Progress<OperationProgress>(), limit.Token));
        return 0;
    }
    if (args[2] == "install")
    {
        var plan = new WorkbenchService(root, args[0]).GetBaselinePlan();
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("\n", plan.Blockers));
    }
    var operation = args[2] switch { "health" => "health", "catalog" => "catalog.list", "demo" => "demo.run",
        "install" => "installation.run", "status" => "installation.status", "generate" => "generate.image", "library" => "library.list", "storage" => "storage.summary",
        _ => throw new ArgumentException("Unknown command") };
    if (operation == "demo.run" && (args.Length < 4 || !Path.IsPathFullyQualified(args[3])))
        throw new ArgumentException("Demo requires an explicit absolute output directory.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(4));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    await using var worker = await PythonWorkerClient.StartAsync(args[0], root, cancellation.Token);
    var result = await worker.ExecuteAsync(operation,
        new { label = "Protocol demonstration — no image generation", outputDirectory = args.Length > 3 ? args[3] : "",
            dataRoot = args.Length > 3 ? args[3] : "", prompt = args.Length > 4 ? args[4] : "A red ceramic house beside a green tree, watercolor illustration, daylight",
            negativePrompt = "", seed = 20261017, steps = args.Length > 5 ? int.Parse(args[5]) : 20, delayMs = 50 },
        e => Console.WriteLine(JsonSerializer.Serialize(e, Wire.Json)), cancellation.Token);
    return result.Kind == "result" ? 0 : 1;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
