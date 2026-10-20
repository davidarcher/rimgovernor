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

    /// <summary>Surgery (#3632): the recipe's native work amount and learning, and the doctor's observed operation speed at the
    /// current stored level only. A surgery's bill giver is the patient, so no table factor applies; other levels are absent.</summary>
    internal static Obs.ProductiveSkillWork? ReadSurgery(Pawn pawn, RecipeDef recipe)
    {
        var learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe);
        var skill = recipe.workSkill == null ? null : pawn.skills?.GetSkill(recipe.workSkill);
        if (skill == null || skill.TotallyDisabled || learning.DuringWork || !learning.RawXpPerWorkTick.IsKnown) return null;
        var speed = recipe.workSpeedStat == null ? 1f : pawn.GetStatValue(recipe.workSpeedStat);
        var rate = speed > 0 && !float.IsNaN(speed) && !float.IsInfinity(speed) ? Fact.Known((double)speed) : default;
        return new(NativeSkillLearning.Read(pawn, skill), learning, Fact.Known((double)recipe.WorkAmountTotal(null)),
            EquatableArray.From(new[] { new Obs.SkillWorkRate(skill.levelInt, rate) }),
            "native RecipeDef.WorkAmountTotal; observed workSpeedStat at the current stored level");
    }
}
