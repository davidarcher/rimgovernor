namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the food family reads (#3254). For cooking: whether a meal can be cooked at the tier the meal policy picks
/// (the review's <see cref="ReviewFacts.Cooking"/>), and whether a cooking campfire a stove kitchen supersedes still
/// waits to be taken down. For supply: the food plan's human-food routes, the food runway against the foothold and
/// the seasonal reserve, the growing capacity, the field coverage and the butcher facility. For baby feeding: the
/// review of the babies' food. For blight: the blighted-plant census and the plants the Need's own cuts were refused.
/// </summary>
public sealed record FoodFacts
{
    /// <summary>The babies' food against their need (<see cref="ReviewFacts.BabyFeeding"/>); the empty review without Biotech baby care.</summary>
    public BabyFeeding BabyFeeding { get; init; } = new();

    /// <summary>The total blighted-plant census on growing-zone or home ground (<see cref="ReviewFacts.Blight"/>).</summary>
    public EquatableArray<BlightedPlant> Blight { get; init; }

    /// <summary>The plants a cut of the blight Need's open Methods could not designate; they are left out of the next cut.</summary>
    public EquatableArray<string> BlightRefused { get; init; }

    /// <summary>A meal of the picked tier can be made now: an active bill of the tier's recipe, or a stocked, powered paste dispenser.</summary>
    public bool Cooking { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire that is not yet deconstructed; from the observed benches, rooms and construction.</summary>
    public bool CampfireRetireOwed { get; init; }

    /// <summary>No human-meat route of the food plan holds nutrition to place (feed, survival trade, raw trade, meals).</summary>
    public bool HumanFoodSettled { get; init; }

    /// <summary>The food runway covers the foothold's days.</summary>
    public bool FootholdFood { get; init; }

    /// <summary>The growing cells cover the foothold's colonists.</summary>
    public bool FootholdProduction { get; init; }

    /// <summary>The fields cover the food plan's growing share.</summary>
    public bool FieldsCovered { get; init; }

    /// <summary>A butcher bench stands outside the kitchen; from the required bench census.</summary>
    public bool ButcherFacility { get; init; }

    /// <summary>The food runway reaches the season's target days.</summary>
    public bool ReserveCovered { get; init; }

    /// <summary>The facts a review already holds, the food thresholds read for the review's season.</summary>
    public static FoodFacts From(ReviewFacts f, PlannerSettings policy)
    {
        var p = policy.Seasonal(f.Calendar, f.DisasterConditions);
        return new()
        {
            Cooking = f.Cooking,
            CampfireRetireOwed = f.CampfireRetireOwed,
            HumanFoodSettled = f.FoodPlan is not { } held || !HumanFoodPlanRules.HumanFoodPending(held),
            FootholdFood = Rules.FootholdFood(f, p),
            FootholdProduction = Rules.FootholdProduction(f),
            FieldsCovered = f.FieldCoverage >= 1 - 1e-9,
            ButcherFacility = Rules.ButcherFacilityMet(f),
            ReserveCovered = Rules.Reached(f.FoodDays, p.FoodTargetDays),
            BabyFeeding = f.BabyFeeding,
            Blight = f.Blight,
        };
    }
}

/// <summary>
/// The food family (#3254): the colony's food, raw and refined.
/// <list type="bullet">
/// <item><c>food/supply</c> is met while the food plan places no human food, the runway reaches the foothold and the
/// season's target days, the fields grow the foothold and the plan's share, and a butcher bench stands. Its fields,
/// fishing zones, forage and hunts and its butcher facility are the runtime work step's, committed under the Need;
/// its butcher bills are the work ledger's.</item>
/// <item><c>food/cooking</c> is met while a meal of the picked tier can be made and no superseded cooking campfire
/// waits to be taken down. Cooking is part of food, not a Once: a lost kitchen reopens the Need on the next review.
/// Its campfire, paste dispenser, kitchen and stove are building work the runtime's work step commits under the
/// Need; the cooking bills are the work ledger's.</item>
/// <item><c>food/baby-feeding</c> is met while a colonist can breastfeed or the baby-edible stock reaches the game's
/// low-baby-food floor. Its one remedy is the baby food bill, which the work ledger places.</item>
/// <item><c>food/blight</c> is met while no blighted plant stands. Its Method is one batch of at most
/// <see cref="BlightCutLimit"/> cut designations, <c>cut-&lt;hash of the plants&gt;</c>, each plant a claim, so a
/// second batch of other plants runs beside an open one.</item>
/// </list>
/// </summary>
public sealed class FoodFamily(bool supply, bool cooking, bool babyFeeding = false, bool blight = false) : IFamily<FoodFacts>
{
    /// <summary>The most cut designations one blight Method carries.</summary>
    public const int BlightCutLimit = 8;

    public string Id => FoodRules.FoodFamilyId;

    /// <summary>The colony census, the pawns the food plan and the babies' food read, and the room census the kitchen, butchery and superseded campfire stand in.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(FoodFacts facts) =>
    [
        .. supply ? [FoodRules.FoodSupplyNeedOf(facts)] : Array.Empty<Need>(),
        .. cooking ? [FoodRules.CookingNeedOf(facts)] : Array.Empty<Need>(),
        .. babyFeeding ? [FoodRules.BabyFeedingNeedOf(facts)] : Array.Empty<Need>(),
        .. blight ? [FoodRules.BlightNeedOf(facts)] : Array.Empty<Need>(),
    ];

    public IReadOnlyList<Need> DeriveSeriousHarm(FoodFacts facts) => [];

    /// <summary>
    /// The blight Need's next cut batch: the undesignated, eligible plants not refused to an open cut, lowest id first.
    /// With none left and none designated it is held. The other Needs' Methods are building, zone and acquisition work
    /// and ledger bills, the runtime's.
    /// </summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, FoodFacts facts)
    {
        if (!ready.Any(n => n.Id == FoodRules.BlightNeed))
        {
            return [];
        }
        var census = facts.Blight;
        var targets = BlightRules.SelectBlightCuts(census, facts.BlightRefused.ToHashSet(StringComparer.Ordinal), BlightCutLimit);
        if (targets.Count == 0)
        {
            // Designated plants are being cut (an open batch continues); otherwise native offers no plant to designate.
            return census.Any(p => p.Designated) ? [] : [NeedMethod.Held(FoodRules.BlightNeed, "blight_cut_targets", "no_eligible_plant")];
        }
        var rows = string.Concat(targets.Select(p => FormattableString.Invariant($"{p.ID}/{p.Definition}/{p.Cell.X}/{p.Cell.Z}\n")));
        var method = new MethodId("cut-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rows))[..16]));
        return [new NeedMethod(FoodRules.BlightNeed, method, "cut blighted plants", id => new PlanSpec(id, new PlanRevision(1),
            targets.Select((p, i) => DomainAction.NewCutPlantAction(new ActionId($"{id.Value}-{i}"), new CutPlant(p.ID, p.Definition, p.Cell)))))];
    }
}

public static class FoodRules
{
    public const string FoodFamilyId = "food";

    /// <summary>The food family's supply Need (#3254).</summary>
    public static readonly NeedId FoodSupplyNeed = NeedId.Of(FoodFamilyId, "supply");

    /// <summary>The food family's cooking Need (#3254).</summary>
    public static readonly NeedId CookingNeed = NeedId.Of(FoodFamilyId, "cooking");

    /// <summary>
    /// The supply Need, a plain threshold over its six required checks: unmet when any check fails, met otherwise.
    /// The runway is held to the season's target
    /// days with no hysteresis. Its deficit is whole while unmet.
    /// </summary>
    public static Need FoodSupplyNeedOf(FoodFacts f)
    {
        var met = f.HumanFoodSettled && f.FootholdFood && f.ButcherFacility && f.ReserveCovered;
        return WholeNeed(FoodSupplyNeed, NeedTier.Wellbeing, f.FieldsCovered && f.FootholdProduction && met);
    }

    /// <summary>
    /// The cooking Need, a plain threshold: unmet while a superseded cooking campfire is owed its deconstruction or
    /// no meal of the picked tier can be made, and met otherwise. Its deficit is whole while unmet.
    /// </summary>
    public static Need CookingNeedOf(FoodFacts f)
    {
        var retire = f.CampfireRetireOwed;
        return WholeNeed(CookingNeed, NeedTier.Wellbeing, !retire && f.Cooking);
    }

    /// <summary>The food family's baby feeding Need (#3254).</summary>
    public static readonly NeedId BabyFeedingNeed = NeedId.Of(FoodFamilyId, "baby-feeding");

    /// <summary>The food family's blight Need (#3254).</summary>
    public static readonly NeedId BlightNeed = NeedId.Of(FoodFamilyId, "blight");

    /// <summary>
    /// The baby feeding Need, a plain threshold: unmet while no colonist can breastfeed and the baby-edible stock is
    /// below the game's low-baby-food floor (<see cref="BabyFeeding.IsShort"/>), met otherwise (no babies included).
    /// Its deficit is whole while unmet.
    /// </summary>
    public static Need BabyFeedingNeedOf(FoodFacts f) =>
        WholeNeed(BabyFeedingNeed, NeedTier.Wellbeing, !f.BabyFeeding.IsShort);

    /// <summary>
    /// The blight Need, a plain threshold over the census: unmet while any blighted plant stands, designated or not
    /// (the Need is met by the cut, not the order), met otherwise. Its deficit is whole while unmet.
    /// </summary>
    public static Need BlightNeedOf(FoodFacts f) =>
        WholeNeed(BlightNeed, NeedTier.Wellbeing, !BlightRules.BlightDeficit(f.Blight));

    /// <summary>A Need met or unmet by <paramref name="met"/>, with a whole deficit while unmet.</summary>
    internal static Need WholeNeed(NeedId id, NeedTier tier, bool met) => new()
    {
        Id = id,
        Tier = tier,
        Satisfaction = met ? Satisfaction.Met : Satisfaction.Unmet,
        Deficit = met ? 0.0 : 1.0,
    };
}
