package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defenseWaitTier names the wait hardening's methods and results (#1065).
const defenseWaitTier policy.DefenseTierName = "wait"

// defenseWaitingFight is the memory of this world's open fight while it
// waits behind its rooms' doors (#1065), nil otherwise.
func defenseWaitingFight(ctx context.Context, journal *store.Store, world store.World) (*policy.CombatMemory, error) {
	fights, err := journal.HeldCombatFights(ctx)
	if err != nil {
		return nil, err
	}
	for _, f := range fights {
		if f.Open && f.World == world && f.Memory.Wait && len(f.Memory.WaitRooms) > 0 {
			m := f.Memory
			return &m, nil
		}
	}
	return nil, nil
}

// defenseWaitRegion is the rectangle around the wait doors and the cells
// behind them.
func defenseWaitRegion(m policy.CombatMemory) bridge.CellRect {
	r := bridge.CellRect{Min: m.WaitRooms[0].Door, Max: m.WaitRooms[0].Door}
	for _, w := range m.WaitRooms {
		for _, c := range []domain.Cell{w.Door, w.Inside} {
			r.Min.X, r.Min.Z = min(r.Min.X, c.X), min(r.Min.Z, c.Z)
			r.Max.X, r.Max.Z = max(r.Max.X, c.X), max(r.Max.Z, c.Z)
		}
	}
	return r
}

// harden admits the waiting fight's builds (#1065): plasteel over its
// rooms' wooden doors, a wall behind each broken door. One method per
// stop tick; the open-plan check upstream keeps one in flight.
func (r *RoutineDefenseLayoutPlanner) harden(call, epoch context.Context, goal store.GoalState, state ControlState, m policy.CombatMemory) (RoutineDefenseLayoutResult, error) {
	p := r.reviewer.player
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	tick := read.Projection.Identity.Tick
	site, _, err := r.native.ReadDefenseSite(call, boundary.Identity(state.Snapshot), defenseWaitRegion(m))
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = r.sameTick(site.Context, state, tick); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	census := map[domain.Cell]policy.WaitDoorCell{}
	for _, c := range site.Cells {
		if !c.Fogged {
			census[c.Cell] = policy.WaitDoorCell{Edifice: c.EdificeDefName, Stuff: c.EdificeStuff, Walkable: c.Walkable}
		}
	}
	stock, _ := read.Projection.Resources.Value()
	buildings, err := policy.WaitHardening(m, census, stock[policy.Resource(policy.WaitDoorStuff)], defenseDefinitions.Door, defenseDefinitions.Wall, defenseDefinitions.WallStuff)
	if err != nil || len(buildings) == 0 {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit, Tier: defenseWaitTier}, err
	}
	id := domain.MintPlanID("routine-defense-wait")
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	var actions []domain.Action
	var previews []policy.Preview
	stockSeen := policy.StockObservation{Snapshot: snapshot, Tick: tick}
	for _, b := range buildings {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), b)
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		preview, ok, err := r.preview(call, action, snapshot, tick, b.Cell())
		if err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
		if !ok {
			clockSchedulerLog("defense-layout.wait: %s/%s at %v refused natively", b.Definition(), b.Stuff(), b.Cell())
			continue
		}
		actions = append(actions, action)
		previews = append(previews, preview.Preview)
		if err = mergeRoutineStock(&stockSeen, preview.Stock, len(actions) == 1); err != nil {
			return RoutineDefenseLayoutResult{}, err
		}
	}
	if len(actions) == 0 {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodRefused, Tier: defenseWaitTier}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if p.session.State() != state {
		return RoutineDefenseLayoutResult{}, defenseControlErr(118)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, defenseControlErr(122)
	}
	method := domain.MethodID(fmt.Sprintf("defense-wait-%d", tick))
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(read.Projection.Bounds), Stock: stockSeen, Previews: previews, Purpose: policy.Defense})
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	if !decision.Admitted {
		clockSchedulerLog("defense-layout.wait: refused=%+v", decision.Refused)
		return RoutineDefenseLayoutResult{Reason: BuildingMethodRefused, Plan: id, Tier: defenseWaitTier}, nil
	}
	clockSchedulerLog("defense-layout.wait: %d builds (%s)", len(actions), method)
	return RoutineDefenseLayoutResult{Reason: BuildingMethodAdmitted, Plan: id, Tier: defenseWaitTier}, nil
}
