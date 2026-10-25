using System;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>Qualified read-only counterfactual: a pawn's stat value at each stored skill level, with the native
/// skill term replaced before native finalization. No pawn, skill, definition or stat cache is rewritten.
/// A pipeline that is not exact for the substitution yields no values and a finding. A factor stat (the skill multiplies the sum of every\r\n/// offset and factor, then only linear terms follow) stays exact under hediff, trait, life-stage, precept and gene factors (#3912);\r\n/// an offset stat does not.</summary>
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
    /// curves, then the ranged damage the defense capacity counts (<see cref="RimGovernor.Brain.GameFacts.Observations.SkillStatCurve.RangedDefenseDps"/>, #3846).</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.PawnBiography Curves(Pawn pawn, RimGovernor.Brain.GameFacts.Observations.PawnBiography row)
    {
        foreach (var (stat, skill) in Biography.Select(s => (s.Stat(), s.Skill())))
        {
            var melee = stat == StatDefOf.MeleeDPS;
            var values = melee ? MeleeDpsLevels(pawn, out var finding) : Levels(pawn, stat, skill, out finding);
            row = Curve(row, stat.defName, skill, values, finding, melee ? MeleeProvenance : Provenance);
        }
        return Curve(row, RimGovernor.Brain.GameFacts.Observations.SkillStatCurve.RangedDefenseDps, SkillDefOf.Shooting, RangedDefenseDpsLevels(pawn, out var ranged), ranged, RangedProvenance);
    }

    private static RimGovernor.Brain.GameFacts.Observations.PawnBiography Curve(RimGovernor.Brain.GameFacts.Observations.PawnBiography row, string name, SkillDef skill, float[]? values, string finding, string provenance) =>
        values == null
            ? row with { SkillStatFindings = [.. row.SkillStatFindings, name + ": " + finding] }
            : values.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                ? row with { SkillStatFindings = [.. row.SkillStatFindings, name + ": non-finite value"] }
                : row with { SkillStatCurves = [.. row.SkillStatCurves, new RimGovernor.Brain.GameFacts.Observations.SkillStatCurve(name, skill.defName,
                    EquatableArray.From(values.Select((v, level) => new RimGovernor.Brain.GameFacts.Observations.SkillStatLevel(level, v)).ToArray()), provenance)] };

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
    internal static float[]? Levels(Pawn pawn, StatDef stat, SkillDef skill, out string finding, int? biologicalYears = null)
        => SelectedLevels(pawn, stat, skill, out finding, biologicalYears, null);

    private static float[]? SelectedLevels(Pawn pawn, StatDef stat, SkillDef skill, out string finding, int? biologicalYears, int? storedLevel)
    {
        // GeneralLaborSpeed is independent of Crafting: native quality changes, native duration does not.
        // Follow the actual stat dependency graph instead of turning a known constant rate into an unknown at level-up.
        if (Independent(pawn, stat, skill))
        {
            finding = "";
            return Enumerable.Repeat(biologicalYears is { } age ? NativeAgeStat.Value(pawn, stat, age) : pawn.GetStatValue(stat), storedLevel.HasValue ? 1 : SkillRecord.MaxLevel + 1).ToArray();
        }
        finding = Disqualified(pawn, stat, skill) ?? "";
        if (finding.Length > 0) return null;
        var record = pawn.skills.GetSkill(skill);
        var request = StatRequest.For(pawn);
        var original = biologicalYears is { } years ? NativeAgeStat.Unfinalized(pawn, stat, years) : stat.Worker.GetValueUnfinalized(request);
        var offset = stat.skillNeedOffsets?.Count == 1;
        var need = offset ? (stat.skillNeedOffsets ?? throw new System.InvalidOperationException("Required native value is unavailable: stat.skillNeedOffsets."))[0] : (stat.skillNeedFactors ?? throw new System.InvalidOperationException("Required native value is unavailable: stat.skillNeedFactors."))[0];
        var current = need.ValueFor(pawn);
        if (!offset && (current <= 0 || float.IsNaN(current) || float.IsInfinity(current)))
        {
            finding = "current skill factor is not positive";
            return null;
        }
        return Enumerable.Range(storedLevel ?? SkillRecord.MinLevel, storedLevel.HasValue ? 1 : SkillRecord.MaxLevel + 1).Select(level =>
        {
            var effective = Math.Clamp(level + record.Aptitude, SkillRecord.MinLevel, SkillRecord.MaxLevel);
            var atLevel = need is SkillNeed_Direct direct ? direct.valuesPerLevel[effective] : (float)((BaseBonusAtLevel ?? throw new System.InvalidOperationException("Required native value is unavailable: BaseBonusAtLevel.")).Invoke(need, [effective]) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
            // An offset is additive with nothing multiplied after it (gated); a factor is followed only by linear
            // multipliers (statFactors before it, capacity lerps after it, inspiration gated), so the ratio is exact.
            var value = offset ? original - current + atLevel : (float)(original / (double)current * atLevel);
            if (biologicalYears is { } age) NativeAgeStat.Finalize(pawn, stat, age, ref value);
            else stat.Worker.FinalizeValue(request, ref value, true);
            return value;
        }).ToArray();
    }

    // Inspected 1.7 overrides: ShootingAccuracy changes explanation text and Mechanitor changes visibility only.
    // Both inherit the complete StatWorker value pipeline, including finalization.
    internal static bool HasBaseValuePipeline(StatDef stat) => stat.Worker.GetType() == typeof(StatWorker)
        || stat.Worker.GetType() == typeof(StatWorker_ShootingAccuracy)
        || stat.Worker.GetType() == typeof(StatWorker_Mechanitor);

    private static string? Disqualified(Pawn pawn, StatDef stat, SkillDef skill)
    {
        if (!HasBaseValuePipeline(stat)) return "custom stat worker";
        var record = pawn.skills?.GetSkill(skill);
        if (record == null || record.TotallyDisabled) return "skill disabled or absent";
        if (BaseBonusAtLevel == null) return "SkillNeed_BaseBonus.ValueAtLevel not found";
        bool Supported(SkillNeed need) => need.skill == skill && (need.GetType() == typeof(SkillNeed_BaseBonus)
            || need.GetType() == typeof(SkillNeed_Direct) && ((SkillNeed_Direct)need).valuesPerLevel.Count > SkillRecord.MaxLevel);
        var offsets = stat.skillNeedOffsets?.Count ?? 0;
        var factors = stat.skillNeedFactors?.Count ?? 0;
        if (offsets == 1 && factors == 0)
        {
            if ((stat.skillNeedOffsets ?? throw new System.InvalidOperationException("Required native value is unavailable: stat.skillNeedOffsets."))[0].GetType() != typeof(SkillNeed_BaseBonus) || !Supported(stat.skillNeedOffsets[0]))
                return "unsupported skill offset";
            if (stat.statFactors?.Count > 0 || stat.capacityFactors?.Count > 0) return "multiplier after skill offset";
        }
        else if (offsets == 0 && factors == 1)
        {
            if (!Supported((stat.skillNeedFactors ?? throw new System.InvalidOperationException("Required native value is unavailable: stat.skillNeedFactors."))[0])) return "unsupported skill factor";
            if (stat.statFactors?.Any(f => !Independent(pawn, f, skill)) == true) return "skill-dependent stat factor";
        }
        else return "not a single skill offset or factor";
        if (stat.parts?.Any(p => !AllowedPart(pawn, p)) == true) return "unsupported stat part";
        if (stat.postProcessStatFactors?.Any(f => !Independent(pawn, f, skill)) == true) return "skill-dependent postprocess factor";
        // Everything below is what the substitution can break (#3912). A factor stat's skill factor multiplies the sum of every
        // offset, trait, hediff, precept, gene and life-stage effect (StatWorker.GetValueUnfinalized applies them first), and the
        // capacity lerps after it are linear, so none of those disqualifies the ratio. An offset stat's skill offset is multiplied
        // by the factors that follow it, so each of those still disqualifies it. An inspiration's offset and factor apply after
        // the skill term, and a comp's modifier is arbitrary code: either disqualifies only where it changes this stat.
        if (pawn.Inspired && (pawn.InspirationDef.statOffsets.GetStatOffsetFromList(stat) != 0f
            || pawn.InspirationDef.statFactors.GetStatFactorFromList(stat) != 1f)) return "inspired";
        if (pawn.Stuff != null) return "pawn has stuff";
        if (CompChanges(pawn, stat)) return "comp stat modifier";
        if (offsets == 0) return null;
        if (pawn.health.hediffSet.hediffs.Any(h => h.CurStage != null
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
        return null;
    }

    // Inspected parts that never read a skill: their observed value is held like capacities and lighting.
    internal static bool AllowedPart(Pawn pawn, StatPart part) => part.GetType() == typeof(StatPart_Glow)
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
            && HasBaseValuePipeline(factor)
            && factor.parts?.Any(p => !AllowedPart(pawn, p)) != true
            && factor.statFactors?.All(f => Independent(pawn, f, skill, path)) != false
            && factor.postProcessStatFactors?.All(f => Independent(pawn, f, skill, path)) != false
            && !CompChanges(pawn, factor);
        path.Remove(factor);
        return independent;
    }

    // A comp's modifier is arbitrary code, so a pawn comp qualifies only if it leaves this stat alone as observed.
    private static bool CompChanges(Pawn pawn, StatDef stat) => pawn.AllComps.Any(c => c.GetStatOffset(stat) != 0f || c.GetStatFactor(stat) != 1f);

    /// <summary>Whether the stat's value reads the skill: its own skill needs or those of any stat it nests.</summary>
    private static bool DependsOn(StatDef stat, SkillDef skill, System.Collections.Generic.HashSet<StatDef>? path = null)
    {
        path ??= new System.Collections.Generic.HashSet<StatDef>();
        if (!path.Add(stat)) return false;
        return stat.skillNeedOffsets?.Any(s => s.skill == skill) == true || stat.skillNeedFactors?.Any(s => s.skill == skill) == true
            || stat.statFactors?.Any(f => DependsOn(f, skill, path)) == true || stat.postProcessStatFactors?.Any(f => DependsOn(f, skill, path)) == true;
    }

    /// <summary>
    /// The pawn's value of <paramref name="stat"/> with <paramref name="skill"/> one stored level higher (#3912), or null when the
    /// stat does not read the skill (its value is the observed one at every level). The skill's top level has no level above:
    /// its observed value. 0 where the stat is disabled for the pawn, as the upkeep speeds are. Throws when the stat reads the
    /// skill by a pipeline the substitution cannot hold exact (<see cref="Disqualified"/>).
    /// </summary>
    internal static float? OneLevelUp(Pawn pawn, StatDef stat, SkillDef skill, int? biologicalYears = null)
    {
        if (!DependsOn(stat, skill)) return null;
        if (stat.Worker.IsDisabledFor(pawn)) return 0f;
        var record = pawn.skills.GetSkill(skill);
        // A skill the pawn cannot learn (totally disabled) or has no level above (the top) moves nothing: the observed value.
        if (record.TotallyDisabled || record.levelInt >= SkillRecord.MaxLevel) return biologicalYears is { } age ? NativeAgeStat.Value(pawn, stat, age) : pawn.GetStatValue(stat);
        var stored = record.levelInt;
        var levels = SelectedLevels(pawn, stat, skill, out var finding, biologicalYears, stored + 1)
            ?? throw new InvalidOperationException($"{stat.defName} at one level more of {skill.defName} is not exact for {pawn.LabelShort}: {finding}");
        return levels[0];
    }
}
