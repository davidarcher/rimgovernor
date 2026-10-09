package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (r *RoundsPopulationJoinerPlanner) admitHospitality(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	work, err := policy.SelectHospitalityWork(read.Projection.Facts, read.Projection.Identity.Tick)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if work.Quest == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if work.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: work.Quest, Reason: work.Reason})
		if !work.Waiting {
			return RoundsPopulationJoinerResult{}, false, nil
		}
	}
	if work.Waiting {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitMethodUsed, "quest_hosting"), NativeWorkTicks: stockWaitTicks}, false, nil
	}
	id := domain.MintPlanID()
	var action domain.Action
	method := domain.MethodID("")
	if work.Assign != nil {
		action, err = domain.NewAssignAction(domain.ActionID(string(id)+"-0"), *work.Assign)
		method = domain.MethodID(fmt.Sprintf("quest-guest-bed-%s-%s-%s", work.Quest, work.Assign.Pawn(), work.Assign.Thing()))
	}
	if work.Shuttle != nil {
		action, err = domain.NewQuestShuttleAction(domain.ActionID(string(id)+"-0"), *work.Shuttle)
		method = domain.MethodID(fmt.Sprintf("quest-pickup-%s-%s-%t", work.Quest, work.Shuttle.Loading(), work.Shuttle.Launch()))
	}
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if action.ID() == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	prefix := string(method) + "-"
	method, verdict, admitted, err := admitStandardMethod(call, r.reviewer.player.journal, goal, prefix, state.Snapshot)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if !admitted {
		return RoundsPopulationJoinerResult{Verdict: verdict}, true, nil
	}
	if !arbiter.tryClaim(nil, "quest:"+string(work.Quest)) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "quest_hosting")}, true, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
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
