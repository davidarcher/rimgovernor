// Package layout holds the tiered colony layout cases (#603). layout/grid
// (#607) runs the field family on the tribal baseline with Stonecutting
// finished, so the build tier reads Masonry, from a fixture hut whose
// south-west corner the controller fixes the colony grid on: the hut ring
// and the field the planner sites both read back natively with their
// corners on grid lines, no field cell in an aisle, and an aisle between
// the two.
package layout

import (
	"context"
	"fmt"
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
	// window is how long the field planner gets to site and enact a field;
	// the watch ends as soon as a fields plan completes.
	window = 6 * time.Minute
)

func init() {
	cases.Register(cases.Case{
		Name: "layout/grid",
		Scope: "Issue #607: on the tribal " + sustained.BaselineSave + " colony with Stonecutting finished (build tier Masonry) and " +
			"a fixture hut standing, the controller fixes the colony grid on the hut's south-west corner and the field planner " +
			"sites its field with an alignment term: the hut ring and a growing zone read back natively with corners on grid " +
			"lines, no zone cell lies in an aisle, and an aisle separates the zone from the hut's module.",
		Start:  cases.Fixture{Op: gridPrepare, Args: map[string]any{}, On: cases.Save{Name: sustained.BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{"field"}, NativeTimeout: 30 * time.Second, Prefix: "layout-grid"},
		Budget: 10 * time.Minute,
		Reason: "one field-family watch over a staged hut: the grid derivation and the first fields plan run on every start",
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
		WatchConfig: sustainedfood.WatchConfig{Watch: window, Until: fieldsPlanned},
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
	// The room: the finished wall ring's bounding box.
	walls := cellsOf(audit["walls"])
	if len(walls) == 0 {
		return fmt.Errorf("no finished player walls read back: %#v", audit)
	}
	room := bounding(walls)
	report["room"] = describe(room, g)
	if !g.OnGridLine(room) {
		return fmt.Errorf("the room's south-west corner %d,%d is %d cells off the grid", room.X, room.Z, g.CornerError(room))
	}
	module := g.Module(domain.Cell{X: room.X, Z: room.Z})
	// The field: every zone off the aisles, and one on a grid intersection
	// outside the room's module, so an aisle lies between them.
	zones := na.AsSlice(audit["zones"])
	if len(zones) == 0 {
		return fmt.Errorf("no growing zone read back after the fields plan: %#v", audit)
	}
	var described []map[string]any
	aligned := false
	for _, row := range zones {
		zone, _ := na.AsMap(row)
		cells := cellsOf(zone["cells"])
		if len(cells) == 0 {
			continue
		}
		rect := bounding(cells)
		d := describe(rect, g)
		d["id"], d["crop"], d["cells"] = zone["id"], zone["crop"], len(cells)
		described = append(described, d)
		for _, c := range cells {
			if u, v := gridOffsets(g, c); u >= policy.ColonyGridModule || v >= policy.ColonyGridModule {
				return fmt.Errorf("zone %v plants aisle cell %d,%d (grid offsets %d,%d)", zone["id"], c.X, c.Z, u, v)
			}
		}
		if g.OnGridLine(rect) && !intersects(rect, module) {
			aligned = true
		}
	}
	report["zones"] = described
	if !aligned {
		return fmt.Errorf("no growing zone sits on a grid intersection outside the room's module %+v: %v", module, described)
	}
	// The planner explained the choice with the alignment term.
	f, err := os.Open(filepath.Join(s.Config().Output, "service", "stderr.log"))
	if err != nil {
		return err
	}
	selections, err := farmselect.Parse(f)
	f.Close()
	if err != nil {
		return err
	}
	last, err := farmselect.Check(selections, farmselect.Expectation{Kind: "outdoor", MinCells: 1, Terms: []string{"alignment"}})
	report["selection"] = last
	return err
}

// fieldsPlanned reports a fields plan, active or retired, with every action
// completed.
func fieldsPlanned(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(id, "routine-fields-") && actions > 0 && stages["completed"] == actions {
			return true
		}
	}
	return false
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

func intersects(a, b policy.Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}
