package executor

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type MovementJournal interface {
	DraftJournal
	PrepareMovement(context.Context, domain.PlanID, domain.ActionID, store.MovementAdmission) (domain.Progress, error)
}

type MovementInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.MovementFacts
}

type MovementDispatch struct {
	Attempt   Placement
	Admission store.MovementAdmission
}

type MovementEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
}

type MovementBoundary interface {
	InspectMovement(context.Context, Target, domain.DraftClaim) (MovementInspection, error)
	MoveTo(context.Context, MovementDispatch) (Receipt, error)
	ObserveMovement(context.Context, MovementDispatch, domain.GenerationSnapshot) (MovementEvidence, error)
}
