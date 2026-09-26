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
// planner asks for a region the store does not hold (planningWindowCovers);
// a timer or event step
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
	// view is the step bundle's decoded planning window view (#650), nil
	// when none rode or it failed to decode. A read the view serves under
	// the step's validity fills the store from it without a native read.
	view *bridge.PlanningWindowView
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

// planningWindowResync is whether a refresh of a held window is also a
// full read: one was requested, or the cadence is due.
func planningWindowResync(requested bool, refreshes int) bool {
	return requested || refreshes%planningWindowResyncEvery == planningWindowResyncEvery-1
}

func (p *planningWindow) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	held, ok := facts.Get[observation.PlanningCells](p.store, facts.PlanningCells)
	ok = ok && planningWindowCovers(held.Value.Region, region)
	if !planningWindowRead(p.review, ok, ok && p.store.Fresh(facts.PlanningCells, p.tick)) {
		return held, nil
	}
	if out, stale := p.fromView(ctx, identity, region); stale == "" {
		return out, nil
	} else if p.view != nil {
		clockSchedulerLog("planning window view unused (%s): reading natively", stale)
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
	// The held window is refreshed where it is; the step's bundle carries
	// this very delta (#593).
	region = held.Value.Region
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

// fromView serves the window from the step's planning window view: the
// view covers region and its oldest chunk validation is fresh for the
// step under the read validity the step fixed (#624), or under the
// planning tolerance where none is carried. The store takes the view's
// rows as of that oldest validation tick, its actual age, never the step
// tick. The reason it cannot serve is returned instead, empty on success.
//
// A planner that panned past the view (#706) is served the view's rows
// inside its region plus one native read per uncovered strip
// (planningWindowUncovered: at most four, together fewer cells than the
// region), filed as of the oldest of the view and those reads. A region
// the view does not overlap at all is read natively in full.
func (p *planningWindow) fromView(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], string) {
	view := p.view
	if view == nil {
		return facts.Held[observation.PlanningCells]{}, "no view"
	}
	covered := planningWindowCovers(view.Region, region)
	if _, overlaps := planningWindowOverlap(view.Region, region); !covered && !overlaps {
		return facts.Held[observation.PlanningCells]{}, fmt.Sprintf("region %+v does not overlap %+v", view.Region, region)
	}
	validated := domain.Tick(view.Validated())
	if validity, ok := domain.ReadValidityFrom(ctx); ok {
		if stale := validity.Stale(domain.AgeInventory, domain.ReadObservation{Scope: viewScope(view.Context), Tick: validated}); stale != "" {
			return facts.Held[observation.PlanningCells]{}, stale
		}
	} else if factsScope(view.Context) != p.scope || !domain.CoversIn(ctx, domain.AgeInventory, validated, domain.Tick(p.tick)) {
		return facts.Held[observation.PlanningCells]{}, fmt.Sprintf("validated at %d, step is at %d", validated, p.tick)
	}
	if !covered {
		return p.fromPannedView(ctx, identity, view, region, int64(validated))
	}
	// One view fills the store once; a later ask in the step is served held.
	p.view = nil
	clockEvent(ctx, "facts", "planning_window_view", fmt.Sprintf("planning window view served: %d/%d chunks reused, validated %d ticks before the step", view.Reused(), len(view.Chunks), p.tick-int64(validated)),
		"revision", view.Revision, "incarnation", view.Incarnation, "chunks", len(view.Chunks), "reused", view.Reused(), "age", p.tick-int64(validated))
	out := facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: view.Region, Cells: view.Cells}, AsOf: int64(validated), Complete: true, Source: planningWindowViewSource, Region: planningRegionRect(view.Region)}
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	return out, ""
}

// planningWindowViewSource is the provenance a view-filled window carries.
const planningWindowViewSource = "rimgovernor/observations_read_bundle#planning_window_view"

