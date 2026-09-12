package executor

import (
	"context"
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
