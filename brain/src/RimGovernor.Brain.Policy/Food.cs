namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the food family reads (#3254). For cooking: whether a meal can be cooked at the tier the meal policy picks
/// (the review's <see cref="RoundsFacts.Cooking"/>), and whether a cooking campfire a stove kitchen supersedes still
/// waits to be taken down. For supply: the food plan's human-food routes, the food runway against the foothold and
/// the seasonal reserve, the growing capacity, the field coverage and the butcher facility.
/// </summary>
public sealed record FoodFacts
{
    /// <summary>A meal of the picked tier can be made now: an active bill of the tier's recipe, or a stocked, powered paste dispenser.</summary>
    public Fact<bool> Cooking { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire that is not yet deconstructed; unknown while the benches, rooms or construction are unread.</summary>
    public Fact<bool> CampfireRetireOwed { get; init; }

    /// <summary>No human-meat route of the food plan holds nutrition to place (feed, survival trade, raw trade, meals).</summary>
    public Fact<bool> HumanFoodSettled { get; init; }

    /// <summary>The food runway covers the foothold's days.</summary>
    public Fact<bool> FootholdFood { get; init; }

    /// <summary>The growing cells cover the foothold's colonists.</summary>
    public Fact<bool> FootholdProduction { get; init; }

    /// <summary>The fields cover the food plan's growing share.</summary>
    public Fact<bool> FieldsCovered { get; init; }

    /// <summary>A butcher bench stands outside the kitchen; unknown while the bench census is unread.</summary>
    public Fact<bool> ButcherFacility { get; init; }

    /// <summary>The food runway reaches the season's target days.</summary>
    public Fact<bool> ReserveCovered { get; init; }

    /// <summary>The facts a review already holds, the food thresholds read for the review's season.</summary>
    public static FoodFacts From(RoundsFacts f, RoundsPolicy policy)
    {
        var p = policy.Seasonal(f.Calendar, f.DisasterConditions);
        return new()
        {
            Cooking = f.Cooking,
            CampfireRetireOwed = f.CampfireRetireOwed,
            HumanFoodSettled = Fact.Known(!PolicyFunctions.HumanFoodPending(f.FoodPlan)),
            FootholdFood = PolicyFunctions.FootholdFood(f, p),
            FootholdProduction = PolicyFunctions.FootholdProduction(f),
            FieldsCovered = PolicyFunctions.Measured(f.FieldCoverage, v => v >= 1 - 1e-9),
            ButcherFacility = PolicyFunctions.ButcherFacilityMet(f),
            ReserveCovered = PolicyFunctions.Measured(f.FoodDays, v => v >= p.FoodTargetDays),
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
/// </list>
/// </summary>
public sealed class FoodFamily(bool supply, bool cooking) : IFamily<FoodFacts>
{
    public string Id => PolicyFunctions.FoodFamilyId;

    /// <summary>The colony census, the pawns the food plan feeds and the room census the kitchen, butchery and superseded campfire stand in.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(FoodFacts facts) =>
        [.. supply ? [PolicyFunctions.FoodSupplyNeedOf(facts)] : Array.Empty<Need>(), .. cooking ? [PolicyFunctions.CookingNeedOf(facts)] : Array.Empty<Need>()];

    /// <summary>The family's Methods are building, zone and acquisition work and ledger bills; it proposes none of its own.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, FoodFacts facts) => [];
}

public static partial class PolicyFunctions
{
    public const string FoodFamilyId = "food";

    /// <summary>The food family's supply Need (#3254).</summary>
    public static readonly NeedId FoodSupplyNeed = NeedId.Of(FoodFamilyId, "supply");

    /// <summary>The food family's cooking Need (#3254).</summary>
    public static readonly NeedId CookingNeed = NeedId.Of(FoodFamilyId, "cooking");

    /// <summary>
    /// The supply Need, a plain threshold over its six checks: unmet when any known check fails, unknown when none
    /// fails and one is unread, met otherwise. The runway is held to the season's target days with no hysteresis. Its
    /// deficit is whole while unmet.
    /// </summary>
    public static Need FoodSupplyNeedOf(FoodFacts f)
    {
        var met = AllFacts(f.HumanFoodSettled, f.FootholdFood, f.FootholdProduction, f.FieldsCovered, f.ButcherFacility, f.ReserveCovered);
        var satisfaction = met.TryGetValue(out var known) ? known ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        static NeedEvidence Check(string key, Fact<bool> fact, string ok, string shortfall) =>
            new(key, fact.TryGetValue(out var v) ? Known(v ? ok : shortfall) : Unknown<string>());
        return new Need
        {
            Id = FoodSupplyNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = WholeDeficit(satisfaction),
            Evidence =
            [
                Check("human_food", f.HumanFoodSettled, "settled", "pending"),
                Check("foothold_food", f.FootholdFood, "covered", "short"),
                Check("foothold_production", f.FootholdProduction, "covered", "short"),
                Check("fields", f.FieldsCovered, "covered", "short"),
                Check("butcher_facility", f.ButcherFacility, "standing", "missing"),
                Check("reserve_days", f.ReserveCovered, "covered", "short"),
            ],
        };
    }

    /// <summary>
    /// The cooking Need, a plain threshold: unmet while a superseded cooking campfire is owed its deconstruction or
    /// no meal of the picked tier can be made, unknown while the cooking fact is unread, and met otherwise. Its deficit
    /// is whole while unmet.
    /// </summary>
    public static Need CookingNeedOf(FoodFacts f)
    {
        var retire = Positive(f.CampfireRetireOwed);
        var satisfaction = retire ? Satisfaction.Unmet : f.Cooking.TryGetValue(out var ready) ? ready ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        return new Need
        {
            Id = CookingNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = WholeDeficit(satisfaction),
            Evidence =
            [
                new("cooking_ready", f.Cooking.TryGetValue(out var known) ? Known(known ? "ready" : "missing") : Unknown<string>()),
                new("campfire_retire", f.CampfireRetireOwed.TryGetValue(out var owed) ? Known(owed ? "owed" : "none") : Unknown<string>()),
            ],
        };
    }

    private static Fact<double> WholeDeficit(Satisfaction satisfaction) => satisfaction switch
    {
        Satisfaction.Unknown => Unknown<double>(),
        Satisfaction.Unmet => Known(1.0),
        _ => Known(0.0),
    };
}
