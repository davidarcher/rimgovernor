package store

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A TradeEconomy request is not one action but a negotiation: open a session,
// read the sheet the session made visible, decide from it, stage those lines,
// re-read, accept or cancel. Each of those phases is dispatched as its own
// one-action plan, because the lines a SetTradeLines action must carry are
// concrete native line ids and counts that do not exist until Open has
// actually run, and a committed domain.PlanSpec is immutable. This record is
// the durable thread tying those separate plans into one player request: which
// phase it is in, which Open action owns its session, what was decided, and
// how it ended.
//
// It is the smallest persistence that makes the sequence crash-safe. Nothing
// here re-derives native state: the session identity still comes only from
// Open's own completion evidence via store.TradeSession, and each successor
// phase reaches it through the cross-plan binding in
// trade_session_reference.go.
type TradeNegotiationPhase string

const (
	// TradeNegotiationPendingOpen is the initial phase: the Open plan is
	// committed and awaiting dispatch and completion.
	TradeNegotiationPendingOpen TradeNegotiationPhase = "pending_open"
	// TradeNegotiationPendingLines means Open completed, the sheet was read,
	// SelectTrade chose lines and a SetLines plan is in flight.
	TradeNegotiationPendingLines TradeNegotiationPhase = "pending_lines"
	// TradeNegotiationPendingAccept means the staged sheet re-read matched
	// and an Accept plan is in flight.
	TradeNegotiationPendingAccept TradeNegotiationPhase = "pending_accept"
	// TradeNegotiationPendingEnd means the negotiation decided not to trade
	// (or could not safely continue) and an End(cancel) plan is in flight.
	TradeNegotiationPendingEnd TradeNegotiationPhase = "pending_end"
	// TradeNegotiationDone is terminal; Outcome says how it ended.
	TradeNegotiationDone TradeNegotiationPhase = "done"
)

type TradeNegotiationOutcome string

const (
	TradeNegotiationOpen     TradeNegotiationOutcome = ""
	TradeNegotiationAccepted TradeNegotiationOutcome = "accepted"
	TradeNegotiationNoTrade  TradeNegotiationOutcome = "no_trade"
	TradeNegotiationRefused  TradeNegotiationOutcome = "refused"
)

func (p TradeNegotiationPhase) valid() bool {
	switch p {
	case TradeNegotiationPendingOpen, TradeNegotiationPendingLines, TradeNegotiationPendingAccept, TradeNegotiationPendingEnd, TradeNegotiationDone:
		return true
	}
	return false
}
func (o TradeNegotiationOutcome) valid() bool {
	switch o {
	case TradeNegotiationOpen, TradeNegotiationAccepted, TradeNegotiationNoTrade, TradeNegotiationRefused:
		return true
	}
	return false
}

// TradeEconomySubmissionRequest is explicit player intent to run one whole
// economic negotiation with one already-resolved trader and negotiator.
type TradeEconomySubmissionRequest struct {
	RequestID      string
	World          World
	Trader         domain.SettlementID
	Negotiator     domain.PawnID
	Policy         domain.TradeEconomicPolicy
	MaxSilverSpend int64
}

// TradeNegotiation is one stored request and everything the driver has
// resolved for it so far.
type TradeNegotiation struct {
	Request TradeEconomySubmissionRequest
	Phase   TradeNegotiationPhase
	Outcome TradeNegotiationOutcome

	// OpenPlan/OpenAction are the first phase's one-action plan; OpenAction is
	// also the key store.LookupTradeSession resolves this negotiation's
	// session by, for every later phase.
	OpenPlan   domain.PlanID
	OpenAction domain.ActionID
	// CurrentPlan/CurrentAction are the phase in flight now, equal to the Open
	// pair while the negotiation is still in pending_open.
	CurrentPlan   domain.PlanID
	CurrentAction domain.ActionID
	Revision      domain.PlanRevision

	// Selected is what SelectTrade chose once the sheet could be read, and
	// Floors the AcceptTrade reserve guards built from it. Both are empty
	// until the corresponding phase computed them.
	Selected []domain.TradeLine
	Floors   []domain.TradeEconomicFloor
	// Evidence is SelectTrade's own per-target decision rows, kept verbatim so
	// a finished negotiation can explain itself.
	Evidence []policy.TradeSelectionEvidence
	// Reason explains a refusal or a no-trade finish.
	Reason string
	// NetSilver is the accepted deal's net silver to the colony.
	NetSilver float64
}

func (q TradeEconomySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	if submissionID(string(q.Trader)) != nil || submissionID(string(q.Negotiator)) != nil {
		return errors.New("trade economy needs an exact trader and negotiator")
	}
	if q.MaxSilverSpend < 0 || q.MaxSilverSpend > 1000000 {
		return errors.New("trade economy silver spend out of range")
	}
	return q.Policy.Validate()
}

