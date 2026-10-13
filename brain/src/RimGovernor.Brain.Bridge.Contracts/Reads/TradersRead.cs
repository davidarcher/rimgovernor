using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>TraderRead</c>: one map trader caravan pawn a trade could be opened with, its entity id, the exact
/// OpenTrade CAS token native computed for it, its trader kind and faction, whether native reports it tradeable now
/// (CanTradeNow, not dismissed and no longer walking in), whether its caravan is still travelling to its trade spot,
/// and the cell it stands on (native walks the negotiator to it on open). Orbital ships share the census; their
/// participant selects a comms job instead of a pawn walk.
/// </summary>
public sealed record TraderRead
{
    public required TradeParticipant Participant { get; init; }

    public string Id { get; init; } = "";

    public string Token { get; init; } = "";

    public string Kind { get; init; } = "";

    public string Faction { get; init; } = "";

    public bool CanTrade { get; init; }

    public bool Travelling { get; init; }

    public string Reason { get; init; } = "";

    public uint GoodsStacks { get; init; }

    public int X { get; init; }

    public int Z { get; init; }
}

/// <summary>
/// Go's <c>NegotiatorRead</c>: one colonist native reports eligible to negotiate (not dead, downed, in a mental
/// state or social-incapable), with its OpenTrade CAS token. Native lists them best trade-price-improvement first.
/// </summary>
public sealed record NegotiatorRead(string Id, string Token);

/// <summary>
/// Go's <c>TradersRead</c>: one complete, unfiltered census of the identified map's traders and eligible
/// negotiators. The read and its decoder (<c>ListTraders</c>, <c>decodeTraders</c>) are wire plumbing and not
/// ported.
/// </summary>
public sealed record TradersRead(ObservationContext? Context, IReadOnlyList<TraderRead> Traders, IReadOnlyList<NegotiatorRead> Negotiators)
{
    /// <summary>Go's <c>tradersMaximumRows</c>: the most trader and negotiator rows one census may hold.</summary>
    public const int MaximumRows = 4096;
}
