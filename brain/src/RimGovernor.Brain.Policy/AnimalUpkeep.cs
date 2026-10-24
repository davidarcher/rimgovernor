using Sex = RimGovernor.Contracts.Wire.Defs.Gender;

namespace RimGovernor.Brain.Policy;


public sealed record UpkeepAnimal
{
    /// <summary>The race row's flag (AnimalRace.Pest): a wild animal military/pests hunts for what it destroys.</summary>
    public bool Pest { get; init; }

    /// <summary>Whether native lets this animal carry an allowed area at all.</summary>
    public bool SupportsAreas { get; init; }

    /// <summary>The allowed area the animal is restricted to; "" when unrestricted.</summary>
    public string AllowedArea { get; init; } = "";

    public PawnId Id { get; init; }

    public Resource Definition { get; init; }

    /// <summary>The native display name; empty when the census omits it.</summary>
    public string Label { get; init; } = "";

    /// <summary>The game Gender.</summary>
    public Sex Gender { get; init; }

    public bool RequiresPen { get; init; }

    /// <summary>A rope-managed animal standing in a pen; false for any other animal.</summary>
    public bool Contained { get; init; }

    /// <summary>A standing release-to-wild designation.</summary>
    public bool Release { get; init; }

    /// <summary>A standing slaughter designation.</summary>
    public bool Slaughter { get; init; }

    /// <summary>The pen holding a rope-managed animal; "" when it is in none.</summary>
    public string Pen { get; init; } = "";

    /// <summary>The pen native would rope a roped animal to; "" when there is none.</summary>
    public string SuitablePen { get; init; } = "";

    /// <summary>
    /// the animals family's husbandry facts straight from the same generic census read that already decodes
    /// Containment/feed facts; SafeToSlaughter decides.
    /// </summary>
    public SlaughterFacts SlaughterFacts { get; init; } = new();

    /// <summary>Native's Designator_ReleaseAnimalToWild acceptance; ReleaseAllowed adds the exclusions policy owns.</summary>
    public bool SafeToRelease { get; init; }

    public EquatableArray<HusbandryTrainable> Training { get; init; }

    /// <summary>Only populated for the wild census (AnimalUpkeepObservation.WildAnimals): native tame eligibility.</summary>
    public bool Tameable { get; init; }

    /// <summary>Only populated for the wild census: a standing tame designation.</summary>
    public bool Tame { get; init; }

    /// <summary>The Animals level taming the wild animal needs (native's TrainableUtility.MinimumHandlingSkill); TamerFor answers whether the roster has it.</summary>
    public int MinimumHandlingSkill { get; init; }

    /// <summary>The sizing facts the herd Need culls and tames by.</summary>
    public HerdFacts Herd { get; init; } = new();

    /// <summary>Care cap input: MedicalCareCategory name; "" for an animal without care settings.</summary>
    public string Care { get; init; } = "";

    /// <summary>Care cap input: a bond to a living pawn.</summary>
    public bool Bonded { get; init; }

    /// <summary>The living humanlike pawns the animal has a Bond relation with (any status); a companion's master is one on the roster.</summary>
    public EquatableArray<string> BondedPawns { get; init; }

    /// <summary>The colonist id mastering the animal ("" unassigned).</summary>
    public string Master { get; init; } = "";

    /// <summary>The Sterilized hediff.</summary>
    public bool Sterilized { get; init; }

    /// <summary>A sterilize surgery bill waiting on the animal.</summary>
    public bool SterilizeQueued { get; init; }

    public bool FollowDrafted { get; init; }

    public bool FollowFieldwork { get; init; }

    /// <summary>Learned Obedience, which native requires to master or follow.</summary>
    public bool Obedient { get; init; }

    /// <summary>The animal's hediffs, none filtered: the medical care cap's and the ward's input.</summary>
    public EquatableArray<Hediff> Conditions { get; init; } = [];

    public bool LifeThreatening { get; init; }
}

/// <summary>One trainable definition's recursive-training eligibility for one animal, ported from AnimalState.TrainingEntry.</summary>
public sealed record HusbandryTrainable
{
    public string Def { get; init; } = "";

    public bool Available { get; init; }

    public bool Learned { get; init; }

    public bool Wanted { get; init; }
}

