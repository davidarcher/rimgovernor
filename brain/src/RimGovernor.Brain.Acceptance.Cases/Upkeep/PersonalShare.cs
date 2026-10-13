using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/personal-share-rich</c> and <c>upkeep/personal-share-poor</c> (Go <c>personal_share.go</c>): the same bare
/// greedy bedroom and the same gear staging run twice, once in a colony stocked with gold and food and once in one
/// stripped of every loose item but wood and food (a poor colony; the equipment goal needs Stable, so it is not
/// Foothold). Each case asserts its own absolute outcome against the shared thresholds below, so the pair diverges
/// only through the colonist's personal wealth share. Each game runs once.
///
/// Staged on the first colonist (the greedy bedroom's owner): a shirt, pants and a flak vest worn, an excellent plate
/// armor on the ground, and a bare bedroom. The second colonist is stripped of all apparel with a basic shirt and
/// pants on the ground (a necessity, never charged).
/// </summary>
internal static class UpkeepPersonalShare
{
    /// <summary>The armor rung the rich colonist wears: plate armor over a flak vest (rung 1).</summary>
    private const int ShareArmorRich = 2;

    /// <summary>How long the poor colony runs after its goals recover, so a gate that leaked would have spent by then.</summary>
    private static readonly TimeSpan s_poorSettle = TimeSpan.FromMinutes(3);

    /// <summary>Ranks the torso armor the fixture offers.</summary>
    private static readonly Dictionary<string, int> s_armorRung = new(StringComparer.Ordinal)
    {
        ["Apparel_FlakVest"] = 1,
        ["Apparel_PlateArmor"] = ShareArmorRich,
    };

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return Build(
            "upkeep/personal-share-rich",
            "rich",
            "Personal wealth shares (#1829): in a colony rich in items the greedy colonist's bedroom reaches the shared impressiveness stage and the plate armor replaces the flak vest, while the bare colonist is dressed.",
            "Bedroom furniture and floor builds, a gear swap, then native room stats and worn apparel.");
        yield return Build(
            "upkeep/personal-share-poor",
            "poor",
            "Personal wealth shares (#1829): in a poor colony the same bedroom stays under the shared stage and the flak vest is kept, yet the bed stays owned and the bare colonist is dressed (necessities are never charged).",
            "Same staging as the rich case, with a settle wait after the standards recover to show the gates hold.");
    }

    private static AcceptanceCase Build(string name, string wealth, string scope, string reason) => new()
    {
        Name = name,
        Scope = scope,
        Start = new LabStart(),
        RequiredOps = ["test/sleeping_setup", "test/sleeping_greedy_status", "test/gear_fixture"],
        Keep = UpkeepSupport.SleepingKeep,
        Serve = new ServeSpec { Families = ["sleeping", "flooring", "gear", "work"], Prefix = UpkeepSupport.Prefix },
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Reason = reason,
        Run = (s, ct) => RunAsync(s, wealth, ct),
    };

    private static async Task RunAsync(ISession s, string wealth, CancellationToken cancellationToken)
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

        var staged = await UpkeepSupport.CallFixtureAsync(h, identity, "test/gear_fixture", new JsonObject { ["mode"] = "share_setup", ["pawn"] = pawn, ["wealth"] = wealth }, cancellationToken).ConfigureAwait(false);
        report["staged"] = staged;
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            using var journal = await UpkeepHomeCoverage.ServeStageAsync(s, service, report, cancellationToken).ConfigureAwait(false);
            using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recoverCts.CancelAfter(TimeSpan.FromMinutes(25));
            foreach (var need in new[] { PolicyFunctions.MaintainHousing, PolicyFunctions.MaintainEquipment })
            {
                _ = await UpkeepSupport.WaitNeedAsync(journal, need, Finding.Met, recoverCts.Token).ConfigureAwait(false);
            }

            if (wealth == "poor")
            {
                await Task.Delay(s_poorSettle, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        h = await UpkeepCampaign.ReattachPausedAsync(s, cancellationToken).ConfigureAwait(false);
        var read = await UpkeepSupport.CallFixtureAsync(h, identity, "test/gear_fixture", new JsonObject { ["mode"] = "share_read" }, cancellationToken).ConfigureAwait(false);
        report["read"] = read;
        var status = await UpkeepSupport.CallFixtureAsync(h, identity, "test/sleeping_greedy_status", new JsonObject { ["pawn"] = pawn }, cancellationToken).ConfigureAwait(false);
        report["status"] = status;
        var impressiveness = status["impressiveness"].AsNumber();
        var rung = 0;
        var quality = "";
        foreach (var row in read["worn"].AsSlice())
        {
            var m = row.AsMap();
            if (s_armorRung.GetValueOrDefault(m?["defName"].AsString() ?? "") is var r && r > rung)
            {
                rung = r;
                quality = m!["quality"].AsString();
            }
        }

        report["armor_rung"] = rung;
        report["armor_quality"] = quality;
        // Necessities are never charged, rich or poor: the bare colonist wears the basic outfit and the greedy
        // colonist still sleeps in an owned bed.
        if (read["bareWorn"].AsSlice().Count == 0)
        {
            throw new AcceptanceException($"the bare colonist was left undressed: {read["bareWorn"].ToJson()}");
        }

        if (status["room"].AsString().Length == 0)
        {
            throw new AcceptanceException($"the greedy colonist's bed sits in no room: {status.ToJson()}");
        }

        if (rung == 0)
        {
            throw new AcceptanceException($"the greedy colonist wears no torso armor at all: {read["worn"].ToJson()}");
        }

        var levels = UpkeepSupport.SlightlyImpressive;
        if (wealth == "rich")
        {
            if (impressiveness < levels)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rich colony bedroom impressiveness {impressiveness:F1}, want >= {levels:F0}"));
            }

            if (rung < ShareArmorRich)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rich colony armor rung {rung} ({quality}), want >= {ShareArmorRich}"));
            }
        }
        else
        {
            if (impressiveness >= levels)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"poor colony bedroom impressiveness {impressiveness:F1} reached the rich stage {levels:F0}"));
            }

            if (rung >= ShareArmorRich)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"poor colony wears the plate armor (rung {rung}, {quality})"));
            }
        }
    }
}
