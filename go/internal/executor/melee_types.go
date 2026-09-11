package executor

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

type MeleeJournal interface {
	DraftJournal
	PrepareMelee(context.Context, domain.PlanID, domain.ActionID, store.MeleeAdmission) (domain.Progress, error)
}

type MeleeInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.MeleeDefenseFacts
}

type MeleeDispatch struct {
	Attempt   Placement
	Admission store.MeleeAdmission
}

type MeleeEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn, Target          domain.PawnID
}

type MeleeBoundary interface {
	InspectMelee(context.Context, Target, domain.DraftClaim) (MeleeInspection, error)
	AttackMelee(context.Context, MeleeDispatch) (Receipt, error)
	ObserveMelee(context.Context, MeleeDispatch, domain.GenerationSnapshot) (MeleeEvidence, error)
}
