package executor

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MeleeJournal is DraftJournal plus the untyped Prepare: a melee intent
// names its pawn and target and native validates them live, so there is no
// admission row.
type MeleeJournal interface {
	DraftJournal
	Prepare(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
}

// MeleeBoundary sends a melee attack or subdue intent through Actions/Apply.
type MeleeBoundary interface {
	WriteMelee(context.Context, Placement) (Receipt, error)
}
