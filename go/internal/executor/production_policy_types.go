package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ProductionPolicyJournal interface {
	Journal
	PrepareProductionPolicy(context.Context, domain.PlanID, domain.ActionID, store.ProductionPolicyAdmission) (domain.Progress, error)
}

// ProductionPolicyInspection carries the fresh floors/stopped facts
// EvaluateProductionPolicy re-checks, plus the Commitments/Drills rows read
// at the same instant: those two rows are owned by other systems and must be
// resent verbatim on the eventual SetProductionPolicy write (a present-but-
// empty row explicitly clears; see bridge.ProductionPolicyTarget's doc
// comment), so they travel with the inspection rather than being recomputed.
type ProductionPolicyInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.ProductionPolicyFacts
	Commitments           []store.ProductionCommitment
	Drills                []store.ProductionDrillTarget
}

type ProductionPolicyDispatch struct {
	Attempt   Placement
	Admission store.ProductionPolicyAdmission
}

type ProductionPolicyEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
}

// ProductionPolicyBoundary is optionally composed, like GearReplaceBoundary:
// the routine planner has already computed the desired floors/stopped
// replacement from a fresh native read, so this family attaches without a
// hard NewWith constructor.
type ProductionPolicyBoundary interface {
	InspectProductionPolicy(context.Context, Target) (ProductionPolicyInspection, error)
	SetProductionPolicy(context.Context, ProductionPolicyDispatch) (Receipt, error)
	ObserveProductionPolicy(context.Context, ProductionPolicyDispatch, domain.GenerationSnapshot) (ProductionPolicyEvidence, error)
}

// EnableProductionPolicy activates the production-policy capability; see
// EnableAcquisition (in acquisition.go) for why capabilities are wired this
// way instead of inferred from a composed Boundary.
func (e *Executor) EnableProductionPolicy(productionPolicy ProductionPolicyBoundary) error {
	if productionPolicy == nil {
		return errors.New("production policy boundary required")
	}
	j, ok := e.journal.(ProductionPolicyJournal)
	if !ok {
		return errors.New("production policy boundary requires typed journal")
	}
	e.productionPolicy, e.productionPolicyJournal = productionPolicy, j
	return nil
}
