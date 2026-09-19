package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// Trade is a single global RimWorld session (only one TradeSession can be
// open at a time), unlike the per-target verticals elsewhere in this
// package, so every one of the four sub-operations below (OpenTrade,
// SetTradeLines, AcceptTrade, EndTrade) addresses "the" session by an
// EntityPrecondition rather than by a caller-chosen id. Session/deal state
// is entirely server side (NativeTradeOperations.cs); this file validates
// wire-contract shape and structural evidence facts only -- it cannot
// predict game-computed values (goodwill deltas, silver counts, exact deal
// signatures) client side, the same limitation documented on the other
// self-computed-token verticals (waste, husbandry, recovery).

func tradeDefCount(defName string, count int32) *o.DefCount {
	return &o.DefCount{DefName: proto.String(defName), Count: proto.Int32(count)}
}

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

// -------------------------------------------------------------- shared
// tradeEffect extracts and structurally validates the common TradeEffect
// facts every sub-operation's evidence carries; op-specific validators
// layer additional checks (e.g. AcceptTrade must close the session).
func tradeEffect(evidence *r.EffectEvidence) (*r.TradeEffect, error) {
	if evidence == nil {
		return nil, contract("trade evidence missing")
	}
	t := evidence.GetTrade()
	if t == nil {
		return nil, contract("trade evidence missing")
	}
	if !diagnostic(t.DealSignature) || !diagnostic(t.SessionId) || !diagnostic(t.FactionId) {
		return nil, contract("trade evidence text invalid")
	}
	for _, id := range t.GetReceivedQuestIds() {
		if validID(id) != nil {
			return nil, contract("trade received quest id invalid")
		}
	}
	for _, line := range t.GetLines() {
		if line == nil || line.GetLineId() == "" {
			return nil, contract("trade line evidence missing id")
		}
	}
	return t, nil
}

type tradeEvidenceValidator func(*r.EffectEvidence) (*r.TradeEffect, error)

func tradeReceiptGeneric(v *r.Receipt, identity *c.Identity, attempt *c.AttemptKey, generation uint64, validate tradeEvidenceValidator) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, attempt) {
		return contract("trade admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, identity, generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("trade applied missing")
		}
		_, err := validate(outcome.Applied.GetObserved())
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("trade uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := validate(outcome.Uncertain.LastObserved)
			return err
		}
		return nil
	default:
		return contract("unsupported trade receipt")
	}
}

func tradeLookupGeneric(reply *r.LookupReply, raw Result, identity *c.Identity, attempt *c.AttemptKey, generation uint64, validate tradeEvidenceValidator) error {
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		return tradeReceiptGeneric(v.Receipt, identity, attempt, generation, validate)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt) {
			return contract("trade in-flight attempt mismatch")
		}
		return buildingContext(v.InFlight.AdmittedContext, identity, generation, true)
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			return contract("trade unknown context missing")
		}
		return buildingContext(v.Unknown.Context, identity, 0, false)
	case *r.LookupReply_Failure:
		return failure(v.Failure, raw)
	default:
		return contract("trade lookup outcome missing")
	}
}

func tradeProgressGeneric(v *r.Progress, identity *c.Identity, attempt *c.AttemptKey, admitted *r.Receipt, validate tradeEvidenceValidator) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, attempt) {
		return contract("trade progress attempt mismatch")
	}
	if err := buildingContext(v.Context, identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("trade progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("trade unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("trade pending missing")
		}
		_, err := validate(outcome.Pending.Evidence)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("trade completed missing")
		}
		_, err := validate(outcome.Completed.Evidence)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("trade absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("trade unsuccessful reason missing")
		}
		_, err := validate(outcome.Unsuccessful.Evidence)
		return err
	default:
		return contract("trade progress state missing")
	}
}

// TradeWriter dispatches all four admitted trade sub-operations; RimWorld's
// single-session constraint means these share one session identity rather
// than one writer per target the way other verticals do.
type TradeWriter struct{ client *Client }

func NewTradeWriter(client *Client) (*TradeWriter, error) {
	if client == nil {
		return nil, contract("trade client missing")
	}
	return &TradeWriter{client}, nil
}

// =============================================================== OpenTrade

type TradeOpenAttempt struct {
	Identity                    *c.Identity
	Attempt                     *c.AttemptKey
	Generation                  uint64
	Trader, TraderToken         string
	Negotiator, NegotiatorToken string
	GiftMode                    bool
}

