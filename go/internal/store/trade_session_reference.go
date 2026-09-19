package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A trade session reference is the cross-plan counterpart of the same-plan
// domain.ActionDependency executor.resolveTradeDependency already honours: it
// names, for one set_lines/accept/end Trade action, the exact Open action whose
// own completion evidence produced the session that action addresses.
//
// It exists because domain.PlanSpec is immutable once committed while
// SetTradeLines requires concrete native line ids and counts that do not exist
// until AFTER Open has executed and produced a live session (RimWorld's trade
// sheet is session-scoped; there is no pre-session sheet preview). A single
// plan therefore cannot carry both Open and a real SetTradeLines, and the
// multi-phase negotiation driver (buildingruntime/trade_economy.go) submits
// each phase as its own one-action plan instead.
//
// Safety: the reference is keyed by the dependent action's OWN identity, which
// is freshly generated entropy at submission time and written in the same
// transaction that creates that action's plan. It is immutable once written
// (re-recording the same pair is a no-op; a different pair is rejected), so no
// replayed or stale action can ever be re-pointed at another negotiation's
// session. Resolution itself still goes through the unchanged, never
// plan-scoped LookupTradeSession, so an Open action that has not been observed
// complete still resolves to unresolved and its dependents still refuse with
// policy.TradeSessionUnresolved rather than guessing a session.
func initializeTradeSessionReferences(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "CREATE TABLE trade_session_references(action_id TEXT PRIMARY KEY REFERENCES actions(id), open_action_id TEXT NOT NULL) STRICT;")
	return err
}
func checkTradeSessionReferencesSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT action_id,open_action_id FROM trade_session_references LIMIT 0")
	return err
}

func recordTradeSessionReference(ctx context.Context, tx *sql.Tx, action, openAction domain.ActionID) error {
	if submissionID(string(action)) != nil || submissionID(string(openAction)) != nil || action == openAction {
		return errors.New("invalid trade session reference")
	}
	var existing string
	err := tx.QueryRowContext(ctx, "SELECT open_action_id FROM trade_session_references WHERE action_id=?", action).Scan(&existing)
	switch {
	case errors.Is(err, sql.ErrNoRows):
		_, err = tx.ExecContext(ctx, "INSERT INTO trade_session_references(action_id,open_action_id) VALUES(?,?)", action, openAction)
		return err
	case err != nil:
		return err
	case existing != string(openAction):
		return errors.New("trade session reference differs from existing record")
	}
	return nil
}

// RecordTradeSessionReference durably binds one dependent trade action to the
// Open action whose session it addresses. It is idempotent on action, the same
// discipline RecordTradeSession uses for openAction.
func (s *Store) RecordTradeSessionReference(ctx context.Context, action, openAction domain.ActionID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = recordTradeSessionReference(ctx, tx, action, openAction); err != nil {
		return err
	}
	return tx.Commit()
}

// LookupTradeSessionReference returns the Open action one dependent trade
// action was bound to, or ok=false when it was never bound (the ordinary case
// for an Open action itself, and for any same-plan-dependency trade plan built
// the original way). It never guesses which session is open.
func (s *Store) LookupTradeSessionReference(ctx context.Context, action domain.ActionID) (domain.ActionID, bool, error) {
	if submissionID(string(action)) != nil {
		return "", false, errors.New("invalid trade session reference lookup")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return "", false, err
	}
	defer tx.Rollback()
	var openAction string
	err = tx.QueryRowContext(ctx, "SELECT open_action_id FROM trade_session_references WHERE action_id=?", action).Scan(&openAction)
	if errors.Is(err, sql.ErrNoRows) {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	if err = tx.Commit(); err != nil {
		return "", false, err
	}
	return domain.ActionID(openAction), true, nil
}

// CommitTradeGoalMethod is CommitGoalMethod for a routine trade phase whose
// actions address the session an earlier Open action established: the plan
// and every one of its trade actions' references to openAction land in one
// transaction, so no dispatch can ever observe the plan without its binding
// and refuse it as TradeSessionUnresolved.
func (s *Store) CommitTradeGoalMethod(ctx context.Context, id domain.GoalID, revision uint64, method domain.MethodID, plan domain.PlanSpec, openAction domain.ActionID) (GoalState, error) {
	if err := plan.Validate(); err != nil {
		return GoalState{}, err
	}
	if len(plan.Actions()) == 0 {
		return GoalState{}, errors.New("empty goal method")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := commitGoalMethod(ctx, tx, id, revision, method, plan)
	if err != nil {
		return GoalState{}, err
	}
	for _, action := range plan.Actions() {
		trade, ok := action.Trade()
		if !ok || trade.Kind() == domain.TradeOpen {
			return GoalState{}, errors.New("trade goal method binds only session-scoped trade actions")
		}
		if err = recordTradeSessionReference(ctx, tx, action.ID(), openAction); err != nil {
			return GoalState{}, err
		}
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}
