using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The sleeping facts of a planned bed (#3840): what Toils_LayDown gives a pawn lying in one, per bed def an adult sleeps
// in, the cribs a nursery plans, and per stuff the game allows it.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// Per sleeping bed def and allowed stuff ("" for a def made from none), as Toils_LayDown reads a built one at Normal
    /// quality: Ground when its adjusted cost list is empty (ApplyBedThoughts' SleptOnGround test), its rest effectiveness
    /// (the BedRestEffectiveness stat when the def's statBases list it, else the stat's valueIfMissing, as LayDown's tick
    /// applies it) and its Comfort stat (the stat def's default for a def that does not set it). Empty without the stat def.
    /// </summary>
    public IReadOnlyList<P.SleepingBedFacts> SleepingBeds()
    {
        var rows = new List<P.SleepingBedFacts>();
        if (DefRow<StatDef>(nameof(StatDefOf.BedRestEffectiveness)) is not { } rest)
        {
            return rows;
        }

        foreach (var bed in Beds().Sleeping.Concat(Beds().All.Where(b => b.Crib)).OrderBy(b => b.Def, StringComparer.Ordinal))
        {
            var row = _thingDefs[bed.Def];
            var stuffs = AllowedStuffs(bed.Def);
            foreach (var stuff in stuffs.Count == 0 ? [string.Empty] : stuffs)
            {
                var effectiveness = StatBaseEntry(row, rest.defName) is null ? rest.valueIfMissing
                    : PlannerStatValue(bed.Def, stuff, CatalogStats.BedRestEffectiveness);
                rows.Add(new P.SleepingBedFacts(bed.Def, stuff, AdjustedCosts(bed.Def, stuff).Count == 0, effectiveness,
                    PlannerStatValue(bed.Def, stuff, CatalogStats.Comfort)));
            }
        }

        return rows;
    }
}
