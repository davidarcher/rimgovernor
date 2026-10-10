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

func (r *RoundsPopulationJoinerPlanner) admitIdeologyQuestWork(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	f := read.Projection.Facts
	hack, err := policy.SelectQuestHack(f, read.Projection.Identity.Map)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	gift, err := policy.SelectQuestGift(f, read.Projection.Identity.Map)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	id := domain.MintPlanID()
	actionID := domain.ActionID(string(id) + "-0")
	var action domain.Action
	var quest domain.QuestID
	var target, kind string
	var crew []domain.PawnID
	if hack.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: hack.Quest, Reason: hack.Reason})
	}
	if gift.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: gift.Quest, Reason: gift.Reason})
	}
	if hack.Hack != nil {
		action, err = domain.NewHackDesignationAction(actionID, *hack.Hack)
		quest, target, kind = hack.Quest, hack.Hack.Target(), "hack"
	} else if gift.Gift != nil {
		action, err = domain.NewGiveItemAction(actionID, *gift.Gift)
		quest, target, kind = gift.Quest, string(gift.Gift.Recipient()), "gift"
		crew = []domain.PawnID{gift.Gift.Hauler()}
	} else {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, false, nil
	}
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if len(crew) > 0 && !arbiter.tryClaim(crew, "quest:"+string(quest)) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(policy.CauseClaim, "quest_ideology")}, true, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	prefix := fmt.Sprintf("quest-%s-%s-%s-", kind, quest, target)
	method, verdict, admitted, err := admitStandardMethod(call, r.reviewer.player.journal, goal, prefix, state.Snapshot)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if !admitted {
		return RoundsPopulationJoinerResult{Verdict: verdict}, true, nil
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
