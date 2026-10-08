package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "wall/cleared-floor",
		Scope: "Planned-ground clearance end to end (#1245, epic #1249): a walled, roofed player room with a stool " +
			"on a laid WoodPlankFloor, all on planned ground, ends with no building, no roof and no constructed floor, " +
			"through the clearance order: the stool, then the walls and door (waiting on remove_roof), then " +
			"remove_floor once the cells are clear. A Go snapshot test cannot see vanilla's deconstruct and " +
			"RemoveFloor jobs, the roof guard or the terrain left behind.",
		Start:       cases.LabStart(),
		RequiredOps: []string{na.LabSpawnTool, "test/roof_cells", "test/floor_cells", "test/deconstruct_target"},
		QuietWorld:  true,
		Budget:      6 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: runClearedFloor,
	})
}

func runClearedFloor(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	center, _ := na.AsMap(s.Prepared()["center"])
	cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	walls, interior := ring(cx+3, cz-2, cx+7, cz+2)
	door, stoolAt := cell{cx + 3, cz}, cell{cx + 5, cz}
	floors := func(label, def string) (int, error) {
		reply, err := h.Call(ctx, label, "test/floor_cells", map[string]any{"cells": cellList(interior), "def": def})
		if err != nil {
			return 0, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return 0, fmt.Errorf("%s: %#v", label, reply)
		}
		return int(na.AsNumber(reply["floored"])), nil
	}
	if n, err := floors("lay-floor", "WoodPlankFloor"); err != nil || n != len(interior) {
		return fmt.Errorf("lay the floor: %d floored, %v", n, err)
	}
	var wallIDs []string
	for _, c := range walls {
		def := "Wall"
		if c == door {
			def = "Door"
		}
		id, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: def, Stuff: "WoodLog", X: c.x, Z: c.z})
		if err != nil {
			return err
		}
		wallIDs = append(wallIDs, id)
	}
	stoolID, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: "Stool", Stuff: "WoodLog", X: stoolAt.x, Z: stoolAt.z})
	if err != nil {
		return err
	}
	// Vanilla roofs the walls too (auto-roof), so clearance unroofs and
	// reads the room's whole footprint; the fixture roofs its interior.
	footprint := append(append([]cell{}, walls...), interior...)
	roofed := func(label string, cells []cell, set bool) (int, error) {
		reply, err := h.Call(ctx, label, "test/roof_cells", map[string]any{"cells": cellList(cells), "set": set})
		if err != nil {
			return 0, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return 0, fmt.Errorf("%s: %#v", label, reply)
		}
		return int(na.AsNumber(reply["roofed"])), nil
	}
	if n, err := roofed("roof", interior, true); err != nil || n != len(interior) {
		return fmt.Errorf("roof the room: %d roofed, %v", n, err)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "clear-authority", identity); err != nil {
		return err
	}
	ground := []any{map[string]any{"origin": map[string]any{"x": cx + 3, "z": cz - 2}, "width": 5, "height": 5}}
	apply := func(key string, intent map[string]any) error {
		intent["key"] = key
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{intent}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		if _, ok := na.AsMap(result["applied"]); !ok {
			return fmt.Errorf("%s: not applied: %#v", key, result)
		}
		return nil
	}
	present := func(id string) (bool, error) {
		row, err := h.Call(ctx, "inspect-"+id, "test/deconstruct_target", map[string]any{"target": id, "action": "inspect"})
		if err != nil {
			return false, err
		}
		p, _ := na.AsBool(row["present"])
		return p, nil
	}
	// Clearance order: furniture, then walls (roof first), then floors.
	if err := apply("stool", deconstructIntent("stool", stoolID, map[string]any{"clearedGround": ground})); err != nil {
		return err
	}
	for i, id := range wallIDs {
		if err := apply(fmt.Sprintf("wall-%d", i), deconstructIntent("", id, map[string]any{"clearedGround": ground})); err != nil {
			return err
		}
	}
	var roofCells []any
	for _, c := range footprint {
		roofCells = append(roofCells, map[string]any{"x": c.x, "z": c.z})
	}
	if err := apply("roof", map[string]any{"removeRoof": map[string]any{"cells": roofCells}}); err != nil {
		return err
	}
	floorsIssued := false
	for advanced := 0; advanced < 15000; advanced += 300 {
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
		tag := fmt.Sprint(advanced)
		roof, err := roofed("roof-read-"+tag, footprint, false)
		if err != nil {
			return err
		}
		standing := 0
		for i, id := range append([]string{stoolID}, wallIDs...) {
			p, err := present(id)
			if err != nil {
				return err
			}
			if p {
				standing++
			} else if i > 0 && roof > 0 {
				return fmt.Errorf("a wall came down at +%d ticks while %d room cells were still roofed", advanced+300, roof)
			}
		}
		if standing == 0 && roof == 0 && !floorsIssued {
			report["buildings_cleared_after_ticks"] = advanced + 300
			for i, c := range interior {
				if err := apply(fmt.Sprintf("floor-%d", i), map[string]any{"removeFloor": map[string]any{
					"cell": map[string]any{"x": c.x, "z": c.z}, "defName": "WoodPlankFloor"}}); err != nil {
					return err
				}
			}
			floorsIssued = true
			continue
		}
		if floorsIssued {
			n, err := floors("floor-read-"+tag, "")
			if err != nil {
				return err
			}
			if n == 0 {
				report["floors_cleared_after_ticks"] = advanced + 300
				return nil
			}
		}
	}
	return fmt.Errorf("room and floor not cleared within 15000 ticks (floors issued: %v)", floorsIssued)
}
