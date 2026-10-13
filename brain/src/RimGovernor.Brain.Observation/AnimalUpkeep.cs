namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>
    /// Decodes the player herd, each animal joined to its pawn table row; an animal the table does not hold, or
    /// holds without its animal state, leaves the herd unknown until a later frame.
    /// </summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep?.Observed;
        if (u is null || HasIssue(u.Issues, "animals"))
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
            var state = pawn.AnimalState;
            var area = Unknown<string>();
            if (state.HasSupportsAllowedAreas && !HasIssue(state.Issues, "allowed_area"))
            {
                area = Known(state.AllowedAreaId);
            }
            var training = new List<HusbandryTrainable>(state.Training.Count);
            foreach (var entry in state.Training)
            {
                training.Add(new HusbandryTrainable
                {
                    Def = entry.DefName,
                    Available = Optional(entry.HasAvailable, entry.Available),
                    Learned = Optional(entry.HasLearned, entry.Learned),
                    Wanted = Optional(entry.HasWanted, entry.Wanted),
                });
            }
            var animal = new UpkeepAnimal
            {
                SupportsAreas = Optional(state.HasSupportsAllowedAreas, state.SupportsAllowedAreas),
                AllowedArea = area,
                ID = new PawnId(pawn.Pawn?.Id ?? ""),
                Label = pawn.Pawn?.Label ?? "",
                Gender = state.Gender,
                Definition = new Resource(pawn.Pawn?.DefName ?? ""),
                RequiresPen = Optional(a.HasRequiresPen, a.RequiresPen),
                Contained = Optional(state.HasContained, state.Contained),
                Release = Optional(state.HasRelease, state.Release),
                Slaughter = Optional(state.HasSlaughter, state.Slaughter),
                Pen = Known(state.PenId),
                SuitablePen = Known(a.SuitablePen?.Id ?? ""),
                SlaughterFacts = new SlaughterFacts
                {
                    Downed = Optional(state.HasDowned, state.Downed),
                    InMentalState = Optional(state.HasInMentalState, state.InMentalState),
                    Pregnant = Optional(state.HasPregnant, state.Pregnant),
                    Mastered = Mastered(state),
                    ColonistBonded = Optional(state.HasColonistBonded, state.ColonistBonded),
                    Designatable = Optional(state.HasSlaughterDesignatable, state.SlaughterDesignatable),
                },
                SafeToRelease = Optional(state.HasSafeToRelease, state.SafeToRelease),
                Herd = HerdFactsOf(pawn, races),
                Training = EquatableArray.From(training),
                Care = CareName(state.HasMedicalCare ? state.MedicalCare : null),
                Bonded = Optional(state.HasBonded, state.Bonded),
                BondedPawns = EquatableArray.From(state.BondedPawnIds),
                Master = Optional(state.HasMasterId, state.MasterId),
                FollowDrafted = Optional(state.HasFollowDrafted, state.FollowDrafted),
                FollowFieldwork = Optional(state.HasFollowFieldwork, state.FollowFieldwork),
                Obedient = Optional(state.HasObedient, state.Obedient),
                Sterilized = Optional(state.HasSterilized, state.Sterilized),
                SterilizeQueued = Optional(state.HasSterilizeQueued, state.SterilizeQueued),
            };
            if (state.Conditions is not null)
            {
                var (conditions, lifeThreatening) = CareConditions(state.Conditions);
                animal = animal with { Conditions = conditions, LifeThreatening = lifeThreatening };
            }
            rows.Add(animal);
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Decodes the factionless census MaintainHerd tames from. A native section issue (bound exceeded, read failed) leaves it unknown.</summary>
    internal static Fact<EquatableArray<UpkeepAnimal>> ColonyWildAnimals(O.ColonyFactsSnapshot v, Pawns pawns, AnimalRaceCatalog races)
    {
        var u = v.Upkeep?.Observed;
        if (u is null || HasIssue(u.Issues, "wild_animals"))
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
            var state = pawn.AnimalState;
            var race = RaceOf(pawn, races);
            rows.Add(new UpkeepAnimal
            {
                ID = new PawnId(pawn.Pawn?.Id ?? ""),
                Definition = new Resource(pawn.Pawn?.DefName ?? ""),
                RequiresPen = Known(false),
                Contained = Known(false),
                Release = Known(false),
                Slaughter = Known(false),
                Pen = Known(""),
                SuitablePen = Known(""),
                Gender = state.Gender,
                Tameable = Optional(state.HasTameable, state.Tameable),
                Tame = Optional(state.HasTame, state.Tame),
                MinimumHandlingSkill = race.MinimumHandlingSkill,
                Pest = race.Pest,
                Herd = HerdFactsOf(pawn, races),
            });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Resolves each feed row's animal against the pawn table, false when one is missing or carries no animal state.</summary>
    private static bool AnimalRows(IReadOnlyList<O.AnimalFeed> feed, Pawns pawns, out List<O.PawnState> output)
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
        races.Race(new Resource(pawn.Pawn?.DefName ?? ""), out var race);
        return race;
    }

    /// <summary>
    /// Decodes the native herd sizing facts with the race's own numbers from the catalog's race row; food-channel
    /// costs and products merge in later through <see cref="MergeHerdFoodFacts"/>.
    /// </summary>
    private static HerdFacts HerdFactsOf(O.PawnState pawn, AnimalRaceCatalog races)
    {
        var state = pawn.AnimalState;
        var race = RaceOf(pawn, races);
        return new HerdFacts
        {
            AgeYears = Optional(state.HasAgeYears, state.AgeYears),
            LifeExpectancy = race.LifeExpectancy,
            ManhunterOnTameFail = race.ManhunterOnTameFail,
            Sick = Optional(state.HasSick, state.Sick),
            Adult = Optional(state.HasAdult, state.Adult),
            Venerated = Optional(state.HasVenerated, state.Venerated),
            Predator = race.Predator,
            TicksToBirth = Optional(state.HasTicksToBirth, state.TicksToBirth),
            LifeStageIndex = Optional(state.HasLifeStageIndex, state.LifeStageIndex),
            TicksToNextLifeStage = Optional(state.HasTicksToNextLifeStage, state.TicksToNextLifeStage),
        };
    }

    /// <summary>Copies each player animal's meat, grazing demand and product flag from the colony food channels, matched by animal ID.</summary>
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
        var product = new HashSet<PawnId>();
        foreach (var g in food.Gatherable)
        {
            if (g.Resource.IsKnown)
            {
                product.Add(new PawnId(g.PawnID));
            }
        }
        foreach (var e in food.EggLayer)
        {
            product.Add(new PawnId(e.PawnID));
        }
        var output = new List<UpkeepAnimal>(rows.Count);
        foreach (var a in rows)
        {
            var herd = a.Herd;
            if (slaughter.TryGetValue(a.ID, out var s))
            {
                herd = herd with { MeatNutrition = s.MeatNutrition, FeedPerDay = s.FeedPerDay };
            }
            output.Add(a with { Herd = herd with { Product = product.Contains(a.ID) } });
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>Whether the animal has a master; unknown until native sends the id.</summary>
    private static Fact<bool> Mastered(O.AnimalState state) =>
        state.HasMasterId ? Known(state.MasterId != "") : Unknown<bool>();
}
