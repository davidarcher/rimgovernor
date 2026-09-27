package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// Trade is RimWorld's single global session. Every operation is an
// idempotent intent naming that session's trader and negotiator: native
// validates it against live state when it applies and refuses with a reason
// when the session is gone or held by a different pair
// (NativeTradeOperations.cs). This file checks wire shape only.

// TradeLineInput is one requested row adjustment for SetTradeLines.
type TradeLineInput struct {
	LineID        string
	AbsoluteCount int32
}

// TradeEconomicFloor is one AcceptTrade reserve-stock guard.
type TradeEconomicFloor struct {
	DefName string
	Count   int32
}

// OpenTradeIntent opens a session with the trader, or reuses the matching
// open session or walk.
func OpenTradeIntent(trader, negotiator string, giftMode bool) *o.Operation {
	return &o.Operation{Command: &o.Operation_OpenTrade{OpenTrade: &o.OpenTrade{TraderId: proto.String(trader), NegotiatorId: proto.String(negotiator), GiftMode: proto.Bool(giftMode)}}}
}

// SetTradeLinesIntent stages absolute counts on the live session.
func SetTradeLinesIntent(trader, negotiator string, lines []TradeLineInput, allowPawns bool) *o.Operation {
	rows := make([]*o.TradeLine, 0, len(lines))
	for _, l := range lines {
		rows = append(rows, &o.TradeLine{LineId: proto.String(l.LineID), AbsoluteCount: proto.Int32(l.AbsoluteCount)})
	}
	return &o.Operation{Command: &o.Operation_SetTradeLines{SetTradeLines: &o.SetTradeLines{TraderId: proto.String(trader), NegotiatorId: proto.String(negotiator), Lines: rows, AllowPawns: proto.Bool(allowPawns)}}}
}

// AcceptTradeIntent accepts the live session's deal if its signature still
// matches.
func AcceptTradeIntent(trader, negotiator, dealSignature string, floors []TradeEconomicFloor, allowEmpty, receiveQuest bool) *o.Operation {
	rows := make([]*o.DefCount, 0, len(floors))
	for _, f := range floors {
		rows = append(rows, &o.DefCount{DefName: proto.String(f.DefName), Count: proto.Int32(f.Count)})
	}
	return &o.Operation{Command: &o.Operation_AcceptTrade{AcceptTrade: &o.AcceptTrade{
		TraderId: proto.String(trader), NegotiatorId: proto.String(negotiator), ExpectedDealSignature: proto.String(dealSignature),
		EconomicFloors: rows, AllowEmpty: proto.Bool(allowEmpty), ReceiveQuest: proto.Bool(receiveQuest),
	}}}
}

// EndTradeIntent ends the live session; a cancel with none left is a no-op.
func EndTradeIntent(trader, negotiator string, kind o.EndTradeKind, receiveQuest bool) *o.Operation {
	return &o.Operation{Command: &o.Operation_EndTrade{EndTrade: &o.EndTrade{TraderId: proto.String(trader), NegotiatorId: proto.String(negotiator), Kind: kind.Enum(), ReceiveQuest: proto.Bool(receiveQuest)}}}
}

func tradeParticipants(trader, negotiator string) error {
	if validID(trader) != nil || validID(negotiator) != nil || trader == negotiator {
		return contract("trade intent requires a distinct trader and negotiator")
	}
	return nil
}

// tradeIntent validates one trade operation's shape.
func tradeIntent(operation *o.Operation) error {
	switch v := operation.GetCommand().(type) {
	case *o.Operation_OpenTrade:
		return tradeParticipants(v.OpenTrade.GetTraderId(), v.OpenTrade.GetNegotiatorId())
	case *o.Operation_SetTradeLines:
		if len(v.SetTradeLines.GetLines()) == 0 {
			return contract("set trade lines requires lines")
		}
		seen := map[string]bool{}
		for _, l := range v.SetTradeLines.GetLines() {
			if validID(l.GetLineId()) != nil || seen[l.GetLineId()] {
				return contract("invalid or duplicate trade line")
			}
			seen[l.GetLineId()] = true
		}
		return tradeParticipants(v.SetTradeLines.GetTraderId(), v.SetTradeLines.GetNegotiatorId())
	case *o.Operation_AcceptTrade:
		if validID(v.AcceptTrade.GetExpectedDealSignature()) != nil {
			return contract("accept trade requires a deal signature")
		}
		seen := map[string]bool{}
		for _, f := range v.AcceptTrade.GetEconomicFloors() {
			if validID(f.GetDefName()) != nil || f.GetCount() < 0 || seen[f.GetDefName()] {
				return contract("invalid or duplicate economic floor")
			}
			seen[f.GetDefName()] = true
		}
		return tradeParticipants(v.AcceptTrade.GetTraderId(), v.AcceptTrade.GetNegotiatorId())
	case *o.Operation_EndTrade:
		if k := v.EndTrade.GetKind(); k != o.EndTradeKind_END_TRADE_KIND_CANCEL && k != o.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG {
			return contract("end trade requires cancel or close_dialog")
		}
		return tradeParticipants(v.EndTrade.GetTraderId(), v.EndTrade.GetNegotiatorId())
	default:
		return contract("not a trade intent")
	}
}

