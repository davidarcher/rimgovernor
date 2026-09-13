package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type CleanJournal interface {
	Journal
	PrepareClean(context.Context, domain.PlanID, domain.ActionID, store.CleanAdmission) (domain.Progress, error)
}

type CleanInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.CleanFacts
}

type CleanDispatch struct {
	Attempt   Placement
	Admission store.CleanAdmission
}

type CleanEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Filth                 string
}

// CleanBoundary is optionally composed, like RepairBoundary/RescueBoundary:
// the pawn is not drafted, so this family attaches without a hard
// NewWithClean ctor.
type CleanBoundary interface {
	InspectClean(context.Context, Target) (CleanInspection, error)
	CleanFilth(context.Context, CleanDispatch) (Receipt, error)
	ObserveClean(context.Context, CleanDispatch, domain.GenerationSnapshot) (CleanEvidence, error)
}

// EnableClean activates the clean capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableClean(clean CleanBoundary) error {
	if clean == nil {
		return errors.New("clean boundary required")
	}
	j, ok := e.journal.(CleanJournal)
	if !ok {
		return errors.New("clean boundary requires typed journal")
	}
	e.clean, e.cleanJournal = clean, j
	return nil
}