// fromPannedView serves region from the view's overlapping rows and a
// native read of each uncovered strip (#706). A strip read that fails
// leaves the view unused, so the caller's one full read serves instead.
func (p *planningWindow) fromPannedView(ctx context.Context, identity *c.Identity, view *bridge.PlanningWindowView, region policy.Rectangle, validated int64) (facts.Held[observation.PlanningCells], string) {
	overlap, _ := planningWindowOverlap(view.Region, region)
	cells := make([]policy.SiteCell, 0, len(view.Cells))
	for _, row := range view.Cells {
		if planningRectContains(overlap, row.Cell) {
			cells = append(cells, row)
		}
	}
	reused := len(cells)
	asOf, read := validated, 0
	for _, strip := range planningWindowUncovered(region, overlap) {
		window, _, err := p.native.ReadPlanningWindow(ctx, identity, strip, 0)
		if err != nil {
			return facts.Held[observation.PlanningCells]{}, fmt.Sprintf("strip %+v read failed: %v", strip, err)
		}
		cells = append(cells, window.Cells...)
		asOf = min(asOf, window.Context.GetTick())
		read += int(strip.Width * strip.Height)
	}
	bridge.SortSiteCells(cells)
	p.view = nil
	clockEvent(ctx, "facts", "planning_window_view", fmt.Sprintf("planning window view served panned: %d cells from the view, %d read natively, validated %d ticks before the step", reused, read, p.tick-validated),
		"revision", view.Revision, "incarnation", view.Incarnation, "chunks", len(view.Chunks), "reused", view.Reused(), "age", p.tick-validated,
		"panned", true, "view_cells", int(overlap.Width*overlap.Height), "read_cells", read)
	out := facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: region, Cells: cells}, AsOf: asOf, Complete: true, Source: planningWindowViewSource, Region: planningRegionRect(region)}
	facts.Put(p.store, p.scope, facts.PlanningCells, out)
	return out, ""
}

// planningWindowOverlap is the intersection of two regions, and whether
// it holds any cell.
func planningWindowOverlap(a, b policy.Rectangle) (policy.Rectangle, bool) {
	minX, minZ := max(a.X, b.X), max(a.Z, b.Z)
	maxX, maxZ := min(a.X+a.Width, b.X+b.Width), min(a.Z+a.Height, b.Z+b.Height)
	if maxX <= minX || maxZ <= minZ {
		return policy.Rectangle{}, false
	}
	return policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ}, true
}

// planningWindowUncovered tiles the cells of region outside overlap (which
// lies inside region) with at most four strips: whole rows below and
// above it, then the columns left and right of it within its rows. A pure
// Z or X pan leaves one strip, a diagonal pan two.
func planningWindowUncovered(region, overlap policy.Rectangle) []policy.Rectangle {
	var strips []policy.Rectangle
	add := func(r policy.Rectangle) {
		if r.Width > 0 && r.Height > 0 {
			strips = append(strips, r)
		}
	}
	add(policy.Rectangle{X: region.X, Z: region.Z, Width: region.Width, Height: overlap.Z - region.Z})
	add(policy.Rectangle{X: region.X, Z: overlap.Z + overlap.Height, Width: region.Width, Height: region.Z + region.Height - overlap.Z - overlap.Height})
	add(policy.Rectangle{X: region.X, Z: overlap.Z, Width: overlap.X - region.X, Height: overlap.Height})
	add(policy.Rectangle{X: overlap.X + overlap.Width, Z: overlap.Z, Width: region.X + region.Width - overlap.X - overlap.Width, Height: overlap.Height})
	return strips
}

func planningRectContains(r policy.Rectangle, cell domain.Cell) bool {
	return cell.X >= r.X && cell.X < r.X+r.Width && cell.Z >= r.Z && cell.Z < r.Z+r.Height
}

// viewScope is the read scope an observation context names.
func viewScope(context *c.ObservationContext) domain.ReadScope {
	identity := context.GetIdentity()
	return domain.ReadScope{Colony: domain.ColonyID(identity.GetColonyId()), Map: domain.MapID(identity.GetMapId()), Load: domain.LoadID(identity.GetLoadToken()), Native: domain.NativeGeneration(context.GetNativeGeneration())}
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
