#nullable enable
using System;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Qualified read-only counterfactual: replace a native skill offset before native finalization.
/// No pawn, skill, definition or stat cache is rewritten. Unsupported modifier pipelines stay absent.</summary>
internal static class NativeProductiveSkill
{
    internal static Obs.ProductiveSkillWork? Read(Pawn pawn, RecipeDef recipe, Building_WorkTable bench)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var stat = recipe.workSpeedStat;
        if (learning is null || learning.Skill != SkillDefOf.Cooking.defName || learning.DuringWork
            || !learning.RawXpPerWorkTick.IsKnown || stat == null || stat.Worker.GetType() != typeof(StatWorker)
            || stat.skillNeedOffsets == null || stat.skillNeedOffsets.Count != 1
            || stat.skillNeedOffsets[0] is not SkillNeed_BaseBonus skillNeed || skillNeed.skill != SkillDefOf.Cooking
            || skillNeed.GetType() != typeof(SkillNeed_BaseBonus)
            || stat.skillNeedFactors?.Count > 0 || stat.statFactors?.Count > 0 || stat.capacityFactors?.Count > 0
            || stat.parts?.Count > 0 || pawn.Inspired || pawn.Stuff != null
            || pawn.health.hediffSet.hediffs.Count > 0
            || pawn.story?.traits.allTraits.Any(t => !t.Suppressed && t.MultiplierOfStat(stat) != 1f) == true
            || pawn.ageTracker.CurLifeStage.statFactors.GetStatFactorFromList(stat) != 1f
            || pawn.Ideo != null && (pawn.Ideo.PreceptsListForReading.Any(p => p.def.conditionalStatAffecters?.Count > 0
                || p.def.statFactors.GetStatFactorFromList(stat) != 1f)
                || pawn.Ideo.GetRole(pawn)?.def.roleEffects?.OfType<RoleEffect_PawnStatFactor>().Any(r => r.statDef == stat && r.modifier != 1f) == true)
            || pawn.genes?.GenesListForReading.Any(g => g.Active && (g.def.conditionalStatAffecters?.Count > 0
                || g.def.statFactors.GetStatFactorFromList(stat) != 1f)) == true
            || pawn.AllComps.Any(c => c.GetType().GetMethod(nameof(ThingComp.GetStatOffset))?.DeclaringType != typeof(ThingComp)
                || c.GetType().GetMethod(nameof(ThingComp.GetStatFactor))?.DeclaringType != typeof(ThingComp))) return null;
        // CookSpeed's Core postfactor is native WorkSpeedGlobal. Its observed value may depend on
        // current condition, but no supported nested pipeline may depend on Cooking itself.
        bool Independent(StatDef factor) => factor.skillNeedOffsets?.Any(s => s.skill == SkillDefOf.Cooking) != true
            && factor.skillNeedFactors?.Any(s => s.skill == SkillDefOf.Cooking) != true
            && factor.statFactors?.Count is not > 0 && factor.postProcessStatFactors?.Count is not > 0
            && factor.Worker.GetType() == typeof(StatWorker)
            && factor.parts?.Any(p => p.GetType() != typeof(StatPart_Glow) && p.GetType() != typeof(StatPart_Slave)
                && p.GetType() != typeof(StatPart_Age)
                && !(p.GetType() == typeof(StatPart_OverseerStatOffset) && !pawn.RaceProps.IsMechanoid)) != true;
        if (stat.postProcessStatFactors?.Any(f => !Independent(f)) == true) return null;
        var evaluate = typeof(SkillNeed_BaseBonus).GetMethod("ValueAtLevel", BindingFlags.Instance | BindingFlags.NonPublic);
        var skill = pawn.skills?.GetSkill(SkillDefOf.Cooking);
        if (evaluate == null || skill == null || skill.TotallyDisabled) return null;
        var observed = NativeSkillLearning.Read(pawn, skill);
        var request = StatRequest.For(pawn);
        var original = stat.Worker.GetValueUnfinalized(request);
        var currentOffset = skillNeed.ValueFor(pawn);
        var table = recipe.workTableSpeedStat == null ? 1f : bench.GetStatValue(recipe.workTableSpeedStat);
        var rates = Enumerable.Range(SkillRecord.MinLevel, SkillRecord.MaxLevel + 1).Select(level =>
        {
            var effective = Math.Clamp(level + skill.Aptitude, SkillRecord.MinLevel, SkillRecord.MaxLevel);
            var value = original - currentOffset + (float)evaluate.Invoke(skillNeed, new object[] { effective })!;
            stat.Worker.FinalizeValue(request, ref value, true);
            return new Obs.SkillWorkRate(level, value > 0 && table > 0 && !float.IsNaN(value) && !float.IsInfinity(value)
                ? Fact.Known((double)value * table) : default);
        }).ToArray();
        return new(observed, learning, Fact.Known((double)recipe.WorkAmountTotal(null)), EquatableArray.From(rates),
            "native SkillNeed_BaseBonus.ValueAtLevel -> StatWorker.FinalizeValue; observed recipe/table; neutral prefinal factors");
    }
}
