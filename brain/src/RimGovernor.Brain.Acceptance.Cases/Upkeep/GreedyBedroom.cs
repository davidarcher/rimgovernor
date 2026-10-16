using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/greedy-bedroom</c> (Go <c>greedy_bedroom.go</c>): a Greedy colonist owns a bare 5x4 bedroom. The room
/// quality gap closer furnishes it from the bedroom template (end table, dresser, lamp) and the rooms/flooring Need floors it
/// until the native room reads slightly impressive (50) and the Greedy thought clears.
/// </summary>
internal static class UpkeepGreedyBedroom
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/greedy-bedroom",
        Scope = "Room quality gap closer (#814): a Greedy colonist's bare bedroom is furnished and floored until it reads slightly impressive and the Greedy thought clears.",
        Start = new LabStart(),
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["sleeping", "flooring", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Reason = "Three furniture builds and a floor, then the native room stat.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var h = s.Harness;
        var prepared = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_setup", new JsonObject { ["greedy"] = true }, cancellationToken).ConfigureAwait(false);
        report["prepared"] = prepared;
        var pawn = prepared["pawn"].AsString();
        if (pawn.Length == 0 || prepared["greedyBed"].AsString().Length == 0)
        {
            throw new AcceptanceException($"fixture returned no greedy bedroom: {prepared.ToJson()}");
        }

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
        var status = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_greedy_status", new JsonObject { ["pawn"] = pawn }, cancellationToken).ConfigureAwait(false);
        report["status"] = status;
        var impressiveness = status["impressiveness"].AsNumber();
        if (impressiveness < UpkeepSupport.SlightlyImpressive)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"greedy bedroom impressiveness {impressiveness:F1}, want >= {UpkeepSupport.SlightlyImpressive:F0}"));
        }

        if (status["greedyThought"].AsBool() == true)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"greedy thought still active at impressiveness {impressiveness:F1}"));
        }
    }
}
