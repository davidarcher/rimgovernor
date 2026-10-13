namespace RimGovernor.Brain.Policy;

// Go: animal_upkeep.go

public sealed record UpkeepAnimal
{
    /// <summary>The race row's flag (AnimalRace.Pest): a wild animal ClearPests hunts for what it destroys.</summary>
    public bool Pest { get; init; }

    public Fact<bool> SupportsAreas { get; init; }

    public Fact<string> AllowedArea { get; init; }

    public PawnId ID { get; init; }

    public Resource Definition { get; init; }

    /// <summary>The native display name; empty when the census omits it.</summary>
    public string Label { get; init; } = "";

    /// <summary>Native Gender ("Male", "Female", "None"); empty when unread.</summary>
    public string Gender { get; init; } = "";

    public Fact<bool> RequiresPen { get; init; }

    public Fact<bool> Contained { get; init; }

    public Fact<bool> Release { get; init; }

    public Fact<bool> Slaughter { get; init; }

    public Fact<string> Pen { get; init; }

    public Fact<string> SuitablePen { get; init; }

    /// <summary>
    /// MaintainHerd-*'s husbandry facts straight from the same generic census read that already decodes
    /// containment/feed facts; SafeToSlaughter (animal_food.go) decides.
    /// </summary>
    public SlaughterFacts SlaughterFacts { get; init; } = new();

    /// <summary>Native's Designator_ReleaseAnimalToWild acceptance; ReleaseAllowed (animal_food.go) adds the exclusions policy owns.</summary>
    public Fact<bool> SafeToRelease { get; init; }

    public EquatableArray<HusbandryTrainable> Training { get; init; }

    /// <summary>Only populated for the wild census (AnimalUpkeepObservation.WildAnimals): native tame eligibility.</summary>
    public Fact<bool> Tameable { get; init; }

    /// <summary>Only populated for the wild census: a standing tame designation.</summary>
    public Fact<bool> Tame { get; init; }

    /// <summary>The Animals level taming the wild animal needs (native's TrainableUtility.MinimumHandlingSkill); TamerFor answers whether the roster has it.</summary>
    public Fact<int> MinimumHandlingSkill { get; init; }

    /// <summary>The sizing facts MaintainHerd culls and tames by.</summary>
    public HerdFacts Herd { get; init; } = new();

    /// <summary>Care cap input: MedicalCareCategory name.</summary>
    public Fact<string> Care { get; init; }

    /// <summary>Care cap input: a bond to a living pawn.</summary>
    public Fact<bool> Bonded { get; init; }

    /// <summary>The living humanlike pawns the animal has a Bond relation with (any status); a companion's master is one on the roster.</summary>
    public EquatableArray<string> BondedPawns { get; init; }

    /// <summary>The colonist id mastering the animal ("" unassigned).</summary>
    public Fact<string> Master { get; init; }

    /// <summary>The Sterilized hediff.</summary>
    public Fact<bool> Sterilized { get; init; }

    /// <summary>A sterilize surgery bill waiting on the animal.</summary>
    public Fact<bool> SterilizeQueued { get; init; }

    public Fact<bool> FollowDrafted { get; init; }

    public Fact<bool> FollowFieldwork { get; init; }

    /// <summary>Learned Obedience, which native requires to master or follow.</summary>
    public Fact<bool> Obedient { get; init; }

    public Fact<EquatableArray<CareCondition>> Conditions { get; init; }

    public Fact<bool> LifeThreatening { get; init; }
}

/// <summary>One trainable definition's recursive-training eligibility for one animal, ported from AnimalState.TrainingEntry.</summary>
public sealed record HusbandryTrainable
{
    public string Def { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<bool> Learned { get; init; }

    public Fact<bool> Wanted { get; init; }
}

public sealed record AnimalUpkeepObservation
{
    public Fact<FoodForecast> Forecast { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }

    /// <summary>The factionless census MaintainHerd tames from; it carries no feed or pen facts.</summary>
    public Fact<EquatableArray<UpkeepAnimal>> WildAnimals { get; init; }

    /// <summary>The load's race catalog, read with every routine reading.</summary>
    public AnimalRaceCatalog AnimalRaces { get; init; } = new();

    public Fact<FoodSupply> Food { get; init; }

    public EquatableArray<Resource> DirectedHerds { get; init; }
}

/// <summary>The containment latch MaintainAnimalContainment stays active on until its review next finds no uncontained animal.</summary>
public sealed record AnimalUpkeepHistory
{
    public bool Containment { get; init; }
}

public sealed record AnimalUpkeepReview
{
    public AnimalUpkeepHistory History { get; init; } = new();

