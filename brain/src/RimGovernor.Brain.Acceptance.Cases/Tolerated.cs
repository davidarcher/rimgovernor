namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Records an exception a case deliberately tolerates (best-effort cleanup, a probe poll, optional evidence)
/// so it shows in the run's stderr instead of vanishing (arch rule 4).
/// </summary>
internal static class Tolerated
{
    internal static void Note(string what, Exception e) =>
        Console.Error.WriteLine($"tolerated: {what}: {e.GetType().Name}: {e.Message}");
}
