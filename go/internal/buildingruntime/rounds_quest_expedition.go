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

func (r *RoundsPopulationJoinerPlanner) admitExpedition(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	work := policy.SelectExpedition(read.Projection.Facts, r.reviewer.policy)
	if work.Quest == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if work.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: work.Quest, Reason: work.Reason})
	}
	if work.Departure == nil {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, false, nil
	}
	if !arbiter.tryClaim(work.Departure.Crew(), "quest:"+string(work.Quest)) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "quest_expedition")}, true, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewCaravanDepartureAction(domain.ActionID(string(id)+"-0"), *work.Departure)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	prefix := fmt.Sprintf("quest-expedition-%s-%s-", work.Quest, work.Site)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "quest_expedition_attempts", "")}, true, nil
	}
	if err = r.reviewer.player.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if r.reviewer.player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, false, ErrControl
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: stockWaitTicks}, true, nil
}
