package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RecoveryServiceJournal interface {
	Journal
	PrepareRecoveryService(context.Context, domain.PlanID, domain.ActionID, store.RecoveryServiceAdmission) (domain.Progress, error)
}

type RecoveryServiceInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.RecoveryServiceFacts
}

type RecoveryServiceDispatch struct {
	Attempt   Placement
	Admission store.RecoveryServiceAdmission
}

type RecoveryServiceEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Thing                 string
}

// RecoveryServiceBoundary is optionally composed, like GearReplaceBoundary:
// the pawn is not drafted, and the planner has already selected the
// pawn/building/method triple from a fresh disaster-recovery selection, so
// this family attaches without a hard NewWith constructor.
type RecoveryServiceBoundary interface {
	InspectRecoveryService(context.Context, Target) (RecoveryServiceInspection, error)
	RecoveryServicePawn(context.Context, RecoveryServiceDispatch) (Receipt, error)
	ObserveRecoveryService(context.Context, RecoveryServiceDispatch, domain.GenerationSnapshot) (RecoveryServiceEvidence, error)
}

// EnableRecoveryService activates the recovery-service capability; see
// EnableAcquisition (in acquisition.go) for why capabilities are wired this
// way instead of inferred from a composed Boundary.
func (e *Executor) EnableRecoveryService(recoveryService RecoveryServiceBoundary) error {
	if recoveryService == nil {
		return errors.New("recovery service boundary required")
	}
	j, ok := e.journal.(RecoveryServiceJournal)
	if !ok {
		return errors.New("recovery service boundary requires typed journal")
	}
	e.recoveryService, e.recoveryServiceJournal = recoveryService, j
	return nil
}
