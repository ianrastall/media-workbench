using System.Text.Json;
using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;

if (args.Length < 3)
{
    Console.WriteLine("Usage: MediaWorkbench.Cli <absolute-python.exe> <scaffold-root> health|catalog|demo|plan [absolute-demo-output-directory]");
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
    var operation = args[2] switch { "health" => "health", "catalog" => "catalog.list", "demo" => "demo.run", _ => throw new ArgumentException("Unknown command") };
    if (operation == "demo.run" && (args.Length < 4 || !Path.IsPathFullyQualified(args[3])))
        throw new ArgumentException("Demo requires an explicit absolute output directory.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    await using var worker = await PythonWorkerClient.StartAsync(args[0], root, cancellation.Token);
    var result = await worker.ExecuteAsync(operation,
        new { label = "Protocol demonstration — no image generation", outputDirectory = args.Length > 3 ? args[3] : "", steps = 5, delayMs = 50 },
        e => Console.WriteLine(JsonSerializer.Serialize(e, Wire.Json)), cancellation.Token);
    return result.Kind == "result" ? 0 : 1;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
