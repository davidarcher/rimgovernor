package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RescueJournal interface {
	Journal
	PrepareRescue(context.Context, domain.PlanID, domain.ActionID, store.RescueAdmission) (domain.Progress, error)
}

type RescueInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.RescueFacts
}

type RescueDispatch struct {
	Attempt   Placement
	Admission store.RescueAdmission
}

type RescueEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Rescuer, Patient      domain.PawnID
}

// RescueBoundary is optionally composed, like SupplyBoundary/TendBoundary:
// neither rescuer nor patient is drafted, so this family attaches without a
// hard NewWithRescue ctor.
type RescueBoundary interface {
	InspectRescue(context.Context, Target) (RescueInspection, error)
	RescuePatient(context.Context, RescueDispatch) (Receipt, error)
	ObserveRescue(context.Context, RescueDispatch, domain.GenerationSnapshot) (RescueEvidence, error)
}

// EnableRescue activates the rescue capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableRescue(rescue RescueBoundary) error {
	if rescue == nil {
		return errors.New("rescue boundary required")
	}
	j, ok := e.journal.(RescueJournal)
	if !ok {
		return errors.New("rescue boundary requires typed journal")
	}
	e.rescue, e.rescueJournal = rescue, j
	return nil
}
