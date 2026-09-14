package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"crypto/rand"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeSubmissionRequest is explicit player intent to run one trade
// sub-operation, the same single-action-plan shape SubmitQuestFulfill uses.
// It commits its own one-action plan immediately; a worker later admits and
// dispatches it. Only TradeOpen ever resolves through this direct-submission
// surface end to end: TradeSetLines/TradeAccept/TradeEnd need a
// domain.ActionDependency on the exact Open action that opened their
// session (see policy.EvaluateTrade/store.TradeSession), which a
// single-action plan has no other action to depend on. Building a plan that
// already contains its own dependency Open action (or extending an existing
// plan with a new dependent action) is a distinct, not-yet-built capability;
// submitting a chained kind through this surface is accepted and stored
// like any other submission, but its action will hold on TradeSessionUnresolved
// forever, never silently misbehaving.
type TradeSubmissionRequest struct {
	RequestID string
	World     World
	Trade     domain.Trade
}
type TradeSubmission struct {
	Request  TradeSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type tradeSubmissionPayload struct {
	Kind                  domain.TradeOperationKind
	Trader                domain.SettlementID
	Negotiator            domain.PawnID
	GiftMode              bool
	Lines                 []domain.TradeLine
	AllowPawns            bool
	ExpectedDealSignature string
	EconomicFloors        []domain.TradeEconomicFloor
	AllowEmpty            bool
	EndKind               domain.TradeEndKind
	ReceiveQuest          bool
}

func tradeSubmissionEncode(t domain.Trade) tradeSubmissionPayload {
	return tradeSubmissionPayload{
		Kind: t.Kind(), Trader: t.Trader(), Negotiator: t.Negotiator(), GiftMode: t.GiftMode(),
		Lines: t.Lines(), AllowPawns: t.AllowPawns(), ExpectedDealSignature: t.ExpectedDealSignature(),
		EconomicFloors: t.EconomicFloors(), AllowEmpty: t.AllowEmpty(), EndKind: t.EndKind(), ReceiveQuest: t.ReceiveQuest(),
	}
}
func tradeSubmissionDecode(p tradeSubmissionPayload) (domain.Trade, error) {
	switch p.Kind {
	case domain.TradeOpen:
		return domain.NewTradeOpen(p.Trader, p.Negotiator, p.GiftMode)
	case domain.TradeSetLines:
		return domain.NewTradeSetLines(p.Lines, p.AllowPawns)
	case domain.TradeAccept:
		return domain.NewTradeAccept(p.ExpectedDealSignature, p.EconomicFloors, p.AllowEmpty, p.ReceiveQuest)
	case domain.TradeEnd:
		return domain.NewTradeEnd(p.EndKind, p.ReceiveQuest)
	default:
		return domain.Trade{}, errors.New("invalid trade submission kind")
	}
}

func (t TradeSubmissionRequest) validate() error {
	if err := submissionID(t.RequestID); err != nil {
		return err
	}
	if err := t.World.Validate(); err != nil {
		return err
	}
	canonical, err := tradeSubmissionDecode(tradeSubmissionEncode(t.Trade))
	if err != nil || canonical != t.Trade {
		return errors.New("invalid trade")
	}
	return nil
}

// SubmitTrade atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitQuestFulfill uses. Submission
// neither acquires authority nor issues a native trade command.
func (s *Store) SubmitTrade(ctx context.Context, q TradeSubmissionRequest) (TradeSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return TradeSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupTradeSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return TradeSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return TradeSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return TradeSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return TradeSubmission{}, false, err
	}
	result := TradeSubmission{Request: q, Plan: domain.PlanID("trade-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("trade-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewTradeAction(result.Action, q.Trade)
	if err != nil {
		return TradeSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return TradeSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return TradeSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "trade", q.World, result.Plan, result.Action); err != nil {
		return TradeSubmission{}, false, err
	}
	data, err := json.Marshal(tradeSubmissionEncode(q.Trade))
	if err != nil {
		return TradeSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO trade_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return TradeSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return TradeSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupTradeSubmission(ctx context.Context, requestID string) (TradeSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return TradeSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupTradeSubmission(ctx, tx, requestID)
	if err != nil {
		return TradeSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return TradeSubmission{}, err
	}
	return result, nil
}
func lookupTradeSubmission(ctx context.Context, tx *sql.Tx, id string) (TradeSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "trade")
	if err != nil {
		return TradeSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM trade_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return TradeSubmission{}, ErrNotFound
	}
	if err != nil {
		return TradeSubmission{}, err
	}
	if len(data) > 32768 {
		return TradeSubmission{}, errors.New("trade submission exceeds bound")
	}
	var payload tradeSubmissionPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return TradeSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return TradeSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return TradeSubmission{}, errors.New("noncanonical trade submission")
	}
	trade, err := tradeSubmissionDecode(payload)
	if err != nil {
		return TradeSubmission{}, err
	}
	result := TradeSubmission{Request: TradeSubmissionRequest{RequestID: id, World: h.World, Trade: trade}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return TradeSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return TradeSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return TradeSubmission{}, errors.New("trade submission plan is corrupt")
	}
	actual, ok := actions[0].Trade()
	if !ok || actual != trade {
		return TradeSubmission{}, errors.New("trade submission differs from intent")
	}
	return result, nil
}