    public Fact<EquatableArray<PawnId>> Containment { get; init; }
}

public enum AnimalContainmentReason
{
    [WireName("")]
    None,

    /// <summary>Mirrors an empty animal_upkeep.containment_method row set.</summary>
    [WireName("no_uncontained_animal")]
    NoDeficit,

    /// <summary>
    /// Ports the enabled-Handling-worker prerequisite: every uncontained animal already has a suitable pen, so
    /// nothing is built, but native delivery still needs an available handler to walk it there.
    /// </summary>
    [WireName("no_enabled_available_handler")]
    WaitingHandler,

    /// <summary>The ordinary wait once a handler exists; native AI, not the controller, delivers an already-suitable-penned animal.</summary>
    [WireName("waiting_for_native_pen_delivery")]
    WaitingNativePen,

    /// <summary>No pen stands yet (none is planned, or its fence, flap or marker is still owed): the barn and pen builder works on it.</summary>
    [WireName("no_standing_pen")]
    NoPen,

    /// <summary>The blocked outcome once every pen stands with its marker and the native lookup still finds no suitable enclosure.</summary>
    [WireName("marker_placed_awaiting_native_pen")]
    MarkerExhausted,
}

public sealed record AnimalContainmentMethod
{
    public AnimalContainmentReason Reason { get; init; }

    public EquatableArray<PawnId> Animals { get; init; }
}

/// <summary>
/// One periodic yield of a race: Kind is milk, wool, eggs or spawner (chemfuel and other periodic spawns),
/// Amount the items per yield and IntervalDays the days between yields at full growth.
/// </summary>
public sealed record RaceProduct
{
    public string Kind { get; init; } = "";

    public Resource Def { get; init; }

    public Fact<double> Amount { get; init; }

    public Fact<double> IntervalDays { get; init; }

    /// <summary>The Nutrition stat of Def (catalog def stat table); unknown for a def the game shows none for, wool.</summary>
    public Fact<double> NutritionPerUnit { get; init; }

    /// <summary>Eggs only, from the egg layer and hatcher comp defs: the egg a fertilized hen lays (empty when the layer cannot be fertilized).</summary>
    public Resource FertilizedDef { get; init; }

    /// <summary>The eggs one mating fertilizes.</summary>
    public int FertilizationCountMax { get; init; }

    /// <summary>Whether only females lay.</summary>
    public bool FemaleOnly { get; init; }

    /// <summary>The fertilized egg's hatcherDaystoHatch.</summary>
    public Fact<double> HatchDays { get; init; }

    /// <summary>The fertilized egg's hatcherPawn.</summary>
    public Resource HatchPawn { get; init; }
}

/// <summary>A good a source yields beside its main one, in units (leather from a hunted deer).</summary>
public sealed record SourceProduct(Resource Def, double Amount);

/// <summary>
/// The static facts of one animal race, the same for every animal of it and fixed for a map load. An unread
/// value is unknown, never zero.
/// </summary>
public sealed record AnimalRace
{
    public Resource Def { get; init; }

    public Fact<double> CarryingCapacity { get; init; }

    public Fact<double> Wildness { get; init; }

    public Fact<double> BodySize { get; init; }

    public Fact<double> CombatPower { get; init; }

    public Fact<double> MarketValue { get; init; }

    public Fact<int> MinimumHandlingSkill { get; init; }

    /// <summary>The native TrainabilityDef name (None, Simple, Intermediate, Advanced).</summary>
    public Fact<string> Trainability { get; init; }

    /// <summary>The training the race can ever learn (sorted).</summary>
    public EquatableArray<string> Trainables { get; init; }

    public EquatableArray<RaceProduct> Products { get; init; }

    /// <summary>What butchering a standing animal yields besides meat: its leather (the LeatherAmount stat) and the def's butcher products.</summary>
    public EquatableArray<SourceProduct> Butchery { get; init; }

    /// <summary>The items the race can eat that a recipe produces (sorted by definition name): the feed a bench can make for it.</summary>
    public EquatableArray<RaceFeedItem> FeedItems { get; init; }

    /// <summary>RaceProperties.lifeExpectancy in years.</summary>
    public Fact<double> LifeExpectancy { get; init; }

    public Fact<double> ManhunterOnTameFail { get; init; }

    public Fact<double> ManhunterOnDamage { get; init; }

    /// <summary>RaceProperties.predator.</summary>
    public bool Predator { get; init; }

    /// <summary>A wild animal that eats trees (RaceProperties.Eats(Tree)), hunted for what it destroys, not for meat.</summary>
    public bool Pest { get; init; }

