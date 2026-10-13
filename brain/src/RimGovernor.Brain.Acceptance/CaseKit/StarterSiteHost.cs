namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The process-wide <see cref="StarterSite"/> a case's fixture start derives its hut arguments from (Go
/// <c>startersite.ArgsFor</c> was a package function over the typed native client). The harness has no typed client, so the
/// host assigns the <see cref="Planner"/> (#3085); until then a case that declares <see cref="ArgsFor"/> fails when its
/// start runs, naming the missing hook.
/// </summary>
public static class StarterSiteHost
{
    /// <summary>The planner that reads the loaded game and derives its first layout plan; assigned by the host.</summary>
    public static IStarterSitePlanner? Planner { get; set; }

    /// <summary>A fixture start's <see cref="FixtureArgsFrom"/> for a <paramref name="size"/> x <paramref name="size"/> fixture hut on the planned site.</summary>
    public static FixtureArgsFrom ArgsFor(int size) => (harness, cancellationToken) =>
    {
        var planner = Planner
            ?? throw new AcceptanceException("no starter-site planner is wired (StarterSiteHost.Planner): the fixture hut's site cannot be derived from the layout plan");
        return new StarterSite(planner).ArgsFor(size)(harness, cancellationToken);
    };
}
