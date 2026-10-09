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
		Name: "wall/replace",
		Scope: "In-place wall replacement (#2529): a BuildingIntent with replace_wall over a built steel wall of a roofed, enclosed room " +
			"swaps it for a wood wall through vanilla's replacement blueprint, and on every single tick from placement to completion the cell " +
			"is impassable and the room stays enclosed. A swap that would drop a roof's last holder, a cell with no built wall and a " +
			"non-wall replacement are refused with typed codes; a plain new-ring wall still gets the Standable Frame. " +
			"A Go snapshot cannot prove the cloned Impassable Frame def, the Harmony frame choice, vanilla's wipe-and-spawn step or room regions.",
		Start: cases.LabStart(), RequiredOps: []string{"test/wall_replacement"},
		Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3},
		Run: runWallReplace,
	})
}

func runWallReplace(ctx context.Context, s cases.Session) error {
	h, id, report := s.Harness(), s.Identity(), s.Report()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "wall-replace-authority", id); err != nil {
		return err
	}
	call := func(label, action string, c cell, ticks int) (map[string]any, error) {
		row, err := h.Call(ctx, label, "test/wall_replacement", map[string]any{"action": action, "x": c.x, "z": c.z, "ticks": ticks})
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(row["success"]); !ok {
			return nil, fmt.Errorf("wall_replacement %s refused: %#v", action, row)
		}
		return row, nil
	}
	prepared, err := call("replace-prepare", "prepare", cell{}, 0)
	if err != nil {
		return err
	}
	centre, _ := na.AsMap(prepared["centre"])
	cx, cz := int(na.AsNumber(centre["x"])), int(na.AsNumber(centre["z"]))
	target, pillar, empty := cell{cx + 3, cz}, cell{cx + 20, cz}, cell{cx + 12, cz + 8}
	apply := func(label, def, stuff string, c cell, replace bool) (map[string]any, error) {
		building := map[string]any{"placement": map[string]any{"defName": def, "stuff": stuff, "x": c.x, "z": c.z, "rotation": "ROTATION_NORTH"}}
		if replace {
			building["replaceWall"] = true
		}
		return na.ApplyOne(ctx, h, label, id, label, map[string]any{"building": building})
	}
	refused := func(label, def, stuff string, c cell, code string) error {
		result, err := apply(label, def, stuff, c, true)
		if err != nil {
			return err
		}
		if refusal, ok := na.AsMap(result["refused"]); !ok || na.AsString(refusal["code"]) != code {
			return fmt.Errorf("%s: expected %s, got %#v", label, code, result)
		}
		return nil
	}
	if err = refused("replace-no-wall", "Wall", "WoodLog", empty, "FAILURE_CODE_NO_WALL_TO_REPLACE"); err != nil {
		return err
	}
	if err = refused("replace-not-a-wall", "Door", "WoodLog", target, "FAILURE_CODE_WALL_REPLACEMENT_REFUSED"); err != nil {
		return err
	}
	if err = refused("replace-last-roof-holder", "Wall", "WoodLog", pillar, "FAILURE_CODE_WALL_REPLACEMENT_STRANDS_ROOF"); err != nil {
		return err
	}

	result, err := apply("replace-wall", "Wall", "WoodLog", target, true)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(result["applied"]); !ok {
		return fmt.Errorf("replacement not applied: %#v", result)
	}
	placed, err := call("replace-placed", "audit", target, 0)
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(placed["impassable"]); !ok || placed["builtStuff"] != "Steel" {
		return fmt.Errorf("the old wall must stand while the blueprint waits: %#v", placed)
	}
	report["placed"] = placed
	for elapsed := 0; elapsed < 3000; elapsed += 100 {
		row, err := call("replace-run", "run", target, 100)
		if err != nil {
			return err
		}
		if na.AsNumber(row["violations"]) != 0 {
			return fmt.Errorf("the enclosure opened during the swap: %#v", row)
		}
		if row["builtDef"] == "Wall" && row["builtStuff"] == "WoodLog" {
			report["completed"] = row
			return newRingStaysStandable(ctx, s, call, apply, empty)
		}
	}
	return fmt.Errorf("replacement wall did not finish within 3000 ticks")
}

// newRingStaysStandable places a plain wall on an empty cell: with no built
// wall under it, its Frame is still the Standable one (#2314).
func newRingStaysStandable(ctx context.Context, s cases.Session, call func(string, string, cell, int) (map[string]any, error),
	apply func(string, string, string, cell, bool) (map[string]any, error), site cell) error {
	result, err := apply("ring-wall", "Wall", "WoodLog", site, false)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(result["applied"]); !ok {
		return fmt.Errorf("new-ring wall not applied: %#v", result)
	}
	for elapsed := 0; elapsed < 3000; elapsed += 5 {
		row, err := call("ring-run", "run", site, 5)
		if err != nil {
			return err
		}
		if row["frameDef"] != "" {
			if standable, _ := na.AsBool(row["frameStandable"]); !standable || row["frameDef"] != "Frame_Wall" {
				return fmt.Errorf("a new-ring wall must keep the Standable Frame: %#v", row)
			}
			s.Report()["new_ring"] = row
			return nil
		}
	}
	return fmt.Errorf("new-ring wall never became a frame")
}
