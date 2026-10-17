using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/repair</c> (Go <c>draft_resume.go</c>, the repair half).</summary>
internal static class CombatLabRepair
{
    // repairDoorHitPoints is the staged gap door's hit points: well under any door's maximum, so it reads damaged.
    private const int DoorHitPoints = 10;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/repair",
        Scope = "Drafted Repair (#900), a native op contract no snapshot can prove: on lab-choke with a damaged wooden door in the gap, "
            + "the brawlers are drafted and one combat.orders repair order on the door applies with job Repair; one tick later that brawler's job is Repair on the door and it is still drafted.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // repairKit closes lab-choke's gap with a damaged wooden door.
    private static void Kit(LabFixture f, int cx, int cz) =>
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz + CombatFixtures.ChokeHalf, HitPoints = DoorHitPoints });

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-choke", cancellationToken, (f, x, z) =>
        {
            Kit(f, x, z);
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var door = staged.Things[^1];
        var colonists = staged.Colonists();
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-repair-acquire", identity, cancellationToken).ConfigureAwait(false);

        // The three brawlers stand two cells inside the gap.
        var brawlers = colonists.GetRange(0, 3);
        await LabOps.DraftAllAsync(h, identity, "repair-draft", brawlers, cancellationToken).ConfigureAwait(false);

        // A lab colonist may be incapable of construction (cannot_repair): the first brawler whose order applies is the repairer.
        var repairer = "";
        var tried = new JsonArray();
        for (var i = 0; i < brawlers.Count; i++)
        {
            var id = brawlers[i];
            var results = await LabOps.IssueAsync(
                h,
                identity,
                "combat-repair-" + i.ToString(CultureInfo.InvariantCulture),
                new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(id), ["repair"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx, cz + CombatFixtures.ChokeHalf) } }),
                cancellationToken).ConfigureAwait(false);
            tried.Add(results[0].DeepClone());
            if (results[0]["applied"].AsBool() == true)
            {
                if (results[0]["jobDef"].AsString() != "Repair")
                {
                    throw new AcceptanceException($"brawler {CaseSupport.Tag(i)} repair applied with job {results[0]["jobDef"].ToJson()}, want Repair: {results[0].ToJson()}");
                }

                repairer = id;
                break;
            }
        }

        report["results"] = tried;
        if (repairer.Length == 0)
        {
            throw new AcceptanceException($"no brawler took the repair order on the door at {CaseSupport.Tag(cx)},{CaseSupport.Tag(cz + CombatFixtures.ChokeHalf)}: {tried.ToJson()}");
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var p = after.Row(repairer);
        report["repairer"] = p.DeepClone();
        if (p["job"].AsString() != "Repair" || p["jobThing"].AsString() != door)
        {
            throw new AcceptanceException($"repairer {repairer} after one tick: job {p["job"].ToJson()} on {p["jobThing"].ToJson()}, want Repair on {door}: {p.ToJson()}");
        }

        if (p["drafted"].AsBool() != true)
        {
            throw new AcceptanceException($"repairer {repairer} undrafted by the Repair job: {p.ToJson()}");
        }
    }
}
