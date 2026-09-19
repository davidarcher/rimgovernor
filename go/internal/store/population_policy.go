package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PopulationPolicySubmissionRequest is explicit player intent to set the
// colony's population capacity policy. It mirrors the request_id/World shape
// of ZoneCreateSubmissionRequest and friends, but deliberately produces no
// plan and no action: setting a population policy issues no native call, so
// there is nothing for a worker to admit, dispatch or observe. It is
// therefore not recorded in the shared submissions table (whose every row
// owns a plan_id and action_id) and needs no submission_header kind, the
// same way work preference requests keep their own request table.
type PopulationPolicySubmissionRequest struct {
	RequestID string
	World     World
	Policy    domain.PopulationPolicy
}

// PopulationPolicySubmission is one stored request together with the world's
// population policy as it stands now.
type PopulationPolicySubmission struct {
	Request PopulationPolicySubmissionRequest
	// Current is the world's current policy, not necessarily Request.Policy.
	// A population policy is a current-value concept rather than a one-shot
	// fact like an accepted quest: a later request ID with different values
	// overwrites it. Replaying an old request ID therefore returns that
	// request unchanged (idempotency) alongside whatever value is current.
	Current domain.PopulationPolicy
}

func (q PopulationPolicySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	canonical, err := domain.NewPopulationPolicy(q.Policy.Maximum(), q.Policy.FoodDays(), q.Policy.RaidThreshold())
	if err != nil || canonical != q.Policy {
		return errors.New("invalid population policy")
	}
	return nil
}

// SubmitPopulationPolicy atomically records one explicit player request and
// makes its values the world's current population policy. Replay is decided
// by request ID exactly as the plan-bearing submissions decide it: the same
// request ID with the same fields returns the stored request and reports no
// creation, and with different fields returns ErrConflict.
func (s *Store) SubmitPopulationPolicy(ctx context.Context, q PopulationPolicySubmissionRequest) (PopulationPolicySubmission, bool, error) {
	if err := q.validate(); err != nil {
		return PopulationPolicySubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PopulationPolicySubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupPopulationPolicySubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return PopulationPolicySubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return PopulationPolicySubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return PopulationPolicySubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO population_policy_submissions(request_id,colony,load_token,map_id,maximum,food_days,raid_threshold) VALUES(?,?,?,?,?,?,?)",
		q.RequestID, q.World.Colony, q.World.Load, q.World.Map, q.Policy.Maximum(), q.Policy.FoodDays(), q.Policy.RaidThreshold()); err != nil {
		return PopulationPolicySubmission{}, false, conflict(err)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO population_policies(colony,load_token,map_id,request_id,maximum,food_days,raid_threshold) VALUES(?,?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id) DO UPDATE SET request_id=excluded.request_id,maximum=excluded.maximum,food_days=excluded.food_days,raid_threshold=excluded.raid_threshold",
		q.World.Colony, q.World.Load, q.World.Map, q.RequestID, q.Policy.Maximum(), q.Policy.FoodDays(), q.Policy.RaidThreshold()); err != nil {
		return PopulationPolicySubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return PopulationPolicySubmission{}, false, err
	}
	return PopulationPolicySubmission{Request: q, Current: q.Policy}, true, nil
}

// LookupPopulationPolicySubmission returns one stored request by request ID.
func (s *Store) LookupPopulationPolicySubmission(ctx context.Context, requestID string) (PopulationPolicySubmission, error) {
	if err := submissionID(requestID); err != nil {
		return PopulationPolicySubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PopulationPolicySubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupPopulationPolicySubmission(ctx, tx, requestID)
	if err != nil {
		return PopulationPolicySubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return PopulationPolicySubmission{}, err
	}
	return result, nil
}

// CurrentPopulationPolicy returns the world's current population policy, or
// ErrNotFound when the player has never set one for that world.
func (s *Store) CurrentPopulationPolicy(ctx context.Context, w World) (domain.PopulationPolicy, error) {
	if err := w.Validate(); err != nil {
		return domain.PopulationPolicy{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.PopulationPolicy{}, err
	}
	defer tx.Rollback()
	policy, err := currentPopulationPolicy(ctx, tx, w)
	if err != nil {
		return domain.PopulationPolicy{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.PopulationPolicy{}, err
	}
	return policy, nil
}

func currentPopulationPolicy(ctx context.Context, tx *sql.Tx, w World) (domain.PopulationPolicy, error) {
	var maximum int32
	var foodDays, raidThreshold float64
	err := tx.QueryRowContext(ctx, "SELECT maximum,food_days,raid_threshold FROM population_policies WHERE colony=? AND load_token=? AND map_id=?", w.Colony, w.Load, w.Map).Scan(&maximum, &foodDays, &raidThreshold)
	if errors.Is(err, sql.ErrNoRows) {
		return domain.PopulationPolicy{}, ErrNotFound
	}
	if err != nil {
		return domain.PopulationPolicy{}, err
	}
	return domain.NewPopulationPolicy(maximum, foodDays, raidThreshold)
}

func lookupPopulationPolicySubmission(ctx context.Context, tx *sql.Tx, id string) (PopulationPolicySubmission, error) {
	var world World
	var maximum int32
	var foodDays, raidThreshold float64
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,maximum,food_days,raid_threshold FROM population_policy_submissions WHERE request_id=?", id).Scan(&world.Colony, &world.Load, &world.Map, &maximum, &foodDays, &raidThreshold)
	if errors.Is(err, sql.ErrNoRows) {
		return PopulationPolicySubmission{}, ErrNotFound
	}
	if err != nil {
		return PopulationPolicySubmission{}, err
	}
	policy, err := domain.NewPopulationPolicy(maximum, foodDays, raidThreshold)
	if err != nil {
		return PopulationPolicySubmission{}, err
	}
	result := PopulationPolicySubmission{Request: PopulationPolicySubmissionRequest{RequestID: id, World: world, Policy: policy}}
	if err = result.Request.validate(); err != nil {
		return PopulationPolicySubmission{}, err
	}
	if result.Current, err = currentPopulationPolicy(ctx, tx, world); err != nil {
		return PopulationPolicySubmission{}, err
	}
	return result, nil
}
