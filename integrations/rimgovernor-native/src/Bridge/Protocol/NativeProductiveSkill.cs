#nullable enable
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Per-stored-level Cooking work rates for one recipe and station: the recipe's work-speed stat at each
/// level from <see cref="NativeSkillStat"/>, times the observed station speed. Unsupported pipelines stay absent.</summary>
internal static class NativeProductiveSkill
{
    /// <summary>Native during-work learning with rates in the caller's conserved work unit. No orders are issued.</summary>
    internal static Obs.ProductiveSkillWork? ReadWork(Pawn pawn, SkillDef skill, StatDef stat, double amount,
        double rateFactor, double rawXp, bool beforeWork, bool fixedDuration, string provenance)
    {
        var record = pawn.skills?.GetSkill(skill);
        if (record == null || record.TotallyDisabled) return null;
        var values = NativeSkillStat.Levels(pawn, stat, skill, out var finding);
        var rates = values != null
            ? values.Select((v, level) => new Obs.SkillWorkRate(level, Fact.Known(v * rateFactor))).ToArray()
            : new[] { new Obs.SkillWorkRate(record.levelInt, Fact.Known(pawn.GetStatValue(stat) * rateFactor)) };
        return new(NativeSkillLearning.Read(pawn, record), new(skill.defName, Fact.Known(rawXp), true), Fact.Known(amount),
            EquatableArray.From(rates), provenance + "; " + (values != null ? NativeSkillStat.Provenance : finding))
        { CallbackIntervalTicks = NativeSkillLearning.Cadence(pawn), CallbackPhase = NativeSkillLearning.Phase(pawn),
            LearnBeforeWork = beforeWork, FixedDuration = fixedDuration };
    }
    internal static Obs.ProductiveSkillWork? Read(Pawn pawn, RecipeDef recipe, Building_WorkTable bench)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var stat = recipe.workSpeedStat;
        if (learning is null || learning.Skill != SkillDefOf.Cooking.defName || learning.DuringWork
            || !learning.RawXpPerWorkTick.IsKnown || stat == null) return null;
        var values = NativeSkillStat.Levels(pawn, stat, SkillDefOf.Cooking, out _);
        if (values == null) return null;
        var observed = NativeSkillLearning.Read(pawn, pawn.skills.GetSkill(SkillDefOf.Cooking));
        var table = recipe.workTableSpeedStat == null ? 1f : bench.GetStatValue(recipe.workTableSpeedStat);
        var rates = values.Select((value, level) => new Obs.SkillWorkRate(level,
            value > 0 && table > 0 && !float.IsNaN(value) && !float.IsInfinity(value) ? Fact.Known((double)value * table) : default)).ToArray();
        return new(observed, learning, Fact.Known((double)recipe.WorkAmountTotal(null)), EquatableArray.From(rates),
            NativeSkillStat.Provenance + "; observed recipe/table");
    }

    /// <summary>Surgery (#3632): the recipe's native work amount and learning, and the doctor's operation speed per stored level
    /// from <see cref="NativeSkillStat"/> (#3637), so a forecast level-up keeps a rate. A stat it cannot curve keeps the observed
    /// speed at the current level only. A surgery's bill giver is the patient, so no table factor applies.</summary>
    internal static Obs.ProductiveSkillWork? ReadSurgery(Pawn pawn, RecipeDef recipe)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var skill = recipe.workSkill == null ? null : pawn.skills?.GetSkill(recipe.workSkill);
        if (skill == null || skill.TotallyDisabled || learning.DuringWork || !learning.RawXpPerWorkTick.IsKnown) return null;
        static Fact<double> Rate(float speed) => speed > 0 && !float.IsNaN(speed) && !float.IsInfinity(speed) ? Fact.Known((double)speed) : default;
        var values = recipe.workSpeedStat == null ? null : NativeSkillStat.Levels(pawn, recipe.workSpeedStat, recipe.workSkill!, out _);
        var rates = values != null ? values.Select((value, level) => new Obs.SkillWorkRate(level, Rate(value))).ToArray()
            : new[] { new Obs.SkillWorkRate(skill.levelInt, Rate(recipe.workSpeedStat == null ? 1f : pawn.GetStatValue(recipe.workSpeedStat))) };
        return new(NativeSkillLearning.Read(pawn, skill), learning, Fact.Known((double)recipe.WorkAmountTotal(null)), EquatableArray.From(rates),
            values != null ? NativeSkillStat.Provenance + "; native RecipeDef.WorkAmountTotal" : "native RecipeDef.WorkAmountTotal; observed workSpeedStat at the current stored level");
    }
}
