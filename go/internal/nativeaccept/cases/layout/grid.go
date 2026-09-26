// Package layout holds the tiered colony layout cases (#603). layout/grid
// (#607, #608) runs the field and capacity families on the tribal baseline
// with Stonecutting finished, so the build tier reads Masonry, from a
// fixture hut whose south-west corner the controller fixes the colony grid
// on: the hut ring reads back natively with its corner on a grid line, every
// growing zone the planner sites is a module patch on the grid (a module
// interior, an 11x5 half, or a ladder patch on a sub-cell corner), no
// field cell lies in an aisle, the second field shares a full co-linear
// edge with the first, and the hut and the fields sit in different
// districts of the grid (#609): the hut on the plaza, every field outside
// it.
//
// The fixture leaves a bed deficit and stone blocks of the map's own stone
// beside the hut door, so the capacity planner raises a second ring beside
// the wood hut and the tier style reads back natively (#637): every wall and
// door of that ring, finished or still a blueprint or frame, is built from
// one stone block definition and its door is a stone Door, the Masonry rungs
// of policy's WallStuff and DoorDef.
package layout

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/farmselect"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	gridPrepare = "test/layout_grid_prepare"
	gridAudit   = "test/layout_grid_audit"
	// window is how long the planners get to site and enact a field and a
	// styled ring; the watch ends as soon as a fields plan completes and the
	// capacity planner has a plan of its own.
	window = 6 * time.Minute
	// bunks is the sleeping spots the hut is furnished with: fewer than the
	// eight colonists the baseline houses, so the capacity goal has a
	// deficit to plan a second ring against from the first review.
	bunks = 5
	// blocks is the stone blocks dropped beside the hut door: a module ring
	// is 48 cells, and a stone wall costs five blocks.
	blocks = 400
)