func tradeOpenOperation(trader, traderToken, negotiator, negotiatorToken string, giftMode bool) *o.Operation {
	return &o.Operation{Command: &o.Operation_OpenTrade{OpenTrade: &o.OpenTrade{
		Trader: gearEntity(trader, traderToken), Negotiator: gearEntity(negotiator, negotiatorToken), GiftMode: proto.Bool(giftMode),
	}}}
}
func tradeOpenCommand(trader, traderToken, negotiator, negotiatorToken string) error {
	if validID(trader) != nil || validID(traderToken) != nil || validID(negotiator) != nil || validID(negotiatorToken) != nil || trader == negotiator {
		return contract("invalid open trade command")
	}
	return nil
}

// PreviewOpenTrade checks an exact already-selected trader/negotiator pair;
// acceptance is not authority.
func (client *Client) PreviewOpenTrade(ctx context.Context, identity *c.Identity, trader, traderToken, negotiator, negotiatorToken string, giftMode bool) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := tradeOpenCommand(trader, traderToken, negotiator, negotiatorToken); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: tradeOpenOperation(trader, traderToken, negotiator, negotiatorToken, giftMode)}, reply)
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
		if value == nil {
			return reply, raw, contract("open trade preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		trade := value.GetTrade()
		if value.Accepted == nil || !diagnostic(value.Reason) || trade == nil || trade.GetSettlementId() != trader {
			err = contract("open trade preview facts missing")
			break
		}
		_, err = tradeEffect(value.Projected)
	default:
		err = contract("open trade preview outcome missing")
	}
	return reply, raw, err
}

func tradeOpenAttempt(v TradeOpenAttempt) (TradeOpenAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return TradeOpenAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return TradeOpenAttempt{}, err
	}
	if v.Generation == 0 {
		return TradeOpenAttempt{}, contract("open trade admission owner or generation mismatch")
	}
	if err := tradeOpenCommand(v.Trader, v.TraderToken, v.Negotiator, v.NegotiatorToken); err != nil {
		return TradeOpenAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

// tradeOpenEvidence accepts the two shapes an open's evidence takes: the
// opened session (id and snapshot present) and, while native walks the
// negotiator to the trader, no session at all (id, signature and snapshot
// all empty). The boundary requires the session once the open completes.
func tradeOpenEvidence(evidence *r.EffectEvidence, expected TradeOpenAttempt) (*r.TradeEffect, error) {
	t, err := tradeEffect(evidence)
	if err != nil {
		return nil, err
	}
	if t.GetExecuted() || t.GetClosed() || t.GetFactionId() == "" {
		return nil, contract("open trade evidence fields invalid")
	}
	if t.GetSessionId() == "" {
		if t.Snapshot != nil || t.GetDealSignature() != "" {
			return nil, contract("open trade walking evidence carries a session")
		}
		return t, nil
	}
	if t.Snapshot == nil || t.Snapshot.GetAfterToken() == "" || t.Snapshot.GetEntityId() == "" {
		return nil, contract("open trade missing session snapshot")
	}
	return t, nil
}

func (writer *TradeWriter) ApplyOpenTrade(ctx context.Context, pre *a.WritePrecondition, trader, traderToken, negotiator, negotiatorToken string, giftMode bool) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid open trade execution")
	}
	if err := tradeOpenCommand(trader, traderToken, negotiator, negotiatorToken); err != nil {
		return nil, Result{}, err
	}
	expected, err := tradeOpenAttempt(TradeOpenAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Trader: trader, TraderToken: traderToken, Negotiator: negotiator, NegotiatorToken: negotiatorToken, GiftMode: giftMode})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: tradeOpenOperation(trader, traderToken, negotiator, negotiatorToken, giftMode)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = tradeReceiptGeneric(v.Receipt, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeOpenEvidence(e, expected) })
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("open trade execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupTradeOpen(ctx context.Context, w TradeOpenAttempt) (*r.LookupReply, Result, error) {
	expected, err := tradeOpenAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	err = tradeLookupGeneric(reply, raw, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeOpenEvidence(e, expected) })
	return reply, raw, err
}
func (client *Client) ObserveTradeOpenProgress(ctx context.Context, w TradeOpenAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := tradeOpenAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = tradeReceiptGeneric(admitted, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeOpenEvidence(e, expected) }); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = tradeProgressGeneric(v.Progress, expected.Identity, expected.Attempt, admitted, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeOpenEvidence(e, expected) })
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("open trade progress outcome missing")
	}
	return reply, raw, err
}

// =========================================================== SetTradeLines

type TradeSetLinesAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Generation            uint64
	Session, SessionToken string
	Lines                 []TradeLineInput
	AllowPawns            bool
}

