package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RepairJournal interface {
	Journal
	PrepareRepair(context.Context, domain.PlanID, domain.ActionID, store.RepairAdmission) (domain.Progress, error)
}

type RepairInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.RepairFacts
}

type RepairDispatch struct {
	Attempt   Placement
	Admission store.RepairAdmission
}

type RepairEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Structure             string
}

// RepairBoundary is optionally composed, like RescueBoundary/TendBoundary: the
// pawn is not drafted, so this family attaches without a hard NewWithRepair ctor.
type RepairBoundary interface {
	InspectRepair(context.Context, Target) (RepairInspection, error)
	RepairStructure(context.Context, RepairDispatch) (Receipt, error)
	ObserveRepair(context.Context, RepairDispatch, domain.GenerationSnapshot) (RepairEvidence, error)
}

// EnableRepair activates the repair capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableRepair(repair RepairBoundary) error {
	if repair == nil {
		return errors.New("repair boundary required")
	}
	j, ok := e.journal.(RepairJournal)
	if !ok {
		return errors.New("repair boundary requires typed journal")
	}
	e.repair, e.repairJournal = repair, j
	return nil
}
