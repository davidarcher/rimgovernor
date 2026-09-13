package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type BedAssignJournal interface {
	Journal
	PrepareBedAssign(context.Context, domain.PlanID, domain.ActionID, store.BedAssignAdmission) (domain.Progress, error)
}

type BedAssignInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.BedAssignFacts
}

type BedAssignDispatch struct {
	Attempt   Placement
	Admission store.BedAssignAdmission
}

type BedAssignEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Bed                   string
}

// BedAssignBoundary is optionally composed, like RecoveryServiceBoundary: the
// pawn is not drafted, and the planner has already selected the pawn/bed pair
// from a fresh ReviewSleeping selection, so this family attaches without a
// hard NewWith constructor.
type BedAssignBoundary interface {
	InspectBedAssign(context.Context, Target) (BedAssignInspection, error)
	AssignBedPawn(context.Context, BedAssignDispatch) (Receipt, error)
	ObserveBedAssign(context.Context, BedAssignDispatch, domain.GenerationSnapshot) (BedAssignEvidence, error)
}

// EnableBedAssign activates the bed-assign capability; see EnableAcquisition
// (in acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableBedAssign(bedAssign BedAssignBoundary) error {
	if bedAssign == nil {
		return errors.New("bed assign boundary required")
	}
	j, ok := e.journal.(BedAssignJournal)
	if !ok {
		return errors.New("bed assign boundary requires typed journal")
	}
	e.bedAssign, e.bedAssignJournal = bedAssign, j
	return nil
}
