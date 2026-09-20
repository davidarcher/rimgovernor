package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ApparelPolicyJournal interface {
	Journal
	PrepareApparelPolicy(context.Context, domain.PlanID, domain.ActionID, store.ApparelPolicyAdmission) (domain.Progress, error)
}
type ApparelPolicyInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.ApparelPolicyFacts
}
type ApparelPolicyDispatch struct {
	Attempt Placement
}
type ApparelPolicyEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Matches               domain.Fact[bool]
}

type ApparelPolicyBoundary interface {
	InspectApparelPolicy(context.Context, Target) (ApparelPolicyInspection, error)
	SetApparelPolicy(context.Context, ApparelPolicyDispatch) (Receipt, error)
	ObserveApparelPolicy(context.Context, Placement, domain.GenerationSnapshot) (ApparelPolicyEvidence, error)
}

func (e *Executor) EnableApparelPolicy(apparelPolicy ApparelPolicyBoundary) error {
	if apparelPolicy == nil {
		return errors.New("apparel policy boundary required")
	}
	j, ok := e.journal.(ApparelPolicyJournal)
	if !ok {
		return errors.New("apparel policy boundary requires typed journal")
	}
	e.apparelPolicy, e.apparelPolicyJournal = apparelPolicy, j
	return nil
}
