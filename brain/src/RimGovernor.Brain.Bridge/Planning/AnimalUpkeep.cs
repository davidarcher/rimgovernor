using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;
namespace RimGovernor.Brain.Bridge;

public static class AnimalUpkeepReads
{
    /// <summary>
    /// Decodes the player herd, each animal joined to its pawn table row; an animal the table does not hold, or
    /// holds without its animal state, throws.
    /// </summary>
    internal static EquatableArray<UpkeepAnimal> ColonyAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep;
        var resolved = AnimalRows(u.Animals.Select(a => a.Pawn), pawns);
        var rows = new List<UpkeepAnimal>();
        for (var i = 0; i < u.Animals.Count; i++)
        {
            var a = u.Animals[i];
            var pawn = resolved[i];
            var animal = new UpkeepAnimal
            {
                Pawn = pawn,
                Colony = a,
                Herd = HerdFactsOf(pawn, races),
            };
            rows.Add(animal);
        }
        return EquatableArray.From(rows);
    }

    /// <summary>Decodes the factionless census the herd Need tames from; a row the pawn table does not hold as an animal throws.</summary>
    internal static EquatableArray<UpkeepAnimal> ColonyWildAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var resolved = AnimalRows(v.Upkeep.WildAnimals, pawns);
        var rows = new List<UpkeepAnimal>();
        foreach (var pawn in resolved)
        {
            var race = RaceOf(pawn, races);
            rows.Add(new UpkeepAnimal
            {
                Pawn = pawn,
                MinimumHandlingSkill = race.MinimumHandlingSkill,
                Pest = race.Pest,
                Herd = HerdFactsOf(pawn, races),
            });
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// The player's roamers (#3812): each roped census row with its roam, in census order.
    /// </summary>
    internal static IReadOnlyList<O.ColonyAnimal> ColonyRoamers(O.ColonyFactsSnapshot v) =>
        [.. v.Upkeep.Animals.Where(a => a.Roping is O.AnimalRoping.Roped)];

    /// <summary>Resolves each census animal against the pawn table; one missing or carrying no animal state throws.</summary>
    private static List<O.PawnState> AnimalRows(IEnumerable<PawnId> ids, Pawns pawns)
    {
        var output = new List<O.PawnState>();
        foreach (var id in ids)
        {
            if (!pawns.TryRow(new C.Ref { Id = id.Value }, out var row) || row.AnimalState is not O.PawnAnimalState.Animal)
            {
                throw new ObservationContractException($"upkeep animal {id.Value} has no animal row in the pawn table");
            }
            output.Add(row);
        }
        return output;
    }

    /// <summary>The animal block of a row the upkeep read resolved as an animal; any other row is a contract break.</summary>
    private static O.AnimalState AnimalOf(O.PawnState pawn) =>
        pawn.AnimalState is O.PawnAnimalState.Animal animal
            ? animal.State
            : throw new ObservationContractException($"pawn {(pawn.Pawn.Id ?? "")} is an upkeep animal without an animal state");

    /// <summary>The catalog row of the pawn's race; a race the catalog does not hold is the zero row, whose numbers are unknown.</summary>
    private static AnimalRace RaceOf(O.PawnState pawn, AnimalRaceCatalog races)
    {
        races.Race(new Resource(pawn.Pawn.DefName ?? ""), out var race);
        return race;
    }

    /// <summary>
    /// Decodes the native herd sizing facts with the race's own numbers from the catalog's race row; food-channel
    /// costs and products merge in later through <see cref="MergeHerdFoodFacts"/>.
    /// </summary>
    private static HerdFacts HerdFactsOf(O.PawnState pawn, AnimalRaceCatalog races)
    {
        var state = AnimalOf(pawn);
        var race = RaceOf(pawn, races);
        return new HerdFacts
        {
            LifeExpectancy = race.LifeExpectancy,
            ManhunterOnTameFail = race.ManhunterOnTameFail,
            Predator = race.Predator,
            Feed = FoodChannelsReads.FeedOf(race, state),
        };
    }

    /// <summary>Copies each player animal's meat nutrition from the colony food channels, matched by animal ID.</summary>
    internal static EquatableArray<UpkeepAnimal> MergeHerdFoodFacts(EquatableArray<UpkeepAnimal> animals, FoodChannelPlanning food)
    {
        var slaughter = new Dictionary<PawnId, SlaughterFoodAnimal>();
        foreach (var s in food.Slaughter)
        {
            slaughter[s.ID] = s;
        }
        var output = new List<UpkeepAnimal>(animals.Count);
        foreach (var a in animals)
        {
            var herd = a.Herd;
            if (slaughter.TryGetValue(a.Id, out var s))
            {
                herd = herd with { MeatNutrition = s.MeatNutrition };
            }
            output.Add(a with { Herd = herd });
        }
        return EquatableArray.From(output);
    }

}
