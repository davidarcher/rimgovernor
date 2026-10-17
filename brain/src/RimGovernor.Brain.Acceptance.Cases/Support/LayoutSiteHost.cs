using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The host-supplied planner behind the starter-site fixtures (Go package <c>startersite</c>): the layout plan derived
/// from the loaded game's map survey gives the fixture hut's site. The host that owns the in-process observation layers
/// assigns <see cref="StarterSiteHost.Planner"/> (#3085, #3087); until it does the cases that need it fail with a message
/// naming the missing hook.
/// </summary>
public static class LayoutSiteHost
{
    /// <summary>Go <c>startersite.BedroomArgs</c>: a fixture start's <see cref="FixtureArgsFrom"/> for the 9x9 hut plus the planned bedrooms.</summary>
    internal static Task<JsonObject?> BedroomArgsAsync(Harness harness, CancellationToken cancellationToken) =>
        Site().BedroomArgsAsync(harness, cancellationToken);

    private static StarterSite Site() => new(
        StarterSiteHost.Planner
        ?? throw new AcceptanceException("no starter-site planner is wired for the in-game brain (StarterSiteHost.Planner): the case cannot place its fixture on the planned site"));
}
