
namespace RimGovernor.Brain.GameAccess;

// Trade is RimWorld's single global session. Every step is an
// idempotent intent naming that session's trader and negotiator, sent through Actions/Apply: native validates it
// against live state when it applies and refuses with a reason when the session is gone or held by a different
// pair (NativeTradeOperations.cs). This file checks wire shape only.
public static class TradeRules
{
    /// <summary>The wire target of a map trader.</summary>
    internal static RimGovernor.Brain.GameFacts.Common.TradeTarget MapTradeTarget(string id) => new() { Kind = new RimGovernor.Brain.GameFacts.Common.MapTradeTarget { TraderId = id } };

    /// <summary>The Actions/Apply trade arm of one domain trade action.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action TradeAction(DomainAction action)
    {
        var t = action.Trade ?? throw BridgeContractException.Contract("not a trade action");
        var participant = t.Participant;
        var negotiator = t.Negotiator.Value;
        if (!participant.IsValid || (participant.Kind == TradeParticipantKind.Map && participant.Id == negotiator))
        {
            throw BridgeContractException.Contract("invalid trade participants");
        }
        RimGovernor.Brain.GameFacts.Common.TradeTarget? target = null;
        switch (participant.Kind)
        {
            case TradeParticipantKind.Map:
                target = MapTradeTarget(participant.Id);
                break;
            case TradeParticipantKind.Settlement:
                target = new RimGovernor.Brain.GameFacts.Common.TradeTarget { Kind = new RimGovernor.Brain.GameFacts.Common.SettlementTradeTarget { SettlementId = participant.Id, CaravanId = participant.Caravan } };
                break;
            case TradeParticipantKind.Orbital:
                target = new RimGovernor.Brain.GameFacts.Common.TradeTarget { Kind = new RimGovernor.Brain.GameFacts.Common.OrbitalTradeTarget { ShipId = participant.Id } };
                break;
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.TradeIntent { Target = target, NegotiatorId = negotiator };
        if (!Enum.IsDefined(t.Kind) || !Enum.IsDefined(t.EndKind)) throw BridgeContractException.Contract("invalid trade operation kind");
        switch (t.Kind)
        {
            case TradeOperationKind.Open:
                intent = intent with { Step = new RimGovernor.Brain.GameOrders.Operations.TradeIntentStep.OpenCase(new RimGovernor.Brain.GameOrders.Operations.OpenTrade { GiftMode = t.GiftMode }) };
                break;
            case TradeOperationKind.SetLines:
                var rows = new List<RimGovernor.Brain.GameOrders.Operations.TradeLine>(t.Lines.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in t.Lines)
                {
                    if (!seen.Add(line.LineId))
                    {
                        throw BridgeContractException.Contract("duplicate trade line");
                    }
                    rows.Add(new RimGovernor.Brain.GameOrders.Operations.TradeLine { LineId = line.LineId, AbsoluteCount = line.AbsoluteCount });
                }
                if (rows.Count == 0)
                {
                    throw BridgeContractException.Contract("set trade lines requires lines");
                }
                intent = intent with { Step = new RimGovernor.Brain.GameOrders.Operations.TradeIntentStep.SetLinesCase(new RimGovernor.Brain.GameOrders.Operations.SetTradeLines { Lines = [.. rows], AllowPawns = t.AllowPawns }) };
                break;
            case TradeOperationKind.Accept:
                intent = intent with { Step = new RimGovernor.Brain.GameOrders.Operations.TradeIntentStep.AcceptCase(new RimGovernor.Brain.GameOrders.Operations.AcceptTrade { ExpectedDealSignature = t.ExpectedDealSignature, AllowEmpty = t.AllowEmpty, ReceiveQuest = t.ReceiveQuest }) };
                break;
            case TradeOperationKind.End:
                RimGovernor.Brain.GameOrders.Operations.EndTradeKind kind = RimGovernor.Brain.GameOrders.Operations.EndTradeKind.Cancel;
                switch (t.EndKind)
                {
                    case TradeEndKind.Cancel: kind = RimGovernor.Brain.GameOrders.Operations.EndTradeKind.Cancel; break;
                    case TradeEndKind.CloseDialog: kind = RimGovernor.Brain.GameOrders.Operations.EndTradeKind.CloseDialog; break;
                    case TradeEndKind.None: throw BridgeContractException.Contract("end trade requires cancel or close_dialog");
                }
                intent = intent with { Step = new RimGovernor.Brain.GameOrders.Operations.TradeIntentStep.EndCase(new RimGovernor.Brain.GameOrders.Operations.EndTrade { Kind = kind, ReceiveQuest = t.ReceiveQuest }) };
                break;

        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }
}
