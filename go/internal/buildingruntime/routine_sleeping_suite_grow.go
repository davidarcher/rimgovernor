package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Suite growth (#1218): the plan row holds the grown interior; the
// sleeping planner raises the new ring, takes the old wall down once the
// extension stands enclosed, then re-sites the furniture
// (policy.NextSuiteGrowth).

// suiteGrowth is the projection's next suite growth step; false without
// the plan, the room census or a complete construction census.
func suiteGrowth(facts observation.ColonyProjection) (policy.SuiteGrowth, bool) {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !pk || !rk || !ck || !census.Colony {
		return policy.SuiteGrowth{}, false
	}
	return policy.NextSuiteGrowth(plan, rooms, census, policy.TidyFurnitureRooms(rooms, census, facts.Cells))
}

// suiteGrowthMethod names a growth step once per grown interior.
func suiteGrowthMethod(g policy.SuiteGrowth, key string) domain.MethodID {
	in := g.Room.Interior
	name := fmt.Sprintf("bedroom-%s-%d-%d-%dx%d", g.Kind, in.X, in.Z, in.Width, in.Height)
	if key != "" {
		digest := sha256.Sum256([]byte(key))
		name += fmt.Sprintf("-%x", digest[:8])
	}
	return domain.MethodID(name)
}

// growSuite admits one growth step: the ring through shellRoom, the old
// wall as one DECONSTRUCT Designate per wall, the furniture as one Reinstall
// batch.
func (r *RoutineSleepingUpkeepPlanner) growSuite(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, g policy.SuiteGrowth) (RoutineBuildingResult, error) {
	switch g.Kind {
	case policy.SuiteGrowthShell:
		return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, g.Room, suiteGrowthMethod(g, ""), "grow suite")
	case policy.SuiteGrowthOpen:
		return r.openSuiteWall(call, epoch, state, review, goal, reading, g)
	case policy.SuiteGrowthRelocate:
		return r.relocateSuite(call, epoch, state, goal, g)
	}
	return RoutineBuildingResult{}, fmt.Errorf("unknown suite growth kind %q", g.Kind)
}

func (r *RoutineSleepingUpkeepPlanner) growthCheck(call, epoch context.Context, state ControlState) func() error {
	p := r.reviewer.player
	return func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: growSuite: p.session.State() != state", ErrControl)
		}
		return nil
	}
}

// openSuiteWall deconstructs the old wall inside the grown interior.
func (r *RoutineSleepingUpkeepPlanner) openSuiteWall(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, g policy.SuiteGrowth) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	method := suiteGrowthMethod(g, "")
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := r.growthCheck(call, epoch, state)
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	actions := make([]domain.Action, 0, len(g.Walls))
	for i, w := range g.Walls {
		value, err := domain.NewDeconstruction(w.ID, w.Building.Definition(), w.Cells[0])
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), value)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	in := g.Room.Interior
	clockSchedulerLog("%s: suite %d,%d: old wall down (%d walls)", goal.Goal.ID, in.X, in.Z, len(actions))
	facts := reading.Projection
	return r.building.admitExcavation(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading}, snapshot, method, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, check)
}

// relocateSuite re-sites the grown suite's furniture in one ordered
// Reinstall batch, once per batch per goal epoch.
func (r *RoutineSleepingUpkeepPlanner) relocateSuite(call, epoch context.Context, state ControlState, goal store.GoalState, g policy.SuiteGrowth) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	things := make([]string, 0, len(g.Moves))
	for _, m := range g.Moves {
		things = append(things, m.Thing)
	}
	method := suiteGrowthMethod(g, strings.Join(things, ","))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	id := domain.MintPlanID()
	actionID := func(i int) domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, i)) }
	actions := make([]domain.Action, 0, len(g.Moves))
	var deps []domain.ActionDependency
	for i, m := range g.Moves {
		value, err := domain.NewMoveBuilding(m.Thing, m.Def, m.Anchor(), m.Rot)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		action, err := domain.NewMoveBuildingAction(actionID(i), value)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		actions = append(actions, action)
		for _, j := range m.After {
			deps = append(deps, domain.ActionDependency{Action: actionID(i), Requires: actionID(j)})
		}
	}
	plan, err := domain.NewPlan(id, 1, actions, deps...)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err := r.growthCheck(call, epoch, state)(); err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err := p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	clockSchedulerLog("%s: suite %s: %d furniture move(s) onto the grown plan", goal.Goal.ID, g.RoomID, len(actions))
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
