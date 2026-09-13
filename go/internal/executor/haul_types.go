package executor

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type HaulJournal interface {
	Journal
	PrepareHaul(context.Context, domain.PlanID, domain.ActionID, store.HaulAdmission) (domain.Progress, error)
}

type HaulInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.HaulFacts
}

type HaulDispatch struct {
	Attempt   Placement
	Admission store.HaulAdmission
}

type HaulEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Thing                 string
}

// HaulBoundary is optionally composed, like TendBoundary: the pawn is not
// drafted, and the planner has already selected the pawn/thing pair, so this
// family attaches without a hard NewWithHaul ctor.
type HaulBoundary interface {
	InspectHaul(context.Context, Target) (HaulInspection, error)
	HaulThing(context.Context, HaulDispatch) (Receipt, error)
	ObserveHaul(context.Context, HaulDispatch, domain.GenerationSnapshot) (HaulEvidence, error)
}