public sealed record AnimalUpkeepObservation
{
    public Fact<FoodForecast> Forecast { get; init; }

    /// <summary>The player herd: every animal of the colony's census, an empty array for a colony without one.</summary>
    public EquatableArray<UpkeepAnimal> Animals { get; init; }

    /// <summary>The factionless census the herd Need tames from; it carries no feed or pen facts.</summary>
    public EquatableArray<UpkeepAnimal> WildAnimals { get; init; }

    /// <summary>The load's race catalog, read with every routine reading.</summary>
    public AnimalRaceCatalog AnimalRaces { get; init; } = new();

    /// <summary>The player's roaming animals (the census rows' Roam, #3812), which a planned pen keeps; a colony without one has
    /// none. Null when native could not read the census, which a pen credit raises as a required fact.</summary>
    public IReadOnlyList<PenRoamer>? Roamers { get; init; }

    public Fact<FoodSupply> Food { get; init; }

    public EquatableArray<Resource> DirectedHerds { get; init; }
}

/// <summary>The animals family's containment read: the pen-bound animals not yet contained.</summary>
public sealed record AnimalUpkeepReview
{
    public EquatableArray<PawnId> Containment { get; init; }
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

    /// <summary>No pen stands yet (none is planned, or its fence, gate or marker is still owed): the pen builder works on it.</summary>
    [WireName("no_standing_pen")]
    NoPen,

    /// <summary>The blocked outcome once every pen stands with its marker and the native lookup still finds no suitable enclosure.</summary>
    [WireName("marker_placed_awaiting_native_pen")]
    MarkerExhausted,
}

public sealed record AnimalContainmentMethod
{
    public AnimalContainmentReason Reason { get; init; }
}

/// <summary>
/// One periodic yield of a race: Kind is milk, wool, eggs or spawner (chemfuel and other periodic spawns),
/// Amount the items per yield and IntervalDays the days between yields at full growth.
/// </summary>
public sealed record RaceProduct
{
    public string Kind { get; init; } = "";

    public Resource Def { get; init; }

    public double Amount { get; init; }

    public double IntervalDays { get; init; }

    /// <summary>Whether Def feeds an eater: its Nutrition stat, or inedible (wool, chemfuel).</summary>
    public RaceProductFood Food { get; init; } = new RaceProductFood.Inedible();

    /// <summary>Eggs only, from the egg layer comp def: the egg a fertilized hen lays (empty when the layer cannot be fertilized).</summary>
    public Resource FertilizedDef { get; init; }

    /// <summary>The eggs one mating fertilizes.</summary>
    public int FertilizationCountMax { get; init; }

    /// <summary>Whether only females lay.</summary>
    public bool FemaleOnly { get; init; }
}

/// <summary>Whether a race product feeds an eater: Feeds with its Nutrition stat per unit, or Inedible (wool, chemfuel).</summary>
public abstract record RaceProductFood
{
    private protected RaceProductFood() { }

    public sealed record Inedible : RaceProductFood;

    public sealed record Feeds(double PerUnit) : RaceProductFood;
}

/// <summary>What butchering a race yields in meat: None for a race without a meat def, else its MeatAmount stat (before butcher efficiency) and the meat's Nutrition stat per unit.</summary>
public abstract record RaceMeat
{
    private protected RaceMeat() { }

    public sealed record None : RaceMeat;

    public sealed record Yields(double Amount, double NutritionPerUnit) : RaceMeat;
}

/// <summary>A race's adult nutrition demand (the adult scale of Herd.Feed): Eats so much a day, or NoNeed for a race without a food need.</summary>
public abstract record RaceFeed
{
    private protected RaceFeed() { }

    public sealed record NoNeed : RaceFeed;

    public sealed record Eats(double PerDay) : RaceFeed;
}

/// <summary>The first life stage that has an ability (breeding, milk): Reaches it at an age in ticks, or Never when no stage does.</summary>
public abstract record RaceStage
{
    private protected RaceStage() { }

    public sealed record Never : RaceStage;

    public sealed record Reaches(long Ticks) : RaceStage;
}

/// <summary>The mean litter of a birth: Mean children, or Unrollable for a litter curve the game cannot roll (fewer than three points).</summary>
public abstract record RaceLitter
{
    private protected RaceLitter() { }

