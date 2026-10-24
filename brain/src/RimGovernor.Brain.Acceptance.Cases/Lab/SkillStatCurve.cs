using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire.Observations;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/skill-stat-curve</c> (#3633, #3813): the pawn read's medical stat curves equal live native stat values at every stored
/// Medicine level, its MeleeDPS curve at every stored Melee level, and a disqualifying hediff yields no medical curve but a finding. test/skill_stat_curve_prepare does the
/// per-level equality natively (it alone may set a level, temporarily); the case then reads both pawns through the
/// ordinary pawn observation the brain uses.
/// </summary>
internal static class LabSkillStatCurve
{
    private const string PrepareTool = "test/skill_stat_curve_prepare";

    // MedicalTendQuality, MedicalTendSpeed, MedicalOperationSpeed, MedicalSurgerySuccessChance x stored levels 0-20.
    private const int Stats = 4;

    // MeleeDPS over Melee: an anesthetic has no MeleeHitChance factor, so both colonists carry it.
    private const int MeleeStats = 1;
    private const int Levels = 21;
    private const int ProductiveStats = 5;

    private static readonly FixtureOp<NoArgs, Fx.SkillStatCurvePrepared> Prepare = ServedOps.Read<Fx.SkillStatCurvePrepared>(PrepareTool);

    internal static TypedCase Case() => new()
    {
        Name = "lab/skill-stat-curve",
        Scope = "Native read differential: each medical stat curve on a healthy colonist's biography equals the live native "
            + "stat at every stored Medicine level, and its MeleeDPS curve at every stored Melee level; five productive stat curves also match every native level, including skill-independent tailoring. An anesthetized colonist carries no medical curve, a finding per medical stat, and its MeleeDPS curve. "
            + "A unit test cannot run the game's stat pipeline; using the curves for value is #3635.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Ops = [PrepareTool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var prepared = await s.FixtureAsync("curve-prepare", Prepare, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("prepare", ServedOps.Json(prepared));
            if (prepared.Samples != (Stats + MeleeStats + ProductiveStats) * Levels || prepared.Failures.Count != 0)
            {
                throw new AcceptanceException($"native stat curves: {prepared.Samples} samples (want {(Stats + MeleeStats + ProductiveStats) * Levels}), disagreeing: {string.Join(", ", prepared.Failures)}");
            }

            var reply = await s.CallAsync("curve-pawns", NativeOps.ListPawns, new ListPawnsRequest
            {
                Scope = s.Scope,
                Filter = new() { Ids = [prepared.Qualified, prepared.Disqualified] },
            }, ct).ConfigureAwait(false);
            var pawns = reply.Pawns;
            PawnBiography Bio(string id) => pawns.SingleOrDefault(p => p.Pawn.Id == id)?.Biography
                ?? throw new AcceptanceException($"curve-pawns: no biography for {id}");
            var healthy = Bio(prepared.Qualified);
            var sick = Bio(prepared.Disqualified);
            s.Record("curves", string.Join("; ", healthy.SkillStatCurves.Select(c => $"{c.Stat}/{c.Skill} x{c.Levels.Count} ({c.Provenance})")));
            s.Record("findings", string.Join("; ", sick.SkillStatFindings));
            if (healthy.SkillStatCurves.Count != Stats + MeleeStats || healthy.SkillStatCurves.Any(c => c.Levels.Count != Levels
                || c.Levels.Select((l, i) => l.StoredLevel != i || !double.IsFinite(l.Value)).Any(bad => bad)))
            {
                throw new AcceptanceException($"curve-pawns: the healthy colonist carries {healthy.SkillStatCurves.Count} curves (want {Stats + MeleeStats} of {Levels} finite levels 0-{Levels - 1})");
            }

            if (sick.SkillStatCurves.Count != MeleeStats || sick.SkillStatCurves[0].Stat != "MeleeDPS" || sick.SkillStatFindings.Count != Stats)
            {
                throw new AcceptanceException($"curve-pawns: the anesthetized colonist carries {sick.SkillStatCurves.Count} curves and {sick.SkillStatFindings.Count} findings (want the MeleeDPS curve and {Stats})");
            }
        },
    };
}
