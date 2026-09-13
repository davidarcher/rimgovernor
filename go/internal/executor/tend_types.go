package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type TendJournal interface {
	Journal
	PrepareTend(context.Context, domain.PlanID, domain.ActionID, store.TendAdmission) (domain.Progress, error)
}

type TendInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.TendFacts
}

type TendDispatch struct {
	Attempt   Placement
	Admission store.TendAdmission
}

type TendEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Doctor, Patient       domain.PawnID
}

// TendBoundary is optionally composed, like SupplyBoundary: neither doctor nor
// patient is drafted, so this family attaches without a hard NewWithTend ctor.
type TendBoundary interface {
	InspectTend(context.Context, Target) (TendInspection, error)
	TendPatient(context.Context, TendDispatch) (Receipt, error)
	ObserveTend(context.Context, TendDispatch, domain.GenerationSnapshot) (TendEvidence, error)
}

// EnableTend activates the tend capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableTend(tend TendBoundary) error {
	if tend == nil {
		return errors.New("tend boundary required")
	}
	j, ok := e.journal.(TendJournal)
	if !ok {
		return errors.New("tend boundary requires typed journal")
	}
	e.tend, e.tendJournal = tend, j
	return nil
}
