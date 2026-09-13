package store

import (
	"context"
	"database/sql"
	"errors"

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
	if h.Kind != "building" && h.Kind != "owned_draft" && h.Kind != "caravan_departure" {
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
	case "caravan_departure":
		_, err = lookupCaravanDepartureSubmission(ctx, tx, id)
	default:
		_, err = lookupDraftSubmission(ctx, tx, id)
	}
	return h, err
}
