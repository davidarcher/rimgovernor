#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// The replies of test/trade_fixture (#3404): participants and pinned stock the stage primitives cannot express (a
// negotiator beside a walking trader, a caravan visiting a settlement, a passing ship, a powered comms console and a
// faction's goodwill), and the inventories the trade read does not cover. A record carries only what a case reads.

/// <summary>
/// <c>teleport_adjacent</c>: the first colonist able to negotiate, moved beside the trader, and a second able one left
/// where it stands (empty when the crew has none).
/// </summary>
public sealed record TradeNegotiators
{
    public bool Success { get; init; }

    public string Negotiator { get; init; } = "";

    public string Other { get; init; } = "";
}

/// <summary>
/// <c>session_setup</c>: a pinned seller (a settlement our one-colonist caravan is visiting, or a passing bulk ship in
/// range of a powered console) stocking 20 components, and the negotiator holding 2000 silver and 6 industrial medicine.
/// </summary>
public sealed record TradeSessionPrepared
{
    public bool Success { get; init; }

    /// <summary>The settlement's or the ship's load id.</summary>
    public string Trader { get; init; } = "";

    /// <summary>The visiting caravan's load id; empty for a ship.</summary>
    public string Caravan { get; init; } = "";

    public string Negotiator { get; init; } = "";
}

/// <summary>
/// <c>session_inventory</c>: what the buyer owns (the caravan's inventory, or the home map's ground and colonists' for a
/// ship, an incoming drop pod not counted) and the seller's components.
/// </summary>
public sealed record TradeInventory
{
    public int Silver { get; init; }

    public int Medicine { get; init; }

    public int Components { get; init; }

    public int SellerComponents { get; init; }

    /// <summary>Components spawned on the home map.</summary>
    public int HomeComponents { get; init; }
}

/// <summary>
/// The <c>request_*</c> controls: a powered comms console, the requestable faction at goodwill 100 and off cooldown, and
/// the negotiator away from the console; then an interrupted contact, goodwill dropped to 1 (still allied) or restored,
/// and the console powered off or on.
/// </summary>
public sealed record TradeRequestPrepared
{
    public bool Success { get; init; }

    public string Faction { get; init; } = "";

    public string Negotiator { get; init; } = "";

    public string Console { get; init; } = "";

    public string TraderKind { get; init; } = "";

    public int Goodwill { get; init; }

    /// <summary>The goodwill the request debits.</summary>
    public int Cost { get; init; }

    public long LastRequestTick { get; init; }

    public bool Ally { get; init; }
}