    public bool Mechanoid { get; init; }

    public bool Insect { get; init; }

    /// <summary>RaceProperties.Eats(Plant).</summary>
    public bool EatsPlant { get; init; }

    /// <summary>RaceProperties.gestationPeriodDays (zero for a race that does not bear live young).</summary>
    public double GestationDays { get; init; }

    /// <summary>The foods the race can ever eat (RaceProperties.CanEverEat), sorted.</summary>
    public EquatableArray<string> Edible { get; init; }

    /// <summary>RaceProperties.mateMtbHours: the mean hours between mating attempts of an eligible pair.</summary>
    public Fact<double> MateMtbHours { get; init; }

    /// <summary>Pawn_AgeTracker.AdultMinAgeTicks: the lead of a young animal before it is adult.</summary>
    public Fact<long> AdultMinAgeTicks { get; init; }

    /// <summary>The first life stage that breeds (unknown when no stage does).</summary>
    public Fact<long> ReproductiveMinAgeTicks { get; init; }

    /// <summary>The first life stage that gives milk (unknown when no stage does).</summary>
    public Fact<long> MilkableMinAgeTicks { get; init; }

    /// <summary>The first life stage that gives wool (unknown when no stage does).</summary>
    public Fact<long> ShearableMinAgeTicks { get; init; }

    /// <summary>A tamed animal of the race loses a Tameness step each period unless it cannot decay (a nearly tame race, or one fence-blocked).</summary>
    public Fact<bool> TamenessCanDecay { get; init; }

    public Fact<int> TamenessDecayPeriodTicks { get; init; }

    /// <summary>
    /// The game's wildness curve at the race's Wildness: what a tame attempt's chance is multiplied by, before
    /// the tamer's TameAnimalChance, bond, prison and venerated-animal factors.
    /// </summary>
    public Fact<double> TameChanceFactor { get; init; }

    /// <summary>The meat a butchery yields (empty for a race with no meat).</summary>
    public Resource MeatDef { get; init; }

    /// <summary>The MeatAmount stat of the race def, before butcher efficiency.</summary>
    public Fact<double> MeatAmount { get; init; }

    /// <summary>Native adult nutrition demand, matching Herd.FeedPerDay.</summary>
    public Fact<double> AdultFeedPerDay { get; init; }

    /// <summary>The Nutrition stat of MeatDef (the catalog's def stat table, no native read).</summary>
    public Fact<double> MeatNutritionPerUnit { get; init; }

    /// <summary>
    /// RaceProperties.lifeStageAges in order, each with the tick it begins and its LifeStageDef's
    /// hungerRateFactor; empty when a stage or its def is missing from the catalog.
    /// </summary>
    public EquatableArray<RaceLifeStage> LifeStages { get; init; }

    /// <summary>
    /// The mean litter of a birth: Rand.ByCurveAverage of RaceProperties.litterSizeCurve, one child without a
    /// curve, never below the one child a birth always gives. Unknown for a curve the game cannot roll (fewer
    /// than three points).
    /// </summary>
    public Fact<double> LitterSize { get; init; }
}

/// <summary>
/// One life stage of a race: MinAgeTicks is its minAge in biological ticks and HungerRateFactor scales the
/// nutrition per day an animal of it eats (Need_Food.BaseHungerRate). BodySizeFactor scales the race's base
/// body size (Pawn.BodySize).
/// </summary>
public sealed record RaceLifeStage(long MinAgeTicks, double HungerRateFactor, double BodySizeFactor);

/// <summary>
/// The game's constants of one animal interaction job (taming, training): per interaction three talks of
/// TalkTicks and Feeds feeds of FeedTicks; a feed is FeedNutritionFraction of the animal's food need, capped at
/// FeedNutritionCap nutrition; MinTrainIntervalTicks separate two training jobs on one animal. The job's three
/// talk toils are game code structure, not a value the game exposes.
/// </summary>
public sealed record AnimalInteraction
{
    public Fact<int> TalkTicks { get; init; }

    public Fact<int> FeedTicks { get; init; }

    public Fact<int> Feeds { get; init; }

    public Fact<int> MinTrainIntervalTicks { get; init; }

    public Fact<double> FeedNutritionFraction { get; init; }

    public Fact<double> FeedNutritionCap { get; init; }
}

/// <summary>One producible feed item and its nutrition per item.</summary>
public sealed record RaceFeedItem(Resource Def, double Nutrition);

/// <summary>Every race the game knows, by definition name.</summary>
public sealed record AnimalRaceCatalog
{
    public IReadOnlyDictionary<Resource, AnimalRace> Races { get; init; } = new Dictionary<Resource, AnimalRace>();

