package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type WasteJournal interface {
	Journal
	PrepareWaste(context.Context, domain.PlanID, domain.ActionID, store.WasteAdmission) (domain.Progress, error)
}

type WasteInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.WasteDispatchFacts
}

type WasteDispatch struct {
	Attempt   Placement
	Admission store.WasteAdmission
}

type WasteEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Target                string
}

// WasteBoundary is optionally composed, like CleanBoundary/RepairBoundary:
// the pawn is not drafted, so this family attaches without a hard
// NewWithWaste ctor.
type WasteBoundary interface {
	InspectWaste(context.Context, Target) (WasteInspection, error)
	ManageWaste(context.Context, WasteDispatch) (Receipt, error)
	ObserveWaste(context.Context, WasteDispatch, domain.GenerationSnapshot) (WasteEvidence, error)
}

// EnableWaste activates the waste capability; see EnableClean (in
// clean_types.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableWaste(waste WasteBoundary) error {
	if waste == nil {
		return errors.New("waste boundary required")
	}
	j, ok := e.journal.(WasteJournal)
	if !ok {
		return errors.New("waste boundary requires typed journal")
	}
	e.waste, e.wasteJournal = waste, j
	return nil
}
