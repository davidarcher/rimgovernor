package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecordProject updates intent at its CAS revision. Save-time flushing carries
// the record in the existing GovernorProjectBlob.
func (s *Store) RecordProject(ctx context.Context, id domain.ProjectID, revision uint64, record string) (ProjectState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ProjectState{}, err
	}
	defer tx.Rollback()
	state, err := loadProject(ctx, tx, id)
	if err != nil {
		return ProjectState{}, err
	}
	if state.Revision != revision {
		return ProjectState{}, ErrConflict
	}
	p := state.Project
	p.Record = record
	out, err := saveProject(ctx, tx, state, p)
	if err != nil {
		return ProjectState{}, err
	}
	return out, tx.Commit()
}

// CommitProjectMethodRecord journals the shared method and changes its saved
// intent atomically, before Hands can dispatch its action.
func (s *Store) CommitProjectMethodRecord(ctx context.Context, id domain.ProjectID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec, record string) (ProjectState, error) {
	if err := plan.Validate(); err != nil {
		return ProjectState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ProjectState{}, err
	}
	defer tx.Rollback()
	state, err := commitProjectMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return ProjectState{}, err
	}
	p := state.Project
	p.Record = record
	out, err := saveProject(ctx, tx, state, p)
	if err != nil {
		return ProjectState{}, err
	}
	return out, tx.Commit()
}
