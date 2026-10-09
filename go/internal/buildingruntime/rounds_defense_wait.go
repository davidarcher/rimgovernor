package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defenseWaitTier names the wait hardening's methods and results.
const defenseWaitTier policy.DefenseTierName = "wait"

// defenseWaitingFight is the memory of this world's open fight while it
// waits behind its rooms' doors or its burn-out waits on fuel,
// nil otherwise.
func defenseWaitingFight(ctx context.Context, journal *store.Store, world store.World) (*policy.CombatMemory, error) {
	fights, err := journal.OpenCombatFights(ctx)
	if err != nil {
		return nil, err
	}
	for _, f := range fights {
		if f.Open && f.World == world && (f.Memory.Wait && len(f.Memory.WaitRooms) > 0 || f.Memory.Burn.Fueling()) {
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

// fight builds the waiting fight's needs: the burn-out's fuel
// while it waits on it, else the wait's hardening.
func (r *RoundsDefenseLayoutPlanner) fight(call, epoch context.Context, goal store.ProjectState, state ControlState, m policy.CombatMemory) (RoundsDefenseLayoutResult, error) {
	if m.Burn.Fueling() {
		return r.fuel(call, epoch, goal, state, *m.Burn)
	}
	return r.harden(call, epoch, goal, state, m)
}

// fightCensus reads the routine observation and the census of region at
// the same tick, keyed by visible cell.
func (r *RoundsDefenseLayoutPlanner) fightCensus(call context.Context, state ControlState, region bridge.CellRect) (observation.RoundsReading, map[domain.Cell]policy.WaitDoorCell, error) {
	p := r.reviewer.player
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return observation.RoundsReading{}, nil, err
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return observation.RoundsReading{}, nil, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return observation.RoundsReading{}, nil, err
	}
	site, _, err := r.native.ReadDefenseSite(call, boundary.Identity(state.Snapshot), region)
	if err != nil {
		return observation.RoundsReading{}, nil, err
	}
	if err = r.sameTick(site.Context, state, read.Projection.Identity.Tick); err != nil {
		return observation.RoundsReading{}, nil, err
	}
	return read, defenseFightCensus(site), nil
}

// defenseFightCensus is a census's visible cells by cell.
func defenseFightCensus(site bridge.DefenseSite) map[domain.Cell]policy.WaitDoorCell {
	census := map[domain.Cell]policy.WaitDoorCell{}
	for _, c := range site.Cells {
		if !c.Fogged {
			census[c.Cell] = policy.WaitDoorCell{Edifice: c.EdificeDefName, Stuff: c.EdificeStuff, Walkable: c.Walkable, Roofed: c.Roofed}
		}
	}
	return census
}

// harden admits the waiting fight's builds: plasteel over its
// rooms' wooden doors, a wall behind each broken door. One method per
// stop tick; the open-plan check upstream keeps one in flight.
func (r *RoundsDefenseLayoutPlanner) harden(call, epoch context.Context, goal store.ProjectState, state ControlState, m policy.CombatMemory) (RoundsDefenseLayoutResult, error) {
	if len(m.WaitRooms) == 0 {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit, Tier: defenseWaitTier}, nil
	}
	read, census, err := r.fightCensus(call, state, defenseWaitRegion(m))
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	stock, _ := read.Projection.Resources.Value()
	buildings, err := policy.WaitHardening(m, census, stock[policy.Resource(policy.WaitDoorStuff)], defenseDefinitions.Door, defenseDefinitions.Wall, defenseDefinitions.WallStuff)
	if err != nil || len(buildings) == 0 {
		return RoundsDefenseLayoutResult{Verdict: BuildingReasonNoDeficit, Tier: defenseWaitTier}, err
	}
	return r.admitFightBuilds(call, epoch, goal, state, read, buildings, defenseWaitTier)
}

// admitFightBuilds previews a fight's builds and admits the placeable ones
// as one method under the layout goal, named by tier and stop tick.
func (r *RoundsDefenseLayoutPlanner) admitFightBuilds(call, epoch context.Context, goal store.ProjectState, state ControlState, read observation.RoundsReading, buildings []domain.Building, tier policy.DefenseTierName) (RoundsDefenseLayoutResult, error) {
	p := r.reviewer.player
	tick := read.Projection.Identity.Tick
	id := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	var actions []domain.Action
	var previews []policy.Preview
	stockSeen := policy.StockObservation{Snapshot: snapshot, Tick: tick}
	for _, b := range buildings {
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), b)
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		preview, ok, err := r.preview(call, action, snapshot, tick, b.Cell())
		if err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
		if !ok {
			continue
		}
		actions = append(actions, action)
		previews = append(previews, preview.Preview)
		if err = mergeRoundsStock(&stockSeen, preview.Stock, len(actions) == 1); err != nil {
			return RoundsDefenseLayoutResult{}, err
		}
	}
	if len(actions) == 0 {
		return RoundsDefenseLayoutResult{Verdict: siteBlocked("defense_layout", "preview_refused"), Tier: tier}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if p.session.State() != state {
		return RoundsDefenseLayoutResult{}, defenseControlErr(118)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsDefenseLayoutResult{}, defenseControlErr(122)
	}
	method := domain.MethodID(fmt.Sprintf("defense-%s-%d", tier, tick))
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(read.Projection.Bounds), Stock: stockSeen, Previews: previews, Purpose: policy.Defense})
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	if !decision.Admitted {
		return RoundsDefenseLayoutResult{Verdict: admissionRefused(decision), Plan: id, Tier: tier}, nil
	}
	defenseAction(call, "defense-layout", slog.LevelInfo, "applied", "builds", string(tier), map[string]any{"builds": len(actions), "method": string(method)})
	return RoundsDefenseLayoutResult{Verdict: BuildingReasonAdmitted, Plan: id, Tier: tier}, nil
}
