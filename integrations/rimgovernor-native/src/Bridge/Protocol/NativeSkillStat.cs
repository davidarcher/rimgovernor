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

    /// <summary>The per-pawn curves the biography carries (#3633). MedicalOperationSpeed has no StatDefOf field;
    /// the surgery recipes' shared base names it.</summary>
    private static readonly Func<StatDef>[] Biography =
    [
        () => StatDefOf.MedicalTendQuality, () => StatDefOf.MedicalTendSpeed,
        () => RecipeDefOf.RemoveBodyPart.workSpeedStat, () => StatDefOf.MedicalSurgerySuccessChance,
    ];

    private static readonly MethodInfo? BaseBonusAtLevel =
        typeof(SkillNeed_BaseBonus).GetMethod("ValueAtLevel", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The biography's medical curves for a pawn with skills, plus one finding per stat that does not qualify.</summary>
    internal static Obs.PawnBiography Curves(Pawn pawn, Obs.PawnBiography row)
    {
        foreach (var stat in Biography.Select(s => s()))
        {
            var values = Levels(pawn, stat, SkillDefOf.Medicine, out var finding);
            row = values == null
                ? row with { SkillStatFindings = [.. row.SkillStatFindings, stat.defName + ": " + finding] }
                : values.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                    ? row with { SkillStatFindings = [.. row.SkillStatFindings, stat.defName + ": non-finite value"] }
                    : row with { SkillStatCurves = [.. row.SkillStatCurves, new Obs.SkillStatCurve(stat.defName, SkillDefOf.Medicine.defName,
                        EquatableArray.From(values.Select((v, level) => new Obs.SkillStatLevel(level, v)).ToArray()), Provenance)] };
        }
        return row;
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
        if (stat.Worker.GetType() != typeof(StatWorker)) return "custom stat worker";
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
        if (pawn.health.hediffSet.hediffs.Count > 0) return "hediffs present";
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
