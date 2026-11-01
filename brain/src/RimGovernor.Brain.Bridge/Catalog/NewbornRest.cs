using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The rest rates a newborn will have (RG-HABITAT-007), read from the defs before any baby exists: Need_Rest's fall
// (BaseRestFallPerTick, the Rested category's rate, times the RestFallRateFactor stat) and gain (BaseRestGainPerTick times
// the bed's rest effectiveness and the RestRateMultiplier stat), each stat as the Human race's statBases (else the stat's
// defaultBaseValue) give it, plus the HumanlikeBaby life stage's statOffsets, times its statFactors, floored at the stat's
// minValue (StatWorker's order). A healthy newborn carries no hediff and full capacities, so the hediff RestFallFactor and the
// capacity factors are 1; genes are unknown before the birth and left out.

public sealed partial class DefinitionCatalog
{
    /// <summary>The expected newborn's rest rates; null when the catalog lacks the Human race, the HumanlikeBaby life stage or a rest stat.</summary>
    public P.NewbornRestRates? NewbornRest()
    {
        if (DefRow<ThingDef>(nameof(ThingDefOf.Human)) is not { } race || DefRow<LifeStageDef>(nameof(LifeStageDefOf.HumanlikeBaby)) is not { } stage
            || DefRow<StatDef>(nameof(StatDefOf.RestFallRateFactor)) is not { } fall || DefRow<StatDef>(nameof(StatDefOf.RestRateMultiplier)) is not { } rate
            || DefRow<StatDef>(nameof(StatDefOf.BedRestEffectiveness)) is not { } bed)
        {
            return null;
        }

        var multiplier = NewbornStat(race, stage, rate);
        return new P.NewbornRestRates(
            GameConstants.RestFallPerTick * NewbornStat(race, stage, fall),
            Need_Rest.BaseRestGainPerTick * bed.valueIfMissing * multiplier,
            Need_Rest.BaseRestGainPerTick * multiplier);
    }

    private static double NewbornStat(ThingDef race, LifeStageDef stage, StatDef stat) =>
        Math.Max(stat.minValue, (Listed(race.statBases, stat, stat.defaultBaseValue) + Listed(stage.statOffsets, stat, 0f)) * Listed(stage.statFactors, stat, 1f));

    // StatUtility.GetStatValueFromList over a def's modifier list: the first entry for the stat, else the default.
    private static float Listed(List<StatModifier>? modifiers, StatDef stat, float otherwise)
    {
        foreach (var m in modifiers ?? [])
        {
            if (m.stat == stat)
            {
                return m.value;
            }
        }

        return otherwise;
    }
}
