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
}
