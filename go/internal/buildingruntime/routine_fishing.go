package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func fishingWork(p observation.ColonyProjection) []policy.WorkRequirement {
	plan, known := p.Facts.FoodPlan.Value()
	if !known {
		return nil
	}
	for _, row := range plan.Portfolio {
		if row.Channel.Kind == policy.FoodFishing && row.Decision != policy.FoodPlanClose && row.DeliveredPerDay > 0 {
			return []policy.WorkRequirement{{Work: policy.WorkFishing, Skill: "Animals"}}
		}
	}
	return nil
}

func (r *RoutineReviewer) fishingResearchNeeds(needs []string) []string {
	r.census.mu.Lock()
	defer r.census.mu.Unlock()
	census := r.census.latest
	if census == nil || census.generation != r.census.generation {
		return needs
	}
	p := census.reading.Projection
	state := r.player.session.State()
	if !routineBuildingBoundary(p.Identity, state.Snapshot, 0) {
		return needs
	}
	c, ck := p.FoodChannels.Value()
	w, wk := c.FishableWater.Value()
	plan, pk := p.Facts.FoodPlan.Value()
	if ck && wk && pk {
		if target := policy.FishingResearchRequest(plan, w.FishingResearched); target != "" {
			return append(needs, target)
		}
	}
	return needs
}

// fishing uses the shared food goal and the ordinary zone Hands handler. The
// footprint offers access; native population-floor settings govern catches.
func (r *RoutineFieldPlanner) fishing(call, epoch context.Context, state ControlState, goal store.GoalState, read observation.RoutineReading) (RoutineFieldResult, bool, error) {
	p := read.Projection
	plan, pk := p.Facts.FoodPlan.Value()
	census, ck := p.FoodChannels.Value()
	water, wk := census.FishableWater.Value()
	researched, rk := water.FishingResearched.Value()
	token, tk := p.ZoneMapToken.Value()
	if !pk || !ck || !wk || !rk || !researched || !tk {
		return RoutineFieldResult{}, false, nil
	}
	for _, entry := range plan.Portfolio {
		if entry.Channel.Kind != policy.FoodFishing || entry.Decision == policy.FoodPlanClose || entry.DeliveredPerDay <= 0 {
			continue
		}
		for _, region := range water.Regions {
			if entry.Channel.ID != policy.FishingRegionID(region.Root) {
				continue
			}
			if open, known := region.Delivering.Value(); known && open {
				return RoutineFieldResult{Reason: BuildingMethodUsed, NativeWorkTicks: 2500}, true, nil
			}
			zoned, zk := region.Zoned.Value()
			if entry.Decision != policy.FoodPlanOpen || !zk || zoned || len(region.ProposedCells) == 0 {
				continue
			}
			method := domain.MethodID("fishing-" + entry.Channel.ID)
			journal := r.reviewer.player.journal
			if _, err := journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
				continue
			} else if !errors.Is(err, store.ErrNotFound) {
				return RoutineFieldResult{}, false, err
			}
			zone, err := domain.NewFishingZone(region.ProposedCells)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
			id := domain.PlanID(fmt.Sprintf("routine-fishing-%x", digest[:16]))
			action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-0"), zone)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			reply, refused, err := previewZone(call, r.native, boundary.Identity(state.Snapshot), bridge.ZoneTarget{Zone: zone, Token: token})
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			if refused != "" {
				continue
			}
			if _, err = boundary.Context(reply.GetEvaluated().Context, state.Snapshot); err != nil {
				return RoutineFieldResult{}, false, err
			}
			spec, err := domain.NewPlan(id, 1, []domain.Action{action})
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			if err = r.reviewer.player.current(call, epoch); err != nil {
				return RoutineFieldResult{}, false, err
			}
			identity, _, err := r.reviewer.native.Identity(call)
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			actual, err := observation.DecodeIdentity(identity)
			if err != nil || !routineBuildingBoundary(actual, state.Snapshot, p.Identity.Tick) || r.reviewer.player.session.State() != state {
				return RoutineFieldResult{}, false, ErrControl
			}
			now := r.reviewer.clock.Now()
			if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
				return RoutineFieldResult{}, false, observation.ErrStale
			}
			snapshot := state.Snapshot
			snapshot.Plan, snapshot.Revision = id, 1
			preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: p.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(zone.Cells()), Costs: domain.Known([]policy.Amount{})}
			decision, err := journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: spec, Current: snapshot, Tick: p.Identity.Tick, Bounds: domain.Known(p.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: p.Identity.Tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
			if err != nil {
				return RoutineFieldResult{}, false, err
			}
			if !decision.Admitted {
				return RoutineFieldResult{Reason: BuildingMethodRefused}, true, nil
			}
			return RoutineFieldResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
		}
	}
	return RoutineFieldResult{}, false, nil
}
