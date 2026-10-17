namespace RimGovernor.Brain.Policy;

/// <summary>
/// The review's trade facts (#3316), derived by <see cref="RoundsRules.InspectRounds"/> and kept by the Rounder
/// per world: the silver gap a purchase need leaves open (the economy/trade Need's), and whether the caravans present
/// leave nothing worth trading (the economy/caravan Need's). Unknown until the first review of a world.
/// </summary>
public sealed record TradeReview
{
    public Fact<double> SilverGap { get; init; }

    public Fact<bool> CaravanRecovered { get; init; }
}

/// <summary>
/// What the economy/trade-mission Need reads (#3316): whether the colony's <c>family/economy/trade_missions</c> record
/// holds a mission still under way (unknown when the record was not read), and whether a supply plan proposed a
/// settlement trip with a prepared mission.
/// </summary>
public sealed record TradeMissionFacts
{
    public Fact<bool> Active { get; init; }

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
    /// The trade Need: unmet while the review's silver gap is open, met once it is closed, unknown while unread. It has
    /// no Method: its sale batches are the work ledger's, filed under the Need. Binary: the silver amount is not a
    /// fraction.
    /// </summary>
    public static Need TradeNeed(TradeReview f)
    {
        var satisfaction = !f.SilverGap.TryGetValue(out var gap) ? Satisfaction.Unknown : gap > 0 ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = Trade,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence = [new("silver_gap", f.SilverGap.TryGetValue(out var g) ? Known(g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) : Unknown<string>())],
        };
    }

    /// <summary>
    /// The caravan Need: unmet while a caravan present has something worth trading, met when none is or nothing is,
    /// unknown while the trader census or the need is unread. Its work is the session phases with each caravan.
    /// </summary>
    public static Need CaravanNeed(TradeReview f)
    {
        var satisfaction = !f.CaravanRecovered.TryGetValue(out var recovered) ? Satisfaction.Unknown : recovered ? Satisfaction.Met : Satisfaction.Unmet;
        return new Need
        {
            Id = Caravan,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence = [new("caravan", satisfaction == Satisfaction.Unknown ? Unknown<string>() : Known(recovered ? "settled" : "trade"))],
        };
    }

    /// <summary>
    /// The trade-mission Need: unmet while the record holds a mission under way or a supply plan proposed one, met
    /// otherwise, unknown while the record was not read. Its work drives the mission one phase at a time.
    /// </summary>
    public static Need TradeMissionNeed(TradeMissionFacts f)
    {
        var satisfaction = !f.Active.TryGetValue(out var active) ? Satisfaction.Unknown : active || f.Proposed ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = TradeMission,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence =
            [
                new("mission", f.Active.TryGetValue(out var a) ? Known(a ? "active" : "none") : Unknown<string>()),
                new("proposed", Known(f.Proposed ? "settlement" : "none")),
            ],
        };
    }

    private static Fact<double> Whole(Satisfaction satisfaction) => satisfaction switch
    {
        Satisfaction.Unknown => Unknown<double>(),
        Satisfaction.Unmet => Known(1.0),
        _ => Known(0.0),
    };
}
