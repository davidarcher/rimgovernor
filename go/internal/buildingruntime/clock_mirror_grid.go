package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// The planning_cells section through mirror_poll (#795): the window travels
// as a CellGrid (bridge.ApplyCellGrid), replacing #357's per-cell
// changed-since read. The held grid is the base a delta's arrays apply over;
// its cells are filed into the mirror's planning_cells table and the store,
// as the list read's rows were.

// applyGridSection lays a planning_cells grid page over the held grid
// (clockFacts.grid) and files the window's cells. A keyframe replaces a
// grid it is newer than; a delta applies only over the grid held at exactly
// its from, since its sparse arrays are relative to that one. It reports
// the arrays applied.
func (f *clockFacts) applyGridSection(scope mirror.Scope, storeScope facts.Scope, section *mp.SectionPage) (int, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	held := f.grid
	if f.gridScope != scope {
		held = nil
	}
	var grid *bridge.CellGrid
	var at mirror.Watermark
	var err error
	var wire *mp.CellGrid
	switch {
	case section.GetKeyframe() != nil:
		at, wire = mirrorMark(section.GetKeyframe().At), section.GetKeyframe().Cells
		if held != nil && !f.gridAt.Before(at) {
			f.fileGrid(scope, storeScope, held, f.gridAt)
			return 0, true
		}
		grid, err = bridge.ApplyCellGrid(nil, true, wire)
	case section.GetDelta() != nil:
		d := section.GetDelta()
		from, to := mirrorMark(d.From), mirrorMark(d.To)
		if held != nil && !f.gridAt.Before(to) {
			f.fileGrid(scope, storeScope, held, f.gridAt)
			return 0, true
		}
		if held == nil || f.gridAt != from {
			return 0, false
		}
		at, wire = to, d.Cells
		grid, err = bridge.ApplyCellGrid(held, false, wire)
	default:
		return 0, false
	}
	if err != nil {
		clockSchedulerLog("mirror poll: planning cell grid refused: %v", err)
		return 0, false
	}
	f.grid, f.gridAt, f.gridScope = grid, at, scope
	f.fileGrid(scope, storeScope, grid, at)
	return bridge.GridArrays(wire), true
}

// fileGrid puts a grid's cells in the mirror (the planning_cells table the
// window's list reads keep too) and the store.
func (f *clockFacts) fileGrid(scope mirror.Scope, storeScope facts.Scope, grid *bridge.CellGrid, at mirror.Watermark) {
	cells := grid.Cells()
	mirror.Put(f.mirror, scope, string(facts.PlanningCells), cellRows(cells), at)
	facts.Put(f.store, storeScope, facts.PlanningCells, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: grid.Rect, Cells: cells}, AsOf: at.Tick, Complete: true, Source: mirrorPollSource, Region: planningRegionRect(grid.Rect)})
}

// pollGrid serves the planning window over region through one immediate
// mirror poll: a delta over the held grid when it is of region, else a
// keyframe. served is false when the poll failed or did not answer the
// section; the caller then keeps the held window or reads it by list.
func (p *planningWindow) pollGrid(ctx context.Context, native MirrorPollNative, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], bool) {
	f := p.facts
	f.mu.Lock()
	epoch := f.epoch
	ask := &mp.SectionAsk{Section: mp.Section_SECTION_PLANNING_CELLS.Enum(), Window: bridge.WireRect(region)}
	if f.grid != nil && f.grid.Rect == region && epoch != nil && f.gridScope == mirrorScopeOf(epoch) {
		ask.Since = &mp.Watermark{Tick: proto.Int64(f.gridAt.Tick), Seq: proto.Uint32(uint32(f.gridAt.Seq))}
	}
	if epoch != nil {
		epoch = proto.Clone(epoch).(*mp.Epoch)
	}
	f.mu.Unlock()
	request := &mp.MirrorPollRequest{Identity: proto.Clone(identity).(*c.Identity), Epoch: epoch, Asks: []*mp.SectionAsk{ask}, ByteBudget: proto.Uint32(bridge.MirrorPollMaxBytes)}
	reply, _, err := native.MirrorPoll(bridge.WithAdmissionClass(ctx, bridge.AdmissionObservation), request)
	if err != nil {
		clockSchedulerLog("mirror poll: planning window read failed: %v", err)
		return facts.Held[observation.PlanningCells]{}, false
	}
	for _, section := range f.applyMirrorPage(reply.GetPage()).served {
		if section != facts.PlanningCells {
			continue
		}
		if held, ok := facts.Get[observation.PlanningCells](p.store, facts.PlanningCells); ok && held.Value.Region == region {
			return held, true
		}
	}
	return facts.Held[observation.PlanningCells]{}, false
}
