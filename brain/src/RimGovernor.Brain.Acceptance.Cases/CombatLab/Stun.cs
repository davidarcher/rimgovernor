using System.Globalization;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/stun</c> (#1050, #1118): the combat_pawns stun_ticks_left fact. lab-mech's scyther is staged stunned for
/// <see cref="StunTicks"/>; the first live frame reads a positive stun no longer than what is left at its tick, and a
/// frame past the stun's end reads none.
/// </summary>
internal static class CombatLabStun
{
    /// <summary>Long enough that a frame lands inside the stun however far a wait's probes overshoot.</summary>
    private const int StunTicks = 4 * CombatLabKit.ProbeTicks;

    /// <summary>The fact rounds up to this many ticks.</summary>
    private const int Rounding = 30;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-mech", (f, _, _) =>
    {
        foreach (var p in f.Pawns.Where(p => p.Side == CombatFixtures.Mech))
        {
            p.StunTicks = StunTicks;
        }
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/stun",
        Scope = "The combat_pawns stun_ticks_left fact (#1050, #1118), a native read: lab-mech's scyther staged stunned reads stun_ticks_left > 0 and "
            + "at most the stun left at the frame's tick in the first live frame, and no stun in the first frame past the stun's end.",
        Ticks = StunTicks + (3 * CombatLabKit.ProbeTicks),
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var scyther = lab.Mechs().Single();
        var staged = await CombatLabKit.TickAsync(s, "staged-tick", cancellationToken).ConfigureAwait(false);
        var frames = new LabFrames(s);
        var stunned = await frames.UntilAsync("stunned", CombatLabKit.ProbeTicks, staged, f => LabFrames.Row(f, scyther) is not null, cancellationToken).ConfigureAwait(false);
        var row = LabFrames.Row(stunned, scyther)!;
        var elapsed = frames.Tick - staged;
        var left = row.StunTicksLeft;
        s.RecordWire("stunned", row);
        if (left <= 0 || left > StunTicks - elapsed + Rounding)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"stunned scyther reads stun_ticks_left {left} {elapsed} ticks into a {StunTicks}-tick stun: {row}"));
        }

        var after = await frames.UntilAsync("expired", StunTicks + (2 * CombatLabKit.ProbeTicks), staged + StunTicks + 1, f => LabFrames.Row(f, scyther) is not null, cancellationToken).ConfigureAwait(false);
        var expired = LabFrames.Row(after, scyther)!;
        s.RecordWire("expired", expired);
        if (expired.StunTicksLeft is > 0 and var still)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"scyther still reads stun_ticks_left {still} {frames.Tick - staged} ticks after a {StunTicks}-tick stun: {expired}"));
        }
    }
}
