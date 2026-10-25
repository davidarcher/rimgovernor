namespace RimGovernor.Brain.Policy;

public static class FactsSaleExtensions
{
    /// <summary><see cref="HerdSaleRules.HerdSaleAnimals"/> over the herd plan of f.</summary>
    public static IReadOnlySet<PawnId> SaleAnimals(this ReviewFacts f) => HerdSaleRules.HerdSaleAnimals(f.AnimalUpkeep.Animals, f.HerdPolicy());
}

public static class HerdSaleRules
{
    /// <summary>
    /// The colony animals the herd plan lets a trader take: the animals
    /// HerdSurplusCandidates lists over each race's ceiling (every animal of a retired race, young ones
    /// included), less any that is bonded (any living Bond relation, whether or not HerdMasterChoice found the
    /// partner on the roster to master it) or, outside a retired race, has a master (HerdMasterChoice gives the
    /// animals the colony works). Founders and companions have no ceiling, so none is listed. A
    /// bonded or mastered animal is not sold. Animals already designated for slaughter or release
    /// are not listed: the cull owns them. The result is empty when nothing is sold.
    /// </summary>
    public static IReadOnlySet<PawnId> HerdSaleAnimals(EquatableArray<UpkeepAnimal> animals, HerdPolicy herd)
    {
        var @out = new HashSet<PawnId>();
        foreach (var r in HerdSurplusRules.HerdSurplusCandidates(animals, herd.PopulationMax, true, herd))
        {
            var a = r.Animal;
            if (a.Bonded || (a.Master.Length != 0 && !herd.Retired.GetValueOrDefault(a.Definition)))
            {
                continue;
            }
            @out.Add(a.Id);
        }
        return @out;
    }

    /// <summary>
    /// Adds the animal-sale reason to the need: sale animals and a known silver
    /// shortfall (SilverShort). Anything unknown adds nothing.
    /// </summary>
    public static TradeNeed AnimalSaleNeed(ItemFacts items, TradeNeed need, IReadOnlySet<PawnId> sale, long silver, long colonists)
    {
        var n = need;
        if (sale.Count == 0 || !SilverGapRules.SilverShort(items, need, silver, colonists))
        {
            return need;
        }
        return n with { SurplusAnimals = sale.Count };
    }
}