    public AnimalInteraction Interaction { get; init; } = new();

    /// <summary>The facts of <paramref name="def"/>; false when the catalog does not know the race.</summary>
    public bool Race(Resource def, out AnimalRace race)
    {
        if (Races.TryGetValue(def, out var found))
        {
            race = found;
            return true;
        }

        race = new AnimalRace();
        return false;
    }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainAnimalContainment = new("MaintainAnimalContainment");

    /// <summary>Reviews containment: the pen-bound animals not yet contained. The herd's feed is the animal feed runway (PlanAnimalFeedRunway).</summary>
    public static AnimalUpkeepReview ReviewAnimalUpkeep(AnimalUpkeepObservation v, AnimalUpkeepHistory previous)
    {
        var r = new AnimalUpkeepReview { History = previous };
        const string Invalid = "invalid animal upkeep facts or history";
        for (var i = 0; i < v.DirectedHerds.Count; i++)
        {
            var race = v.DirectedHerds[i];
            DomainException.Require(ValidResource(race) && !v.DirectedHerds.Take(i).Contains(race), Invalid);
        }

        if (!v.Animals.TryGetValue(out var animals))
        {
            return r;
        }

        var seen = new HashSet<PawnId>();
        var containment = new List<PawnId>();
        var containmentKnown = true;
        foreach (var animal in animals)
        {
            DomainException.Require(FoodId(animal.ID.Value) && seen.Add(animal.ID) && ValidResource(animal.Definition), Invalid);
            var pk = animal.RequiresPen.TryGetValue(out var pen);
            var ck = animal.Contained.TryGetValue(out var contained);
            var rk = animal.Release.TryGetValue(out var release);
            var sk = animal.Slaughter.TryGetValue(out var slaughter);
            if (!pk || (pen && (!ck || !rk || !sk)))
            {
                containmentKnown = false;
            }
            else if (pen && !contained && !release && !slaughter)
            {
                containment.Add(animal.ID);
            }
        }

        if (containmentKnown)
        {
            containment.Sort();
            r = r with
            {
                Containment = Known(EquatableArray.From(containment)),
                History = r.History with { Containment = containment.Count > 0 },
            };
        }

        return r;
    }

    /// <summary>
    /// Ports animal_upkeep.containment_method: an already-suitable herd only ever waits for an enabled Handling
    /// worker and then native delivery; otherwise the pens (PensStand) are built first and, once they all
    /// stand, the wait is on the native lookup. Native pen eligibility, footprint legality and construction
    /// admission remain the building family's.
    /// </summary>
    public static AnimalContainmentMethod SelectAnimalContainmentMethod(IEnumerable<UpkeepAnimal> animals, Fact<bool> handlerAvailable, bool pensStand)
    {
        var seen = new HashSet<PawnId>();
        var rows = new List<UpkeepAnimal>();
        var allSuitable = true;
        foreach (var a in animals)
        {
            DomainException.Require(FoodId(a.ID.Value) && seen.Add(a.ID), "invalid animal containment census");
            DomainException.Require(a.RequiresPen.TryGetValue(out var pen), "animal pen requirement unknown");
            if (!pen)
            {
                continue;
            }

            var ck = a.Contained.TryGetValue(out var contained);
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            DomainException.Require(ck && rk && sk, "animal containment state unknown");
            if (contained || release || slaughter)
            {
                continue;
            }

            rows.Add(a);
            if (!a.SuitablePen.TryGetValue(out var suitable) || suitable.Length == 0)
            {
                allSuitable = false;
            }
        }

        rows.Sort((x, y) => x.ID.CompareTo(y.ID));
        var ids = EquatableArray.From(rows.Select(a => a.ID));
        if (rows.Count == 0)
        {
            return new AnimalContainmentMethod { Reason = AnimalContainmentReason.NoDeficit };
        }

        if (allSuitable)
        {
            DomainException.Require(handlerAvailable.TryGetValue(out var available), "handler availability unknown");
            return new AnimalContainmentMethod
            {
                Reason = available ? AnimalContainmentReason.WaitingNativePen : AnimalContainmentReason.WaitingHandler,
                Animals = ids,
            };
        }

        if (!pensStand)
        {
            return new AnimalContainmentMethod { Reason = AnimalContainmentReason.NoPen, Animals = ids };
        }

        return new AnimalContainmentMethod { Reason = AnimalContainmentReason.MarkerExhausted, Animals = ids };
    }
}
