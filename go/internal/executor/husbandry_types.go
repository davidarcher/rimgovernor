package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type HusbandryJournal interface {
	Journal
	PrepareHusbandry(context.Context, domain.PlanID, domain.ActionID, store.HusbandryAdmission) (domain.Progress, error)
}

type HusbandryInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.HusbandryFacts
}

type HusbandryDispatch struct {
	Attempt   Placement
	Admission store.HusbandryAdmission
}

type HusbandryEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Animal                domain.PawnID
}

// HusbandryBoundary is optionally composed, like GearReplaceBoundary: no pawn
// is drafted, and the planner has already selected the animal/method pair
// from the native herd census, so this family attaches without a hard
// NewWith constructor.
type HusbandryBoundary interface {
	InspectHusbandry(context.Context, Target) (HusbandryInspection, error)
	WriteHusbandry(context.Context, HusbandryDispatch) (Receipt, error)
	ObserveHusbandry(context.Context, HusbandryDispatch, domain.GenerationSnapshot) (HusbandryEvidence, error)
}

// EnableHusbandry activates the husbandry capability; see EnableAcquisition
// (in acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableHusbandry(husbandry HusbandryBoundary) error {
	if husbandry == nil {
		return errors.New("husbandry boundary required")
	}
	j, ok := e.journal.(HusbandryJournal)
	if !ok {
		return errors.New("husbandry boundary requires typed journal")
	}
	e.husbandry, e.husbandryJournal = husbandry, j
	return nil
}
