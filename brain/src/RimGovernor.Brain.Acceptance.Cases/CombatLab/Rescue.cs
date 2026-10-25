
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/rescue</c> (#867, #881): rescue under fire. On lab-open with colonist 2 downed, a bed and a player door
/// behind the line, the geometry read's rescue_path route reaches the downed colonist with line-of-fire flags that agree
/// with their lines, inside the main-thread budget once warm; a rescue order applies, a rescue of a standing raider
/// refuses cannot_rescue, and the door forbids and allows.
/// </summary>
internal static class CombatLabRescue
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, cx, cz) =>
    {
        // A downed pawn drops its weapon: stage it unarmed.
        foreach (var p in f.Pawns.Where(p => p.Side == CombatFixtures.Colonist && p.Index == 2))
        {
            p.Downed = true;
            p.Weapon = "";
            p.WeaponStuff = "";
        }

        f.Things.Add(new FixtureThing { Def = "Bed", Stuff = "WoodLog", X = cx - 6, Z = cz - 16 });
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz - 13 });
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/rescue",
        Scope = "Rescue under fire (#867), a native op contract no snapshot can prove: on lab-open with colonist 2 downed, a bed and a player door "
            + "behind the line, combat_geometry's rescue_path role returns colonist 0's pathfinder route ending beside the downed colonist, every "
            + "cell's hostile line-of-fire flag agreeing with its lines, and a second (warm) read within the 50 ms main-thread budget (#881); one "
            + "combat_orders batch forbids the door, orders colonist 0 to rescue colonist 2 (Rescue) and refuses colonist 1's rescue of a standing "
            + "raider (cannot_rescue); the paused game reads colonist 0 on Rescue of colonist 2 and the door forbidden, and an allow order clears it.",
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
        var colonists = lab.Colonists();
        var hostiles = lab.Hostiles();
        if (colonists.Count != 3 || hostiles.Count != 3)
        {
            throw new AcceptanceException($"staged {colonists.Count} colonists and {hostiles.Count} hostiles, want 3 and 3");
        }

        var downed = Fixture.Pawns.Last(p => p.Side == CombatFixtures.Colonist && p.Index == 2);
        await RescuePathAsync(s, colonists[0], lab.At(downed.X, downed.Z), hostiles, cancellationToken).ConfigureAwait(false);

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, colonists.GetRange(0, 2), cancellationToken).ConfigureAwait(false);
        var door = lab.Cell(0, -13);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "rescue",
            cancellationToken,
            CombatLabKit.Order(null, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = door, Mode = RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Forbid })),
            CombatLabKit.Order(colonists[0], new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RescueCase(new RimGovernor.Brain.GameOrders.Operations.CombatRescue { Bed = new RimGovernor.Brain.GameOrders.Operations.RescueBed.Search(), Downed = CombatLabKit.Pawn(colonists[2]) })),
            CombatLabKit.Order(colonists[1], new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RescueCase(new RimGovernor.Brain.GameOrders.Operations.CombatRescue { Bed = new RimGovernor.Brain.GameOrders.Operations.RescueBed.Search(), Downed = CombatLabKit.Pawn(hostiles[0]) }))).ConfigureAwait(false);
        CombatLabKit.Applied("forbid", results[0]);
        CombatLabKit.Applied("rescue", results[1], "Rescue");
        CombatLabKit.Refused("rescue raider", results[2], "cannot_rescue");

        var rescuer = await CombatLabKit.PawnAsync(s, "rescuer", colonists[0], cancellationToken).ConfigureAwait(false);
        if (CombatLabKit.Job(rescuer) != "Rescue" || CombatLabKit.JobThing(rescuer) != colonists[2])
        {
            throw new AcceptanceException($"colonist 0 after rescue: want Rescue of {colonists[2]}, got {rescuer.Job}");
        }

        await ForbiddenAsync(s, door, true, cancellationToken).ConfigureAwait(false);
        CombatLabKit.Applied("allow", await CombatLabKit.OrderAsync(s, "allow", null, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = door, Mode = RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Allow }), cancellationToken).ConfigureAwait(false));
        await ForbiddenAsync(s, door, false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ForbiddenAsync(TypedSession s, RimGovernor.Brain.Domain.Cell door, bool want, CancellationToken cancellationToken)
    {
        var forbidden = (await CombatLabKit.BuildingAtAsync(s, want ? "door-forbidden" : "door-allowed", door, cancellationToken).ConfigureAwait(false)).Settings.Forbidden;
        if (forbidden != want)
        {
            throw new AcceptanceException($"door forbidden {forbidden} after {(want ? "forbid" : "allow")}");
        }
    }

    // The rescue_path role: a route from the rescuer to the downed colonist, ending next to it, whose every cell's hostile
    // line-of-fire flag is the OR of its lines; the second (warm) read fits the main-thread budget.
    private static async Task RescuePathAsync(TypedSession s, string rescuer, RimGovernor.Brain.Domain.Cell downed, IReadOnlyList<string> hostiles, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest
        {
            Identity = s.Identity,
            Cells = [],
            HostileIds = CombatLabKit.PawnIds(hostiles),
            Propose = new RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose.RescuePath { Pawn = new RimGovernor.Brain.Domain.PawnId(rescuer), To = downed },
        };

        // The first read on a fresh game pays one-time costs; the second, warm, must fit the budget.
        var cold = await CombatLabKit.GeometryAsync(s, "rescue-path", request, cancellationToken).ConfigureAwait(false);
        var g = await CombatLabKit.GeometryAsync(s, "rescue-path-warm", request, cancellationToken).ConfigureAwait(false);
        if (g.MainThreadMs > CombatGeometryContract.BudgetMs)
        {
            throw new AcceptanceException($"warm rescue_path read took {g.MainThreadMs} ms of main thread (cold {cold.MainThreadMs} ms), over {CombatGeometryContract.BudgetMs} ms");
        }

        var route = g.Proposed;
        for (var i = 0; i < route.Count; i++)
        {
            var any = route[i].Lines.Any(l => l.LineOfFire);
            if (route[i].HostileLineOfFire != any)
            {
                throw new AcceptanceException($"route cell {i} line-of-fire flag {route[i].HostileLineOfFire} disagrees with its lines: {route[i]}");
            }
        }

        s.Record("rescue_path_cells", route.Count);
        if (route.Count == 0 || CombatLabKit.Chebyshev(route[^1].Cell, downed) > 1)
        {
            throw new AcceptanceException($"rescue route of {route.Count} cells does not end next to {CombatLabKit.Show(downed)}: last {CombatLabKit.Show(route.Count == 0 ? null : route[^1].Cell)}");
        }
    }
}