func tradeSetLinesOperation(session, sessionToken string, lines []TradeLineInput, allowPawns bool) *o.Operation {
	protoLines := make([]*o.TradeLine, 0, len(lines))
	for _, l := range lines {
		protoLines = append(protoLines, &o.TradeLine{LineId: proto.String(l.LineID), AbsoluteCount: proto.Int32(l.AbsoluteCount)})
	}
	return &o.Operation{Command: &o.Operation_SetTradeLines{SetTradeLines: &o.SetTradeLines{
		Session: gearEntity(session, sessionToken), Lines: protoLines, AllowPawns: proto.Bool(allowPawns),
	}}}
}
func tradeSetLinesCommand(session, sessionToken string, lines []TradeLineInput) error {
	if validID(session) != nil || validID(sessionToken) != nil || len(lines) == 0 {
		return contract("invalid set trade lines command")
	}
	seen := make(map[string]bool, len(lines))
	for _, l := range lines {
		if validID(l.LineID) != nil {
			return contract("invalid trade line id")
		}
		if seen[l.LineID] {
			return contract("duplicate trade line in one request")
		}
		seen[l.LineID] = true
	}
	return nil
}

// PreviewSetTradeLines checks an exact already-staged line request against
// the currently open session; acceptance is not authority.
func (client *Client) PreviewSetTradeLines(ctx context.Context, identity *c.Identity, session, sessionToken string, lines []TradeLineInput, allowPawns bool) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := tradeSetLinesCommand(session, sessionToken, lines); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: tradeSetLinesOperation(session, sessionToken, lines, allowPawns)}, reply)
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
		if value == nil {
			return reply, raw, contract("set trade lines preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		trade := value.GetTrade()
		if value.Accepted == nil || !diagnostic(value.Reason) || trade == nil || trade.GetSessionId() != session {
			err = contract("set trade lines preview facts missing")
			break
		}
		effect, effErr := tradeEffect(value.Projected)
		if effErr != nil {
			err = effErr
			break
		}
		if len(effect.GetLines()) != len(lines) {
			err = contract("set trade lines preview projection line count mismatch")
		}
	default:
		err = contract("set trade lines preview outcome missing")
	}
	return reply, raw, err
}

func tradeSetLinesAttempt(v TradeSetLinesAttempt) (TradeSetLinesAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return TradeSetLinesAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return TradeSetLinesAttempt{}, err
	}
	if v.Generation == 0 {
		return TradeSetLinesAttempt{}, contract("set trade lines admission owner or generation mismatch")
	}
	if err := tradeSetLinesCommand(v.Session, v.SessionToken, v.Lines); err != nil {
		return TradeSetLinesAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}
func tradeSetLinesEvidence(evidence *r.EffectEvidence, expected TradeSetLinesAttempt) (*r.TradeEffect, error) {
	t, err := tradeEffect(evidence)
	if err != nil {
		return nil, err
	}
	if t.GetExecuted() || t.GetClosed() {
		return nil, contract("set trade lines evidence must not be executed or closed")
	}
	if len(t.GetLines()) != len(expected.Lines) {
		return nil, contract("set trade lines evidence line count mismatch")
	}
	if t.Snapshot == nil || t.Snapshot.GetAfterToken() == "" {
		return nil, contract("set trade lines missing session snapshot")
	}
	return t, nil
}

func (writer *TradeWriter) ApplySetTradeLines(ctx context.Context, pre *a.WritePrecondition, session, sessionToken string, lines []TradeLineInput, allowPawns bool) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid set trade lines execution")
	}
	if err := tradeSetLinesCommand(session, sessionToken, lines); err != nil {
		return nil, Result{}, err
	}
	expected, err := tradeSetLinesAttempt(TradeSetLinesAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Session: session, SessionToken: sessionToken, Lines: lines, AllowPawns: allowPawns})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: tradeSetLinesOperation(session, sessionToken, lines, allowPawns)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = tradeReceiptGeneric(v.Receipt, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeSetLinesEvidence(e, expected) })
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("set trade lines execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupTradeSetLines(ctx context.Context, w TradeSetLinesAttempt) (*r.LookupReply, Result, error) {
	expected, err := tradeSetLinesAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	err = tradeLookupGeneric(reply, raw, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeSetLinesEvidence(e, expected) })
	return reply, raw, err
}
func (client *Client) ObserveTradeSetLinesProgress(ctx context.Context, w TradeSetLinesAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := tradeSetLinesAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = tradeReceiptGeneric(admitted, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeSetLinesEvidence(e, expected) }); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = tradeProgressGeneric(v.Progress, expected.Identity, expected.Attempt, admitted, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeSetLinesEvidence(e, expected) })
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("set trade lines progress outcome missing")
	}
	return reply, raw, err
}

