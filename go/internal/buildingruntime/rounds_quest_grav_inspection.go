package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

func (r *RoundsPopulationJoinerPlanner) admitGravInspection(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	work, err := policy.SelectQuestGravInspection(read.Projection.Facts, read.Projection.Identity.Map)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if work.Quest == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if work.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: work.Quest, Reason: work.Reason})
	}
	if work.Waiting {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, false, nil
	}
	service := *work.Service
	if !arbiter.tryClaim([]domain.PawnID{service.Pawn()}, "service:"+service.Thing()) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "grav_inspection")}, true, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(string(id)+"-0"), service)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	prefix := fmt.Sprintf("grav-inspection-%s-%s-", work.Quest, service.Thing())
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "grav_inspection_attempts", "")}, true, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	if err = r.reviewer.player.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if r.reviewer.player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, false, ErrControl
	}
	if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: stockWaitTicks}, true, nil
}
