using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/siege-stage</c> (#1147, #1154): the stage spec's siege raid and the siege fight's native facts. lab-siege
/// stages its raiders under a real LordJob_Siege; they camp (LordToil_Siege) and a raider works FinishFrame on a camp
/// frame, the frame's emergency census lists an engaging besieger far from every colonist meanwhile, and a drafted
/// colonist's attack on the builder's frame applies as AttackStatic on it.
/// </summary>
internal static class CombatLabSiegeStage
{
    /// <summary>
    /// Bounds the wait for a builder on a frame: the supply drop pods land, the raiders haul them to the blueprints and
    /// start a frame, a few thousand ticks on the vanilla camp.
    /// </summary>
    private const int BuildTicks = 10000 + CombatLabKit.ProbeTicks;

    /// <summary>How far from the nearest colonist a camped besieger counts as far away: the camp is ~32 cells out, past the 20-cell watch.</summary>
    private const int FarCells = 25;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-siege");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/siege-stage",
        Scope = "The stage spec's siege raid (#1147) and the siege fight's native facts (#1154): lab-siege stages its raiders under a real "
            + "LordJob_Siege at the camp spot; every raider stays in that lord and one, in LordToil_Siege, works a FinishFrame job on a camp frame. "
            + "While camped, the frame's emergency census lists an engaging besieger at least 25 cells from every colonist (the fight is open while "
            + "the camp is far away), and a drafted colonist's attack order on the builder's frame applies as AttackStatic on that frame.",
        Ticks = BuildTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var hostiles = lab.Hostiles();
        var frames = new LabFrames(s);
        Observations.ThreatPawn? far = null;
        Observations.PawnState? builder = null;
        _ = await s.RunUntilAsync("build", BuildTicks, async ct =>
        {
            var pawns = (await CombatLabKit.PawnsAsync(s, "raiders", hostiles, ct).ConfigureAwait(false)).Values.Where(p => !p.Dead).ToList();
            if (pawns.FirstOrDefault(p => p.LordJobClass != "LordJob_Siege") is { } strayed)
            {
                throw new AcceptanceException($"raider {strayed.Pawn.Id} left the siege lord: {strayed.LordJobClass} / {strayed.LordToilClass}");
            }

            if (far is null && pawns.Any(p => p.LordToilClass == "LordToil_Siege") && await frames.NextAsync(ct).ConfigureAwait(false) is { } frame)
            {
                far = FarThreat(frame, hostiles);
            }

            builder = pawns.FirstOrDefault(p => p.LordToilClass == "LordToil_Siege" && CombatLabKit.Job(p) == "FinishFrame");
            return (Wait.Signature(string.Join(",", pawns.Select(p => p.LordToilClass + ":" + CombatLabKit.Job(p))), far is null), builder is not null && far is not null);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("far_threat", far);
        s.RecordWire("builder", builder);
        var target = CombatLabKit.JobThing(builder!) ?? throw new AcceptanceException($"builder {builder!.Pawn.Id} works FinishFrame on no thing: {builder.Job}");

        var colonist = lab.Colonists()[0];
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, [colonist], cancellationToken).ConfigureAwait(false);
        CombatLabKit.Applied("attack frame", await CombatLabKit.OrderAsync(s, "attack-frame", colonist, new Operations.CombatOrderOrder.AttackCase(CombatLabKit.Pawn(target)), cancellationToken).ConfigureAwait(false), "AttackStatic");
        var p = await CombatLabKit.PawnAsync(s, "attacker", colonist, cancellationToken).ConfigureAwait(false);
        if (CombatLabKit.Job(p) != "AttackStatic" || CombatLabKit.JobThing(p) != target)
        {
            throw new AcceptanceException($"colonist {colonist} after the frame attack: want AttackStatic on {target}, got {p.Job}");
        }
    }

    // The emergency census's engaging besieger at least FarCells from the nearest colonist: the threat the fight opens on.
    // The census classes a pawn a hostile threat when it is a manhunter, of a hostile faction or breaking out of prison;
    // it is engaging unless the native marks it passive.
    private static Observations.ThreatPawn? FarThreat(Observations.BundleSnapshot frame, List<string> hostiles) =>
        frame.Emergency?.Threats?.Pawns.FirstOrDefault(t =>
            (t.FactionHostile == true || t.PrisonBreak == true || (t.MentalState ?? "").Contains("manhunter", StringComparison.OrdinalIgnoreCase))
            && t.Passive != true
            && hostiles.Contains(t.Pawn?.Id ?? "", StringComparer.Ordinal)
            && t.NearestColonistDistance >= FarCells);
}