// =============================================================== AcceptTrade

type TradeAcceptAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Generation            uint64
	Session, SessionToken string
	ExpectedDealSignature string
	EconomicFloors        []TradeEconomicFloor
	AllowEmpty            bool
	ReceiveQuest          bool
}

func tradeAcceptOperation(session, sessionToken, expectedDealSignature string, floors []TradeEconomicFloor, allowEmpty, receiveQuest bool) *o.Operation {
	protoFloors := make([]*o.DefCount, 0, len(floors))
	for _, f := range floors {
		protoFloors = append(protoFloors, tradeDefCount(f.DefName, f.Count))
	}
	return &o.Operation{Command: &o.Operation_AcceptTrade{AcceptTrade: &o.AcceptTrade{
		Session: gearEntity(session, sessionToken), ExpectedDealSignature: proto.String(expectedDealSignature),
		EconomicFloors: protoFloors, AllowEmpty: proto.Bool(allowEmpty), ReceiveQuest: proto.Bool(receiveQuest),
	}}}
}
func tradeAcceptCommand(session, sessionToken, expectedDealSignature string, floors []TradeEconomicFloor) error {
	if validID(session) != nil || validID(sessionToken) != nil || validID(expectedDealSignature) != nil {
		return contract("invalid accept trade command")
	}
	seen := make(map[string]bool, len(floors))
	for _, f := range floors {
		if validID(f.DefName) != nil || f.Count < 0 {
			return contract("invalid economic floor")
		}
		if seen[f.DefName] {
			return contract("duplicate economic floor def")
		}
		seen[f.DefName] = true
	}
	return nil
}

// PreviewAcceptTrade checks an exact already-staged deal against the
// currently open session; acceptance is not authority.
func (client *Client) PreviewAcceptTrade(ctx context.Context, identity *c.Identity, session, sessionToken, expectedDealSignature string, floors []TradeEconomicFloor, allowEmpty, receiveQuest bool) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := tradeAcceptCommand(session, sessionToken, expectedDealSignature, floors); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: tradeAcceptOperation(session, sessionToken, expectedDealSignature, floors, allowEmpty, receiveQuest)}, reply)
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
		if value == nil {
			return reply, raw, contract("accept trade preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		trade := value.GetTrade()
		if value.Accepted == nil || !diagnostic(value.Reason) || trade == nil || trade.GetSessionId() != session {
			err = contract("accept trade preview facts missing")
			break
		}
		_, err = tradeEffect(value.Projected)
	default:
		err = contract("accept trade preview outcome missing")
	}
	return reply, raw, err
}

func tradeAcceptAttempt(v TradeAcceptAttempt) (TradeAcceptAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return TradeAcceptAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return TradeAcceptAttempt{}, err
	}
	if v.Generation == 0 {
		return TradeAcceptAttempt{}, contract("accept trade admission owner or generation mismatch")
	}
	if err := tradeAcceptCommand(v.Session, v.SessionToken, v.ExpectedDealSignature, v.EconomicFloors); err != nil {
		return TradeAcceptAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}
func tradeAcceptEvidence(evidence *r.EffectEvidence, expected TradeAcceptAttempt) (*r.TradeEffect, error) {
	t, err := tradeEffect(evidence)
	if err != nil {
		return nil, err
	}
	if !t.GetClosed() {
		return nil, contract("accept trade evidence must close the session")
	}
	return t, nil
}

func (writer *TradeWriter) ApplyAcceptTrade(ctx context.Context, pre *a.WritePrecondition, session, sessionToken, expectedDealSignature string, floors []TradeEconomicFloor, allowEmpty, receiveQuest bool) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid accept trade execution")
	}
	if err := tradeAcceptCommand(session, sessionToken, expectedDealSignature, floors); err != nil {
		return nil, Result{}, err
	}
	expected, err := tradeAcceptAttempt(TradeAcceptAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Session: session, SessionToken: sessionToken, ExpectedDealSignature: expectedDealSignature, EconomicFloors: floors, AllowEmpty: allowEmpty, ReceiveQuest: receiveQuest})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: tradeAcceptOperation(session, sessionToken, expectedDealSignature, floors, allowEmpty, receiveQuest)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = tradeReceiptGeneric(v.Receipt, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeAcceptEvidence(e, expected) })
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("accept trade execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupTradeAccept(ctx context.Context, w TradeAcceptAttempt) (*r.LookupReply, Result, error) {
	expected, err := tradeAcceptAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	err = tradeLookupGeneric(reply, raw, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeAcceptEvidence(e, expected) })
	return reply, raw, err
}
func (client *Client) ObserveTradeAcceptProgress(ctx context.Context, w TradeAcceptAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := tradeAcceptAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = tradeReceiptGeneric(admitted, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeAcceptEvidence(e, expected) }); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = tradeProgressGeneric(v.Progress, expected.Identity, expected.Attempt, admitted, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeAcceptEvidence(e, expected) })
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("accept trade progress outcome missing")
	}
	return reply, raw, err
}

