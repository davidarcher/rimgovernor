using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// A pest is a wild animal military/pests hunts for what it destroys rather than for meat: an alphabeaver pack
// arrives as a NegativeEvent letter, is factionless, never hostile and not a predator, so no emergency census
// answers it while it defoliates the map. The race rows say which races are pests (AnimalRace.Pest: an animal
// that eats trees); native's NativeHuntAcquisition.PestRace applies the same rule to decide which animals the
// hunt census offers as pest rows, and this decides which rows of the wild-animal census count toward the Need.s
// deficit.
public static partial class PestRules
{
    /// <summary>
    /// Counts the pests in the wild-animal census (every living factionless animal on the map, so a pest
    /// anywhere counts, not only one near the colony).
    /// </summary>
    public static int PestCensus(EquatableArray<UpkeepAnimal> wild) => wild.Count(row => row.Pest);

    /// <summary>
    /// Picks the pest hunts to admit: every undesignated, unheld pest row in native order (nearest the colony
    /// first), one hunt per pest, up to the hunting budget (native admits at most two outstanding hunts per
    /// map) and never more than the census's pest count. An unknown budget admits nothing. Throws when the
    /// pest census is unavailable.
    /// </summary>
    public static IReadOnlyList<AcquisitionSource> SelectPestAcquisition(
        EquatableArray<AcquisitionSource> sources,
        int pests,
        IReadOnlyDictionary<string, bool>? held,
        int huntSlots)
    {
        if (pests < 0)
        {
            throw new DomainException("negative pest census");
        }
        static (double Weight, bool Accepted) Accept(AcquisitionSource row) => (1, row.Hunt && row.Pest);
        return AcquisitionRules.SelectAcquisitionCore(sources, (double)pests, 0.0, Accept, held ?? new Dictionary<string, bool>(), huntSlots);
    }
}
