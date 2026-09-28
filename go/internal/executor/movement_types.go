package executor

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MovementJournal is Journal plus the untyped Prepare: a move is an
// idempotent intent native validates at apply time (alive, spawned,
// drafted, reachable), so there is no movement admission row to persist.
type MovementJournal interface {
	Journal
	Prepare(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
}

// MovementBoundary sends one move intent. Its receipt is terminal: the
// order was given (or already matched) or native refused it. Arrival is
// not an effect of the action; the worker gates the plan's final action
// on it.
type MovementBoundary interface {
	WriteMovement(context.Context, Placement) (Receipt, error)
}
