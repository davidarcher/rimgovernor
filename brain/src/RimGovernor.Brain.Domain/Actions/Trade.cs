using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// Selects one of the four native trade sub-operations: opening a session with an already-selected
/// trader/negotiator pair, staging an already-computed set of line adjustments, accepting an already-observed
/// deal, or ending the session. Trade is RimWorld's single global session (only one TradeSession can be open
/// at a time), so this action never carries a session id: every operation names the trader and negotiator of
/// "the" live session and native refuses when they no longer hold it.
/// </summary>
public enum TradeOperationKind
{
    Open,
    SetLines,
    Accept,
    End,
}

/// <summary>
/// Mirrors the native EndTradeKind enum's two supported values: cancel abandons the deal; close_dialog sweeps a
/// stale/foreign dialog with no goodwill effect. <see cref="None"/> (the Go empty string) means not an end.
/// </summary>
public enum TradeEndKind
{
    [WireName("")]
    None = 0,
    Cancel,
    CloseDialog,
}

/// <summary>
/// One requested row adjustment for SetTradeLines: an absolute (not relative) target count for one
/// already-identified trade sheet row. <paramref name="AbsoluteCount"/> is signed, exactly as operations.proto's
/// int32 absolute_count is: positive buys (the colony receives), negative sells (the colony gives), zero clears
/// the row. "Absolute" distinguishes it from a relative delta, not from a sign.
/// </summary>
public readonly record struct TradeLine(string LineId, int AbsoluteCount);

/// <summary>
/// Explicit intent to run one of the four trade sub-operations against RimWorld's single global session.
/// Fields outside the selected Kind's scope are always empty so each variant carries only the intent it needs.
/// Lines are sorted by line id and free of duplicates. This action never selects a trader, computes which lines
/// to propose, or judges deal value; a planner upstream of this boundary makes that choice conservatively, and
/// native alone re-derives and re-checks the live session/sheet at admission time.
/// </summary>
public sealed record Trade : IActionPayload
{
    private Trade(
        TradeOperationKind kind,
        TradeParticipant participant,
        PawnId negotiator,
        bool giftMode,
        IEnumerable<TradeLine> lines,
        bool allowPawns,
        string expectedDealSignature,
        bool allowEmpty,
        TradeEndKind endKind,
        bool receiveQuest
    )
    {
        var rows = lines.OrderBy(l => l.LineId, StringComparer.Ordinal).ToList();
        // Every intent names the session's pair; native refuses when the live session is held by another pair or none is open.
        DomainException.Require(
            participant.IsValid && negotiator.IsValid && !(participant.Kind == TradeParticipantKind.Map && participant.Id == negotiator.Value),
            "trade requires a distinct valid trader and negotiator"
        );
        switch (kind)
        {
            case TradeOperationKind.Open:
                DomainException.Require(
                    rows.Count == 0 && !allowPawns && expectedDealSignature.Length == 0 && !allowEmpty && endKind == TradeEndKind.None && !receiveQuest,
                    "open trade carries no other operation's fields"
                );
                break;
            case TradeOperationKind.SetLines:
                DomainException.Require(
                    !giftMode && expectedDealSignature.Length == 0 && !allowEmpty && endKind == TradeEndKind.None && !receiveQuest,
                    "set trade lines carries no other operation's fields"
                );
                DomainException.Require(rows.Count != 0, "set trade lines requires a nonempty line list");
                break;
            case TradeOperationKind.Accept:
                DomainException.Require(!giftMode && rows.Count == 0 && !allowPawns && endKind == TradeEndKind.None, "accept trade carries no other operation's fields");
                DomainException.Require(Identity.IsValidText(expectedDealSignature), "accept trade requires an expected deal signature");
                break;
            case TradeOperationKind.End:
                DomainException.Require(
                    !giftMode && rows.Count == 0 && !allowPawns && expectedDealSignature.Length == 0 && !allowEmpty,
                    "end trade carries no other operation's fields"
                );
                DomainException.Require(endKind is TradeEndKind.Cancel or TradeEndKind.CloseDialog, "end trade requires an explicit cancel or close_dialog kind");
                break;
            default:
                throw new DomainException("invalid trade operation kind");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in rows)
        {
            DomainException.Require(Identity.IsValidText(l.LineId) && seen.Add(l.LineId), "invalid or duplicate trade line");
        }
        Kind = kind;
        Participant = participant;
        Negotiator = negotiator;
        GiftMode = giftMode;
        Lines = EquatableArray.From(rows);
        AllowPawns = allowPawns;
        ExpectedDealSignature = expectedDealSignature;
        AllowEmpty = allowEmpty;
        EndKind = endKind;
        ReceiveQuest = receiveQuest;
    }

