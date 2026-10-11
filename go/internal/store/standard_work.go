package store

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Deficit reports an open Standard whose review found a deficit: the state a
// planner may work.
func (g StandardState) Deficit() bool {
	return g.Standard.Status == domain.StandardOpen && g.Standard.Finding == domain.FindingUnmet
}

// CommitOwnerMethod stores a method for the Standard like CommitMethodReason.
func (s *Store) CommitOwnerMethod(ctx context.Context, owner StandardState, method domain.MethodID, reason string, plan domain.PlanSpec) error {
	_, err := s.CommitMethodReason(ctx, owner.Standard.ID, owner.Revision, method, reason, plan)
	return err
}

// LoadOwnerMethod reads the Standard's binding of method in its current epoch,
// including a retired plan's.
func (s *Store) LoadOwnerMethod(ctx context.Context, owner StandardState, method domain.MethodID) (domain.Method, error) {
	return s.LoadMethod(ctx, owner.Standard.ID, owner.Standard.Episode, method)
}

// LoadOwnerMethods lists the Standard's methods in its current epoch, retired
// bindings included.
func (s *Store) LoadOwnerMethods(ctx context.Context, owner StandardState) ([]domain.Method, error) {
	return s.LoadMethods(ctx, owner.Standard.ID, owner.Standard.Episode)
}

func decisionOf(owner StandardState, refused []policy.Refusal) BuildingMethodDecision {
	return BuildingMethodDecision{Standard: owner, Refused: refused}
}

// OwnerSummary is the lifecycle view of a Standard the HTTP API reports.
type OwnerSummary struct {
	ID string
	// Kind is the Concern the Standard serves.
	Kind     domain.ConcernID
	Status   domain.StandardStatus
	Finding  domain.Finding
	Priority int
	Episode  uint64
	Revision uint64
	Snapshot domain.GenerationSnapshot
	Retired  bool
}

// SummarizeOwner reads the Standard's lifecycle summary.
func SummarizeOwner(o StandardState) OwnerSummary {
	g := o.Standard
	return OwnerSummary{string(g.ID), g.Kind, g.Status, g.Finding, g.Priority, g.Episode, o.Revision, g.Snapshot, o.Retired}
}

// ownerActive reports a Standard that is active in the goal vocabulary.
func ownerActive(owner StandardState) bool {
	return owner.Standard.Status == domain.StandardOpen
}