type tradeNegotiationPayload struct {
	Trader         domain.SettlementID
	Negotiator     domain.PawnID
	Policy         domain.TradeEconomicPolicy
	MaxSilverSpend int64
}
type tradeNegotiationState struct {
	Selected  []domain.TradeLine
	Floors    []domain.TradeEconomicFloor
	Evidence  []policy.TradeSelectionEvidence
	Reason    string
	NetSilver float64
}

func initializeTradeNegotiations(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE trade_negotiations(
		request_id TEXT PRIMARY KEY,
		payload BLOB NOT NULL,
		phase TEXT NOT NULL,
		outcome TEXT NOT NULL,
		open_plan_id TEXT NOT NULL,
		open_action_id TEXT NOT NULL,
		current_plan_id TEXT NOT NULL,
		current_action_id TEXT NOT NULL,
		state BLOB NOT NULL) STRICT;`)
	return err
}
func checkTradeNegotiationsSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT request_id,payload,phase,outcome,open_plan_id,open_action_id,current_plan_id,current_action_id,state FROM trade_negotiations LIMIT 0")
	return err
}

func decodeCanonical[T any](payload []byte, bound int) (T, error) {
	var zero, value T
	if len(payload) > bound {
		return zero, errors.New("trade negotiation record exceeds bound")
	}
	if err := json.Unmarshal(payload, &value); err != nil {
		return zero, err
	}
	canonical, err := json.Marshal(value)
	if err != nil {
		return zero, err
	}
	if !bytes.Equal(canonical, payload) {
		return zero, errors.New("noncanonical trade negotiation record")
	}
	return value, nil
}

// SubmitTradeEconomy atomically stores one explicit player intent and commits
// its first phase: a single TradeOpen action against the already-resolved
// trader and negotiator. It never issues a native command, acquires authority
// or guesses a session; every later phase is committed by
// AdvanceTradeNegotiation once the phase before it is observed complete.
func (s *Store) SubmitTradeEconomy(ctx context.Context, q TradeEconomySubmissionRequest) (TradeNegotiation, bool, error) {
	if err := q.validate(); err != nil {
		return TradeNegotiation{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeNegotiation{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupTradeNegotiation(ctx, tx, q.RequestID)
	if err == nil {
		if !sameTradeEconomyRequest(old.Request, q) {
			return TradeNegotiation{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return TradeNegotiation{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return TradeNegotiation{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return TradeNegotiation{}, false, err
	}
	result := TradeNegotiation{
		Request: q, Phase: TradeNegotiationPendingOpen,
		OpenPlan:   domain.PlanID("trade-economy-" + hex.EncodeToString(entropy[:16])),
		OpenAction: domain.ActionID("trade-economy-open-" + hex.EncodeToString(entropy[16:])),
		Revision:   1,
	}
	result.CurrentPlan, result.CurrentAction = result.OpenPlan, result.OpenAction
	trade, err := domain.NewTradeOpen(q.Trader, q.Negotiator, false)
	if err != nil {
		return TradeNegotiation{}, false, err
	}
	if err = commitTradePhase(ctx, tx, result.OpenPlan, result.OpenAction, trade); err != nil {
		return TradeNegotiation{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "trade_economy", q.World, result.OpenPlan, result.OpenAction); err != nil {
		return TradeNegotiation{}, false, err
	}
	payload, err := json.Marshal(tradeNegotiationPayload{q.Trader, q.Negotiator, q.Policy, q.MaxSilverSpend})
	if err != nil {
		return TradeNegotiation{}, false, err
	}
	state, err := json.Marshal(tradeNegotiationState{})
	if err != nil {
		return TradeNegotiation{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO trade_negotiations(request_id,payload,phase,outcome,open_plan_id,open_action_id,current_plan_id,current_action_id,state) VALUES(?,?,?,?,?,?,?,?,?)",
		q.RequestID, payload, string(result.Phase), string(TradeNegotiationOpen), result.OpenPlan, result.OpenAction, result.CurrentPlan, result.CurrentAction, state); err != nil {
		return TradeNegotiation{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return TradeNegotiation{}, false, err
	}
	return result, true, nil
}

func sameTradeEconomyRequest(a, b TradeEconomySubmissionRequest) bool {
	left, leftErr := json.Marshal(a)
	right, rightErr := json.Marshal(b)
	return leftErr == nil && rightErr == nil && bytes.Equal(left, right)
}

func commitTradePhase(ctx context.Context, tx *sql.Tx, plan domain.PlanID, id domain.ActionID, trade domain.Trade) error {
	action, err := domain.NewTradeAction(id, trade)
	if err != nil {
		return err
	}
	spec, err := domain.NewPlan(plan, 1, []domain.Action{action})
	if err != nil {
		return err
	}
	return createPlan(ctx, tx, spec)
}

// TradeNegotiationStep is one phase transition the driver asks for: the trade
// operation to commit next, the phase that commit puts the negotiation in, and
// whatever the driver resolved on the way there.
type TradeNegotiationStep struct {
	Phase    TradeNegotiationPhase
	Trade    domain.Trade
	Selected []domain.TradeLine
	Floors   []domain.TradeEconomicFloor
	Evidence []policy.TradeSelectionEvidence
	Reason   string
}

// AdvanceTradeNegotiation commits one negotiation's next phase as its own
// one-action plan and binds that action to this negotiation's Open action, so
// the executor can resolve the session across plans. Both happen in one
// transaction with the phase update, so a crash can never leave a committed
// successor action with no session binding.
//
// It is guarded on the phase the caller believed the negotiation was in:
// advancing a negotiation that has already moved on returns ErrConflict rather
// than opening a second deal.
func (s *Store) AdvanceTradeNegotiation(ctx context.Context, requestID string, from TradeNegotiationPhase, step TradeNegotiationStep) (TradeNegotiation, error) {
	if err := submissionID(requestID); err != nil {
		return TradeNegotiation{}, err
	}
	if !step.Phase.valid() || step.Phase == TradeNegotiationPendingOpen || step.Phase == TradeNegotiationDone {
		return TradeNegotiation{}, errors.New("invalid trade negotiation step")
	}
	// Done is terminal in both directions: a finished negotiation can never be
	// the phase an advance came from, so a stale driver cannot commit a further
	// native action against a session that has already been settled.
	if !from.valid() || from == TradeNegotiationDone {
		return TradeNegotiation{}, errors.New("a finished trade negotiation accepts no further phase")
	}
	if step.Trade.Kind() == domain.TradeOpen {
		return TradeNegotiation{}, errors.New("a negotiation opens exactly once")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeNegotiation{}, err
	}
	defer tx.Rollback()
	current, err := lookupTradeNegotiation(ctx, tx, requestID)
	if err != nil {
		return TradeNegotiation{}, err
	}
	if current.Phase != from {
		return TradeNegotiation{}, ErrConflict
	}
	var entropy [16]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return TradeNegotiation{}, err
	}
	next := current
	next.Phase = step.Phase
	next.CurrentPlan = domain.PlanID("trade-phase-" + hex.EncodeToString(entropy[:8]))
	next.CurrentAction = domain.ActionID("trade-phase-action-" + hex.EncodeToString(entropy[8:]))
	if step.Selected != nil {
		next.Selected = step.Selected
	}
	if step.Floors != nil {
		next.Floors = step.Floors
	}
	if step.Evidence != nil {
		next.Evidence = step.Evidence
	}
	if step.Reason != "" {
		next.Reason = step.Reason
	}
	if err = commitTradePhase(ctx, tx, next.CurrentPlan, next.CurrentAction, step.Trade); err != nil {
		return TradeNegotiation{}, err
	}
	if err = recordTradeSessionReference(ctx, tx, next.CurrentAction, next.OpenAction); err != nil {
		return TradeNegotiation{}, err
	}
	if err = writeTradeNegotiation(ctx, tx, next); err != nil {
		return TradeNegotiation{}, err
	}
	if err = tx.Commit(); err != nil {
		return TradeNegotiation{}, err
	}
	return next, nil
}

// FinalizeTradeNegotiation moves one negotiation to its terminal phase with an
// explicit outcome. It commits no further plan: whatever native action the
// outcome describes has already been observed.
func (s *Store) FinalizeTradeNegotiation(ctx context.Context, requestID string, from TradeNegotiationPhase, outcome TradeNegotiationOutcome, reason string, netSilver float64, evidence []policy.TradeSelectionEvidence) (TradeNegotiation, error) {
	if err := submissionID(requestID); err != nil {
		return TradeNegotiation{}, err
	}
	if !outcome.valid() || outcome == TradeNegotiationOpen {
		return TradeNegotiation{}, errors.New("a finished negotiation needs an explicit outcome")
	}
	// A negotiation finishes exactly once; re-finishing one would overwrite the
	// outcome already recorded against a settled deal.
	if !from.valid() || from == TradeNegotiationDone {
		return TradeNegotiation{}, errors.New("a finished trade negotiation cannot finish again")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeNegotiation{}, err
	}
	defer tx.Rollback()
	current, err := lookupTradeNegotiation(ctx, tx, requestID)
	if err != nil {
		return TradeNegotiation{}, err
	}
	if current.Phase != from {
		return TradeNegotiation{}, ErrConflict
	}
	next := current
	next.Phase, next.Outcome, next.NetSilver = TradeNegotiationDone, outcome, netSilver
	if reason != "" {
		next.Reason = reason
	}
	if evidence != nil {
		next.Evidence = evidence
	}
	if err = writeTradeNegotiation(ctx, tx, next); err != nil {
		return TradeNegotiation{}, err
	}
	if err = tx.Commit(); err != nil {
		return TradeNegotiation{}, err
	}
	return next, nil
}

func writeTradeNegotiation(ctx context.Context, tx *sql.Tx, v TradeNegotiation) error {
	state, err := json.Marshal(tradeNegotiationState{v.Selected, v.Floors, v.Evidence, v.Reason, v.NetSilver})
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "UPDATE trade_negotiations SET phase=?,outcome=?,current_plan_id=?,current_action_id=?,state=? WHERE request_id=?",
		string(v.Phase), string(v.Outcome), v.CurrentPlan, v.CurrentAction, state, v.Request.RequestID)
	return err
}

// LookupTradeEconomy returns one stored negotiation by request ID.
func (s *Store) LookupTradeEconomy(ctx context.Context, requestID string) (TradeNegotiation, error) {
	if err := submissionID(requestID); err != nil {
		return TradeNegotiation{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeNegotiation{}, err
	}
	defer tx.Rollback()
	result, err := lookupTradeNegotiation(ctx, tx, requestID)
	if err != nil {
		return TradeNegotiation{}, err
	}
	if err = tx.Commit(); err != nil {
		return TradeNegotiation{}, err
	}
	return result, nil
}

// UnfinishedTradeNegotiations returns every negotiation in one world that has
// not reached a terminal phase, oldest request first. The driver ticks these.
func (s *Store) UnfinishedTradeNegotiations(ctx context.Context, w World) ([]TradeNegotiation, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, `SELECT n.request_id FROM trade_negotiations n JOIN submissions s ON s.request_id=n.request_id
		WHERE n.phase<>? AND s.kind='trade_economy' AND s.colony=? AND s.load_token=? AND s.map_id=? ORDER BY n.rowid`,
		string(TradeNegotiationDone), w.Colony, w.Load, w.Map)
	if err != nil {
		return nil, err
	}
	var ids []string
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	if err = rows.Err(); err != nil {
		rows.Close()
		return nil, err
	}
	if err = rows.Close(); err != nil {
		return nil, err
	}
	out := make([]TradeNegotiation, 0, len(ids))
	for _, id := range ids {
		value, err := lookupTradeNegotiation(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		out = append(out, value)
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return out, nil
}

func lookupTradeNegotiation(ctx context.Context, tx *sql.Tx, id string) (TradeNegotiation, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "trade_economy")
	if err != nil {
		return TradeNegotiation{}, err
	}
	var payload, state []byte
	var phase, outcome, openPlan, openAction, currentPlan, currentAction string
	err = tx.QueryRowContext(ctx, "SELECT payload,phase,outcome,open_plan_id,open_action_id,current_plan_id,current_action_id,state FROM trade_negotiations WHERE request_id=?", id).
		Scan(&payload, &phase, &outcome, &openPlan, &openAction, &currentPlan, &currentAction, &state)
	if errors.Is(err, sql.ErrNoRows) {
		return TradeNegotiation{}, ErrNotFound
	}
	if err != nil {
		return TradeNegotiation{}, err
	}
	request, err := decodeCanonical[tradeNegotiationPayload](payload, 16384)
	if err != nil {
		return TradeNegotiation{}, err
	}
	resolved, err := decodeCanonical[tradeNegotiationState](state, 65536)
	if err != nil {
		return TradeNegotiation{}, err
	}
	result := TradeNegotiation{
		Request: TradeEconomySubmissionRequest{RequestID: id, World: h.World, Trader: request.Trader, Negotiator: request.Negotiator, Policy: request.Policy, MaxSilverSpend: request.MaxSilverSpend},
		Phase:   TradeNegotiationPhase(phase), Outcome: TradeNegotiationOutcome(outcome),
		OpenPlan: domain.PlanID(openPlan), OpenAction: domain.ActionID(openAction),
		CurrentPlan: domain.PlanID(currentPlan), CurrentAction: domain.ActionID(currentAction),
		Revision: h.Revision,
		Selected: resolved.Selected, Floors: resolved.Floors, Evidence: resolved.Evidence,
		Reason: resolved.Reason, NetSilver: resolved.NetSilver,
	}
	if !result.Phase.valid() || !result.Outcome.valid() {
		return TradeNegotiation{}, errors.New("trade negotiation phase is corrupt")
	}
	if result.OpenPlan != h.Plan || result.OpenAction != h.Action {
		return TradeNegotiation{}, errors.New("trade negotiation header differs from its open plan")
	}
	if err = result.Request.validate(); err != nil {
		return TradeNegotiation{}, err
	}
	return result, nil
}
