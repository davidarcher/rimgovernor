namespace RimGovernor.Brain.Policy;

public static class RoundsFactsSaleExtensions
{
    /// <summary>Go <c>RoundsFacts.SaleAnimals</c>: <see cref="PolicyFunctions.HerdSaleAnimals"/> over the herd plan of f.</summary>
    public static IReadOnlySet<PawnId> SaleAnimals(this RoundsFacts f) => PolicyFunctions.HerdSaleAnimals(f.AnimalUpkeep.Animals, f.HerdPolicy());
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>HerdSaleAnimals</c>: the colony animals the herd plan lets a trader take: the animals
    /// HerdSurplusCandidates lists over each race's ceiling (every animal of a retired race, young ones
    /// included), less any that is bonded (any living Bond relation, whether or not HerdMasterChoice found the
    /// partner on the roster to master it) or, outside a retired race, has a master (HerdMasterChoice gives the
    /// animals the colony works). Founders and companions have no ceiling, so none is listed. An unread census or
    /// any unknown designation, bond or master sells nothing. Animals already designated for slaughter or release
    /// are not listed: the cull owns them. The result is empty when nothing is sold.
    /// </summary>
    public static IReadOnlySet<PawnId> HerdSaleAnimals(Fact<IReadOnlyList<UpkeepAnimal>> animals, HerdPolicy herd)
    {
        var @out = new HashSet<PawnId>();
        if (!animals.TryGetValue(out var rows))
        {
            return @out;
        }
        var (removals, unknown) = HerdSurplusCandidates(rows, herd.PopulationMax, true, herd);
        if (unknown)
        {
            return @out;
        }
        foreach (var r in removals)
        {
            var a = r.Animal;
            var mk = a.Master.TryGetValue(out var master);
            if (!a.Bonded.TryGetValue(out var bonded) || bonded || !mk || (!master.IsEmpty && !herd.Retired.GetValueOrDefault(a.Definition)))
            {
                continue;
            }
            @out.Add(a.ID);
        }
        return @out;
    }

    /// <summary>
    /// Go <c>AnimalSaleNeed</c>: adds the animal-sale reason to the need: sale animals and a known silver
    /// shortfall (SilverShort). Anything unknown adds nothing.
    /// </summary>
    public static Fact<TradeNeed> AnimalSaleNeed(ItemFacts items, Fact<TradeNeed> need, IReadOnlySet<PawnId> sale, Fact<long> silver, Fact<long> colonists)
    {
        if (!need.TryGetValue(out var n) || sale.Count == 0 || !Positive(SilverShort(items, need, silver, colonists)))
        {
            return need;
        }
        return Known(n with { SurplusAnimals = sale.Count });
    }
}
