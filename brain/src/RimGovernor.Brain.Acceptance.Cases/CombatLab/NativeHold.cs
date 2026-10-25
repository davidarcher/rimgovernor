
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/native-hold</c> (#2477, #1857): the brain assigns one ranged firing cell and vanilla execution acquires
/// and retargets on its own. One defender moves to the cell, holds fire for <see cref="HoldTicks"/> with live hostiles in
/// range, then gets hold-position and fire-at-will once; its shots must reach a second live hostile after the first target
/// is gone, and every frame must show it on the cell.
/// </summary>
internal static class CombatLabNativeHold
{
    private const int HoldTicks = 600;

    /// <summary>The hold-fire phase needs a live hostile at most this far from the cell (the charge rifle's reach).</summary>
    private const int RangeCells = 24;

    /// <summary>Bounds the engagement: two archers 16 and 20 cells out, one downed and the next shot, well inside it.</summary>
    private const int FightTicks = 4800;

    /// <summary>The cell's offset from the fixture centre.</summary>
    private const int CellDz = -9;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-ranged", (f, cx, cz) =>
    {
        var z = cz + CellDz;
        f.Colonists = 1;
        f.Layout = null;
        f.Pawns =
        [
            new LabPawn { Side = CombatFixtures.Colonist, Index = 0, X = cx, Z = z - 2, Weapon = "Gun_ChargeRifle", Apparel = "Apparel_PowerArmor" },
            new LabPawn { Side = CombatFixtures.Hostile, Kind = CombatFixtures.Gunner, X = cx - 2, Z = z + 16, Weapon = "Bow_Short" },
            new LabPawn { Side = CombatFixtures.Hostile, Kind = CombatFixtures.Gunner, X = cx + 2, Z = z + 20, Weapon = "Bow_Short" },
        ];
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/native-hold",
        Scope = "The brain assigns one ranged firing cell; vanilla native execution acquires and retargets without micro orders (#2477, #1857). "
            + "On a pinned lab one defender moves to the cell, holds fire for 600 ticks with a live hostile in range, then receives hold-position "
            + "and fire-at-will once. Its shot events must name a second live hostile after the first target is observed unavailable through "
            + "ordinary combat, and every frame must show it on the assigned cell.",
        Ticks = CombatLabKit.ProbeTicks + (HoldTicks + CombatLabKit.ProbeTicks) + (FightTicks + CombatLabKit.ProbeTicks),
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var defender = lab.Colonists()[0];
        var hostiles = lab.Hostiles().ToHashSet(StringComparer.Ordinal);
        var cell = lab.Cell(0, CellDz);
        s.Record("assigned_cell", CombatLabKit.Show(cell));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, [defender], cancellationToken).ConfigureAwait(false);
        foreach (var r in await CombatLabKit.OrdersAsync(
            s,
            "move",
            cancellationToken,
            CombatLabKit.Order(defender, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold)),
            CombatLabKit.Order(defender, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MoveCase(cell))).ConfigureAwait(false))
        {
            CombatLabKit.Applied("move", r);
        }

        var frames = new LabFrames(s);
        _ = await frames.UntilAsync("arrive", CombatLabKit.ProbeTicks, 0, f => LabFrames.Row(f, defender)?.Cell == cell, cancellationToken).ConfigureAwait(false);
        CombatLabKit.Applied("hold-position", await CombatLabKit.OrderAsync(s, "hold-position", defender, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.HoldPositionCase(new RimGovernor.Brain.GameOrders.Operations.Clear()), cancellationToken).ConfigureAwait(false));

        // Hold fire: the defender stays on the cell and silent while a live hostile is in range.
        var start = await CombatLabKit.TickAsync(s, "hold-start", cancellationToken).ConfigureAwait(false);
        _ = await frames.UntilAsync("hold-fire", HoldTicks + CombatLabKit.ProbeTicks, start, f =>
        {
            OnCell(f, defender, cell);
            if (!hostiles.Any(id => LabFrames.Row(f, id) is { Downed: not true, Dead: not true, Cell: { } at } && CombatLabKit.Chebyshev(at, cell) <= RangeCells))
            {
                throw new AcceptanceException($"hold-fire phase has no live hostile within {RangeCells} cells at tick {f.Context?.Tick}");
            }

            if (Shots(frames, defender, start).FirstOrDefault() is { } shot)
            {
                throw new AcceptanceException($"defender fired under hold fire: {shot}");
            }

            return (f.Context?.Tick ?? 0) >= start + HoldTicks;
        }, cancellationToken).ConfigureAwait(false);

        // The last assignment: from here vanilla acquires and retargets on its own.
        foreach (var r in await CombatLabKit.OrdersAsync(
            s,
            "engage",
            cancellationToken,
            CombatLabKit.Order(defender, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.HoldPositionCase(new RimGovernor.Brain.GameOrders.Operations.Clear())),
            CombatLabKit.Order(defender, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.AtWill))).ConfigureAwait(false))
        {
            CombatLabKit.Applied("engage", r);
        }

        var release = await CombatLabKit.TickAsync(s, "engage-start", cancellationToken).ConfigureAwait(false);
        string? first = null;
        long gone = -1;
        RimGovernor.Brain.GameFacts.Mirror.CombatEventRow? retarget = null;
        _ = await frames.UntilAsync("retarget", FightTicks + CombatLabKit.ProbeTicks, release, f =>
        {
            OnCell(f, defender, cell);
            var shots = Shots(frames, defender, release).ToList();
            if (shots.FirstOrDefault(e => !hostiles.Contains(e.TargetId ?? "")) is { } stray)
            {
                throw new AcceptanceException($"defender shot an unexpected target: {stray}");
            }

            first ??= shots.FirstOrDefault()?.TargetId;
            if (first is not null && gone < 0 && LabFrames.Row(f, first) is null or { Downed: true } or { Dead: true })
            {
                gone = f.Context?.Tick ?? 0;
            }

            retarget = gone < 0 ? null : shots.FirstOrDefault(e => e.TargetId != first && e.At.Tick > gone && LabFrames.Row(f, e.TargetId!) is { Downed: false, Dead: false });
            return retarget is not null;
        }, cancellationToken).ConfigureAwait(false);
        s.Record("first_target", first);
        s.Record("first_unavailable_tick", gone);
        s.RecordWire("retarget", retarget);
    }

    // The frame shows the defender standing, alive, on the assigned cell; anything else fails the case.
    private static void OnCell(RimGovernor.Brain.GameFacts.Observations.BundleSnapshot f, string defender, RimGovernor.Brain.Domain.Cell cell)
    {
        if (LabFrames.Row(f, defender) is not { Downed: not true, Dead: not true } row || row.Cell != cell)
        {
            throw new AcceptanceException($"hold lost at tick {f.Context?.Tick}: defender {LabFrames.Row(f, defender)?.ToString() ?? "absent"}, assigned {CombatLabKit.Show(cell)}");
        }
    }

    // The defender's shot_fired events from tick from on, oldest first.
    private static IEnumerable<RimGovernor.Brain.GameFacts.Mirror.CombatEventRow> Shots(LabFrames frames, string defender, long from) =>
        frames.Events.Where(e => e.Kind == RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.ShotFired && e.ThingId == defender && e.At.Tick >= from);
}
