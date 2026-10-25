namespace RimGovernor.Brain.Policy;

/// <summary>
/// The review's trade facts (#3316), derived by <see cref="RoundsRules.InspectRounds"/> and kept by the Rounder
/// per world: the silver gap a purchase need leaves open (the economy/trade Need's), and whether the caravans present
/// leave nothing worth trading (the economy/caravan Need's). A review before the food planner awaits its plan.
/// </summary>
public abstract record TradeReview
{
    private protected TradeReview() { }
    public sealed record AwaitingFoodPlan : TradeReview;
    public sealed record Assessed(double SilverGap, bool CaravanRecovered) : TradeReview;
}

/// <summary>
/// What the economy/trade-mission Need reads (#3316): whether the colony's <c>family/economy/trade_missions</c> record
/// holds a mission still under way (unknown when the record was not read), and whether a supply plan proposed a
/// settlement trip with a prepared mission.
/// </summary>
public sealed record TradeMissionFacts
{
    public bool Active { get; init; }

    public bool Proposed { get; init; }
}

public sealed partial class EconomyFamily
{
    /// <summary>The silver gap answered with sale goods, through the work ledger.</summary>
    public static readonly NeedId Trade = NeedId.Of(FamilyId, "trade");

    /// <summary>A caravan present with something worth trading.</summary>
    public static readonly NeedId Caravan = NeedId.Of(FamilyId, "caravan");

    /// <summary>A settlement trade mission proposed or under way.</summary>
    public static readonly NeedId TradeMission = NeedId.Of(FamilyId, "trade-mission");

    /// <summary>
    /// The trade Need: unmet while the review's silver gap is open, met once it is closed; an unread gap raises
    /// <see cref="RequiredFactException"/>. It has
    /// no Method: its sale batches are the work ledger's, filed under the Need. Binary: the silver amount is not a
    /// fraction.
    /// </summary>
    public static Need TradeNeed(TradeReview f)
    {
        if (f is not TradeReview.Assessed assessed)
            throw new RequiredFactException("trade review awaiting food plan");
        var gap = assessed.SilverGap;
        return FoodRules.WholeNeed(Trade, NeedTier.Development, gap <= 0);
    }

    /// <summary>
    /// The caravan Need: unmet while a caravan present has something worth trading, met when none is or nothing is; an
    /// unread trader census or need raises <see cref="RequiredFactException"/>. Its work is the session phases with each caravan.
    /// </summary>
    public static Need CaravanNeed(TradeReview f)
    {
        if (f is not TradeReview.Assessed assessed)
            throw new RequiredFactException("trade review awaiting food plan");
        return FoodRules.WholeNeed(Caravan, NeedTier.Wellbeing, assessed.CaravanRecovered);
    }

    /// <summary>
    /// The trade-mission Need: unmet while the record holds a mission under way or a supply plan proposed one, met
    /// otherwise; an unread record raises <see cref="RequiredFactException"/>. Its work drives the mission one phase at a time.
    /// </summary>
    public static Need TradeMissionNeed(TradeMissionFacts f)
    {
        var active = f.Active;
        return FoodRules.WholeNeed(TradeMission, NeedTier.Development, !active && !f.Proposed);
    }
}