// =================================================================== EndTrade

type TradeEndAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Generation            uint64
	Session, SessionToken string
	Kind                  o.EndTradeKind
	ReceiveQuest          bool
}

func tradeEndOperation(session, sessionToken string, kind o.EndTradeKind, receiveQuest bool) *o.Operation {
	return &o.Operation{Command: &o.Operation_EndTrade{EndTrade: &o.EndTrade{
		Session: gearEntity(session, sessionToken), Kind: kind.Enum(), ReceiveQuest: proto.Bool(receiveQuest),
	}}}
}
func tradeEndCommand(session, sessionToken string, kind o.EndTradeKind) error {
	if validID(session) != nil || validID(sessionToken) != nil {
		return contract("invalid end trade command")
	}
	if kind != o.EndTradeKind_END_TRADE_KIND_CANCEL && kind != o.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG {
		return contract("end trade requires an explicit cancel or close_dialog kind")
	}
	return nil
}

// PreviewEndTrade checks an exact end request against the currently open
// session; acceptance is not authority. close_dialog always succeeds
// structurally even against a stale/foreign session -- see NativeTradeOperations.PrepareEnd.
func (client *Client) PreviewEndTrade(ctx context.Context, identity *c.Identity, session, sessionToken string, kind o.EndTradeKind, receiveQuest bool) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := tradeEndCommand(session, sessionToken, kind); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: tradeEndOperation(session, sessionToken, kind, receiveQuest)}, reply)
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
		if value == nil {
			return reply, raw, contract("end trade preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		if value.Accepted == nil || !diagnostic(value.Reason) {
			err = contract("end trade preview facts missing")
			break
		}
		_, err = tradeEffect(value.Projected)
	default:
		err = contract("end trade preview outcome missing")
	}
	return reply, raw, err
}

func tradeEndAttempt(v TradeEndAttempt) (TradeEndAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return TradeEndAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return TradeEndAttempt{}, err
	}
	if v.Generation == 0 {
		return TradeEndAttempt{}, contract("end trade admission owner or generation mismatch")
	}
	if err := tradeEndCommand(v.Session, v.SessionToken, v.Kind); err != nil {
		return TradeEndAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

// tradeEndEvidence deliberately does not require Closed: close_dialog
// against a stale/foreign session sweeps the window but reports
// closed=false, exactly mirroring NativeTradeOperations' faithfully-ported
// asymmetric cancel/close_dialog behavior.
func tradeEndEvidence(evidence *r.EffectEvidence, expected TradeEndAttempt) (*r.TradeEffect, error) {
	return tradeEffect(evidence)
}

func (writer *TradeWriter) ApplyEndTrade(ctx context.Context, pre *a.WritePrecondition, session, sessionToken string, kind o.EndTradeKind, receiveQuest bool) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid end trade execution")
	}
	if err := tradeEndCommand(session, sessionToken, kind); err != nil {
		return nil, Result{}, err
	}
	expected, err := tradeEndAttempt(TradeEndAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Session: session, SessionToken: sessionToken, Kind: kind, ReceiveQuest: receiveQuest})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: tradeEndOperation(session, sessionToken, kind, receiveQuest)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = tradeReceiptGeneric(v.Receipt, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeEndEvidence(e, expected) })
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("end trade execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupTradeEnd(ctx context.Context, w TradeEndAttempt) (*r.LookupReply, Result, error) {
	expected, err := tradeEndAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	err = tradeLookupGeneric(reply, raw, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeEndEvidence(e, expected) })
	return reply, raw, err
}
func (client *Client) ObserveTradeEndProgress(ctx context.Context, w TradeEndAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := tradeEndAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = tradeReceiptGeneric(admitted, expected.Identity, expected.Attempt, expected.Generation, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeEndEvidence(e, expected) }); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = tradeProgressGeneric(v.Progress, expected.Identity, expected.Attempt, admitted, func(e *r.EffectEvidence) (*r.TradeEffect, error) { return tradeEndEvidence(e, expected) })
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("end trade progress outcome missing")
	}
	return reply, raw, err
}
