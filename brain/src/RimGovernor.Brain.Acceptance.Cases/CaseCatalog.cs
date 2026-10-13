namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The registration entry point of the acceptance cases (Go: each area package's <c>init()</c>). The
/// <c>acceptance</c> command calls <see cref="Register"/> once at startup; a case area adds one call here
/// (<c>BuildCases.Register(registry)</c>) and registers its <see cref="AcceptanceCase"/> values through the public
/// <see cref="CaseRegistry"/> API only, never the harness internals.
/// </summary>
public static class CaseCatalog
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // The case areas land with #3090 and #3093; each area adds one line below (sorted by area).
        WallCases.Register(registry);
        ZoneCases.Register(registry);
    }
}
