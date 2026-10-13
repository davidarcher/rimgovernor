using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

// A pest is a wild animal ClearPests hunts for what it destroys rather than for meat: an alphabeaver pack
// arrives as a NegativeEvent letter, is factionless, never hostile and not a predator, so no emergency census
// answers it while it defoliates the map. The race rows say which races are pests (AnimalRace.Pest: an animal
// that eats trees); native's NativeHuntAcquisition.PestRace applies the same rule to decide which animals the
// hunt census offers as pest rows, and this decides which rows of the wild-animal census count as the goal's
// deficit.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Counts the pests in the wild-animal census (every living factionless animal on the map, so a pest
    /// anywhere counts, not only one near the colony). Unknown while the census is unknown.
    /// </summary>
    public static Fact<int> PestCensus(Fact<EquatableArray<UpkeepAnimal>> wild)
    {
        if (!wild.TryGetValue(out var rows))
        {
            return Unknown<int>();
        }
        return Known(rows.Count(row => row.Pest));
    }

    /// <summary>
    /// Picks the pest hunts to admit: every undesignated, unheld pest row in native order (nearest the colony
    /// first), one hunt per pest, up to the hunting budget (native admits at most two outstanding hunts per
    /// map) and never more than the census's pest count. An unknown budget admits nothing. Throws when the
    /// pest census is unavailable.
    /// </summary>
    public static IReadOnlyList<AcquisitionSource> SelectPestAcquisition(
        Fact<EquatableArray<AcquisitionSource>> sources,
        Fact<int> pests,
        IReadOnlyDictionary<string, bool>? held,
        Fact<int> huntSlots)
    {
        if (!pests.TryGetValue(out var count) || count < 0)
        {
            throw new DomainException("pest census unavailable");
        }
        static (double Weight, bool Accepted) Accept(AcquisitionSource row) => (1, row.Hunt && row.Pest);
        return SelectAcquisitionCore(sources, Known((double)count), Known(0.0), Accept, held ?? new Dictionary<string, bool>(), huntSlots);
    }
}
