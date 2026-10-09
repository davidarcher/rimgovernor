package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

func fishingWork(p observation.ColonyProjection) []policy.WorkRequirement {
	plan, known := p.Facts.FoodPlan.Value()
	if !known {
		return nil
	}
	for _, row := range plan.Portfolio {
		if row.Channel.Kind == policy.CandidateFishing && row.Decision != policy.FoodPlanClose && row.Selected() {
			return []policy.WorkRequirement{{Work: policy.WorkFishing}}
		}
	}
	return nil
}

// studyWork is the DarkStudy owner the held entities owe; an unread
// held or studiable fact is logged loudly and leaves the requirement unknown,
// so the work planner waits.
func studyWork(ctx context.Context, p observation.ColonyProjection) domain.Fact[[]policy.WorkRequirement] {
	work, reason := policy.StudyWork(p.Facts.Containment)
	if _, known := work.Value(); !known {
		telemetry.Decide(ctx, telemetry.Decision{Kind: "routine_skip", Component: "routine-work", Level: slog.LevelWarn, Verdict: "waiting", Reason: "entity_study_unread", Target: "entity_study", Attrs: map[string]any{"detail": reason}})
	}
	return work
}

// censusResearchNeeds adds the research the latest census asks for: the
// fishing request and, once everyone has a bedroom, ComplexFurniture.
func (r *Rounder) censusResearchNeeds(needs []string) []string {
	r.census.mu.Lock()
	defer r.census.mu.Unlock()
	census := r.census.latest
	if census == nil || census.generation != r.census.generation {
		return needs
	}
	p := census.reading.Projection
	state := r.player.session.State()
	if !roundsBuildingBoundary(p.Identity, state.Snapshot, 0) {
		return needs
	}
	c, ck := p.FoodChannels.Value()
	w, wk := c.FishableWater.Value()
	plan, pk := p.Facts.FoodPlan.Value()
	if ck && wk && pk {
		if target := policy.FishingResearchRequest(plan, w.FishingResearched); target != "" {
			needs = append(needs, target)
		}
	}
	layout, lk := p.LayoutPlan.Value()
	rooms, rk := p.Rooms.Value()
	sleeping, sk := p.Facts.Sleeping.Value()
	if lk && rk && sk {
		needs = policy.BedResearchRequest(needs, layout, rooms, sleeping, bedroomTargets(p))
	}
	return needs
}

// fishing uses the shared food goal and the ordinary zone Hands handler. The
// footprint offers access; native population-floor settings govern catches.
func (r *RoundsFieldPlanner) fishing(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading) (RoundsFieldResult, bool, error) {
	p := read.Projection
	plan, pk := p.Facts.FoodPlan.Value()
	census, ck := p.FoodChannels.Value()
	water, wk := census.FishableWater.Value()
	researched, rk := water.FishingResearched.Value()
	if !pk || !ck || !wk || !rk || !researched {
		return RoundsFieldResult{}, false, nil
	}
	for _, entry := range plan.Portfolio {
		if entry.Channel.Kind != policy.CandidateFishing || entry.Decision == policy.FoodPlanClose || !entry.Selected() {
			continue
		}
		for _, region := range water.Regions {
			if entry.Channel.ID != policy.FishingRegionID(region.Root) {
				continue
			}
			// An open zone is fished by the game; it must not hold the rest of the
			// field planner, or crops are never planned while the fish run down to
			// their population floor.
			if open, known := region.Delivering.Value(); known && open {
				continue
			}
			zoned, zk := region.Zoned.Value()
			if entry.Decision != policy.FoodPlanOpen || !zk || zoned || len(region.ProposedCells) == 0 {
				continue
			}
			method := domain.MethodID("fishing-" + entry.Channel.ID)
			journal := r.reviewer.player.journal
			if _, err := journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
				continue
			} else if !errors.Is(err, store.ErrNotFound) {
				return RoundsFieldResult{}, false, err
			}
			zone, err := domain.NewFishingZone(region.ProposedCells)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			id := domain.MintPlanID()
			action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-0"), zone)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			reply, refused, err := previewZone(call, r.native, boundary.Identity(state.Snapshot), zone)
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			if refused != "" {
				continue
			}
			if _, err = boundary.Context(reply.GetEvaluated().Context, state.Snapshot); err != nil {
				return RoundsFieldResult{}, false, err
			}
			spec, err := domain.NewPlan(id, 1, []domain.Action{action})
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			if err = r.reviewer.player.current(call, epoch); err != nil {
				return RoundsFieldResult{}, false, err
			}
			actual, err := stepScope(call, r.reviewer.native)
			if err != nil || !roundsBuildingBoundary(actual, state.Snapshot, p.Identity.Tick) || r.reviewer.player.session.State() != state {
				return RoundsFieldResult{}, false, fmt.Errorf("%w: fishing: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, p.Identity.Tick) || r.reviewer.player.sessio", ErrControl)
			}
			now := r.reviewer.clock.Now()
			if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
				return RoundsFieldResult{}, false, observation.ErrStale
			}
			snapshot := state.Snapshot
			snapshot.Plan, snapshot.Revision = id, 1
			preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: p.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(zone.Cells()), Costs: domain.Known([]policy.Amount{})}
			decision, err := admitMethod(call, journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: spec, Current: snapshot, Tick: p.Identity.Tick, Bounds: domain.Known(p.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: p.Identity.Tick}, Previews: []policy.Preview{preview}, Purpose: policy.Rounds})
			if err != nil {
				return RoundsFieldResult{}, false, err
			}
			if !decision.Admitted {
				return RoundsFieldResult{Verdict: admissionRefused(decision)}, true, nil
			}
			return RoundsFieldResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
		}
	}
	return RoundsFieldResult{}, false, nil
}
