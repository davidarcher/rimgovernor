using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The first-plan facts the starter-site survey read from the loaded game (Go <c>startersite.Survey</c>): the game tick, the colonist count and the scored map.</summary>
public sealed record StarterSiteSurvey(ulong Tick, long Colonists, MapSurvey Survey);

/// <summary>
/// The host-supplied planner behind the starter-site fixtures (Go package <c>startersite</c>): the layout plan derived
/// from the loaded game's map survey gives the fixture hut's site, and the survey itself feeds the layout audits. The
/// C# harness has no typed native client, so the host that owns the in-process observation layers assigns
/// <see cref="StarterSiteHost.Planner"/> and <see cref="Surveyor"/> (#3085, #3087); until it does the cases that need them fail with a
/// message naming the missing hook.
/// </summary>
public static class LayoutSiteHost
{
    /// <summary>Reads the loaded game's colony and scored map (Go <c>startersite.Survey</c>).</summary>
    public static Func<Harness, CancellationToken, Task<StarterSiteSurvey>>? Surveyor { get; set; }

    /// <summary>Go <c>startersite.Args</c>: a fixture start's <see cref="FixtureArgsFrom"/> for the 9x9 hut.</summary>
    internal static Task<JsonObject?> ArgsAsync(Harness harness, CancellationToken cancellationToken) =>
        Site().ArgsAsync(harness, cancellationToken);

    /// <summary>Go <c>startersite.BedroomArgs</c>: <see cref="ArgsAsync"/> plus the planned bedrooms.</summary>
    internal static Task<JsonObject?> BedroomArgsAsync(Harness harness, CancellationToken cancellationToken) =>
        Site().BedroomArgsAsync(harness, cancellationToken);

    /// <summary>Go <c>startersite.Survey</c>.</summary>
    internal static Task<StarterSiteSurvey> SurveyAsync(Harness harness, CancellationToken cancellationToken)
    {
        var survey = Surveyor
            ?? throw new AcceptanceException("no starter-site surveyor is wired for the in-game brain (StarterSiteHost.Surveyor): the case cannot read the loaded map's survey");
        return survey(harness, cancellationToken);
    }

    private static StarterSite Site() => new(
        StarterSiteHost.Planner
        ?? throw new AcceptanceException("no starter-site planner is wired for the in-game brain (StarterSiteHost.Planner): the case cannot place its fixture on the planned site"));
}
