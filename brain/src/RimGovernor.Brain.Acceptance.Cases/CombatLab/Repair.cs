
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/repair</c> (#900): drafted Repair. On lab-choke with a damaged wooden door in the gap, a drafted brawler's
/// repair order on the door applies with job Repair, and the paused game reads that brawler on Repair of the door, still drafted.
/// </summary>
internal static class CombatLabRepair
{
    /// <summary>The gap door's hit points: well under any door's maximum, so it reads damaged.</summary>
    private const int DoorHitPoints = 10;

    // lab-choke's gap closed with a damaged wooden door: the last fixture thing.
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-choke", (f, cx, cz) =>
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz + CombatFixtures.ChokeHalf, HitPoints = DoorHitPoints }));

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/repair",
        Scope = "Drafted Repair (#900), a native op contract no snapshot can prove: on lab-choke with a damaged wooden door in the gap, the "
            + "brawlers are drafted and a combat_orders repair order on the door applies with job Repair (the first brawler capable of it); the "
            + "paused pawn read then gives that brawler job Repair on the door, still drafted.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var door = lab.Thing(Fixture.Things.Count - 1);
        var cell = lab.Cell(0, CombatFixtures.ChokeHalf);
        var brawlers = lab.Colonists().GetRange(0, 3);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, brawlers, cancellationToken).ConfigureAwait(false);

        // A lab colonist may be incapable of construction (cannot_repair): the first brawler whose order applies is the repairer.
        string? repairer = null;
        var tried = new List<RimGovernor.Brain.Domain.CombatResult>();
        for (var i = 0; i < brawlers.Count && repairer is null; i++)
        {
            var r = (await CombatLabKit.OrdersAsync(s, $"repair-{i}", cancellationToken, CombatLabKit.Order(brawlers[i], new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RepairCase(new RimGovernor.Brain.GameOrders.Operations.CombatRepair { Cell = cell }))).ConfigureAwait(false))[0];
            tried.Add(r);
            if (r.Applied == true)
            {
                CombatLabKit.Applied($"brawler {i}", r, "Repair");
                repairer = brawlers[i];
            }
        }

        if (repairer is null)
        {
            throw new AcceptanceException($"no brawler took the repair order on the door at {CombatLabKit.Show(cell)}: {string.Join("; ", tried)}");
        }

        var p = await CombatLabKit.PawnAsync(s, "repairer", repairer, cancellationToken).ConfigureAwait(false);
        s.RecordWire("repairer", p);
        if (CombatLabKit.Job(p) != "Repair" || CombatLabKit.JobThing(p) != door || !p.Drafted)
        {
            throw new AcceptanceException($"repairer {repairer}: want drafted on Repair of {door}, got drafted {p.Drafted} job {p.Job}");
        }
    }
}
