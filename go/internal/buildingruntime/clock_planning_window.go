package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// PlanningWindowNative is the optional native read behind the planning
// window (bridge.Client.ReadPlanningWindow); a scheduler whose native side
// lacks it serves no window and a current native's colony read plans no
// site. The since tick asks for a delta over a held window (#357).
type PlanningWindowNative interface {
	ReadPlanningWindow(context.Context, *c.Identity, policy.Rectangle, int64) (bridge.PlanningWindow, bridge.Result, error)
}

// planningWindowResyncEvery is the delta refresher's backstop cadence:
// every this-many refreshes of a held window is a full read compared
// against the delta, so a cell change the native grid missed surfaces as
// drift instead of living on in the store.
const planningWindowResyncEvery = 8

// planningWindow is the step's refresher for the planning_cells section
// (#356): the scheduler attaches one to each step's context
// (observation.WithPlanningWindow) and every planning colony read in the
// step whose reply carries no cells asks it. It reads natively once per
// full review step when the held section is stale, and on demand when a
// planner asks for a region the store does not hold; a timer or event step
// with a held window serves it whatever its age, since stale state is
// re-planned at apply (operations_preview, CAS tokens). The step's read
// cache makes a second ask in the same step free.
//
// A refresh of a held window asks for the cells changed since its as-of
// tick and merges them (#357). Every planningWindowResyncEvery-th refresh,
// and the first after a planner's apply was refused on a map CAS token
// (facts.Store.RequestResync), reads the whole window as well and logs
// how many rows the delta got wrong as `[facts] planning_cells resync
// drift=<n>`; non-zero drift is a bug against the native hook list.
type planningWindow struct {
	native PlanningWindowNative
	store  *facts.Store
	// refreshes counts the refreshes of a held window across steps
	// (clockFacts.windowRefreshes); the step goroutine alone touches it.
	refreshes *int
	scope     facts.Scope
	tick      int64
	review    bool
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

// planningWindowResync is whether a refresh of a held window is also a
// full read: one was requested, or the cadence is due.
func planningWindowResync(requested bool, refreshes int) bool {
	return requested || refreshes%planningWindowResyncEvery == planningWindowResyncEvery-1
}

func (p *planningWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	held, ok := facts.Get[observation.PlanningCells](p.store, facts.PlanningCells)
	ok = ok && held.Value.Region == region
	if !planningWindowRead(p.review, ok, ok && p.store.Fresh(facts.PlanningCells, p.tick)) {
		return held, nil
	}
	requested := p.store.ResyncDue(facts.PlanningCells)
	if !ok {
		window, _, err := p.native.ReadPlanningWindow(ctx, identity, region, 0)
		if err != nil {
			return facts.Held[observation.PlanningCells]{}, err
		}
		return p.put(window.Region, window.Cells, window.Context.GetTick()), nil
	}
	refreshes := 0
	if p.refreshes != nil {
		refreshes = *p.refreshes
		*p.refreshes++
	}
	window, _, err := p.native.ReadPlanningWindow(ctx, identity, region, held.AsOf)
	if err != nil {
		clockSchedulerLog("planning window: read failed, serving the held window as of %d: %v", held.AsOf, err)
		return held, nil
	}
	cells := window.Cells
	if window.Delta {
		cells = mergePlanningCells(held.Value.Cells, window)
	}
	if window.Delta && planningWindowResync(requested, refreshes) {
		full, _, err := p.native.ReadPlanningWindow(ctx, identity, region, 0)
		if err != nil {
			clockSchedulerLog("planning window: resync read failed, keeping the delta as of %d: %v", window.Context.GetTick(), err)
		} else {
			// Drift is meaningful only when both reads describe one tick.
			if full.Context.GetTick() == window.Context.GetTick() {
				drift := planningWindowDrift(cells, full.Cells)
				clockEvent(ctx, "facts", "planning_cells_resync", fmt.Sprintf("planning_cells resync drift=%d", drift), "drift", drift, "requested", requested, "since", held.AsOf, "changed", len(window.Cells)+len(window.Fogged), "unchanged", window.Unchanged)
			}
			window, cells = full, full.Cells
		}
	}
	return p.put(window.Region, cells, window.Context.GetTick()), nil
}

func (p *planningWindow) put(region policy.Rectangle, cells []policy.SiteCell, tick int64) facts.Held[observation.PlanningCells] {
	out := facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: region, Cells: cells}, AsOf: tick, Complete: true, Source: "rimgovernor/observations_get_cells", Region: planningRegionRect(region)}
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	return out
}

// mergePlanningCells lays a delta over the held rows: a listed cell
// replaces the held row, a cell the delta names fogged leaves, and every
// other held row stays. Rows come back in row-major order.
func mergePlanningCells(held []policy.SiteCell, delta bridge.PlanningWindow) []policy.SiteCell {
	rows := make(map[domain.Cell]policy.SiteCell, len(held)+len(delta.Cells))
	for _, row := range held {
		rows[row.Cell] = row
	}
	for _, row := range delta.Cells {
		rows[row.Cell] = row
	}
	for _, cell := range delta.Fogged {
		delete(rows, cell)
	}
	out := make([]policy.SiteCell, 0, len(rows))
	for _, row := range rows {
		out = append(out, row)
	}
	bridge.SortSiteCells(out)
	return out
}

// planningWindowDrift counts the cells on which a merged delta and a full
// read of the same tick disagree: a row in one and not the other, or a
// row whose facts differ.
func planningWindowDrift(merged, full []policy.SiteCell) int {
	rows := make(map[domain.Cell]policy.SiteCell, len(merged))
	for _, row := range merged {
		rows[row.Cell] = row
	}
	drift := 0
	for _, row := range full {
		if have, ok := rows[row.Cell]; !ok || have != row {
			drift++
		}
		delete(rows, row.Cell)
	}
	return drift + len(rows)
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
