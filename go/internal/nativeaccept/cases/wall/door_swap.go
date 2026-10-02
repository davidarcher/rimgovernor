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
		Name: "wall/door-swap",
		Scope: "Planned-ground door swap (#1245, epic #1249): a player door on a walled, roofed room's ring, " +
			"given DECONSTRUCT Designate.replace_with_wall, ends a Wall of the door's stuff on the same cell while the " +
			"room keeps its roof, built by force-ordered pawns under normal game rules. Reports which path vanilla " +
			"took (wall blueprint over the door in one apply, or deconstruct then place). A Go snapshot test cannot " +
			"see GenConstruct's placement rule, the ordered jobs or the built wall.",
		Start:       cases.LabStart(),
		RequiredOps: []string{na.LabSpawnTool, "test/roof_cells", "test/cell_things"},
		QuietWorld:  true,
		Budget:      5 * time.Minute,
		Run:         runDoorSwap,
	})
}

func runDoorSwap(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	center, _ := na.AsMap(s.Prepared()["center"])
	cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	// A 3x3 roofed room east of the centre with its door on the west side.
	walls, interior := ring(cx+3, cz-2, cx+7, cz+2)
	door := cell{cx + 3, cz}
	doorID := ""
	for _, c := range walls {
		def := "Wall"
		if c == door {
			def = "Door"
		}
		id, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: def, Stuff: "WoodLog", X: c.x, Z: c.z})
		if err != nil {
			return err
		}
		if c == door {
			doorID = id
		}
	}
	roofed := func(label string, set bool) (int, error) {
		reply, err := h.Call(ctx, label, "test/roof_cells", map[string]any{"cells": cellList(interior), "set": set})
		if err != nil {
			return 0, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return 0, fmt.Errorf("%s: %#v", label, reply)
		}
		return int(na.AsNumber(reply["roofed"])), nil
	}
	if n, err := roofed("roof", true); err != nil || n != len(interior) {
		return fmt.Errorf("roof the room: %d roofed, %v", n, err)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "swap-authority", identity); err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "swap", "operations_apply", map[string]any{"identity": identity, "actions": []any{
		deconstructIntent("swap", doorID, map[string]any{"replaceWithWall": true}),
	}})
	if err != nil {
		return err
	}
	results := na.AsSlice(reply["results"])
	if len(results) != 1 {
		return fmt.Errorf("swap: expected one result: %#v", reply)
	}
	result, _ := na.AsMap(results[0])
	applied, ok := na.AsMap(result["applied"])
	if !ok {
		return fmt.Errorf("swap not applied: %#v", result)
	}
	report["swap"] = applied
	// A replacement named at apply is the in-place path: vanilla accepted
	// the wall blueprint over the standing door.
	inPlace := containsKey(applied, "replacementId")
	report["path"] = map[bool]string{true: "blueprint_over_door", false: "deconstruct_then_place"}[inPlace]
	where := fmt.Sprintf("%d,%d", door.x, door.z)
	for advanced := 0; advanced < 12000; advanced += 300 {
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
		if n, err := roofed(fmt.Sprintf("roof-read-%d", advanced), false); err != nil || n != len(interior) {
			return fmt.Errorf("the room lost roof during the swap at +%d ticks: %d of %d roofed, %v", advanced+300, n, len(interior), err)
		}
		things, err := h.Call(ctx, fmt.Sprintf("cell-%d", advanced), "test/cell_things", map[string]any{"cell": where})
		if err != nil {
			return err
		}
		wall, doorStands := false, false
		for _, t := range na.AsSlice(things["things"]) {
			row, _ := na.AsMap(t)
			switch row["def"] {
			case "Wall":
				wall = row["stuff"] == "WoodLog"
			case "Door":
				doorStands = true
			}
		}
		if wall && !doorStands {
			report["swapped_after_ticks"] = advanced + 300
			return nil
		}
	}
	return fmt.Errorf("door at %s not swapped for a WoodLog wall within 12000 ticks", where)
}

// containsKey finds key anywhere in a decoded evidence tree.
func containsKey(v any, key string) bool {
	switch t := v.(type) {
	case map[string]any:
		for k, x := range t {
			if k == key || containsKey(x, key) {
				return true
			}
		}
	case []any:
		for _, x := range t {
			if containsKey(x, key) {
				return true
			}
		}
	}
	return false
}
