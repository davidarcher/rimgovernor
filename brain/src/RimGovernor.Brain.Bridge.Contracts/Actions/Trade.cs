using Op = RimGovernor.Protocol.Operations;
using WireCommon = RimGovernor.Protocol.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go trade.go and trade_target.go (mapTradeTarget). Trade is RimWorld's single global session. Every step is an
// idempotent intent naming that session's trader and negotiator, sent through Actions/Apply: native validates it
// against live state when it applies and refuses with a reason when the session is gone or held by a different
// pair (NativeTradeOperations.cs). This file checks wire shape only.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>mapTradeTarget</c>: the wire target of a map trader.</summary>
    internal static WireCommon.TradeTarget MapTradeTarget(string id) => new() { MapTrader = new WireCommon.MapTradeTarget { TraderId = id } };

    /// <summary>Go's <c>tradeAction</c>: the Actions/Apply trade arm of one domain trade action.</summary>
    private static Op.Action TradeAction(DomainAction action)
    {
        var t = action.Trade ?? throw Contract("not a trade action");
        var participant = t.Participant;
        var negotiator = t.Negotiator.Value;
        if (!participant.IsValid || !IsValidId(negotiator) || (participant.Kind == TradeParticipantKind.Map && participant.Id == negotiator))
        {
            throw Contract("invalid trade participants");
        }
        WireCommon.TradeTarget? target = null;
        switch (participant.Kind)
        {
            case TradeParticipantKind.Map:
                target = MapTradeTarget(participant.Id);
                break;
            case TradeParticipantKind.Settlement:
                target = new WireCommon.TradeTarget { Settlement = new WireCommon.SettlementTradeTarget { SettlementId = participant.Id, CaravanId = participant.Caravan } };
                break;
            case TradeParticipantKind.Orbital:
                target = new WireCommon.TradeTarget { OrbitalShip = new WireCommon.OrbitalTradeTarget { ShipId = participant.Id } };
                break;
        }
        var intent = new Op.TradeIntent { Target = target, NegotiatorId = negotiator };
        switch (t.Kind)
        {
            case TradeOperationKind.Open:
                intent.Open = new Op.OpenTrade { GiftMode = t.GiftMode };
                break;
            case TradeOperationKind.SetLines:
                var rows = new List<Op.TradeLine>(t.Lines.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in t.Lines)
                {
                    if (!IsValidId(line.LineId) || !seen.Add(line.LineId))
                    {
                        throw Contract("invalid or duplicate trade line");
                    }
                    rows.Add(new Op.TradeLine { LineId = line.LineId, AbsoluteCount = line.AbsoluteCount });
                }
                if (rows.Count == 0)
                {
                    throw Contract("set trade lines requires lines");
                }
                intent.SetLines = new Op.SetTradeLines { Lines = { rows }, AllowPawns = t.AllowPawns };
                break;
            case TradeOperationKind.Accept:
                if (!IsValidId(t.ExpectedDealSignature))
                {
                    throw Contract("accept trade requires a deal signature");
                }
                intent.Accept = new Op.AcceptTrade { ExpectedDealSignature = t.ExpectedDealSignature, AllowEmpty = t.AllowEmpty, ReceiveQuest = t.ReceiveQuest };
                break;
            case TradeOperationKind.End:
                var kind = t.EndKind switch
                {
                    TradeEndKind.Cancel => Op.EndTradeKind.Cancel,
                    TradeEndKind.CloseDialog => Op.EndTradeKind.CloseDialog,
                    _ => throw Contract("end trade requires cancel or close_dialog"),
                };
                intent.End = new Op.EndTrade { Kind = kind, ReceiveQuest = t.ReceiveQuest };
                break;
            default:
                throw Contract("unknown trade step");
        }
        return new Op.Action { Trade = intent };
    }
}
