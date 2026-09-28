package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
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
// full review step, and on demand when a
// planner asks for a region the store does not hold (planningWindowCovers);
// a timer or event step
// with a held window serves it whatever its age, since stale state is
// re-planned at apply (Actions/Apply checks live). The step's read
// cache makes a second ask in the same step free.
type planningWindow struct {
	native PlanningWindowNative
	store  *facts.Store
	// mirror holds the window's rows across steps (clockFacts.mirror);
	// the step goroutine alone refreshes it.
	mirror *mirror.Mirror
	scope  facts.Scope
	tick   int64
	review bool
	// refreshed is set once a review step has read the window.
	refreshed bool
}

// planningWindowRead is the refresher's decision: whether the window is
// read natively rather than served from the store. held is whether the
// store holds the requested region at all; a review step reads it once.
func planningWindowRead(review, refreshed, held bool) bool {
	return !held || review && !refreshed
}

// planningWindowSlack is how far, in cells on each axis, a planner's
// region may sit from the held window's before the window is re-read
// at the new place (#593). The window is centred on the colony's centre,
// which moves a cell or two as colonists walk; a window that followed it
// exactly was read in full on every review step, and a site a few cells
// past one edge is no better than one a few cells inside the other.
const planningWindowSlack int32 = 4

// planningWindowCovers is whether a held window of region held serves a
// planner asking for region: the same size, offset by at most
// planningWindowSlack on each axis.
func planningWindowCovers(held, region policy.Rectangle) bool {
	if held.Width != region.Width || held.Height != region.Height {
		return false
	}
	dx, dz := held.X-region.X, held.Z-region.Z
	return max(dx, -dx) <= planningWindowSlack && max(dz, -dz) <= planningWindowSlack
}

func (p *planningWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	held, ok := facts.Get[observation.PlanningCells](p.store, facts.PlanningCells)
	ok = ok && planningWindowCovers(held.Value.Region, region)
	if !planningWindowRead(p.review, p.refreshed, ok) {
		return held, nil
	}
	if ok {
		region = held.Value.Region
	}
	window, _, err := p.native.ReadPlanningWindow(ctx, identity, region)
	p.refreshed = p.refreshed || err == nil && p.review
	if err != nil {
		if ok {
			clockSchedulerLog("planning window: read failed, serving the held window as of %d: %v", held.AsOf, err)
			return held, nil
		}
		return facts.Held[observation.PlanningCells]{}, err
	}
	return p.put(identity, window.Region, window.Cells, window.Context.GetTick()), nil
}

func (p *planningWindow) put(identity *c.Identity, region policy.Rectangle, cells []policy.SiteCell, tick int64) facts.Held[observation.PlanningCells] {
	return p.file(identity, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: region, Cells: cells}, AsOf: tick, Complete: true, Source: "rimgovernor/observations_get_cells", Region: planningRegionRect(region)})
}

// file puts the window in the store and publishes its rows as the
// planning_cells mirror section, however it was read (a grid or a full
// read), so a recording holds the rows a review's cells are rebuilt from
// (snapshot bindings). An
// unchanged table is not republished.
func (p *planningWindow) file(identity *c.Identity, out facts.Held[observation.PlanningCells]) facts.Held[observation.PlanningCells] {
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	if p.mirror == nil {
		p.mirror = mirror.New()
	}
	ms := mirrorScope(p.scope, identity)
	if table, ok := mirror.Get[domain.Cell, policy.SiteCell](p.mirror, ms, string(facts.PlanningCells)); !ok || table.AsOf != mirror.At(out.AsOf) || !sameWindowRows(table.Rows, out.Value.Cells) {
		mirror.Put(p.mirror, ms, string(facts.PlanningCells), cellRows(out.Value.Cells), mirror.At(out.AsOf))
	}
	return out
}

func sameWindowRows(rows map[domain.Cell]policy.SiteCell, cells []policy.SiteCell) bool {
	if len(rows) != len(cells) {
		return false
	}
	for _, cell := range cells {
		if row, ok := rows[cell.Cell]; !ok || row != cell {
			return false
		}
	}
	return true
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