    public sealed record Unrollable : RaceLitter;

    public sealed record Mean(double Children) : RaceLitter;
}

/// <summary>A race's combat power: Rated with the highest PawnKindDef.combatPower of the race, or Unrated when no pawn kind is of it.</summary>
public abstract record RaceCombat
{
    private protected RaceCombat() { }

    public sealed record Unrated : RaceCombat;

    public sealed record Rated(double Power) : RaceCombat;
}


/// <summary>A good a source yields beside its main one, in units (leather from a hunted deer).</summary>
public sealed record SourceProduct(Resource Def, double Amount);

/// <summary>
/// The static facts of one animal race, the same for every animal of it and fixed for a map load. A fact some
/// races lack is a named state; a race the catalog does not know reads as one with every such fact absent.
/// </summary>
public sealed record AnimalRace
{
    public Resource Def { get; init; }

    public double CarryingCapacity { get; init; }

    public double Wildness { get; init; }

    public double BodySize { get; init; }

    public RaceCombat CombatPower { get; init; } = new RaceCombat.Unrated();

    /// <summary>The Animals level handling the race needs (native's TrainableUtility.MinimumHandlingSkill).</summary>
    public int MinimumHandlingSkill { get; init; }

    /// <summary>The training the race can ever learn (sorted).</summary>
    public EquatableArray<string> Trainables { get; init; }

    public EquatableArray<RaceProduct> Products { get; init; }

    /// <summary>What butchering a standing animal yields besides meat: its leather (the LeatherAmount stat) and the def's butcher products.</summary>
    public EquatableArray<SourceProduct> Butchery { get; init; }

    /// <summary>The items the race can eat that a recipe produces (sorted by definition name): the feed a bench can make for it.</summary>
    public EquatableArray<RaceFeedItem> FeedItems { get; init; }

    /// <summary>RaceProperties.lifeExpectancy in years.</summary>
    public double LifeExpectancy { get; init; }

    /// <summary>RaceProperties.manhunterOnTameFailChance.</summary>
    public double ManhunterOnTameFail { get; init; }

    public double ManhunterOnDamage { get; init; }

    /// <summary>RaceProperties.predator.</summary>
    public bool Predator { get; init; }

    /// <summary>A wild animal that eats trees (RaceProperties.Eats(Tree)), hunted for what it destroys, not for meat.</summary>
    public bool Pest { get; init; }

    /// <summary>RaceProperties.Eats(Plant).</summary>
    public bool EatsPlant { get; init; }

    /// <summary>RaceProperties.gestationPeriodDays (zero for a race that does not bear live young).</summary>
    public double GestationDays { get; init; }

    /// <summary>The foods the race can ever eat (RaceProperties.CanEverEat), sorted.</summary>
    public EquatableArray<string> Edible { get; init; }

    /// <summary>RaceProperties.mateMtbHours: the mean hours between mating attempts of an eligible pair.</summary>
    public double MateMtbHours { get; init; }

    /// <summary>Pawn_AgeTracker.AdultMinAgeTicks: the lead of a young animal before it is adult.</summary>
    public long AdultMinAgeTicks { get; init; }

    /// <summary>The first life stage that breeds.</summary>
    public RaceStage Reproductive { get; init; } = new RaceStage.Never();

    /// <summary>The first life stage that gives milk.</summary>
    public RaceStage Milkable { get; init; } = new RaceStage.Never();

    /// <summary>
    /// The game's wildness curve at the race's Wildness: what a tame attempt's chance is multiplied by, before
    /// the tamer's TameAnimalChance, bond, prison and venerated-animal factors.
    /// </summary>
    public double TameChanceFactor { get; init; }

    /// <summary>The meat butchering one adult yields (the catalog's def stat table, no native read).</summary>
    public RaceMeat Meat { get; init; } = new RaceMeat.None();

    /// <summary>Native adult nutrition demand, the adult scale of Herd.Feed.</summary>
    public RaceFeed AdultFeed { get; init; } = new RaceFeed.NoNeed();

    /// <summary>
    /// RaceProperties.lifeStageAges in order, each with the tick it begins and its LifeStageDef's
    /// hungerRateFactor; empty when a stage or its def is missing from the catalog.
    /// </summary>
    public EquatableArray<RaceLifeStage> LifeStages { get; init; }

