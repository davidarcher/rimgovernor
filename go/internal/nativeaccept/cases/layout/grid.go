// Package layout holds the tiered colony layout cases (#603). layout/grid
// runs the field and capacity families on the tribal baseline with
// Stonecutting finished, so the build tier reads Masonry, from a fixture hut:
// the field planner sites growing zones outside the hut.
//
// The fixture leaves a bed deficit and stone blocks of the map's own stone
// beside the hut door, so the capacity planner raises a second ring beside
// the wood hut and the tier style reads back natively (#637): every wall and
// door of that ring, finished or still a blueprint or frame, is built from
// one stone block definition and its door is a stone Door, the Masonry rungs
// of policy's WallStuff and DoorDef. With a layout plan recorded the ring is
// exactly a planned room's walls and its door opens onto a spine hallway
// (#787).
package layout

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"

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
		Scope: "On the tribal " + sustained.BaselineSave + " colony with Stonecutting finished (build tier Masonry) and " +
			"a fixture hut standing, the field planner sites growing zones outside the hut. With a bed deficit and stone " +
			"blocks stocked the capacity planner's ring reads back in the Masonry tier style: stone walls of one block " +
			"definition and a stone Door (#637). With a v2 layout plan recorded the ring is exactly a planned room's walls " +
			"and its door opens onto a spine hallway (#787).",
		Start: cases.Fixture{Op: gridPrepare, ArgsFrom: startersite.Args, Args: map[string]any{"sleepingSpots": bunks, "stoneBlocks": blocks},
			On: cases.Save{Name: sustained.BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{"field", "shelter", "expansion"}, NativeTimeout: 30 * time.Second, Prefix: "layout-grid"},
		Budget: 10 * time.Minute,
		Reason: "one watch over a staged hut: the first fields plan and the styled capacity ring run on every start",
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
	var layout store.LayoutPlanRecord
	var laid bool
	var audit map[string]any
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: window, Extra: []policy.GoalID{policy.MaintainHousing}, Until: layoutPlanned},
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
			if layout, laid, err = journal.LayoutPlan(ctx, review.Snapshot, review.Tick); err != nil {
				return err
			}
			report["layout_plan"] = map[string]any{"recorded": laid, "rooms": len(layout.Plan.Rooms)}
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
	report["room"] = describe(room)
	// The fields: every zone outside the hut.
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
		d := describe(rect)
		d["id"], d["crop"], d["cells"] = zone["id"], zone["crop"], len(cells)
		described = append(described, d)
		if intersects(rect, room) {
			return fmt.Errorf("zone %v %+v lies in the hut %+v", zone["id"], rect, room)
		}
		rects = append(rects, rect)
	}
	report["zones"] = described
	if len(rects) == 0 {
		return fmt.Errorf("no field read back: %v", described)
	}
	// The planner explained the choice.
	f, err := os.Open(filepath.Join(s.Config().Output, "service", "stderr.log"))
	if err != nil {
		return err
	}
	selections, err := farmselect.Parse(f)
	f.Close()
	if err != nil {
		return err
	}
	last, err := farmselect.Check(selections, farmselect.Expectation{Kind: "outdoor", MinCells: 1})
	report["selection"] = last
	if err != nil {
		return err
	}
	if err := styledRing(room, shell, report); err != nil {
		return err
	}
	if laid {
		return plannedRing(layout.Plan, shell, report)
	}
	return nil
}

// plannedRing checks the capacity ring against the v2 layout plan (#787):
// the stone ring is exactly a planned room's walls, its door stands where
// the plan put it, and the door's threshold lies on a spine hallway.
func plannedRing(plan policy.LayoutPlan, shell []shellCell, report na.Report) error {
	ring := map[domain.Cell]string{}
	for _, c := range shell {
		if policy.StoneBlockResource(policy.Resource(c.stuff)) {
			ring[c.cell] = c.def
		}
	}
	for _, room := range plan.Rooms {
		fp, err := room.Footprint()
		if err != nil {
			continue
		}
		walls := fp.Walls()
		match := len(walls) == len(ring)
		for _, w := range walls {
			if _, ok := ring[w]; !ok {
				match = false
				break
			}
		}
		if !match {
			continue
		}
		report["planned_room"] = map[string]any{"role": string(room.Role), "x": room.Interior.X, "z": room.Interior.Z, "width": room.Interior.Width, "height": room.Interior.Height, "door_x": room.Door.X, "door_z": room.Door.Z}
		if def := ring[room.Door]; def == "" || def == "Wall" {
			return fmt.Errorf("the planned %s room's door cell %v holds %q, not a door", room.Role, room.Door, def)
		}
		threshold, half := fp.Threshold(), policy.SpineWidth/2
		for _, s := range plan.Spine {
			if threshold.X >= min(s.From.X, s.To.X)-half && threshold.X <= max(s.From.X, s.To.X)+half &&
				threshold.Z >= min(s.From.Z, s.To.Z)-half && threshold.Z <= max(s.From.Z, s.To.Z)+half {
				return nil
			}
		}
		return fmt.Errorf("the planned %s room's door %v opens onto %v, off every spine hallway %v", room.Role, room.Door, threshold, plan.Spine)
	}
	return fmt.Errorf("the stone ring (%d cells) matches no planned room's walls", len(ring))
}

// styledRing checks the Masonry tier style on the ring the capacity planner
// raised beside the wood hut (#637): every wall and door of it, finished or
// still a blueprint or frame, is built from one stone block definition and its
// door is a stone Door.
func styledRing(hut policy.Rectangle, shell []shellCell, report na.Report) error {
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
		ring = append(ring, c.cell)
	}
	bounds := bounding(ring)
	described := describe(bounds)
	report["styled_room"] = described
	if intersects(bounds, hut) {
		return fmt.Errorf("the stone ring %+v stands in the hut %+v", bounds, hut)
	}
	return nil
}

// layoutPlanned reports a completed fields plan and a shell plan of the
// shelter or capacity planner's own: the styled ring is read back from its
// blueprints and frames, so the watch does not wait on the last wall.
func layoutPlanned(sample map[string]any) bool {
	if !fieldsPlanned(sample) {
		return false
	}
	for _, goal := range []policy.GoalID{policy.MaintainHousing} {
		capacity, _ := sample[string(goal)].(map[string]any)
		if planned(capacity, buildingruntime.IsShellMethod, false) {
			return true
		}
	}
	return false
}

// fieldsPlanned reports a fields plan, active or retired, with every action
// completed.
func fieldsPlanned(sample map[string]any) bool {
	return planned(sample, func(m domain.MethodID) bool { return strings.HasPrefix(string(m), "fields-") }, true)
}

// planned reports a plan of the sampled goal, active or retired, whose
// method matches (#987): with complete set, every one of its actions has
// completed, otherwise it need only hold actions.
func planned(sample map[string]any, match func(domain.MethodID) bool, complete bool) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if !match(domain.MethodID(method)) || actions == 0 {
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

func describe(r policy.Rectangle) map[string]any {
	return map[string]any{"x": r.X, "z": r.Z, "width": r.Width, "height": r.Height}
}

func intersects(a, b policy.Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}
