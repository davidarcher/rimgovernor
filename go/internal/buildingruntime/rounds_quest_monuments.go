package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

func (r *RoundsPopulationJoinerPlanner) admitMonument(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	cells, err := r.reviewer.fieldProtected(call, state, review, read.Projection, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	protected := make([]policy.Rectangle, 0, len(cells))
	for _, cell := range cells {
		protected = append(protected, policy.Rectangle{X: cell.X, Z: cell.Z, Width: 1, Height: 1})
	}
	stock := map[policy.Resource]int64{}
	if rows, known := read.Projection.Facts.Resources.Value(); known {
		for _, row := range rows {
			stock[row.Resource] += row.Count
		}
	}
	choice := policy.SelectQuestMonument(read.Projection.Facts.QuestOffers, read.Projection.Identity.Map, stock, protected)
	if choice.Quest == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if choice.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: choice.Quest, Reason: policy.QuestSkipReason(choice.Reason)})
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if choice.Kind == "protect" || choice.Kind == "wait" {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, true, nil
	}
	id := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	aid := domain.ActionID(string(id) + "-0")
	var action domain.Action
	var preview bridge.BuildingPreview
	switch choice.Kind {
	case "install":
		action, err = domain.NewMoveBuildingAction(aid, choice.Move)
		if !arbiter.tryClaim(nil, "move-building:"+choice.Move.Thing()) {
			return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "monument_install")}, true, nil
		}
	case "haul":
		action, err = domain.NewHaulAction(aid, choice.Haul)
		if !arbiter.tryClaim([]domain.PawnID{choice.Haul.Pawn()}, "haul-item:"+choice.Haul.Thing()) {
			return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "monument_haul")}, true, nil
		}
	case "build":
		action, err = domain.NewBuildingAction(aid, choice.Build, domain.TierExpand)
		if err != nil {
			return RoundsPopulationJoinerResult{}, false, err
		}
		native, ok := r.reviewer.native.(interface {
			PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
		})
		if !ok {
			return RoundsPopulationJoinerResult{}, false, nil
		}
		preview, _, err = native.PreviewBuilding(call, action, snapshot)
	default:
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	target := choice.Move.Thing()
	if choice.Kind == "haul" {
		target = choice.Haul.Thing()
	}
	if choice.Kind == "build" {
		target = fmt.Sprintf("%+v", choice.Build)
	}
	prefix := fmt.Sprintf("monument-%s-%s-%s-", choice.Quest, choice.Kind, target)
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
	if choice.Kind == "build" {
		decision, err := admitMethod(call, r.reviewer.player.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: read.Projection.Identity.Tick, Bounds: domain.Known(read.Projection.Bounds), Stock: preview.Stock, Previews: []policy.Preview{preview.Preview}, Purpose: policy.Rounds})
		if err != nil {
			return RoundsPopulationJoinerResult{}, false, err
		}
		if !decision.Admitted {
			return RoundsPopulationJoinerResult{Verdict: admissionRefused(decision)}, true, nil
		}
	} else if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: stockWaitTicks}, true, nil
}
