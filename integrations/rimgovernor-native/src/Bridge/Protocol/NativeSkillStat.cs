#nullable enable
using System;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Qualified read-only counterfactual: a pawn's stat value at each stored skill level, with the native
/// skill term replaced before native finalization. No pawn, skill, definition or stat cache is rewritten.
/// A pipeline that is not exact for the substitution yields no values and a finding.</summary>
internal static class NativeSkillStat
{
    internal const string Provenance = "native stat dependency graph at each stored skill level -> StatWorker.FinalizeValue; observed conditions held";

    /// <summary>The MeleeDPS curve's provenance: StatWorker_MeleeDPS is damage × MeleeHitChance / cooldown, and only the
    /// hit chance reads the Melee skill, so each level's value is the observed MeleeDPS scaled by its hit chance.</summary>
    internal const string MeleeProvenance = "StatWorker_MeleeDPS = damage x MeleeHitChance / cooldown; MeleeHitChance at each stored Melee level -> StatWorker.FinalizeValue; observed damage, cooldown and conditions held";

    /// <summary>The per-pawn curves the biography carries: the medical ones over Medicine (#3633) and MeleeDPS over Melee
    /// (#3813, the sparring ring's credit). MedicalOperationSpeed has no StatDefOf field; the surgery recipes' shared base names it.</summary>
    private static readonly (Func<StatDef> Stat, Func<SkillDef> Skill)[] Biography =
    [
        (() => StatDefOf.MedicalTendQuality, () => SkillDefOf.Medicine), (() => StatDefOf.MedicalTendSpeed, () => SkillDefOf.Medicine),
        (() => RecipeDefOf.RemoveBodyPart.workSpeedStat, () => SkillDefOf.Medicine), (() => StatDefOf.MedicalSurgerySuccessChance, () => SkillDefOf.Medicine),
        (() => StatDefOf.MeleeDPS, () => SkillDefOf.Melee),
    ];

