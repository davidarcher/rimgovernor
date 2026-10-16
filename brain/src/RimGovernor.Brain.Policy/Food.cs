namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the food family reads (#3254): whether a meal can be cooked at the tier the meal policy picks (the review's
/// <see cref="RoundsFacts.Cooking"/>), and whether a cooking campfire a stove kitchen supersedes still waits to be
/// taken down.
/// </summary>
public sealed record FoodFacts
{
    /// <summary>A meal of the picked tier can be made now: an active bill of the tier's recipe, or a stocked, powered paste dispenser.</summary>
    public Fact<bool> Cooking { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire that is not yet deconstructed; unknown while the benches, rooms or construction are unread.</summary>
    public Fact<bool> CampfireRetireOwed { get; init; }

    /// <summary>The facts a review already holds.</summary>
    public static FoodFacts From(RoundsFacts f) => new()
    {
        Cooking = f.Cooking,
        CampfireRetireOwed = f.CampfireRetireOwed,
    };
}

/// <summary>
/// The food family (#3254): the colony's refined food. <c>food/cooking</c> is met while a meal of the picked tier can
/// be made and no superseded cooking campfire waits to be taken down. Cooking is part of food, not a Once: a lost
/// kitchen reopens the Need on the next review. Its campfire, paste dispenser, kitchen and stove are building work
/// the runtime's work step commits under the Need; the cooking bills are the work ledger's.
/// </summary>
public sealed class FoodFamily(bool cooking) : IFamily<FoodFacts>
{
    public string Id => PolicyFunctions.FoodFamilyId;

    /// <summary>The colony census and the room census the kitchen and the superseded campfire stand in.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Rooms, FactFamily.Colony];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(FoodFacts facts) => cooking ? [PolicyFunctions.CookingNeedOf(facts)] : [];

    /// <summary>The family's Methods are building work and ledger bills; it proposes none of its own.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, FoodFacts facts) => [];
}

public static partial class PolicyFunctions
{
    public const string FoodFamilyId = "food";

    /// <summary>The food family's cooking Need (#3254).</summary>
    public static readonly NeedId CookingNeed = NeedId.Of(FoodFamilyId, "cooking");

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
            Deficit = satisfaction switch
            {
                Satisfaction.Unknown => Unknown<double>(),
                Satisfaction.Unmet => Known(1.0),
                _ => Known(0.0),
            },
            Evidence =
            [
                new("cooking_ready", f.Cooking.TryGetValue(out var known) ? Known(known ? "ready" : "missing") : Unknown<string>()),
                new("campfire_retire", f.CampfireRetireOwed.TryGetValue(out var owed) ? Known(owed ? "owed" : "none") : Unknown<string>()),
            ],
        };
    }
}
