using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/jealous-ascetic</c> (Go <c>jealous_ascetic.go</c>): a Jealous colonist owns a bare bedroom beside an
/// Ascetic colonist's room that already holds an end table. The jealous room is raised until BedroomJealous clears;
/// the ascetic room is capped below decent and must never rise.
/// </summary>
internal static class UpkeepJealousAscetic
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/jealous-ascetic",
        Scope = "Room quality targets (#813/#826): a Jealous colonist's bedroom is raised until the jealous thought clears, while an Ascetic colonist's room is never upgraded.",
        Start = new LabStart(),
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["sleeping", "flooring", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Reason = "Furniture and floor builds in the jealous room, then native room stats and thoughts.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var h = s.Harness;
        var prepared = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_setup", new JsonObject { ["jealous_ascetic"] = true }, cancellationToken).ConfigureAwait(false);
        report["prepared"] = prepared;
        var jealous = prepared["pawn"].AsString();
        var ascetic = prepared["ascetic"].AsString();
        if (jealous.Length == 0 || ascetic.Length == 0 || prepared["jealousBed"].AsString().Length == 0 || prepared["asceticBed"].AsString().Length == 0)
        {
            throw new AcceptanceException($"fixture returned no jealous/ascetic bedrooms: {prepared.ToJson()}");
        }

        var before = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_greedy_status", new JsonObject { ["pawn"] = ascetic, ["thought"] = "Ascetic" }, cancellationToken).ConfigureAwait(false);
        report["ascetic_before"] = before;
        var start = before["impressiveness"].AsNumber();
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            using var journal = await UpkeepHomeCoverage.ServeStageAsync(s, service, report, cancellationToken).ConfigureAwait(false);
            using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recoverCts.CancelAfter(TimeSpan.FromMinutes(25));
            await UpkeepSupport.WaitNeedAsync(journal, ShelterFamily.Sleeping, Finding.Met, recoverCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        var jstatus = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_greedy_status", new JsonObject { ["pawn"] = jealous, ["thought"] = "BedroomJealous" }, cancellationToken).ConfigureAwait(false);
        report["jealous_status"] = jstatus;
        var astatus = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_greedy_status", new JsonObject { ["pawn"] = ascetic, ["thought"] = "Ascetic" }, cancellationToken).ConfigureAwait(false);
        report["ascetic_status"] = astatus;
        if (jstatus["thoughtActive"].AsBool() == true)
        {
            throw new AcceptanceException($"jealous thought still active: jealous room {jstatus["impressiveness"].ToJson()}, ascetic room {astatus["impressiveness"].ToJson()}");
        }

        // Room stats drift slightly with dirt and wear; a controller upgrade (furniture or floor) moves
        // impressiveness by whole points.
        var end = astatus["impressiveness"].AsNumber();
        if (end > start + 0.5)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ascetic room impressiveness rose {start:F1} -> {end:F1}"));
        }
    }
}
