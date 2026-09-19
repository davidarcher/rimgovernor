package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// PlanningWindowNative is the optional native read behind the planning
// window (bridge.Client.ReadPlanningWindow); a scheduler whose native side
// lacks it serves no window and a current native's colony read plans no
// site.
type PlanningWindowNative interface {
	ReadPlanningWindow(context.Context, *c.Identity, policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error)
}

// planningWindow is the step's refresher for the planning_cells section
// (#356): the scheduler attaches one to each step's context
// (observation.WithPlanningWindow) and every planning colony read in the
// step whose reply carries no cells asks it. It reads natively once per
// full review step when the held section is stale, and on demand when a
// planner asks for a region the store does not hold; a timer or event step
// with a held window serves it whatever its age, since stale state is
// re-planned at apply (operations_preview, CAS tokens). The step's read
// cache makes a second ask in the same step free.
type planningWindow struct {
	native PlanningWindowNative
	store  *facts.Store
	scope  facts.Scope
	tick   int64
	review bool
}

// planningWindowRead is the refresher's decision: whether the window is
// read natively rather than served from the store. held is whether the
// store holds the requested region at all, fresh whether that row still
// serves the step's tick under FactColony's tolerance.
func planningWindowRead(review, held, fresh bool) bool {
	if !held {
		return true
	}
	return review && !fresh
}

func (p *planningWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	held, ok := facts.Get[observation.PlanningCells](p.store, facts.PlanningCells)
	ok = ok && held.Value.Region == region
	if !planningWindowRead(p.review, ok, ok && p.store.Fresh(facts.PlanningCells, p.tick)) {
		return held, nil
	}
	window, _, err := p.native.ReadPlanningWindow(ctx, identity, region)
	if err != nil {
		if ok {
			clockSchedulerLog("planning window: read failed, serving the held window as of %d: %v", held.AsOf, err)
			return held, nil
		}
		return facts.Held[observation.PlanningCells]{}, err
	}
	out := facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: window.Region, Cells: window.Cells}, AsOf: window.Context.GetTick(), Complete: true, Source: "rimgovernor/observations_get_cells", Region: planningRegionRect(window.Region)}
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	return out, nil
}

// planningRegionRect is the inclusive cell bounds of a planning region, so
// an invalidation narrowed to a rectangle (#359) can leave a window it
// does not touch fresh.
func planningRegionRect(region policy.Rectangle) facts.Rect {
	if region.Width <= 0 || region.Height <= 0 {
		return facts.Rect{}
	}
	return facts.Rect{MinX: region.X, MinZ: region.Z, MaxX: region.X + region.Width - 1, MaxZ: region.Z + region.Height - 1}
}
