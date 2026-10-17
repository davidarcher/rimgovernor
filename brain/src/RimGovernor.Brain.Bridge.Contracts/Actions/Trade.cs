using Op = RimGovernor.Contracts.Wire.Operations;
using WireCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Trade is RimWorld's single global session. Every step is an
// idempotent intent naming that session's trader and negotiator, sent through Actions/Apply: native validates it
// against live state when it applies and refuses with a reason when the session is gone or held by a different
// pair (NativeTradeOperations.cs). This file checks wire shape only.
public static class TradeRules
{
    /// <summary>The wire target of a map trader.</summary>
    internal static WireCommon.TradeTarget MapTradeTarget(string id) => new() { Kind = new WireCommon.MapTradeTarget { TraderId = id } };

    /// <summary>The Actions/Apply trade arm of one domain trade action.</summary>
    internal static Op.Action TradeAction(DomainAction action)
    {
        var t = action.Trade ?? throw BridgeContractException.Contract("not a trade action");
        var participant = t.Participant;
        var negotiator = t.Negotiator.Value;
        if (!participant.IsValid || (participant.Kind == TradeParticipantKind.Map && participant.Id == negotiator))
        {
            throw BridgeContractException.Contract("invalid trade participants");
        }
        WireCommon.TradeTarget? target = null;
        switch (participant.Kind)
        {
            case TradeParticipantKind.Map:
                target = MapTradeTarget(participant.Id);
                break;
            case TradeParticipantKind.Settlement:
                target = new WireCommon.TradeTarget { Kind = new WireCommon.SettlementTradeTarget { SettlementId = participant.Id, CaravanId = participant.Caravan } };
                break;
            case TradeParticipantKind.Orbital:
                target = new WireCommon.TradeTarget { Kind = new WireCommon.OrbitalTradeTarget { ShipId = participant.Id } };
                break;
        }
        var intent = new Op.TradeIntent { Target = target, NegotiatorId = negotiator };
        switch (t.Kind)
        {
            case TradeOperationKind.Open:
                intent = intent with { Step = new Op.TradeIntentStep.OpenCase(new Op.OpenTrade { GiftMode = t.GiftMode }) };
                break;
            case TradeOperationKind.SetLines:
                var rows = new List<Op.TradeLine>(t.Lines.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in t.Lines)
                {
                    if (!seen.Add(line.LineId))
                    {
                        throw BridgeContractException.Contract("duplicate trade line");
                    }
                    rows.Add(new Op.TradeLine { LineId = line.LineId, AbsoluteCount = line.AbsoluteCount });
                }
                if (rows.Count == 0)
                {
                    throw BridgeContractException.Contract("set trade lines requires lines");
                }
                intent = intent with { Step = new Op.TradeIntentStep.SetLinesCase(new Op.SetTradeLines { Lines = [.. rows], AllowPawns = t.AllowPawns }) };
                break;
            case TradeOperationKind.Accept:
                intent = intent with { Step = new Op.TradeIntentStep.AcceptCase(new Op.AcceptTrade { ExpectedDealSignature = t.ExpectedDealSignature, AllowEmpty = t.AllowEmpty, ReceiveQuest = t.ReceiveQuest }) };
                break;
            case TradeOperationKind.End:
                var kind = t.EndKind switch
                {
                    TradeEndKind.Cancel => Op.EndTradeKind.Cancel,
                    TradeEndKind.CloseDialog => Op.EndTradeKind.CloseDialog,
                    _ => throw BridgeContractException.Contract("end trade requires cancel or close_dialog"),
                };
                intent = intent with { Step = new Op.TradeIntentStep.EndCase(new Op.EndTrade { Kind = kind, ReceiveQuest = t.ReceiveQuest }) };
                break;
            default:
                throw BridgeContractException.Contract("unknown trade step");
        }
        return new Op.Action { Intent = intent };
    }
}
