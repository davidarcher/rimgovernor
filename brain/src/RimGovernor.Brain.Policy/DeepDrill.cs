namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Adds the extraction ladder only while a measured runway of a resource the catalog generates as a deep
    /// deposit is in deficit. Explicit operator research still takes precedence.
    /// </summary>
    public static IReadOnlyList<string> DeepDrillingResearch(IReadOnlyList<string> needs, IReadOnlyList<ResourceRunway> runways, ItemFacts items)
    {
        foreach (var row in runways)
        {
            if (!items.IsDeepResource(row.Resource))
            {
                continue;
            }
            if (row.Deficit.TryGetValue(out var deficit) && deficit)
            {
                return [.. needs, "DeepDrilling", "GroundPenetratingScanner"];
            }
        }
        return needs;
    }
}
