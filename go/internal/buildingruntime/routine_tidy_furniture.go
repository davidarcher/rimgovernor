package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// move commits a furniture proposal (#809) as one plan: a MoveBuilding
// (Reinstall) action per batch move, each ordered after the moves that
// clear its target, and journals every moved piece moving under the plan.
// The executor previews each reinstall natively before dispatch; a pawn
// uninstalls a piece only once it can reserve it, so nobody is interrupted.
func (r *RoutineTidyPlanner) move(call, epoch context.Context, state ControlState, goal store.GoalState, read observation.RoutineReading, proposal policy.TidyProposal) (RoutineTidyResult, error) {
	p := r.reviewer.player
	var things []string
	for _, m := range proposal.Moves {
		things = append(things, m.Thing)
	}
	method := tidyMethodID(proposal.Item.ID+"/"+strings.Join(things, ","), "furniture")
	if _, err := p.journal.LatestMethodPlan(call, goal.Goal.ID, method); err == nil {
		return RoutineTidyResult{Verdict: waitFor(WaitMethodUsed, "tidy_furniture")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineTidyResult{}, err
	}
	id := domain.MintPlanID()
	tick := read.Projection.Identity.Tick
	actions := make([]domain.Action, 0, len(proposal.Moves))
	var deps []domain.ActionDependency
	actionID := func(i int) domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, i)) }
	for i, m := range proposal.Moves {
		value, err := domain.NewMoveBuilding(m.Thing, m.Def, m.Anchor(), m.Rot)
		if err != nil {
			return r.abandonFurniture(call, state, tick, proposal, "")
		}
		action, err := domain.NewMoveBuildingAction(actionID(i), value)
		if err != nil {
			return RoutineTidyResult{}, err
		}
		actions = append(actions, action)
		for _, j := range m.After {
			deps = append(deps, domain.ActionDependency{Action: actionID(i), Requires: actionID(j)})
		}
	}
	plan, err := domain.NewPlan(id, 1, actions, deps...)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if err = r.commit(call, epoch, state, goal, read, method, plan); err != nil {
		return RoutineTidyResult{}, err
	}
	clockEvent(call, "layout", "tidy", "tidy furniture batch admitted: "+proposal.Explanation, "item", proposal.Item.ID, "plan", string(id), "moves", len(actions))
	for _, row := range tidyFurnitureRows(proposal, store.LayoutTidyMoving, string(id)) {
		if err = p.journal.RecordLayoutTidy(call, state.Snapshot, tick, row); err != nil {
			return RoutineTidyResult{}, err
		}
	}
	return RoutineTidyResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// tidyFurnitureRows is one row per moved piece: From its first leg's
// origin, To its final slot.
func tidyFurnitureRows(proposal policy.TidyProposal, status store.LayoutTidyStatus, plan string) []store.LayoutTidy {
	var out []store.LayoutTidy
	index := map[string]int{}
	for _, m := range proposal.Moves {
		i, seen := index[m.Thing]
		if !seen {
			index[m.Thing] = len(out)
			out = append(out, store.LayoutTidy{Item: m.Thing, Kind: policy.TidyFurniture, Status: status, From: m.From, PlanID: plan, Explanation: proposal.Explanation})
			i = len(out) - 1
		}
		out[i].To = m.To
	}
	return out
}

func (r *RoutineTidyPlanner) abandonFurniture(call context.Context, state ControlState, tick domain.Tick, proposal policy.TidyProposal, plan string) (RoutineTidyResult, error) {
	clockEvent(call, "layout", "tidy", "tidy furniture batch abandoned: "+proposal.Item.ID, "item", proposal.Item.ID)
	for _, row := range tidyFurnitureRows(proposal, store.LayoutTidyAbandoned, plan) {
		if err := r.reviewer.player.journal.RecordLayoutTidy(call, state.Snapshot, tick, row); err != nil {
			return RoutineTidyResult{}, err
		}
	}
	return RoutineTidyResult{Verdict: siteBlocked("furniture_move", "move_invalid")}, nil
}

// finishFurniture closes a moving furniture batch once its plan's work is
// done: a piece whose final move completed is journaled done, any other
// abandoned. A move whose prerequisite failed can never run, so it is
// cancelled rather than left holding the batch open.
func (r *RoutineTidyPlanner) finishFurniture(call context.Context, state ControlState, tick domain.Tick, tidies []store.LayoutTidy, planID string) (RoutineTidyResult, error) {
	p := r.reviewer.player
	var rows []store.LayoutTidy
	for _, t := range tidies {
		if t.Kind == policy.TidyFurniture && t.Status == store.LayoutTidyMoving && t.PlanID == planID {
			rows = append(rows, t)
		}
	}
	record := func(completed map[string]bool) (RoutineTidyResult, error) {
		for _, t := range rows {
			t.Status, t.Tick = store.LayoutTidyAbandoned, tick
			if completed[t.Item] {
				t.Status = store.LayoutTidyDone
			}
			if err := p.journal.RecordLayoutTidy(call, state.Snapshot, tick, t); err != nil {
				return RoutineTidyResult{}, err
			}
		}
		clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy furniture batch closed: %d of %d pieces moved", len(completed), len(rows)), "plan", planID)
		return RoutineTidyResult{Verdict: BuildingReasonAdmitted}, nil
	}
	plan, err := p.journal.LoadPlan(call, domain.PlanID(planID))
	if errors.Is(err, store.ErrNotFound) {
		return record(nil)
	}
	if err != nil {
		return RoutineTidyResult{}, err
	}
	actions := plan.Spec.Actions()
	failed := map[domain.ActionID]bool{}
	for i, a := range actions {
		v := plan.Progress[i].View()
		effect, known := v.Effect.Value()
		closed := v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled || v.Stage == domain.Completed && (!known || effect != domain.EffectCompleted)
		if closed && !v.Unresolved {
			failed[a.ID()] = true
		}
	}
	for changed := true; changed; {
		changed = false
		for _, d := range plan.Spec.Dependencies() {
			if failed[d.Requires] && !failed[d.Action] {
				failed[d.Action], changed = true, true
			}
		}
	}
	for i, a := range actions {
		if v := plan.Progress[i].View(); failed[a.ID()] && (v.Stage == domain.Pending || v.Stage == domain.Prepared) && !v.Unresolved {
			if _, err := p.journal.Cancel(call, plan.Spec.ID(), a.ID()); err != nil {
				return RoutineTidyResult{}, err
			}
		}
	}
	if plan, err = p.journal.LoadPlan(call, domain.PlanID(planID)); err != nil {
		return RoutineTidyResult{}, err
	}
	if store.PlanOpen(plan) {
		return RoutineTidyResult{Verdict: BuildingReasonExistingWork}, nil
	}
	// A piece's final leg is its last action in the batch.
	final := map[string]int{}
	for i, a := range plan.Spec.Actions() {
		if m, ok := a.MoveBuilding(); ok {
			final[m.Thing()] = i
		}
	}
	completed := map[string]bool{}
	for thing, i := range final {
		v := plan.Progress[i].View()
		effect, known := v.Effect.Value()
		completed[thing] = v.Stage == domain.Completed && known && effect == domain.EffectCompleted
		if !completed[thing] {
			delete(completed, thing)
		}
	}
	return record(completed)
}
