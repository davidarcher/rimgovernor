package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type CaravanDepartureJournal interface {
	Journal
	PrepareCaravanDeparture(context.Context, domain.PlanID, domain.ActionID, store.CaravanDepartureAdmission) (domain.Progress, error)
}

type CaravanDepartureInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.CaravanDepartureFacts
	Policy                policy.CaravanDeparturePolicy
}

type CaravanDepartureDispatch struct {
	Attempt   Placement
	Admission store.CaravanDepartureAdmission
}

type CaravanDepartureEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Crew                  []domain.PawnID
	CaravanID             string
}

// CaravanDepartureBoundary mirrors GearReplaceBoundary/EquipBoundary: crew and
// cargo are already selected by the planner from a fresh native caravan
// catalog census (Python's caravan_catalog), so this family attaches without
// a hard NewWith constructor. FormCaravan is a one-shot native operation, the
// same shape Draft/Attack/Equip already use; the executor observes departure
// via the native world census (world_progression.caravan_outcome), not a
// second dispatch.
type CaravanDepartureBoundary interface {
	InspectCaravanDeparture(context.Context, Target) (CaravanDepartureInspection, error)
	DepartCaravan(context.Context, CaravanDepartureDispatch) (Receipt, error)
	ObserveCaravanDeparture(context.Context, CaravanDepartureDispatch, domain.GenerationSnapshot) (CaravanDepartureEvidence, error)
}

// EnableCaravanDeparture activates the caravan-departure capability; see
// EnableAcquisition (in acquisition.go) for why capabilities are wired this
// way instead of inferred from a composed Boundary.
func (e *Executor) EnableCaravanDeparture(caravanDeparture CaravanDepartureBoundary) error {
	if caravanDeparture == nil {
		return errors.New("caravan departure boundary required")
	}
	j, ok := e.journal.(CaravanDepartureJournal)
	if !ok {
		return errors.New("caravan departure boundary requires typed journal")
	}
	e.caravanDeparture, e.caravanDepartureJournal = caravanDeparture, j
	return nil
}
