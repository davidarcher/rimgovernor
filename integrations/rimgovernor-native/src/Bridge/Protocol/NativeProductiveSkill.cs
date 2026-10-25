using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>Per-stored-level productive work rates for one recipe and station: the recipe's work-speed stat at each
/// level from <see cref="NativeSkillStat"/>, times the observed station speed. Unsupported pipelines stay absent.</summary>
internal static class NativeProductiveSkill
{
    /// <summary>Native during-work learning with rates in the caller's conserved work unit. No orders are issued.</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.ProductiveSkillWork? ReadWork(Pawn pawn, SkillDef skill, StatDef stat, double amount,
        double rateFactor, double rawXp, bool beforeWork, bool fixedDuration, string provenance)
    {
        var record = pawn.skills?.GetSkill(skill);
        if (record == null || record.TotallyDisabled) return null;
        var values = NativeSkillStat.Levels(pawn, stat, skill, out var finding);
        var rates = values != null
            ? values.Select((v, level) => new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(level, v * rateFactor)).ToArray()
            : new[] { new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(record.levelInt, pawn.GetStatValue(stat) * rateFactor) };
        return new(NativeSkillLearning.Read(pawn, record), new(skill.defName, rawXp, true), amount,
            EquatableArray.From(rates), provenance + "; " + (values != null ? NativeSkillStat.Provenance : finding))
        { CallbackIntervalTicks = NativeSkillLearning.Cadence(pawn), CallbackPhase = NativeSkillLearning.Phase(pawn), CallbackElapsedTicks = NativeSkillLearning.Elapsed(pawn),
            LearnBeforeWork = beforeWork, FixedDuration = fixedDuration };
    }
    internal static RimGovernor.Brain.GameFacts.Observations.ProductiveSkillWork? Read(Pawn pawn, RecipeDef recipe, Building_WorkTable bench, double? remaining = null)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var skill = recipe.workSkill;
        var record = skill == null ? null : pawn.skills?.GetSkill(skill);
        if (record == null || record.TotallyDisabled || learning == null) return null;
        var table = recipe.workTableSpeedStat == null ? 1f : bench.GetStatValue(recipe.workTableSpeedStat);
        var values = recipe.workSpeedStat == null ? Enumerable.Repeat(1f, 21).ToArray()
            : NativeSkillStat.Levels(pawn, recipe.workSpeedStat, (skill ?? throw new System.InvalidOperationException("Required native value is unavailable: skill.")), out _);
        var rates = values != null ? values.Select((value, level) => new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(level, (double)value * table)).ToArray()
            : new[] { new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(record.levelInt, (double)pawn.GetStatValue(recipe.workSpeedStat) * table) };
        return new(NativeSkillLearning.Read(pawn, record), learning, remaining ?? recipe.WorkAmountTotal(null), EquatableArray.From(rates),
            NativeSkillStat.Provenance + "; Toils_Recipe callback; observed recipe/table")
        { CallbackIntervalTicks = NativeSkillLearning.Cadence(pawn), CallbackPhase = NativeSkillLearning.Phase(pawn), CallbackElapsedTicks = NativeSkillLearning.Elapsed(pawn), LearnBeforeWork = true };
    }
    /// <summary>Surgery (#3632): the recipe's native work amount and learning, and the doctor's operation speed per stored level
    /// from <see cref="NativeSkillStat"/> (#3637), so a forecast level-up keeps a rate. A stat it cannot curve keeps the observed
    /// speed at the current level only. A surgery's bill giver is the patient, so no table factor applies.</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.ProductiveSkillWork? ReadSurgery(Pawn pawn, RecipeDef recipe)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var skill = recipe.workSkill == null ? null : pawn.skills?.GetSkill(recipe.workSkill);
        if (skill == null || skill.TotallyDisabled || learning == null) return null;
        double Rate(float speed) => !float.IsNaN(speed) && !float.IsInfinity(speed) ? speed : throw new System.InvalidOperationException("surgery speed " + pawn.GetUniqueLoadID() + " " + recipe.defName + " is not finite");
        var values = recipe.workSpeedStat == null ? null : NativeSkillStat.Levels(pawn, recipe.workSpeedStat, (recipe.workSkill ?? throw new System.InvalidOperationException("Required native value is unavailable: recipe.workSkill.")), out _);
        var rates = values != null ? values.Select((value, level) => new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(level, Rate(value))).ToArray()
            : new[] { new RimGovernor.Brain.GameFacts.Observations.SkillWorkRate(skill.levelInt, Rate(recipe.workSpeedStat == null ? 1f : pawn.GetStatValue(recipe.workSpeedStat))) };
        return new(NativeSkillLearning.Read(pawn, skill), learning, (double)recipe.WorkAmountTotal(null), EquatableArray.From(rates),
            values != null ? NativeSkillStat.Provenance + "; native RecipeDef.WorkAmountTotal" : "native RecipeDef.WorkAmountTotal; observed workSpeedStat at the current stored level")
        { CallbackIntervalTicks = NativeSkillLearning.Cadence(pawn), CallbackPhase = NativeSkillLearning.Phase(pawn), CallbackElapsedTicks = NativeSkillLearning.Elapsed(pawn), LearnBeforeWork = true };
    }
}
