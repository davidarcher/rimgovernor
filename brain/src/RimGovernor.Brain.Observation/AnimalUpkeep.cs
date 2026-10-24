namespace RimGovernor.Brain.Observation;

public static class AnimalUpkeepReads
{
    /// <summary>
    /// Decodes the player herd, each animal joined to its pawn table row; an animal the table does not hold, or
    /// holds without its animal state, throws.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep;
        var resolved = AnimalRows(u.Animals.Select(a => a.Pawn), pawns);
        var rows = new List<UpkeepAnimal>();
        for (var i = 0; i < u.Animals.Count; i++)
        {
            var a = u.Animals[i];
            var pawn = resolved[i];
            var state = AnimalOf(pawn);
            var training = new List<HusbandryTrainable>(state.Training.Count);
            foreach (var entry in state.Training)
            {
                training.Add(new HusbandryTrainable
                {
                    Def = entry.DefName,
                    Available = entry.Available,
                    Learned = entry.Learned,
                    Wanted = entry.Wanted,
                });
            }
            var animal = new UpkeepAnimal
            {
                SupportsAreas = state.SupportsAllowedAreas,
                AllowedArea = pawn.Settings.AllowedAreaId ?? "",
                Id = new PawnId(pawn.Pawn.Id ?? ""),
                Label = pawn.Pawn.Label ?? "",
                Gender = state.Gender,
                Definition = new Resource(pawn.Pawn.DefName ?? ""),
                RequiresPen = a.Roping is O.AnimalRoping.Roped,
                Contained = state.PenId is not null,
                Release = state.Release,
                Slaughter = state.Slaughter,
                Pen = state.PenId ?? "",
                SuitablePen = a.Roping is O.AnimalRoping.Roped { SuitablePen: { } suitable } ? suitable : "",
                SlaughterFacts = new SlaughterFacts
                {
                    Downed = state.Downed,
                    InMentalState = state.InMentalState,
                    Pregnant = state.Pregnancy is not O.AnimalPregnancy.NotPregnant,
                    Mastered = state.MasterId.Length != 0,
                    ColonistBonded = state.ColonistBonded,
                    Designatable = state.SlaughterDesignatable,
                },
                SafeToRelease = state.SafeToRelease,
                Herd = HerdFactsOf(pawn, races),
                Training = EquatableArray.From(training),
                Care = pawn.Settings.Care is O.PawnCare.Configured care ? WireEnums.MedicalCareName(care.MedicalCare) : "",
                Bonded = state.Bonded,
                BondedPawns = EquatableArray.From(state.BondedPawnIds),
                Master = state.MasterId,
                FollowDrafted = state.FollowDrafted,
                FollowFieldwork = state.FollowFieldwork,
                Obedient = state.Obedient,
                Sterilized = state.Sterilized,
                SterilizeQueued = state.SterilizeQueued,
                Conditions = pawn.Health.Hediffs,
                LifeThreatening = pawn.Health.LifeThreatening,
            };
            rows.Add(animal);
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Decodes the factionless census the herd Need tames from; a row the pawn table does not hold as an animal throws.</summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyWildAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var resolved = AnimalRows(v.Upkeep.WildAnimals, pawns);
        var rows = new List<UpkeepAnimal>();
        foreach (var pawn in resolved)
        {
            var state = AnimalOf(pawn);
            var race = RaceOf(pawn, races);
            rows.Add(new UpkeepAnimal
            {
                Id = new PawnId(pawn.Pawn.Id ?? ""),
                Definition = new Resource(pawn.Pawn.DefName ?? ""),
                RequiresPen = false,
                Contained = false,
                Release = state.Release,
                Slaughter = state.Slaughter,
                Bonded = state.Bonded,
                Sterilized = state.Sterilized,
                Conditions = pawn.Health.Hediffs,
                LifeThreatening = pawn.Health.LifeThreatening,
                Gender = state.Gender,
                Tameable = state.Tameable,
                Tame = state.Tame,
                MinimumHandlingSkill = race.MinimumHandlingSkill,
                Pest = race.Pest,
                Herd = HerdFactsOf(pawn, races),
            });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>
    /// The player's roamers (#3812): each roped census row with its roam, in census order.
    /// </summary>
    internal static IReadOnlyList<PenRoamer> ColonyRoamers(O.ColonyFactsSnapshot v)
    {
        var roamers = new List<PenRoamer>();
        foreach (var a in v.Upkeep.Animals)
        {
            if (a.Roping is O.AnimalRoping.Roped { Roam: var roam })
            {
                roamers.Add(new PenRoamer(a.Pawn.Value, roam.Race, roam.MtbDays, roam.MarketValue, roam.ReachesMapEdge));
            }
        }
        return roamers;
    }

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
            AgeYears = state.AgeYears,
            LifeExpectancy = race.LifeExpectancy,
            ManhunterOnTameFail = race.ManhunterOnTameFail,
            Sick = state.Sick,
            Adult = state.Adult,
            Venerated = state.Venerated,
            Predator = race.Predator,
            Pregnancy = state.Pregnancy,
            LifeStageIndex = state.LifeStageIndex,
            NextLifeStage = state.NextLifeStage,
            Feed = FoodChannelsReads.FeedOf(race, state),
        };
    }

    /// <summary>Copies each player animal's meat nutrition from the colony food channels, matched by animal ID.</summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> MergeHerdFoodFacts(Fact<EquatableArray<UpkeepAnimal>> animals, FoodChannels food)
    {
        if (!animals.TryGetValue(out var rows))
        {
            return animals;
        }
        var slaughter = new Dictionary<PawnId, SlaughterFoodAnimal>();
        foreach (var s in food.Slaughter)
        {
            slaughter[s.ID] = s;
        }
        var output = new List<UpkeepAnimal>(rows.Count);
        foreach (var a in rows)
        {
            var herd = a.Herd;
            if (slaughter.TryGetValue(a.Id, out var s))
            {
                herd = herd with { MeatNutrition = s.MeatNutrition };
            }
            output.Add(a with { Herd = herd });
        }
        return Known(EquatableArray.From(output));
    }

}