    /// <summary>Go <c>NewTradeOpen</c>: opens the single global trade session with one already-selected trader pawn (the caravan's trader entity) and negotiator pawn.</summary>
    public static Trade NewTradeOpen(string trader, PawnId negotiator, bool giftMode) =>
        new(TradeOperationKind.Open, MapTrader(trader), negotiator, giftMode, [], false, "", false, TradeEndKind.None, false);

    /// <summary>Go <c>NewTradeSetLines</c>: stages an already-computed set of absolute line adjustments against the live session held by trader and negotiator.</summary>
    public static Trade NewTradeSetLines(string trader, PawnId negotiator, IEnumerable<TradeLine> lines, bool allowPawns) =>
        new(TradeOperationKind.SetLines, MapTrader(trader), negotiator, false, lines, allowPawns, "", false, TradeEndKind.None, false);

    /// <summary>Go <c>NewTradeAccept</c>: accepts the currently open session's deal, exactly matching an already-observed deal signature and with no other native check than the vanilla trade rules.</summary>
    public static Trade NewTradeAccept(string trader, PawnId negotiator, string expectedDealSignature, bool allowEmpty, bool receiveQuest) =>
        new(TradeOperationKind.Accept, MapTrader(trader), negotiator, false, [], false, expectedDealSignature, allowEmpty, TradeEndKind.None, receiveQuest);

    /// <summary>Go <c>NewTradeEnd</c>: ends the currently open session, either abandoning the deal (cancel) or sweeping a stale/foreign dialog with no goodwill effect (close_dialog).</summary>
    public static Trade NewTradeEnd(string trader, PawnId negotiator, TradeEndKind kind, bool receiveQuest) =>
        new(TradeOperationKind.End, MapTrader(trader), negotiator, false, [], false, "", false, kind, receiveQuest);

    private static TradeParticipant MapTrader(string trader) => new(TradeParticipantKind.Map, trader);

    /// <summary>Go <c>WithParticipant</c>: changes only the seller identity; the four existing trade steps, floors and signature checks remain the same lifecycle.</summary>
    public Trade WithParticipant(TradeParticipant p)
    {
        p.Validate();
        return new Trade(Kind, p, Negotiator, GiftMode, Lines, AllowPawns, ExpectedDealSignature, AllowEmpty, EndKind, ReceiveQuest);
    }

    public TradeOperationKind Kind { get; }

    public TradeParticipant Participant { get; }

    /// <summary>The seller's identity (Go <c>Trader</c>).</summary>
    public string Trader => Participant.Id;

    public PawnId Negotiator { get; }

    public bool GiftMode { get; }

    public EquatableArray<TradeLine> Lines { get; }

    public bool AllowPawns { get; }

    public string ExpectedDealSignature { get; }

    public bool AllowEmpty { get; }

    public TradeEndKind EndKind { get; }

    public bool ReceiveQuest { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewTradeAction(ActionId id, Trade trade) => Create(id, ActionKind.Trade, trade);

    public Trade? Trade => PayloadOf<Trade>(ActionKind.Trade);
}
