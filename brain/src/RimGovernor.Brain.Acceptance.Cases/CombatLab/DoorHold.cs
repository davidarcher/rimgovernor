
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/door-hold</c> (#1155, epic #929): a forbidden door holds colony animals. lab-manhunter's colonist and
/// huskies are walled in a granite room whose one door faces the wargs; with the door forbidden and both huskies zoned
/// outside it they stay in while the wargs are on the map, and once the door is allowed a husky walks out.
/// </summary>
internal static class CombatLabDoorHold
{
    // The room: a granite ring around the colonist and huskies, one steel door in its north wall facing the wargs.
    private const int HalfX = 3;
    private const int HalfZ = 2;

    /// <summary>How long the forbidden door must hold at least; the wargs reach the room inside it.</summary>
    private const int HoldTicks = 600;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-manhunter", (f, cx, cz) =>
    {
        for (var x = cx - HalfX; x <= cx + HalfX; x++)
        {
            for (var z = cz - HalfZ; z <= cz + HalfZ; z++)
            {
                if (x == cx && z == cz + HalfZ)
                {
                    f.Things.Add(new FixtureThing { Def = "Door", Stuff = "Steel", X = x, Z = z });
                }
                else if (x == cx - HalfX || x == cx + HalfX || z == cz - HalfZ || z == cz + HalfZ)
                {
                    f.Things.Add(new FixtureThing { Def = "Wall", Stuff = "BlocksGranite", X = x, Z = z });
                }
            }
        }
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/door-hold",
        Scope = "A forbidden door holds colony animals (#1155, epic #929): lab-manhunter with its colonist and both huskies walled in a granite "
            + "room whose one door faces the wargs; one combat_orders batch zones both huskies onto a cell outside it and forbids the door (all "
            + "applied). For at least 600 ticks, while a warg is alive on the map, both huskies stay inside the room and the door reads forbidden; "
            + "a second order allows the door and a husky then walks out.",
        Ticks = 2 * (HoldTicks + CombatLabKit.ProbeTicks),
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),

        // The wargs are manhunters pressing on the room; a quiet world would freeze them and the hold would prove nothing.
        QuietWorld = false,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var animals = lab.Animals();
        var wargs = lab.Manhunters();
        if (animals.Count != 2 || wargs.Count != 2)
        {
            throw new AcceptanceException($"staged {animals.Count} animals and {wargs.Count} manhunters, want 2 and 2");
        }

        var door = lab.Cell(0, HalfZ);
        bool Inside(RimGovernor.Brain.Domain.Cell? c) =>
            c is { X: { } x, Z: { } z } && x > lab.Cx - HalfX && x < lab.Cx + HalfX && z > lab.Cz - HalfZ && z < lab.Cz + HalfZ;

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        // Areas first: a forbidden door makes the outside cell unreachable, and the area order is refused for it.
        var outside = lab.Cell(0, HalfZ + 3);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "hold",
            cancellationToken,
            [
                .. animals.Select(a => CombatLabKit.Order(a, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalArea { Area = new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase(outside) }))),
                CombatLabKit.Order(null, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = door, Mode = RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Forbid })),
            ]).ConfigureAwait(false);
        foreach (var r in results)
        {
            CombatLabKit.Applied("hold", r);
        }

        // Every probe of the hold checks the door, a warg and both huskies, until the hold has lasted HoldTicks.
        var start = await CombatLabKit.TickAsync(s, "hold-start", cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("hold", HoldTicks + CombatLabKit.ProbeTicks, async ct =>
        {
            var tick = await CombatLabKit.TickAsync(s, "hold-tick", ct).ConfigureAwait(false);
            if ((await CombatLabKit.BuildingAtAsync(s, "hold-door", door, ct).ConfigureAwait(false)).Settings.Forbidden != true)
            {
                throw new AcceptanceException($"tick +{tick - start}: door not forbidden");
            }

            var pawns = await CombatLabKit.PawnsAsync(s, "hold-pawns", [.. animals, .. wargs], ct).ConfigureAwait(false);
            if (!wargs.Any(w => pawns.GetValueOrDefault(w) is { Dead: false }))
            {
                throw new AcceptanceException($"tick +{tick - start}: no warg left on the map, the hold proves nothing");
            }

            foreach (var a in animals)
            {
                if (pawns.GetValueOrDefault(a) is not { } p || !Inside((p.Pawn.Place as RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)?.Cell))
                {
                    throw new AcceptanceException($"tick +{tick - start}: husky {a} left the room through the forbidden door: {CombatLabKit.Show((pawns.GetValueOrDefault(a)?.Pawn.Place as RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)?.Cell)}");
                }
            }

            return (Wait.Signature(tick), tick - start >= HoldTicks);
        }, cancellationToken).ConfigureAwait(false);

        // The control: with the door allowed the zoned huskies leave, so the hold was the door's.
        CombatLabKit.Applied("allow", await CombatLabKit.OrderAsync(s, "allow", null, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = door, Mode = RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Allow }), cancellationToken).ConfigureAwait(false));
        _ = await s.RunUntilAsync("leave", HoldTicks + CombatLabKit.ProbeTicks, async ct =>
        {
            var pawns = await CombatLabKit.PawnsAsync(s, "leave-pawns", animals, ct).ConfigureAwait(false);
            var cells = animals.Select(a => (pawns.GetValueOrDefault(a)?.Pawn.Place as RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)?.Cell).ToList();
            return (Wait.Signature(string.Join(",", cells.Select(CombatLabKit.Show))), cells.Any(c => c is not null && !Inside(c)));
        }, cancellationToken).ConfigureAwait(false);
    }
}
