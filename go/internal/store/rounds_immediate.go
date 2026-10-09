package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func mergeImmediateRows[T any](old, fresh []T, concern func(T) domain.ConcernID) []T {
	var out []T
	for _, row := range old {
		if !policy.ImmediateConcern(concern(row)) {
			out = append(out, row)
		}
	}
	return append(out, fresh...)
}

// retainOrdinaryRounds overlays the scoped transaction on the prior record.
// Copying the untouched record retains each derived history's observation
// time; absence from this inspection is never a recovery or a new observation.
func retainOrdinaryRounds(ctx context.Context, tx *sql.Tx, r *Rounds, previous Rounds, result *RoundsResult) error {
	next := previous
	next.Revision, next.Snapshot, next.Tick = r.Revision, r.Snapshot, r.Tick
	next.Enabled, next.Immediate, next.Latches = r.Enabled, true, r.Latches
	next.Standards = mergeImmediateRows(previous.Standards, r.Standards, func(v RoundsStandard) domain.ConcernID { return v.Concern })
	next.Projects = mergeImmediateRows(previous.Projects, r.Projects, func(v RoundsProject) domain.ConcernID { return v.Concern })
	next.Incidents = mergeImmediateRows(previous.Incidents, r.Incidents, func(v RoundsIncident) domain.ConcernID { return v.Kind })
	next.Emergency = mergeImmediateRows(previous.Emergency, r.Emergency, func(v domain.ConcernID) domain.ConcernID { return v })
	next.NoOps = mergeImmediateRows(previous.NoOps, r.NoOps, func(v policy.NoOpRecord) domain.ConcernID { return v.Concern })
	result.Standards, result.Projects, result.Incidents = nil, nil, nil
	for _, b := range next.Standards {
		state, err := loadStandard(ctx, tx, b.Standard)
		if err != nil {
			return err
		}
		result.Standards = append(result.Standards, state)
	}
	for _, b := range next.Projects {
		state, err := loadProject(ctx, tx, b.Project)
		if err != nil {
			return err
		}
		result.Projects = append(result.Projects, state)
	}
	for _, b := range next.Incidents {
		state, err := loadIncident(ctx, tx, b.Incident)
		if err != nil {
			return err
		}
		result.Incidents = append(result.Incidents, state)
	}
	*r, result.Emergency = next, next.Emergency
	return nil
}
