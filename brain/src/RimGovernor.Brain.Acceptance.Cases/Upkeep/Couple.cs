using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/couple</c> (Go <c>couple.go</c>): two bedless lovers beside one vacant double bed. the shelter family's sleeping Need must
/// assign both to it through AssignIntents and recovers only once each is observed sleeping there, so neither sleeps
/// alone.
/// </summary>
internal static class UpkeepCouple
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/couple",
        Scope = "Couples share a bedroom (#812): two willing lovers are both assigned one vacant double bed via AssignIntents and the shelter family's sleeping Need recovers on their observed shared sleep.",
        Start = new LabStart(),
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["sleeping", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 3 },
        Reason = "Two assignments, then a night of observed shared sleep.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        if (!Dyn.Contains(s.Names, "test/sleeping_setup"))
        {
            throw new AcceptanceException("missing test/sleeping_setup");
        }

        var h = s.Harness;
        var prepared = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_setup", new JsonObject { ["couple"] = true }, cancellationToken).ConfigureAwait(false);
        report["prepared"] = prepared;
        var pawn = prepared["pawn"].AsString();
        var partner = prepared["partner"].AsString();
        var doubleBed = prepared["doubleBed"].AsString();
        if (pawn.Length == 0 || partner.Length == 0 || doubleBed.Length == 0)
        {
            throw new AcceptanceException($"fixture returned no couple: {prepared.ToJson()}");
        }

        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            using var journal = await UpkeepHomeCoverage.ServeStageAsync(s, service, report, cancellationToken).ConfigureAwait(false);
            using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recoverCts.CancelAfter(TimeSpan.FromMinutes(15));
            await UpkeepSupport.WaitNeedAsync(journal, ShelterFamily.Sleeping, Finding.Met, recoverCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        var observed = await UpkeepSupport.ReadColonyFactsAsync(h, identity, "beds-after", cancellationToken).ConfigureAwait(false);
        var upkeep = UpkeepSupport.UpkeepSection(observed, "beds-after");
        foreach (var raw in upkeep["beds"].AsSlice())
        {
            var row = raw.AsMap();
            if (row?["bed"].AsMap()?["id"].AsString() != doubleBed)
            {
                continue;
            }

            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in row["owners"].AsSlice())
            {
                owners.Add(o.RefId());
            }

            report["double_bed_owners"] = row["owners"]?.DeepClone();
            if (!owners.Contains(pawn) || !owners.Contains(partner) || owners.Count != 2)
            {
                throw new AcceptanceException($"double bed {doubleBed} owned by {row["owners"].ToJson()}, want the couple {pawn} and {partner}");
            }

            return;
        }

        throw new AcceptanceException($"double bed {doubleBed} missing after recovery");
    }
}
