using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/orders</c> (#850): batched combat orders. On lab-open with five drafted colonists (one with frag grenades)
/// and a player door, one batch orders attack_ground, move, attack, fire_mode hold, hold_position, move-then-stop and
/// door hold_open, plus an unowned hostile and a non-door cell: every order applies or refuses with its named reason,
/// and the paused game reads each job, target and the door back as ordered. A pawn's passage opens the held door, and
/// a close order clears the latch and shuts it once the pawn has gone.
/// </summary>
internal static class CombatLabOrders
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, cx, cz) =>
    {
        // Two more riflemen (hold and stop), frag grenades on colonist 0 (attack_ground) and a door behind the line.
        f.Colonists = 5;
        foreach (var p in f.Pawns.Where(p => p.Side == CombatFixtures.Colonist && p.Index == 0))
        {
            p.Weapon = "Weapon_GrenadeFrag";
            p.WeaponStuff = "";
        }

        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Colonist, Index = 3, X = cx - 5, Z = cz - 10, Weapon = CombatFixtures.Rifle });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Colonist, Index = 4, X = cx + 5, Z = cz - 10, Weapon = CombatFixtures.Rifle });
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz - 13 });
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/orders",
        Scope = "Batched combat orders (#850), a native op contract no unit test reaches: on lab-open (five drafted colonists, one with frag "
            + "grenades, a player door) one operations_apply batch orders attack_ground, move, attack, fire_mode hold, hold_position, move-then-stop "
            + "and door hold_open, plus an unowned hostile and a non-door cell; every order applies or refuses with its named reason, the paused "
            + "list reads give each pawn's job, target and player-forced flag and the door's latch (held open, not opened) as ordered, the held "
            + "pawn's combat row reads fire mode hold, a pawn's passage opens the held door, and a close order unlatches and shuts it once the "
            + "pawn has gone.",
        Ticks = (3 * CombatLabKit.ProbeTicks) + 600,
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
        if (colonists.Count != 5 || hostiles.Count != 3)
        {
            throw new AcceptanceException($"staged {colonists.Count} colonists and {hostiles.Count} hostiles, want 5 and 3");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var drafted = await CombatLabKit.OrdersAsync(s, "draft", cancellationToken, [.. colonists.Select(c => CombatLabKit.Order(c, new Operations.CombatOrderOrder.DraftCase(new Operations.Clear())))]).ConfigureAwait(false);
        foreach (var r in drafted)
        {
            CombatLabKit.Applied("draft", r);
        }

        var ground = lab.Cell(-2, -3);
        var moveTo = lab.Cell(7, -12);
        var stopFrom = lab.Cell(5, -15);
        var door = lab.Cell(0, -13);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "orders",
            cancellationToken,
            CombatLabKit.Order(colonists[0], new Operations.CombatOrderOrder.AttackGroundCase(ground)),
            CombatLabKit.Order(colonists[1], new Operations.CombatOrderOrder.MoveCase(moveTo)),
            CombatLabKit.Order(colonists[2], new Operations.CombatOrderOrder.AttackCase(CombatLabKit.Pawn(hostiles[2]))),
            CombatLabKit.Order(colonists[3], new Operations.CombatOrderOrder.FireModeCase(Operations.CombatFireMode.Hold)),
            CombatLabKit.Order(colonists[3], new Operations.CombatOrderOrder.HoldPositionCase(new Operations.Clear())),
            CombatLabKit.Order(colonists[4], new Operations.CombatOrderOrder.MoveCase(stopFrom)),
            CombatLabKit.Order(colonists[4], new Operations.CombatOrderOrder.StopCase(new Operations.Clear())),
            CombatLabKit.Order(null, new Operations.CombatOrderOrder.DoorCase(new Operations.CombatDoor { Cell = door, Mode = Operations.CombatDoorMode.HoldOpen })),
            CombatLabKit.Order(hostiles[0], new Operations.CombatOrderOrder.MoveCase(moveTo)),
            CombatLabKit.Order(null, new Operations.CombatOrderOrder.DoorCase(new Operations.CombatDoor { Cell = lab.Cell(10, 0), Mode = Operations.CombatDoorMode.Close })))
            .ConfigureAwait(false);
        string[] wantJob = ["AttackStatic", "Goto", "AttackStatic", "", "Wait_Combat", "Goto", "", ""];
        for (var i = 0; i < wantJob.Length; i++)
        {
            CombatLabKit.Applied($"order {i}", results[i], wantJob[i]);
        }

        CombatLabKit.Refused("order 8", results[8], "not_drafted");
        CombatLabKit.Refused("order 9", results[9], "not_a_door");

        // The orders took their jobs on the paused game: read them before a tick moves anyone.
        var after = await CombatLabKit.PawnsAsync(s, "after", colonists, cancellationToken).ConfigureAwait(false);
        s.RecordWireRows("after", after.Values.OrderBy(p => p.Pawn.Id, StringComparer.Ordinal));
        Observations.PawnState Row(int i) => after.GetValueOrDefault(colonists[i]) ?? throw new AcceptanceException($"colonist {i} not listed");
        void Check(int i, string job, bool forced, string? thing = null, Common.Cell? cell = null)
        {
            var p = Row(i);
            if (CombatLabKit.Job(p) != job || p.Job.PlayerForced != forced
                || (thing is not null && CombatLabKit.JobThing(p) != thing) || (cell is not null && p.Job.TargetACell != cell))
            {
                throw new AcceptanceException($"colonist {i}: want job {job} forced {forced} target {thing ?? CombatLabKit.Show(cell)}, got {p.Job}");
            }
        }

        Check(0, "AttackStatic", true, cell: ground);
        Check(1, "Goto", true, cell: moveTo);
        Check(2, "AttackStatic", true, thing: hostiles[2]);
        Check(3, "Wait_Combat", true);
        if (CombatLabKit.Job(Row(4)) == "Goto" || Row(4).Job.PlayerForced)
        {
            throw new AcceptanceException($"colonist 4 still holds a forced move after stop: {Row(4).Job}");
        }

        var latched = await CombatLabKit.BuildingAtAsync(s, "door-latched", door, cancellationToken).ConfigureAwait(false);
        if (latched.Door is not { HoldOpen: true, Open: false })
        {
            throw new AcceptanceException($"door after hold_open: want held open and still shut, got {latched.Door}");
        }

        var held = await new LabFrames(s).UntilAsync("fire-mode", CombatLabKit.ProbeTicks, 0, f => LabFrames.Row(f, colonists[3]) is not null, cancellationToken).ConfigureAwait(false);
        if (LabFrames.Row(held, colonists[3]) is not { Draft.FireMode: Operations.CombatFireMode.Hold } holder)
        {
            throw new AcceptanceException($"colonist 3 after fire_mode hold: {LabFrames.Row(held, colonists[3])}");
        }

        s.RecordWire("held_row", holder);

        // An ordinary passage opens the latched door, and the latch keeps it open.
        CombatLabKit.Applied("door-passage", (await CombatLabKit.OrdersAsync(s, "door-passage", cancellationToken, CombatLabKit.Order(colonists[4], new Operations.CombatOrderOrder.MoveCase(door))).ConfigureAwait(false))[0]);
        _ = await s.RunUntilAsync("door-opened", CombatLabKit.ProbeTicks, async ct =>
        {
            var d = (await CombatLabKit.BuildingAtAsync(s, "door", door, ct).ConfigureAwait(false)).Door;
            return (Wait.Signature(d?.Open, d?.HoldOpen), d?.Open == true);
        }, cancellationToken).ConfigureAwait(false);

        // Once the pawn has gone, a close order unlatches and shuts it.
        CombatLabKit.Applied("door-clearance", (await CombatLabKit.OrdersAsync(s, "door-clearance", cancellationToken, CombatLabKit.Order(colonists[4], new Operations.CombatOrderOrder.MoveCase(lab.Cell(5, -10)))).ConfigureAwait(false))[0]);
        await s.RunTicksAsync("clearance", 600, cancellationToken).ConfigureAwait(false);
        CombatLabKit.Applied("door-close", (await CombatLabKit.OrdersAsync(s, "door-close", cancellationToken, CombatLabKit.Order(null, new Operations.CombatOrderOrder.DoorCase(new Operations.CombatDoor { Cell = door, Mode = Operations.CombatDoorMode.Close }))).ConfigureAwait(false))[0]);
        _ = await s.RunUntilAsync("door-closed", CombatLabKit.ProbeTicks, async ct =>
        {
            var d = (await CombatLabKit.BuildingAtAsync(s, "door", door, ct).ConfigureAwait(false)).Door;
            return (Wait.Signature(d?.Open, d?.HoldOpen), d is { Open: false, HoldOpen: false });
        }, cancellationToken).ConfigureAwait(false);
    }
}
