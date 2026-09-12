package executor

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RangedJournal interface {
	DraftJournal
	PrepareRangedAttack(context.Context, domain.PlanID, domain.ActionID, store.MeleeAdmission) (domain.Progress, error)
}

type RangedInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.RangedDefenseFacts
}

type RangedDispatch struct {
	Attempt   Placement
	Admission store.MeleeAdmission
}

type RangedEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn, Target          domain.PawnID
}

type RangedBoundary interface {
	InspectRanged(context.Context, Target, domain.DraftClaim) (RangedInspection, error)
	AttackRanged(context.Context, RangedDispatch) (Receipt, error)
	ObserveRanged(context.Context, RangedDispatch, domain.GenerationSnapshot) (RangedEvidence, error)
}
