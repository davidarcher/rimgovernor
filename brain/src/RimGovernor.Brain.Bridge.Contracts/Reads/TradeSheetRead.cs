using RimGovernor.Protocol.Common;
using TradeTarget = RimGovernor.Protocol.Common.TradeTarget;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>TradeSheetRow</c>: one validated trade sheet line, the native line id SetTradeLines addresses, the
/// definition it trades, both sides' current counts, both prices and the eligibility flags economic selection
/// reads. Each optional proto field's presence is carried explicitly (the <c>...Known</c> flags) so an absent
/// field is never read as a zero.
/// </summary>
public sealed record TradeSheetRow
{
    public string LineId { get; init; } = "";

    public string DefName { get; init; } = "";

    public string Stuff { get; init; } = "";

    public long ColonyCount { get; init; }

    public long TraderCount { get; init; }

    public double BuyPrice { get; init; }

    public double SellPrice { get; init; }

    public double MarketValue { get; init; }

    public long TransferNow { get; init; }

    public long MinimumCount { get; init; }

    public long MaximumCount { get; init; }

    public bool BuyPriceKnown { get; init; }

    public bool SellPriceKnown { get; init; }

    public bool TraderWillTrade { get; init; }

    public bool TraderWillTradeKnown { get; init; }

    public bool Currency { get; init; }

    public bool CurrencyKnown { get; init; }

    public bool Pawn { get; init; }

    public bool PawnKnown { get; init; }

    /// <summary>Marks the favor currency row, which has no ThingDef (empty <see cref="DefName"/>).</summary>
    public bool Favor { get; init; }

    // A pawn row's purchase facts: the offered pawn's load id, skills with passions, and whether it can do
    // violence. Absent on every other row, and on a pawn native could not read.
    public string PawnId { get; init; } = "";

    public string PawnGender { get; init; } = "";

    public IReadOnlyList<TradeSheetSkill> Skills { get; init; } = [];

    public bool ViolenceCapable { get; init; }

    public bool ViolenceCapableKnown { get; init; }

    // A prisoner row's sale facts, read from the game: guest status (Guest, Prisoner, Slave), AllSellableColonyPawns'
    // secure test, downed, and the extra home / host faction ids a sale (MemberSold) angers. Absent on a pawn
    // without a guest tracker or faction.
    public string GuestStatus { get; init; } = "";

    public bool PrisonerSecure { get; init; }

    public bool PrisonerSecureKnown { get; init; }

    public bool PawnDowned { get; init; }

    public bool PawnDownedKnown { get; init; }

    public string ExtraHomeFaction { get; init; } = "";

    public string ExtraHostFaction { get; init; } = "";

    // A non-pawn row's first colony thing and its quality: a packed sculpture's row matches its packed item by ThingId.
    public string ThingId { get; init; } = "";

    public int Quality { get; init; }

    public bool QualityKnown { get; init; }

    /// <summary>The thing's hit-point fraction (what a gear sale reads).</summary>
    public double HitPoints { get; init; }

    public bool HitPointsKnown { get; init; }

    /// <summary>The stockpile the thing lies in.</summary>
    public string ZoneId { get; init; } = "";
}

/// <summary>Go's <c>TradeSheetSkill</c>: one skill of an offered pawn.</summary>
public sealed record TradeSheetSkill(string Name, int Level, string Passion, bool Disabled);

/// <summary>
/// Go's <c>TradeSheetRead</c>: one complete, unfiltered read of the currently open session's trade sheet.
/// Completeness is a precondition, not a report: the read refuses rather than return a partial sheet whenever
/// native filtered, truncated or failed to read any row, because an incomplete inventory view is unusable for an
/// economic decision. The read and its decoders (<c>ReadTradeSheet</c>, <c>tradeSheetPage</c>,
/// <c>tradeSheetRow</c>) are wire plumbing and not ported.
/// </summary>
public sealed record TradeSheetRead
{
    public TradeTarget? Target { get; init; }

    public ObservationContext? Context { get; init; }

    public string SessionId { get; init; } = "";

    public string Trader { get; init; } = "";

    public string Negotiator { get; init; } = "";

    public bool GiftMode { get; init; }

    public bool CanTradeNow { get; init; }

    public double Balance { get; init; }

    public bool BalanceKnown { get; init; }

    public bool ColonyCanAfford { get; init; }

    public bool TraderHasSilver { get; init; }

    /// <summary>
    /// A session paid in the trader faction's royal favor (the Royalty tribute collector): SellPrice is then each
    /// row's favor value and the Favor row's TransferNow the favor the negotiator gains. Absent on the wire reads
    /// as silver.
    /// </summary>
    public bool FavorCurrency { get; init; }

    public string DealSignature { get; init; } = "";

    public IReadOnlyList<TradeSheetRow> Rows { get; init; } = [];
}