    /// <summary>
    /// The mean litter of a birth: Rand.ByCurveAverage of RaceProperties.litterSizeCurve, one child without a
    /// curve, never below the one child a birth always gives.
    /// </summary>
    public RaceLitter LitterSize { get; init; } = new RaceLitter.Unrollable();
}

/// <summary>
/// One life stage of a race: MinAgeTicks is its minAge in biological ticks and HungerRateFactor scales the
/// nutrition per day an animal of it eats (Need_Food.BaseHungerRate). BodySizeFactor scales the race's base
/// body size (Pawn.BodySize).
/// </summary>
public sealed record RaceLifeStage(long MinAgeTicks, double HungerRateFactor, double BodySizeFactor);

/// <summary>
/// The game's constants of one animal interaction job (taming, training): per interaction three talks of
/// TalkTicks and Feeds feeds of FeedTicks, each positive (the catalog refuses a game whose constants are not). The
/// job's three talk toils are game code structure, not a value the game exposes.
/// </summary>
public sealed record AnimalInteraction
{
    public int TalkTicks { get; init; }

    public int FeedTicks { get; init; }

    public int Feeds { get; init; }
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

public static class AnimalUpkeepRules
{
    /// <summary>
    /// Reviews containment for the animals family's containment Need: the pen-bound animals not yet contained. No
    /// history is kept; each read derives it afresh. The herd's feed is the animal feed runway (PlanAnimalFeedRunway).
    /// </summary>
    public static AnimalUpkeepReview ReviewAnimalUpkeep(AnimalUpkeepObservation v)
    {
        var r = new AnimalUpkeepReview();
        const string Invalid = "invalid animal upkeep facts";
        for (var i = 0; i < v.DirectedHerds.Count; i++)
        {
            var race = v.DirectedHerds[i];
            DomainException.Require(AdmissionRules.ValidResource(race) && !v.DirectedHerds.Take(i).Contains(race), Invalid);
        }

        var seen = new HashSet<PawnId>();
        var containment = new List<PawnId>();
        foreach (var animal in v.Animals)
        {
            DomainException.Require(FoodForecastRules.FoodId(animal.Id.Value) && seen.Add(animal.Id) && AdmissionRules.ValidResource(animal.Definition), Invalid);
            if (animal.RequiresPen && !animal.Contained && !animal.Release && !animal.Slaughter)
            {
                containment.Add(animal.Id);
            }
        }

        containment.Sort();
        return r with { Containment = EquatableArray.From(containment) };
    }

    /// <summary>
    /// Ports animal_upkeep.containment_method: an already-suitable herd only ever waits for an enabled Handling
    /// worker and then native delivery; otherwise the pens (PensStand) are built first and, once they all
    /// stand, the wait is on the native lookup. Native pen eligibility, footprint legality and construction
    /// admission remain the building family's.
    /// </summary>
    public static AnimalContainmentMethod SelectAnimalContainmentMethod(IEnumerable<UpkeepAnimal> animals, bool handlerAvailable, bool pensStand)
    {
        var seen = new HashSet<PawnId>();
        var deficit = 0;
        var allSuitable = true;
        foreach (var a in animals)
        {
            DomainException.Require(FoodForecastRules.FoodId(a.Id.Value) && seen.Add(a.Id), "invalid animal containment census");
            var pen = a.RequiresPen;
            if (!pen)
            {
                continue;
            }

            if (a.Contained || a.Release || a.Slaughter)
            {
                continue;
            }

            deficit++;
            if (a.SuitablePen.Length == 0)
            {
                allSuitable = false;
            }
        }

        if (deficit == 0)
        {
            return new AnimalContainmentMethod { Reason = AnimalContainmentReason.NoDeficit };
        }

        if (allSuitable)
        {
            return new AnimalContainmentMethod
            {
                Reason = handlerAvailable ?AnimalContainmentReason.WaitingNativePen : AnimalContainmentReason.WaitingHandler,
            };
        }

        if (!pensStand)
        {
            return new AnimalContainmentMethod { Reason = AnimalContainmentReason.NoPen };
        }

        return new AnimalContainmentMethod { Reason = AnimalContainmentReason.MarkerExhausted };
    }
}
