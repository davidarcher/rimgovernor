namespace RimGovernor.Brain.Observation;

public static class AnimalUpkeepReads
{
    /// <summary>
    /// Decodes the player herd, each animal joined to its pawn table row; an animal the table does not hold, or
    /// holds without its animal state, leaves the herd unknown until a later frame.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "animals"))
        {
            return Unknown<EquatableArray<UpkeepAnimal>>();
        }
        if (!AnimalRows(u.Animals, pawns, out var resolved))
        {
            return Unknown<EquatableArray<UpkeepAnimal>>();
        }
        var rows = new List<UpkeepAnimal>();
        for (var i = 0; i < u.Animals.Count; i++)
        {
            var a = u.Animals[i];
            var pawn = resolved[i];
            var state = pawn.AnimalState ?? new O.AnimalState();
            var area = Unknown<string>();
            if (state.SupportsAllowedAreas is not null)
            {
                area = Known((state.AllowedAreaId ?? ""));
            }
            var training = new List<HusbandryTrainable>(state.Training.Count);
            foreach (var entry in state.Training)
            {
                training.Add(new HusbandryTrainable
                {
                    Def = entry.DefName,
                    Available = Known(entry.Available),
                    Learned = Known(entry.Learned),
                    Wanted = Known(entry.Wanted),
                });
            }
            var animal = new UpkeepAnimal
            {
                SupportsAreas = ColonyDecodeReads.Optional(state.SupportsAllowedAreas is not null, state.SupportsAllowedAreas.GetValueOrDefault()),
                AllowedArea = area,
                Id = new PawnId(pawn.Pawn.Id ?? ""),
                Label = pawn.Pawn.Label ?? "",
                Gender = state.Gender,
                Definition = new Resource(pawn.Pawn.DefName ?? ""),
                RequiresPen = ColonyDecodeReads.Optional(a.RequiresPen is not null, a.RequiresPen.GetValueOrDefault()),
                Contained = ColonyDecodeReads.Optional(state.Contained is not null, state.Contained.GetValueOrDefault()),
                Release = Known(state.Release),
                Slaughter = Known(state.Slaughter),
                Pen = Known((state.PenId ?? "")),
                SuitablePen = Known(a.SuitablePen?.Id ?? ""),
                SlaughterFacts = new SlaughterFacts
                {
                    Downed = Known(state.Downed),
                    InMentalState = Known(state.InMentalState),
                    Pregnant = Known(state.Pregnant),
                    Mastered = Mastered(state),
                    ColonistBonded = ColonyDecodeReads.Optional(state.ColonistBonded is not null, state.ColonistBonded.GetValueOrDefault()),
                    Designatable = ColonyDecodeReads.Optional(state.SlaughterDesignatable is not null, state.SlaughterDesignatable.GetValueOrDefault()),
                },
                SafeToRelease = ColonyDecodeReads.Optional(state.SafeToRelease is not null, state.SafeToRelease.GetValueOrDefault()),
                Herd = HerdFactsOf(pawn, races),
                Training = EquatableArray.From(training),
                Care = RoundsWorkReads.CareName(state.MedicalCare is not null ? state.MedicalCare.GetValueOrDefault() : null),
                Bonded = ColonyDecodeReads.Optional(state.Bonded is not null, state.Bonded.GetValueOrDefault()),
                BondedPawns = EquatableArray.From(state.BondedPawnIds),
                Master = ColonyDecodeReads.Optional(state.MasterId is not null, (state.MasterId ?? "")),
                FollowDrafted = ColonyDecodeReads.Optional(state.FollowDrafted is not null, state.FollowDrafted.GetValueOrDefault()),
                FollowFieldwork = ColonyDecodeReads.Optional(state.FollowFieldwork is not null, state.FollowFieldwork.GetValueOrDefault()),
                Obedient = ColonyDecodeReads.Optional(state.Obedient is not null, state.Obedient.GetValueOrDefault()),
                Sterilized = ColonyDecodeReads.Optional(state.Sterilized is not null, state.Sterilized.GetValueOrDefault()),
                SterilizeQueued = ColonyDecodeReads.Optional(state.SterilizeQueued is not null, state.SterilizeQueued.GetValueOrDefault()),
            };
            if (state.Conditions is not null)
            {
                var (conditions, lifeThreatening) = CareConditionsRules.CareConditions(state.Conditions);
                animal = animal with { Conditions = conditions, LifeThreatening = lifeThreatening };
            }
            rows.Add(animal);
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Decodes the factionless census the herd Need tames from. A native section issue (bound exceeded, read failed) leaves it unknown.</summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyWildAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "wild_animals"))
        {
            return Unknown<EquatableArray<UpkeepAnimal>>();
        }
        if (!AnimalRows(u.WildAnimals, pawns, out var resolved))
        {
            return Unknown<EquatableArray<UpkeepAnimal>>();
        }
        var rows = new List<UpkeepAnimal>();
        foreach (var pawn in resolved)
        {
            var state = pawn.AnimalState ?? new O.AnimalState();
            var race = RaceOf(pawn, races);
            rows.Add(new UpkeepAnimal
            {
                Id = new PawnId(pawn.Pawn.Id ?? ""),
                Definition = new Resource(pawn.Pawn.DefName ?? ""),
                RequiresPen = Known(false),
                Contained = Known(false),
                Release = Known(false),
                Slaughter = Known(false),
                Pen = Known(""),
                SuitablePen = Known(""),
                Gender = state.Gender,
                Tameable = Known(state.Tameable),
                Tame = Known(state.Tame),
                MinimumHandlingSkill = race.MinimumHandlingSkill,
                Pest = race.Pest,
                Herd = HerdFactsOf(pawn, races),
            });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>
    /// The player's roamers (#3812): each census row with Roam, in census order. Null when native could not read the census
    /// (an "animals" section issue), which a pen credit raises as a required fact.
    /// </summary>
    internal static IReadOnlyList<PenRoamer>? ColonyRoamers(O.ColonyFactsSnapshot v)
    {
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "animals"))
        {
            return null;
        }
        return [.. u.Animals.Where(a => a.Roam is not null).Select(a => new PenRoamer(a.Pawn?.Id ?? "", a.Roam!.Race, a.Roam.MtbDays, a.Roam.MarketValue, a.Roam.ReachesMapEdge))];
    }

    /// <summary>Resolves each feed row's animal against the pawn table, false when one is missing or carries no animal state.</summary>
    private static bool AnimalRows(EquatableArray<O.AnimalFeed> feed, Pawns pawns, out List<O.PawnState> output)
    {
        output = new List<O.PawnState>(feed.Count);
        foreach (var a in feed)
        {
            if (!pawns.TryGetRow(a.Pawn, out var row) || row.AnimalState is null)
            {
                return false;
            }
            output.Add(row);
        }
        return true;
    }

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
        var state = pawn.AnimalState ?? new O.AnimalState();
        var race = RaceOf(pawn, races);
        return new HerdFacts
        {
            AgeYears = Known(state.AgeYears),
            LifeExpectancy = race.LifeExpectancy,
            ManhunterOnTameFail = race.ManhunterOnTameFail,
            Sick = ColonyDecodeReads.Optional(state.Sick is not null, state.Sick.GetValueOrDefault()),
            Adult = Known(state.Adult),
            Venerated = ColonyDecodeReads.Optional(state.Venerated is not null, state.Venerated.GetValueOrDefault()),
            Predator = race.Predator,
            TicksToBirth = ColonyDecodeReads.Optional(state.TicksToBirth is not null, state.TicksToBirth.GetValueOrDefault()),
            LifeStageIndex = Known(state.LifeStageIndex),
            TicksToNextLifeStage = ColonyDecodeReads.Optional(state.TicksToNextLifeStage is not null, state.TicksToNextLifeStage.GetValueOrDefault()),
        };
    }

    /// <summary>Copies each player animal's meat and grazing demand from the colony food channels, matched by animal ID.</summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> MergeHerdFoodFacts(Fact<EquatableArray<UpkeepAnimal>> animals, Fact<FoodChannels> channels)
    {
        if (!animals.TryGetValue(out var rows) || !channels.TryGetValue(out var food))
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
                herd = herd with { MeatNutrition = s.MeatNutrition, FeedPerDay = s.FeedPerDay };
            }
            output.Add(a with { Herd = herd });
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>Whether the animal has a master; unknown until native sends the id.</summary>
    private static Fact<bool> Mastered(O.AnimalState state) =>
        state.MasterId is not null ? Known((state.MasterId ?? "") != "") : Unknown<bool>();
}
