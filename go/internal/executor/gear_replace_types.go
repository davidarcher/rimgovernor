package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type GearReplaceJournal interface {
	Journal
	PrepareGearReplace(context.Context, domain.PlanID, domain.ActionID, store.GearReplaceAdmission) (domain.Progress, error)
}

type GearReplaceInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.GearReplaceFacts
}

type GearReplaceDispatch struct {
	Attempt   Placement
	Admission store.GearReplaceAdmission
}

type GearReplaceEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Thing                 string
}

// GearReplaceBoundary is optionally composed, like EquipBoundary: the pawn is
// not drafted, and the planner has already selected the pawn/item pair from
// the native replacement census, so this family attaches without a hard
// NewWith constructor.
type GearReplaceBoundary interface {
	InspectGearReplace(context.Context, Target) (GearReplaceInspection, error)
	GearReplacePawn(context.Context, GearReplaceDispatch) (Receipt, error)
	ObserveGearReplace(context.Context, GearReplaceDispatch, domain.GenerationSnapshot) (GearReplaceEvidence, error)
}

// EnableGearReplace activates the gear-replace capability; see EnableAcquisition
// (in acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableGearReplace(gearReplace GearReplaceBoundary) error {
	if gearReplace == nil {
		return errors.New("gear replace boundary required")
	}
	j, ok := e.journal.(GearReplaceJournal)
	if !ok {
		return errors.New("gear replace boundary requires typed journal")
	}
	e.gearReplace, e.gearReplaceJournal = gearReplace, j
	return nil
}