func init() {
	cases.Register(cases.Case{
		Name: "layout/grid",
		Scope: "Issue #607: on the tribal " + sustained.BaselineSave + " colony with Stonecutting finished (build tier Masonry) and " +
			"a fixture hut standing, the controller fixes the colony grid on the hut's south-west corner and the field planner " +
			"sites module fields on the grid: the hut ring reads back natively with its corner on a grid line, every growing " +
			"zone is a module patch on the grid outside the hut's module, no zone cell lies in an aisle, the second field " +
			"shares a full co-linear edge with the first (#608), and the hut and the fields lie in different districts (#609). " +
			"With a bed deficit and stone blocks stocked the capacity planner's ring reads back in the Masonry tier style: " +
			"stone walls of one block definition and a stone Door (#637).",
		Start: cases.Fixture{Op: gridPrepare, ArgsFrom: startersite.Args, Args: map[string]any{"sleepingSpots": bunks, "stoneBlocks": blocks},
			On: cases.Save{Name: sustained.BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{"field", "shelter", "expansion"}, NativeTimeout: 30 * time.Second, Prefix: "layout-grid"},
		Budget: 10 * time.Minute,
		Reason: "one watch over a staged hut: the grid derivation, the first fields plan and the styled capacity ring run on every start",
		Run:    grid,
	})
}

func grid(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	report := s.Report()
	report["fixture"] = prepared
	if finished, _ := na.AsBool(prepared["finished"]); !finished {
		return fmt.Errorf("fixture did not finish Stonecutting: %#v", prepared)
	}
	hutOrigin, _ := na.AsMap(prepared["hutOrigin"])
	origin := domain.Cell{X: int32(na.AsNumber(hutOrigin["x"])), Z: int32(na.AsNumber(hutOrigin["z"]))}
	var record store.ColonyGridRecord
	var audit map[string]any
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: window, Extra: []policy.GoalID{policy.EnsureInitialShelter, policy.EnsureExpansion}, Until: layoutPlanned},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return fmt.Errorf("load routine review: %w", err)
			}
			var ok bool
			if record, ok, err = journal.ColonyGrid(ctx, review.Snapshot, review.Tick); err != nil {
				return err
			}
			report["colony_grid"] = map[string]any{"recorded": ok, "origin_x": record.Grid.Origin.X, "origin_z": record.Grid.Origin.Z, "pitch": record.Grid.Pitch, "source": string(record.Grid.Source)}
			if !ok {
				return fmt.Errorf("no colony grid recorded by the review at tick %d", review.Tick)
			}
			if audit, err = h.Call(ctx, "layout-audit", gridAudit, map[string]any{}); err != nil {
				return err
			}
			report["audit"] = audit
			return nil
		},
	})
	if err != nil {
		return err
	}
	g := record.Grid
	if !g.Valid() || g.Origin != origin || g.Source != policy.ColonyGridFromRoom {
		return fmt.Errorf("grid %+v was not fixed on the fixture hut's corner %v from the largest room", g, origin)
	}
	// The room: the wood fixture hut's finished ring, bounding box.
	shell := append(shellCellsOf(audit["walls"], false), shellCellsOf(audit["planned"], true)...)
	var walls []domain.Cell
	for _, c := range shell {
		if !c.planned && c.stuff == "WoodLog" {
			walls = append(walls, c.cell)
		}
	}
	if len(walls) == 0 {
		return fmt.Errorf("no finished wood player walls read back: %#v", audit)
	}
	room := bounding(walls)
	report["room"] = describe(room, g)
	if !g.OnGridLine(room) {
		return fmt.Errorf("the room's south-west corner %d,%d is %d cells off the grid", room.X, room.Z, g.CornerError(room))
	}
	module := g.Module(domain.Cell{X: room.X, Z: room.Z})
	if d := g.District(domain.Cell{X: room.X, Z: room.Z}); d != policy.DistrictPlaza {
		return fmt.Errorf("the hut's module is in the %s district, not the plaza", d)
	}
	// The fields: every zone off the aisles, each a module patch (#608)
	// outside the room's module, and the second sharing a full co-linear
	// edge with the first: one pitch away along an axis, or across the
	// half-module divider.
	zones := na.AsSlice(audit["zones"])
	if len(zones) == 0 {
		return fmt.Errorf("no growing zone read back after the fields plan: %#v", audit)
	}
	var described []map[string]any
	var rects []policy.Rectangle
	for _, row := range zones {
		zone, _ := na.AsMap(row)
		cells := cellsOf(zone["cells"])
		if len(cells) == 0 {
			continue
		}
		rect := bounding(cells)
		d := describe(rect, g)
		d["id"], d["crop"], d["cells"], d["district"] = zone["id"], zone["crop"], len(cells), string(g.District(domain.Cell{X: rect.X, Z: rect.Z}))
		described = append(described, d)
		for _, c := range cells {
			if u, v := gridOffsets(g, c); u >= policy.ColonyGridModule || v >= policy.ColonyGridModule {
				return fmt.Errorf("zone %v plants aisle cell %d,%d (grid offsets %d,%d)", zone["id"], c.X, c.Z, u, v)
			}
		}
		if !modulePatch(g, rect) {
			return fmt.Errorf("zone %v %+v is not a module patch on the grid: %v", zone["id"], rect, described)
		}
		if intersects(rect, module) {
			return fmt.Errorf("zone %v %+v lies in the room's module %+v", zone["id"], rect, module)
		}
		if d := g.District(domain.Cell{X: rect.X, Z: rect.Z}); d == policy.DistrictPlaza {
			return fmt.Errorf("zone %v %+v shares the plaza with the hut", zone["id"], rect)
		}
		rects = append(rects, rect)
	}
	report["zones"] = described
	if len(rects) < 2 {
		return fmt.Errorf("one field only; the module rows need a second: %v", described)
	}
	if !rowPartners(g, rects[0], rects[1]) {
		return fmt.Errorf("the second field %+v shares no full co-linear edge with the first %+v", rects[1], rects[0])
	}
	// The planner explained the choice with the alignment and row terms.
	f, err := os.Open(filepath.Join(s.Config().Output, "service", "stderr.log"))
	if err != nil {
		return err
	}
	selections, err := farmselect.Parse(f)
	f.Close()
	if err != nil {
		return err
	}
	last, err := farmselect.Check(selections, farmselect.Expectation{Kind: "outdoor", MinCells: 1, Terms: []string{"alignment", "row"}})
	report["selection"] = last
	if err != nil {
		return err
	}
	return styledRing(g, module, shell, report)
}

// styledRing checks the Masonry tier style on the ring the capacity planner
// raised beside the wood hut (#637): every wall and door of it, finished or
// still a blueprint or frame, is built from one stone block definition, its
// door is a stone Door, and no cell of it stands in an aisle.
func styledRing(g policy.ColonyGrid, hut policy.Rectangle, shell []shellCell, report na.Report) error {
	stuffs := map[string]int{}
	var stone []shellCell
	for _, c := range shell {
		if policy.StoneBlockResource(policy.Resource(c.stuff)) {
			stone = append(stone, c)
			stuffs[c.stuff]++
		}
	}
	doors := map[string]int{}
	for _, c := range stone {
		if c.def != "Wall" {
			doors[c.def]++
		}
	}
	report["styled"] = map[string]any{"cells": len(stone), "stuff": stuffs, "doors": doors, "shell_cells": len(shell)}
	if len(stone) == 0 {
		return fmt.Errorf("the capacity planner raised no stone ring at Masonry: %d wall, door, blueprint and frame cells read back, none of stone blocks", len(shell))
	}
	if len(stuffs) != 1 {
		return fmt.Errorf("the stone ring mixes %d block definitions: %v", len(stuffs), stuffs)
	}
	if doors["Door"] == 0 {
		return fmt.Errorf("the stone ring has no stone Door: %v", doors)
	}
	var ring []domain.Cell
	for _, c := range stone {
		if u, v := gridOffsets(g, c.cell); u >= policy.ColonyGridModule || v >= policy.ColonyGridModule {
			return fmt.Errorf("the stone ring builds aisle cell %d,%d (grid offsets %d,%d)", c.cell.X, c.cell.Z, u, v)
		}
		ring = append(ring, c.cell)
	}
	bounds := bounding(ring)
	described := describe(bounds, g)
	described["district"] = string(g.District(domain.Cell{X: bounds.X, Z: bounds.Z}))
	report["styled_room"] = described
	if intersects(bounds, hut) {
		return fmt.Errorf("the stone ring %+v stands in the hut's module %+v", bounds, hut)
	}
	return nil
}

