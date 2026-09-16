package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PopulationDecisionSubmissionRequest is explicit player intent to record one
// per-pawn population direction. Like PopulationPolicySubmissionRequest it
// deliberately produces no plan and no action -- recording a decision issues
// no native call -- so it keeps its own request table rather than a row in
// the shared submissions table, whose every row owns a plan_id and action_id.
type PopulationDecisionSubmissionRequest struct {
	RequestID string
	World     World
	Directive domain.PopulationDirective
}

// PopulationDecisionSubmission is one stored request together with the pawn's
// direction as it stands now.
type PopulationDecisionSubmission struct {
	Request PopulationDecisionSubmissionRequest
	// Current is the pawn's current directive, not necessarily
	// Request.Directive: a decision is a current-value concept per pawn, so a
	// later request ID replaces it. Replaying an old request ID returns that
	// request unchanged (idempotency) alongside whatever is current.
	Current domain.PopulationDirective
}

func (q PopulationDecisionSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	canonical, err := domain.NewPopulationDirective(q.Directive.Pawn(), q.Directive.Decision())
	if err != nil || canonical != q.Directive {
		return errors.New("invalid population decision")
	}
	return nil
}

// SubmitPopulationDecision atomically records one explicit player request and
// makes it the pawn's current population direction, deciding replay by
// request ID exactly as the plan-bearing submissions do: the same request ID
// with the same fields returns the stored request and reports no creation,
// and with different fields returns ErrConflict.
//
// A rescue, capture or recruit decision requires an already established
// population capacity policy for the world and reports ErrNotFound when there
// is none ("set an explicit population maximum and food reserve first").
// The ignore decision skips that check entirely, so
// a player can always withdraw a direction they previously gave. The check
// runs inside this transaction, against the policy as it actually stands,
// rather than against a value read earlier by a caller.
//
// Pawn liveness and candidacy are deliberately not checked here. They are
// fresher-than-store native facts, and this store holds no census. The interpreter
// bounds the pawn to an observed identity, and native custody dispatch
// establishes eligibility at inspection, the same way every other
// pawn-identifier command in this controller does.
func (s *Store) SubmitPopulationDecision(ctx context.Context, q PopulationDecisionSubmissionRequest) (PopulationDecisionSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return PopulationDecisionSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PopulationDecisionSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupPopulationDecisionSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return PopulationDecisionSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return PopulationDecisionSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return PopulationDecisionSubmission{}, false, err
	}
	if q.Directive.RequiresPolicy() {
		if _, err = currentPopulationPolicy(ctx, tx, q.World); err != nil {
			return PopulationDecisionSubmission{}, false, err
		}
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO population_decision_submissions(request_id,colony,load_token,map_id,pawn,decision) VALUES(?,?,?,?,?,?)",
		q.RequestID, q.World.Colony, q.World.Load, q.World.Map, string(q.Directive.Pawn()), string(q.Directive.Decision())); err != nil {
		return PopulationDecisionSubmission{}, false, conflict(err)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO population_decisions(colony,load_token,map_id,pawn,request_id,decision) VALUES(?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id,pawn) DO UPDATE SET request_id=excluded.request_id,decision=excluded.decision",
		q.World.Colony, q.World.Load, q.World.Map, string(q.Directive.Pawn()), q.RequestID, string(q.Directive.Decision())); err != nil {
		return PopulationDecisionSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return PopulationDecisionSubmission{}, false, err
	}
	return PopulationDecisionSubmission{Request: q, Current: q.Directive}, true, nil
}

// LookupPopulationDecisionSubmission returns one stored request by request ID.
func (s *Store) LookupPopulationDecisionSubmission(ctx context.Context, requestID string) (PopulationDecisionSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return PopulationDecisionSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PopulationDecisionSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupPopulationDecisionSubmission(ctx, tx, requestID)
	if err != nil {
		return PopulationDecisionSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return PopulationDecisionSubmission{}, err
	}
	return result, nil
}

// PopulationDecisions returns every recorded per-pawn direction for one
// world, ordered by pawn. An empty result is not an error: a world where the
// player has named no individuals simply has none.
func (s *Store) PopulationDecisions(ctx context.Context, w World) ([]domain.PopulationDirective, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT pawn,decision FROM population_decisions WHERE colony=? AND load_token=? AND map_id=? ORDER BY pawn", w.Colony, w.Load, w.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.PopulationDirective
	for rows.Next() {
		var pawn, decision string
		if err = rows.Scan(&pawn, &decision); err != nil {
			return nil, err
		}
		directive, err := domain.NewPopulationDirective(domain.PawnID(pawn), domain.PopulationDecision(decision))
		if err != nil {
			return nil, err
		}
		out = append(out, directive)
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if err = rows.Close(); err != nil {
		return nil, err
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return out, nil
}

// CurrentPopulationDecision returns one pawn's current direction, or
// ErrNotFound when the player has never named that pawn in that world.
func (s *Store) CurrentPopulationDecision(ctx context.Context, w World, pawn domain.PawnID) (domain.PopulationDirective, error) {
	if err := w.Validate(); err != nil {
		return domain.PopulationDirective{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.PopulationDirective{}, err
	}
	defer tx.Rollback()
	directive, err := currentPopulationDecision(ctx, tx, w, pawn)
	if err != nil {
		return domain.PopulationDirective{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.PopulationDirective{}, err
	}
	return directive, nil
}

func currentPopulationDecision(ctx context.Context, tx *sql.Tx, w World, pawn domain.PawnID) (domain.PopulationDirective, error) {
	var decision string
	err := tx.QueryRowContext(ctx, "SELECT decision FROM population_decisions WHERE colony=? AND load_token=? AND map_id=? AND pawn=?", w.Colony, w.Load, w.Map, string(pawn)).Scan(&decision)
	if errors.Is(err, sql.ErrNoRows) {
		return domain.PopulationDirective{}, ErrNotFound
	}
	if err != nil {
		return domain.PopulationDirective{}, err
	}
	return domain.NewPopulationDirective(pawn, domain.PopulationDecision(decision))
}

func lookupPopulationDecisionSubmission(ctx context.Context, tx *sql.Tx, id string) (PopulationDecisionSubmission, error) {
	var world World
	var pawn, decision string
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,pawn,decision FROM population_decision_submissions WHERE request_id=?", id).Scan(&world.Colony, &world.Load, &world.Map, &pawn, &decision)
	if errors.Is(err, sql.ErrNoRows) {
		return PopulationDecisionSubmission{}, ErrNotFound
	}
	if err != nil {
		return PopulationDecisionSubmission{}, err
	}
	directive, err := domain.NewPopulationDirective(domain.PawnID(pawn), domain.PopulationDecision(decision))
	if err != nil {
		return PopulationDecisionSubmission{}, err
	}
	result := PopulationDecisionSubmission{Request: PopulationDecisionSubmissionRequest{RequestID: id, World: world, Directive: directive}}
	if err = result.Request.validate(); err != nil {
		return PopulationDecisionSubmission{}, err
	}
	if result.Current, err = currentPopulationDecision(ctx, tx, world, directive.Pawn()); err != nil {
		return PopulationDecisionSubmission{}, err
	}
	return result, nil
}
