using System.Text.Json;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

public sealed class InstallationPlanner : IInstallationPlanner
{
    public InstallationPlan Plan(ModelRecipe recipe) => NativeCore.PlanInstallation(recipe);

    public static IReadOnlyList<ModelRecipe> ReadCatalog(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "recipes"), "*.json")
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<ModelRecipe>(File.ReadAllText(path), Wire.Json)
                ?? throw new InvalidDataException($"Empty recipe: {path}"))
            .ToArray();
}
