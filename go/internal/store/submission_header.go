package store

import (
	"context"
	"database/sql"
	"errors"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type submissionHeader struct {
	Kind     string
	World    World
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func insertSubmissionHeader(ctx context.Context, tx *sql.Tx, id, kind string, w World, plan domain.PlanID, action domain.ActionID) error {
	_, err := tx.ExecContext(ctx, "INSERT INTO submissions(request_id,kind,colony,load_token,map_id,plan_id,action_id,revision) VALUES(?,?,?,?,?,?,?,'1')", id, kind, w.Colony, w.Load, w.Map, plan, action)
	return conflict(err)
}
func lookupSubmissionHeader(ctx context.Context, tx *sql.Tx, id, kind string) (submissionHeader, error) {
	var revision string
	h := submissionHeader{Revision: 1}
	err := tx.QueryRowContext(ctx, "SELECT kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions WHERE request_id=?", id).Scan(&h.Kind, &h.World.Colony, &h.World.Load, &h.World.Map, &h.Plan, &h.Action, &revision)
	if errors.Is(err, sql.ErrNoRows) {
		return h, ErrNotFound
	}
	if err != nil {
		return h, err
	}
	if revision != "1" {
		return h, errors.New("invalid submitted revision")
	}
	if h.Kind != "building" && h.Kind != "research_select" && h.Kind != "resource_policy" {
		return h, errors.New("invalid submission kind")
	}
	if kind != "" && h.Kind != kind {
		return h, ErrConflict
	}
	if err = h.World.Validate(); err != nil {
		return h, err
	}
	return h, nil
}
func lookupAnySubmission(ctx context.Context, tx *sql.Tx, id string) (submissionHeader, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "")
	if err != nil {
		return h, err
	}
	switch h.Kind {
	case "building":
		_, err = lookupSubmission(ctx, tx, id)
	case "research_select":
		_, err = lookupResearchSelectSubmission(ctx, tx, id)
	case "resource_policy":
		_, err = lookupResourcePolicySubmission(ctx, tx, id)
	default:
		err = errors.New("invalid submission kind")
	}
	return h, err
}

// AuthorizePlayerPlan verifies that target is a player-submitted plan for the
// root world so it may be dispatched under the root's authority. There is one
// author of orders: a submission is guidance the bot executes, not a separate
// grant, so it needs no control record of its own.
func (s *Store) AuthorizePlayerPlan(ctx context.Context, root, target domain.GenerationSnapshot) error {
	if root.Validate() != nil || target.Validate() != nil || root.Native == 0 || root.Plan == target.Plan {
		return ErrConflict
	}
	matching := target
	matching.Plan, matching.Revision = root.Plan, root.Revision
	if matching != root {
		return ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var id string
	if err = tx.QueryRowContext(ctx, "SELECT request_id FROM submissions WHERE plan_id=?", target.Plan).Scan(&id); errors.Is(err, sql.ErrNoRows) {
		return ErrConflict
	} else if err != nil {
		return err
	}
	h, err := lookupAnySubmission(ctx, tx, id)
	if err != nil {
		return err
	}
	if h.Kind == "resource_policy" || h.World != (World{Colony: root.Colony, Load: root.Load, Map: root.Map}) || h.Revision != target.Revision {
		return ErrConflict
	}
	var retired int
	if err = tx.QueryRowContext(ctx, "SELECT retired FROM plans WHERE id=?", target.Plan).Scan(&retired); err != nil {
		return err
	}
	if retired != 0 {
		return ErrConflict
	}
	return tx.Commit()
}

// PlayerPlans lists the player guidance plans (building and research
// submissions) for one world with their submitted revisions, so reviewers can
// treat them as selected intent under the world's root plan.
func (s *Store) PlayerPlans(ctx context.Context, w World) (map[domain.PlanID]uint64, error) {
	if err := w.Validate(); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT plan_id,revision FROM submissions WHERE kind IN ('building','research_select') AND colony=? AND load_token=? AND map_id=?", w.Colony, w.Load, w.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := map[domain.PlanID]uint64{}
	for rows.Next() {
		var plan domain.PlanID
		var revision string
		if err = rows.Scan(&plan, &revision); err != nil {
			return nil, err
		}
		parsed, err := strconv.ParseUint(revision, 10, 64)
		if err != nil {
			return nil, err
		}
		result[plan] = parsed
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	return result, tx.Commit()
}