    private static readonly MethodInfo? BaseBonusAtLevel =
        typeof(SkillNeed_BaseBonus).GetMethod("ValueAtLevel", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The RangedDefenseDps curve's provenance (#3846): Shooting moves only ShootingAccuracyPawn, which ShotReport's
    /// shooter factor reads, so each level's value is the observed ranged DPS at the hit factor that level's accuracy gives.</summary>
    internal const string RangedProvenance = "NativeDefenseStats.PawnRangedDps x PawnRangedHitFactor; ShootingAccuracyPawn at each stored Shooting level -> StatWorker.FinalizeValue -> ShotReport.HitFactorFromShooter; observed weapon, distance and conditions held";

    /// <summary>The biography's curves for a pawn with skills, plus one finding per stat that does not qualify: the stat
    /// curves, then the ranged damage the defense capacity counts (<see cref="Obs.SkillStatCurve.RangedDefenseDps"/>, #3846).</summary>
    internal static Obs.PawnBiography Curves(Pawn pawn, Obs.PawnBiography row)
    {
        foreach (var (stat, skill) in Biography.Select(s => (s.Stat(), s.Skill())))
        {
            var melee = stat == StatDefOf.MeleeDPS;
            var values = melee ? MeleeDpsLevels(pawn, out var finding) : Levels(pawn, stat, skill, out finding);
            row = Curve(row, stat.defName, skill, values, finding, melee ? MeleeProvenance : Provenance);
        }
        return Curve(row, Obs.SkillStatCurve.RangedDefenseDps, SkillDefOf.Shooting, RangedDefenseDpsLevels(pawn, out var ranged), ranged, RangedProvenance);
    }

    private static Obs.PawnBiography Curve(Obs.PawnBiography row, string name, SkillDef skill, float[]? values, string finding, string provenance) =>
        values == null
            ? row with { SkillStatFindings = [.. row.SkillStatFindings, name + ": " + finding] }
            : values.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                ? row with { SkillStatFindings = [.. row.SkillStatFindings, name + ": non-finite value"] }
                : row with { SkillStatCurves = [.. row.SkillStatCurves, new Obs.SkillStatCurve(name, skill.defName,
                    EquatableArray.From(values.Select((v, level) => new Obs.SkillStatLevel(level, v)).ToArray()), provenance)] };

    /// <summary>The pawn's defense-capacity ranged DPS at each stored Shooting level (see <see cref="RangedProvenance"/>): its
    /// primary's ranged DPS times the hit factor of the ShootingAccuracyPawn at the level. Zero at every level without a
    /// ranged primary (it lands no shots); null with the accuracy's finding when its pipeline does not qualify.</summary>
    internal static float[]? RangedDefenseDpsLevels(Pawn pawn, out string finding)
    {
        finding = "";
        var dps = NativeDefenseStats.PawnRangedDps(pawn);
        if (NativeDefenseStats.PawnRangedHitFactor(pawn) == 0) return new float[SkillRecord.MaxLevel + 1];
        var accuracy = Levels(pawn, StatDefOf.ShootingAccuracyPawn, SkillDefOf.Shooting, out finding);
        return accuracy?.Select(a => (float)(dps * NativeDefenseStats.PawnRangedHitFactor(pawn, a))).ToArray();
    }

    /// <summary>The pawn's MeleeDPS at each stored Melee level (see <see cref="MeleeProvenance"/>): the observed MeleeDPS times
    /// the MeleeHitChance at the level over the current one. Null with the hit chance's finding when its pipeline does not qualify.</summary>
    internal static float[]? MeleeDpsLevels(Pawn pawn, out string finding)
    {
        var hit = Levels(pawn, StatDefOf.MeleeHitChance, SkillDefOf.Melee, out finding);
        if (hit == null) return null;
        var now = pawn.GetStatValue(StatDefOf.MeleeHitChance);
        if (!(now > 0))
        {
            finding = "melee hit chance is not positive";
            return null;
        }
        var dps = pawn.GetStatValue(StatDefOf.MeleeDPS);
        return hit.Select(h => dps * h / now).ToArray();
    }

    /// <summary>The finalized stat value at each stored level <see cref="SkillRecord.MinLevel"/>..<see cref="SkillRecord.MaxLevel"/>
    /// (index = stored level, aptitude applied as native GetLevel does), or null with the disqualifying <paramref name="finding"/>.</summary>
    internal static float[]? Levels(Pawn pawn, StatDef stat, SkillDef skill, out string finding)
    {
        // GeneralLaborSpeed is independent of Crafting: native quality changes, native duration does not.
        // Follow the actual stat dependency graph instead of turning a known constant rate into an unknown at level-up.
        if (Independent(pawn, stat, skill))
        {
            finding = "";
            return Enumerable.Repeat(pawn.GetStatValue(stat), SkillRecord.MaxLevel + 1).ToArray();
        }
        finding = Disqualified(pawn, stat, skill) ?? "";
        if (finding.Length > 0) return null;
        var record = pawn.skills!.GetSkill(skill);
        var request = StatRequest.For(pawn);
        var original = stat.Worker.GetValueUnfinalized(request);
        var offset = stat.skillNeedOffsets?.Count == 1;
        var need = offset ? stat.skillNeedOffsets![0] : stat.skillNeedFactors![0];
        var current = need.ValueFor(pawn);
        if (!offset && (current <= 0 || float.IsNaN(current) || float.IsInfinity(current)))
        {
            finding = "current skill factor is not positive";
            return null;
        }
        return Enumerable.Range(SkillRecord.MinLevel, SkillRecord.MaxLevel + 1).Select(level =>
        {
            var effective = Math.Clamp(level + record.Aptitude, SkillRecord.MinLevel, SkillRecord.MaxLevel);
            var atLevel = need is SkillNeed_Direct direct ? direct.valuesPerLevel[effective] : (float)BaseBonusAtLevel!.Invoke(need, [effective])!;
            // An offset is additive with nothing multiplied after it (gated); a factor is followed only by linear
            // multipliers (statFactors before it, capacity lerps after it, inspiration gated), so the ratio is exact.
            var value = offset ? original - current + atLevel : (float)(original / (double)current * atLevel);
            stat.Worker.FinalizeValue(request, ref value, true);
            return value;
        }).ToArray();
    }

    private static string? Disqualified(Pawn pawn, StatDef stat, SkillDef skill)
    {
        // StatWorker_ShootingAccuracy overrides only the explanation text; its value pipeline is StatWorker's.
        if (stat.Worker.GetType() != typeof(StatWorker) && stat.Worker.GetType() != typeof(StatWorker_ShootingAccuracy)) return "custom stat worker";
        var record = pawn.skills?.GetSkill(skill);
        if (record == null || record.TotallyDisabled) return "skill disabled or absent";
        if (BaseBonusAtLevel == null) return "SkillNeed_BaseBonus.ValueAtLevel not found";
        bool Supported(SkillNeed need) => need.skill == skill && (need.GetType() == typeof(SkillNeed_BaseBonus)
            || need.GetType() == typeof(SkillNeed_Direct) && ((SkillNeed_Direct)need).valuesPerLevel.Count > SkillRecord.MaxLevel);
        var offsets = stat.skillNeedOffsets?.Count ?? 0;
        var factors = stat.skillNeedFactors?.Count ?? 0;
        if (offsets == 1 && factors == 0)
        {
            if (stat.skillNeedOffsets![0].GetType() != typeof(SkillNeed_BaseBonus) || !Supported(stat.skillNeedOffsets[0]))
                return "unsupported skill offset";
            if (stat.statFactors?.Count > 0 || stat.capacityFactors?.Count > 0) return "multiplier after skill offset";
        }
        else if (offsets == 0 && factors == 1)
        {
            if (!Supported(stat.skillNeedFactors![0])) return "unsupported skill factor";
            if (stat.statFactors?.Any(f => !Independent(pawn, f, skill)) == true) return "skill-dependent stat factor";
        }
        else return "not a single skill offset or factor";
        if (stat.parts?.Any(p => !AllowedPart(pawn, p)) == true) return "unsupported stat part";
        if (stat.postProcessStatFactors?.Any(f => !Independent(pawn, f, skill)) == true) return "skill-dependent postprocess factor";
        if (pawn.Inspired) return "inspired";
        if (pawn.Stuff != null) return "pawn has stuff";
        // A hediff's offset adds before the skill offset's sum is finalized, so only a hediff factor on the stat breaks an
        // offset substitution; a factor stat keeps the stricter rule.
        if (offsets == 0 && pawn.health.hediffSet.hediffs.Count > 0) return "hediffs present";
        if (offsets == 1 && pawn.health.hediffSet.hediffs.Any(h => h.CurStage != null
                && HediffStatsUtility.GetStatFactorForSeverity(stat, h.CurStage, pawn, h.Severity) != 1f)) return "hediff factor";
        if (pawn.story?.traits.allTraits.Any(t => !t.Suppressed && t.MultiplierOfStat(stat) != 1f) == true) return "trait factor";
        if (pawn.ageTracker.CurLifeStage.statFactors.GetStatFactorFromList(stat) != 1f) return "life stage factor";
        bool Conditional(System.Collections.Generic.List<ConditionalStatAffecter>? affecters) => affecters?.Any(c =>
            c.statOffsets.GetStatOffsetFromList(stat) != 0f || c.statFactors.GetStatFactorFromList(stat) != 1f) == true;
        if (pawn.Ideo != null && (pawn.Ideo.PreceptsListForReading.Any(p => Conditional(p.def.conditionalStatAffecters)
                || p.def.statFactors.GetStatFactorFromList(stat) != 1f)
            || pawn.Ideo.GetRole(pawn)?.def.roleEffects?.OfType<RoleEffect_PawnStatFactor>().Any(r => r.statDef == stat && r.modifier != 1f) == true))
            return "ideology factor";
        if (pawn.genes?.GenesListForReading.Any(g => g.Active && (Conditional(g.def.conditionalStatAffecters)
                || g.def.statFactors.GetStatFactorFromList(stat) != 1f)) == true) return "gene factor";
        if (pawn.AllComps.Any(c => c.GetType().GetMethod(nameof(ThingComp.GetStatOffset))?.DeclaringType != typeof(ThingComp)
                || c.GetType().GetMethod(nameof(ThingComp.GetStatFactor))?.DeclaringType != typeof(ThingComp))) return "comp stat modifier";
        return null;
    }

    // Inspected parts that never read a skill: their observed value is held like capacities and lighting.
    private static bool AllowedPart(Pawn pawn, StatPart part) => part.GetType() == typeof(StatPart_Glow)
        || part.GetType() == typeof(StatPart_Slave) || part.GetType() == typeof(StatPart_Age)
        || part.GetType() == typeof(StatPart_Trainable) // Native Dig training multiplier, independent of skill level.
        || part.GetType() == typeof(StatPart_OverseerStatOffset) && !pawn.RaceProps.IsMechanoid;

    // A nested stat (CookSpeed's postfactor and the medical speeds' factor are native WorkSpeedGlobal) may depend on
    // current condition, but no supported nested pipeline may depend on the skill itself.
    private static bool Independent(Pawn pawn, StatDef factor, SkillDef skill) =>
        Independent(pawn, factor, skill, new System.Collections.Generic.HashSet<StatDef>());

    private static bool Independent(Pawn pawn, StatDef factor, SkillDef skill, System.Collections.Generic.HashSet<StatDef> path)
    {
        if (!path.Add(factor)) return false;
        var independent = factor.skillNeedOffsets?.Any(s => s.skill == skill) != true
            && factor.skillNeedFactors?.Any(s => s.skill == skill) != true
            && factor.Worker.GetType() == typeof(StatWorker)
            && factor.parts?.Any(p => !AllowedPart(pawn, p)) != true
            && factor.statFactors?.All(f => Independent(pawn, f, skill, path)) != false
            && factor.postProcessStatFactors?.All(f => Independent(pawn, f, skill, path)) != false
            && pawn.AllComps.All(c => c.GetType().GetMethod(nameof(ThingComp.GetStatOffset))?.DeclaringType == typeof(ThingComp)
                && c.GetType().GetMethod(nameof(ThingComp.GetStatFactor))?.DeclaringType == typeof(ThingComp));
        path.Remove(factor);
        return independent;
    }
}
