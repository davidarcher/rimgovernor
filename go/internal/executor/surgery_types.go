package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SurgeryJournal interface {
	Journal
	PrepareSurgery(context.Context, domain.PlanID, domain.ActionID, store.SurgeryAdmission) (domain.Progress, error)
}

type SurgeryInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.SurgeryFacts
}

type SurgeryDispatch struct {
	Attempt   Placement
	Admission store.SurgeryAdmission
}

type SurgeryEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Patient               domain.PawnID
}

// SurgeryBoundary is optionally composed, like BedAssignBoundary/RecoveryServiceBoundary:
// the patient is not drafted and there is no doctor role, so this family
// attaches without a hard NewWith constructor.
type SurgeryBoundary interface {
	InspectSurgery(context.Context, Target) (SurgeryInspection, error)
	QueueSurgery(context.Context, SurgeryDispatch) (Receipt, error)
	ObserveSurgery(context.Context, SurgeryDispatch, domain.GenerationSnapshot) (SurgeryEvidence, error)
}

// EnableSurgery activates the surgery capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of inferred
// from a composed Boundary.
func (e *Executor) EnableSurgery(surgery SurgeryBoundary) error {
	if surgery == nil {
		return errors.New("surgery boundary required")
	}
	j, ok := e.journal.(SurgeryJournal)
	if !ok {
		return errors.New("surgery boundary requires typed journal")
	}
	e.surgery, e.surgeryJournal = surgery, j
	return nil
}