// tradeEffect structurally validates the TradeEffect evidence every trade
// reply carries.
func tradeEffect(evidence *r.EffectEvidence) error {
	t := evidence.GetTrade()
	if t == nil {
		return contract("trade evidence missing")
	}
	if !diagnostic(t.DealSignature) || !diagnostic(t.SessionId) || !diagnostic(t.FactionId) {
		return contract("trade evidence text invalid")
	}
	for _, id := range t.GetReceivedQuestIds() {
		if validID(id) != nil {
			return contract("trade received quest id invalid")
		}
	}
	for _, line := range t.GetLines() {
		if line.GetLineId() == "" {
			return contract("trade line evidence missing id")
		}
	}
	return nil
}

func tradeReceipt(v *r.Receipt, identity *c.Identity, attempt *c.AttemptKey) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, attempt) {
		return contract("trade admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, identity, 0, false); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("trade applied missing")
		}
		return tradeEffect(outcome.Applied.GetObserved())
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("trade uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			return tradeEffect(outcome.Uncertain.LastObserved)
		}
		return nil
	default:
		return contract("unsupported trade receipt")
	}
}

// PreviewTrade asks native whether an intent would apply now; acceptance is
// not authority.
func (client *Client) PreviewTrade(ctx context.Context, identity *c.Identity, operation *o.Operation) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := tradeIntent(operation); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: operation}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil || value.Accepted == nil || !diagnostic(value.Reason) || value.GetTrade() == nil {
			return reply, raw, contract("trade preview facts missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err == nil {
			err = tradeEffect(value.Projected)
		}
	default:
		err = contract("trade preview outcome missing")
	}
	return reply, raw, err
}

// TradeWriter applies trade intents.
type TradeWriter struct{ client *Client }

func NewTradeWriter(client *Client) (*TradeWriter, error) {
	if client == nil {
		return nil, contract("trade client missing")
	}
	return &TradeWriter{client}, nil
}

func (writer *TradeWriter) ApplyTrade(ctx context.Context, pre *a.WritePrecondition, operation *o.Operation) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil {
		return nil, Result{}, contract("invalid trade execution")
	}
	if err := tradeIntent(operation); err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: operation}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = tradeReceipt(v.Receipt, pre.Identity, pre.Attempt)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("trade execute outcome missing")
	}
	return reply, raw, err
}

// tradeAction is the Actions/Apply trade arm of one domain trade action.
func tradeAction(action domain.Action) (*o.Action, error) {
	t, ok := action.Trade()
	if !ok {
		return nil, contract("not a trade action")
	}
	trader, negotiator := t.Trader(), string(t.Negotiator())
	if err := tradeParticipants(trader, negotiator); err != nil {
		return nil, err
	}
	intent := &o.TradeIntent{TraderId: proto.String(trader), NegotiatorId: proto.String(negotiator)}
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
		floors := make([]*o.DefCount, 0, len(t.EconomicFloors()))
		seen := map[string]bool{}
		for _, f := range t.EconomicFloors() {
			if validID(f.DefName) != nil || f.Count < 0 || seen[f.DefName] {
				return nil, contract("invalid or duplicate economic floor")
			}
			seen[f.DefName] = true
			floors = append(floors, &o.DefCount{DefName: proto.String(f.DefName), Count: proto.Int32(f.Count)})
		}
		intent.Step = &o.TradeIntent_Accept{Accept: &o.AcceptTrade{ExpectedDealSignature: proto.String(t.ExpectedDealSignature()),
			EconomicFloors: floors, AllowEmpty: proto.Bool(t.AllowEmpty()), ReceiveQuest: proto.Bool(t.ReceiveQuest())}}
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
