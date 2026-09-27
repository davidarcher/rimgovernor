package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HaulJournal is Journal plus the untyped Prepare: a haul intent names its
// pawn and item itself and native checks both when it applies, so there is
// no haul admission row to persist.
type HaulJournal interface {
	Journal
	Prepare(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
}

// HaulInspection is the native read a haul dispatch is journaled at.
type HaulInspection struct {
	StartedAt, ObservedAt time.Time
	Snapshot              domain.GenerationSnapshot
	Tick                  domain.Tick
}

// HaulBoundary is optionally composed, like TradeBoundary: the planner has
// already selected the pawn and the item. Haul is an intent-mode kind, so
// its receipt is terminal and there is nothing to observe.
type HaulBoundary interface {
	InspectHaul(context.Context, Target) (HaulInspection, error)
	WriteHaul(context.Context, Placement) (Receipt, error)
}

// EnableHaul activates the haul capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableHaul(haul HaulBoundary) error {
	if haul == nil {
		return errors.New("haul boundary required")
	}
	j, ok := e.journal.(HaulJournal)
	if !ok {
		return errors.New("haul boundary requires typed journal")
	}
	e.haul, e.haulJournal = haul, j
	return nil
}