// shellPlanPrefix is the plan id every whole-shell plan a shelter-style
// planner admits carries (buildingruntime).
const shellPlanPrefix = "routine-shell-"

// layoutPlanned reports a completed fields plan and a shell plan of the
// shelter or capacity planner's own: the styled ring is read back from its
// blueprints and frames, so the watch does not wait on the last wall.
func layoutPlanned(sample map[string]any) bool {
	if !fieldsPlanned(sample) {
		return false
	}
	for _, goal := range []policy.GoalID{policy.EnsureInitialShelter, policy.EnsureExpansion} {
		capacity, _ := sample[string(goal)].(map[string]any)
		if planned(capacity, shellPlanPrefix, false) {
			return true
		}
	}
	return false
}

// fieldsPlanned reports a fields plan, active or retired, with every action
// completed.
func fieldsPlanned(sample map[string]any) bool { return planned(sample, "routine-fields-", true) }

// planned reports a plan of the sampled goal, active or retired, whose id
// carries the prefix: with complete set, every one of its actions has
// completed, otherwise it need only hold actions.
func planned(sample map[string]any, prefix string, complete bool) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if !strings.HasPrefix(id, prefix) || actions == 0 {
			continue
		}
		if !complete || stages["completed"] == actions {
			return true
		}
	}
	return false
}

// shellCell is one wall, door, blueprint or frame the audit read back, with
// the stuff it is or becomes.
type shellCell struct {
	cell       domain.Cell
	def, stuff string
	planned    bool
}

func shellCellsOf(raw any, planned bool) []shellCell {
	var out []shellCell
	for _, item := range na.AsSlice(raw) {
		row, _ := na.AsMap(item)
		def, _ := row["def"].(string)
		stuff, _ := row["stuff"].(string)
		out = append(out, shellCell{cell: domain.Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))},
			def: def, stuff: stuff, planned: planned})
	}
	return out
}

func cellsOf(raw any) []domain.Cell {
	var out []domain.Cell
	for _, item := range na.AsSlice(raw) {
		row, _ := na.AsMap(item)
		out = append(out, domain.Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))})
	}
	return out
}

func bounding(cells []domain.Cell) policy.Rectangle {
	minX, minZ, maxX, maxZ := cells[0].X, cells[0].Z, cells[0].X, cells[0].Z
	for _, c := range cells[1:] {
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
	}
	return policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}

func describe(r policy.Rectangle, g policy.ColonyGrid) map[string]any {
	return map[string]any{"x": r.X, "z": r.Z, "width": r.Width, "height": r.Height, "corner_error": g.CornerError(r), "on_grid": g.OnGridLine(r)}
}

// gridOffsets are a cell's offsets within its pitch square along the
// grid's axes, from the module corner: 0..12 module, 13..15 aisle.
func gridOffsets(g policy.ColonyGrid, c domain.Cell) (int32, int32) {
	m := g.Module(c)
	u, v := c.X-m.X, c.Z-m.Z
	if u < 0 {
		u = -u
	}
	if v < 0 {
		v = -v
	}
	return u, v
}

// modulePatch reports a module interior, an 11x5 half module, or a ladder
// patch starting on one of the module's sub-cell corners (#608).
func modulePatch(g policy.ColonyGrid, r policy.Rectangle) bool {
	for _, sub := range g.SubCells(g.Module(domain.Cell{X: r.X, Z: r.Z})) {
		if sub == r || sub.X == r.X && sub.Z == r.Z && r.Width <= 4 && r.Height <= 4 {
			return true
		}
	}
	return false
}

// rowPartners reports two equal patches facing each other with a full
// co-linear edge: one pitch apart along an axis, or a half module's
// divider apart along its short axis.
func rowPartners(g policy.ColonyGrid, a, b policy.Rectangle) bool {
	if a.Width != b.Width || a.Height != b.Height {
		return false
	}
	dx, dz := b.X-a.X, b.Z-a.Z
	abs := func(v int32) int32 {
		if v < 0 {
			return -v
		}
		return v
	}
	divider := policy.ColonyGridSubCell + 1
	return dz == 0 && (abs(dx) == g.Pitch || a.Width == policy.ColonyGridSubCell && abs(dx) == divider) ||
		dx == 0 && (abs(dz) == g.Pitch || a.Height == policy.ColonyGridSubCell && abs(dz) == divider)
}

func intersects(a, b policy.Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}
