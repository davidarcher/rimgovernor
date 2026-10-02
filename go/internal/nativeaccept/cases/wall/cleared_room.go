package wall

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "wall/cleared-room",
		Scope: "Cleared-ground deconstruction (#1366, epic #1249): a small walled, roofed player room inside the " +
			"cleared ground ends unroofed with its walls and door gone, through a remove_roof intent and " +
			"DECONSTRUCT Designate.cleared_ground whose walls wait (designated, waitingForRoof) until pawns remove the roof; " +
			"a wall of a room straddling the ground's edge is refused. A Go snapshot test cannot see vanilla room " +
			"geometry, the NoRoof area work or the native job guard.",
		Start:       cases.LabStart(),
		RequiredOps: []string{na.LabSpawnTool, "test/roof_cells", "test/deconstruct_target"},
		QuietWorld:  true,
		Budget:      5 * time.Minute,
		Run:         runClearedRoom,
	})
}

type cell struct{ x, z int }

// ring is the wall ring of the square room whose interior spans
// [x0+1,x1-1] x [z0+1,z1-1], and that interior.
func ring(x0, z0, x1, z1 int) (walls, interior []cell) {
	for x := x0; x <= x1; x++ {
		for z := z0; z <= z1; z++ {
			if x == x0 || x == x1 || z == z0 || z == z1 {
				walls = append(walls, cell{x, z})
			} else {
				interior = append(interior, cell{x, z})
			}
		}
	}
	return walls, interior
}

func cellList(cells []cell) string {
	parts := make([]string, len(cells))
	for i, c := range cells {
		parts[i] = fmt.Sprintf("%d,%d", c.x, c.z)
	}
	return strings.Join(parts, ";")
}

func runClearedRoom(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	center, _ := na.AsMap(s.Prepared()["center"])
	cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	// Room A (inside the ground) east of the centre, room B (straddling)
	// west of it; each a 3x3 interior with a door on its centre-facing side.
	aWalls, aInterior := ring(cx+3, cz-2, cx+7, cz+2)
	bWalls, bInterior := ring(cx-7, cz-2, cx-3, cz+2)
	aDoor, bDoor := cell{cx + 3, cz}, cell{cx - 3, cz}
	spawn := func(walls []cell, door cell) ([]string, error) {
		var ids []string
		for _, c := range walls {
			def := "Wall"
			if c == door {
				def = "Door"
			}
			id, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: def, Stuff: "WoodLog", X: c.x, Z: c.z})
			if err != nil {
				return nil, err
			}
			ids = append(ids, id)
		}
		return ids, nil
	}
	aIDs, err := spawn(aWalls, aDoor)
	if err != nil {
		return err
	}
	bIDs, err := spawn(bWalls, bDoor)
	if err != nil {
		return err
	}
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
	if n, err := roofed("roof-a", aInterior, true); err != nil || n != len(aInterior) {
		return fmt.Errorf("roof room A: %d roofed, %v", n, err)
	}
	if n, err := roofed("roof-b", bInterior, true); err != nil || n != len(bInterior) {
		return fmt.Errorf("roof room B: %d roofed, %v", n, err)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "clear-authority", identity); err != nil {
		return err
	}
	rect := func(x, z, w, hgt int) map[string]any {
		return map[string]any{"origin": map[string]any{"x": x, "z": z}, "width": w, "height": hgt}
	}
	// Ground covers all of room A and only the eastern part of room B.
	ground := []any{rect(cx+3, cz-2, 5, 5), rect(cx-5, cz-2, 3, 5)}
	apply := func(key string, intent map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{intent}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	// The straddling room's west wall (x0, cz) is refused.
	straddle, err := apply("straddle", deconstructIntent("straddle", bIDs[2], map[string]any{"clearedGround": ground}))
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(straddle["refused"]); !ok || !strings.Contains(fmt.Sprint(straddle), "outside the cleared ground") {
		return fmt.Errorf("straddling room wall not refused for its room outside the ground: %#v", straddle)
	}
	report["straddle"] = straddle
	// Room A: designate its walls first, which wait on the roof, then the roof.
	for i, id := range aIDs {
		key := fmt.Sprintf("wall-a-%d", i)
		result, err := apply(key, deconstructIntent(key, id, map[string]any{"clearedGround": ground}))
		if err != nil {
			return err
		}
		applied, ok := na.AsMap(result["applied"])
		if !ok {
			return fmt.Errorf("%s: not applied: %#v", key, result)
		}
		if i == 0 {
			if !strings.Contains(fmt.Sprint(applied), "waitingForRoof:true") {
				return fmt.Errorf("%s: roofed room wall does not report waiting for the roof: %#v", key, applied)
			}
			report["first_wall"] = applied
		}
	}
	// Vanilla roofs the walls too (auto-roof), so clearance unroofs the
	// room's whole footprint.
	aFootprint := append(append([]cell{}, aWalls...), aInterior...)
	var roofCells []any
	for _, c := range aFootprint {
		roofCells = append(roofCells, map[string]any{"x": c.x, "z": c.z})
	}
	roof, err := apply("roof-a", map[string]any{"key": "roof-a", "removeRoof": map[string]any{"cells": roofCells}})
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(roof["applied"]); !ok {
		return fmt.Errorf("remove_roof not applied: %#v", roof)
	}
	report["remove_roof"] = roof
	unroofedAt := -1
	for advanced := 0; advanced < 12000; advanced += 300 {
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
		n, err := roofed(fmt.Sprintf("roof-read-%d", advanced), aFootprint, false)
		if err != nil {
			return err
		}
		if n == 0 && unroofedAt < 0 {
			unroofedAt = advanced + 300
		}
		standing := 0
		for _, id := range aIDs {
			row, err := h.Call(ctx, "inspect-"+id, "test/deconstruct_target", map[string]any{"target": id, "action": "inspect"})
			if err != nil {
				return err
			}
			if present, _ := na.AsBool(row["present"]); present {
				standing++
			}
		}
		// The walls hold the roof: none may come down while it stands.
		if n > 0 && standing < len(aIDs) {
			return fmt.Errorf("a wall came down at +%d ticks while %d room cells were still roofed", advanced+300, n)
		}
		if n == 0 && standing == 0 {
			report["unroofed_after_ticks"] = unroofedAt
			report["cleared_after_ticks"] = advanced + 300
			if n, err := roofed("roof-b-kept", bInterior, false); err != nil || n != len(bInterior) {
				return fmt.Errorf("straddling room lost its roof: %d roofed, %v", n, err)
			}
			return nil
		}
	}
	return fmt.Errorf("room A not cleared within 12000 ticks (unroofed at %d)", unroofedAt)
}
