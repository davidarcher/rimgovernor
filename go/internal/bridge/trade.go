package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// Trade is RimWorld's single global session. Every step is an idempotent
// intent naming that session's trader and negotiator, sent through
// Actions/Apply: native validates it against live state when it applies and
// refuses with a reason when the session is gone or held by a different pair
// (NativeTradeOperations.cs). This file checks wire shape only.

// tradeAction is the Actions/Apply trade arm of one domain trade action.
func tradeAction(action domain.Action) (*o.Action, error) {
	t, ok := action.Trade()
	if !ok {
		return nil, contract("not a trade action")
	}
	participant, negotiator := t.Participant(), string(t.Negotiator())
	if participant.Validate() != nil || validID(negotiator) != nil || participant.Kind == domain.TradeParticipantMap && participant.ID == negotiator {
		return nil, contract("invalid trade participants")
	}
	var target *c.TradeTarget
	switch participant.Kind {
	case domain.TradeParticipantMap:
		target = mapTradeTarget(participant.ID)
	case domain.TradeParticipantSettlement:
		target = &c.TradeTarget{Kind: &c.TradeTarget_Settlement{Settlement: &c.SettlementTradeTarget{SettlementId: proto.String(participant.ID), CaravanId: proto.String(participant.Caravan)}}}
	case domain.TradeParticipantOrbital:
		target = &c.TradeTarget{Kind: &c.TradeTarget_OrbitalShip{OrbitalShip: &c.OrbitalTradeTarget{ShipId: proto.String(participant.ID)}}}
	}
	intent := &o.TradeIntent{Target: target, NegotiatorId: proto.String(negotiator)}
	switch t.Kind() {
	case domain.TradeOpen:
		intent.Step = &o.TradeIntent_Open{Open: &o.OpenTrade{GiftMode: proto.Bool(t.GiftMode())}}
	case domain.TradeSetLines:
		rows := make([]*o.TradeLine, 0, len(t.Lines()))
		seen := map[string]bool{}
		for _, l := range t.Lines() {
			if validID(l.LineID) != nil || seen[l.LineID] {
				return nil, contract("invalid or duplicate trade line")
			}
			seen[l.LineID] = true
			rows = append(rows, &o.TradeLine{LineId: proto.String(l.LineID), AbsoluteCount: proto.Int32(l.AbsoluteCount)})
		}
		if len(rows) == 0 {
			return nil, contract("set trade lines requires lines")
		}
		intent.Step = &o.TradeIntent_SetLines{SetLines: &o.SetTradeLines{Lines: rows, AllowPawns: proto.Bool(t.AllowPawns())}}
	case domain.TradeAccept:
		if validID(t.ExpectedDealSignature()) != nil {
			return nil, contract("accept trade requires a deal signature")
		}
		intent.Step = &o.TradeIntent_Accept{Accept: &o.AcceptTrade{ExpectedDealSignature: proto.String(t.ExpectedDealSignature()),
			AllowEmpty: proto.Bool(t.AllowEmpty()), ReceiveQuest: proto.Bool(t.ReceiveQuest())}}
	case domain.TradeEnd:
		kind := o.EndTradeKind_END_TRADE_KIND_CANCEL
		switch t.EndKind() {
		case domain.TradeEndCancel:
		case domain.TradeEndCloseDialog:
			kind = o.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG
		default:
			return nil, contract("end trade requires cancel or close_dialog")
		}
		intent.Step = &o.TradeIntent_End{End: &o.EndTrade{Kind: kind.Enum(), ReceiveQuest: proto.Bool(t.ReceiveQuest())}}
	default:
		return nil, contract("unknown trade step")
	}
	return &o.Action{Intent: &o.Action_Trade{Trade: intent}}, nil
}
