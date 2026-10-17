using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

if (args.Length < 3)
{
    Console.WriteLine("Usage: MediaWorkbench.Cli <absolute-python.exe> <application-root> health|catalog|demo|plan|install|status|generate|library [absolute-data-directory] [prompt] [steps]");
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
    if (args[2] == "install")
    {
        var plan = new WorkbenchService(root, args[0]).GetBaselinePlan();
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join("\n", plan.Blockers));
    }
    var operation = args[2] switch { "health" => "health", "catalog" => "catalog.list", "demo" => "demo.run",
        "install" => "installation.run", "status" => "installation.status", "generate" => "generate.image", "library" => "library.list",
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
