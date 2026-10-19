namespace MediaWorkbench.Core;

/// <summary>Suggested picture controls. Runtime/precision and readiness remain owned by verified setup.</summary>
public sealed record PictureDefaults(int Steps, double GuidanceScale, string Sampler, int Width, int Height)
{
    public static PictureDefaults Small { get; } = new(20, 7.5, "pndm", 512, 512);
    public static PictureDefaults Large { get; } = new(20, 5, "pndm", 1024, 1024);
    public static PictureDefaults ForModel(bool usesXlSizes) => usesXlSizes ? Large : Small;
}
