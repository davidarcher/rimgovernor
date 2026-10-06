package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// PlanningWindowNative is the native read behind the planning window
// (bridge.Client.ReadPlanningWindow); the scheduler requires it.
type PlanningWindowNative interface {
	ReadPlanningWindow(context.Context, *c.Identity, policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error)
}

// planningWindow is the step's refresher for the planning_cells section
// (#356): the scheduler attaches one to each step's context
// (observation.WithPlanningWindow) and every planning colony read in the
// step whose reply carries no cells asks it. Each ask cuts the window from
// the newest snapshot frame's grid (#1345) and files it; a failed read
// serves the held window of the same region.
type planningWindow struct {
	native PlanningWindowNative
	store  *facts.Store
	scope  facts.Scope
	// layout reads the stored layout plan for the step's world (nil
	// before the step knows it); the window covers its extent (#1282).
	layout func(context.Context) (store.LayoutPlanRecord, bool, error)
}

// PlanExtent is the stored layout plan's extent, empty when there is none.
func (p *planningWindow) PlanExtent(ctx context.Context) (policy.Rectangle, error) {
	if p.layout == nil {
		return policy.Rectangle{}, nil
	}
	record, ok, err := p.layout(ctx)
	if err != nil || !ok {
		return policy.Rectangle{}, err
	}
	extent, _ := record.Plan.Extent()
	return extent, nil
}

func (p *planningWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	window, _, err := p.native.ReadPlanningWindow(ctx, identity, region)
	if err != nil {
		if held, ok := facts.Read[observation.PlanningCells](ctx, p.store, facts.PlanningCells); ok && held.Value.Region == region {
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
// planning_cells table, however it was read (a grid or a full
// read), so a recording holds the rows a review's cells are rebuilt from
// (snapshot bindings). An
// unchanged table is not republished.
func (p *planningWindow) file(identity *c.Identity, out facts.Held[observation.PlanningCells]) facts.Held[observation.PlanningCells] {
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	if table, ok := facts.GetTable[domain.Cell, policy.SiteCell](p.store, p.scope, string(facts.PlanningCells)); !ok || table.AsOf != facts.At(out.AsOf) || !sameWindowRows(table.Rows, out.Value.Cells) {
		facts.PutTable(p.store, p.scope, string(facts.PlanningCells), cellRows(out.Value.Cells), facts.At(out.AsOf))
	}
	return out
}

func sameWindowRows(rows map[domain.Cell]policy.SiteCell, cells []policy.SiteCell) bool {
	if len(rows) != len(cells) {
		return false
	}
	for _, cell := range cells {
		if row, ok := rows[cell.Cell]; !ok || !row.Equal(cell) {
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

// standaloneWindow gives a planning read outside a scheduler step (a
// planner stepped on its own) a window read straight from source, when
// source serves one and ctx carries no step refresher.
func standaloneWindow(ctx context.Context, source any) context.Context {
	native, ok := source.(PlanningWindowNative)
	if !ok || observation.PlanningWindowFrom(ctx) != nil {
		return ctx
	}
	return observation.WithPlanningWindow(ctx, directWindow{native})
}

// directWindow reads the planning window natively on every ask.
type directWindow struct{ native PlanningWindowNative }

func (d directWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	window, _, err := d.native.ReadPlanningWindow(ctx, identity, region)
	if err != nil {
		return facts.Held[observation.PlanningCells]{}, err
	}
	return facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: window.Region, Cells: window.Cells}, AsOf: window.Context.GetTick(), Complete: true, Source: "rimgovernor/observations_get_cells", Region: planningRegionRect(window.Region)}, nil
}
