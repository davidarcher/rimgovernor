package layout

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// layout/tidy-furniture (#809): the fixture hut stands empty at Masonry
// tier; the case spawns one wooden bed in the hut's interior corner
// farthest from the door, turned east, which makes the hut a bedroom whose
// derived plan (#800) wants the bed centred on the far wall. The tidy
// family's furniture kind re-sites it through the game's Reinstall (#808).
// The case proves the bed stands on its plan slot with the plan's rotation,
// kept its label (quality), stuff and hit points, the room is still a
// bedroom, and the journal records the piece's tidy (moving until the
// planner closes the batch, then done).
const tidyFurnitureWindow = 12 * time.Minute

func init() {
	cases.Register(cases.Case{
		Name: "layout/tidy-furniture",
		Scope: "Issue #809: on the tribal " + sustained.BaselineSave + " colony at Masonry with an empty fixture hut, a bed spawned off " +
			"its bedroom plan is re-sited by TidyLayout's furniture kind through Reinstall onto the plan's slot and rotation, keeping " +
			"its quality, stuff and hit points; the room stays a bedroom and the tidy is journaled done.",
		Start:       cases.Fixture{Op: gridPrepare, ArgsFrom: startersite.Args, Args: map[string]any{"sleepingSpots": 0}, On: cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{gridAudit, na.LabSpawnTool},
		Keep:        []string{string(na.NeedFood)},
		Serve:       &cases.ServeSpec{Families: []string{"tidy", "defense", "supply"}, NativeTimeout: 30 * time.Second, Prefix: "layout-tidy-furniture"},
		Budget:      20 * time.Minute,
		Reason:      "one serve window: the review proposes the move and a pawn uninstalls, carries and reinstalls the bed",
		Run:         tidyFurniture,
	})
}

// furnitureRead is one building's observed state the move must keep.
type furnitureRead struct {
	label, stuff, rotation string
	hp                     int
	cells                  []domain.Cell
}

func readFurniture(ctx context.Context, s cases.Session, h *na.Harness, label, id string) (furnitureRead, error) {
	reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "ids": []string{id}})
	if err != nil {
		return furnitureRead{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return furnitureRead{}, err
	}
	rows := na.AsSlice(observed["buildings"])
	if len(rows) != 1 {
		return furnitureRead{}, fmt.Errorf("list_buildings %s: want one row, got %#v", id, observed)
	}
	row, _ := na.AsMap(rows[0])
	ref, _ := na.AsMap(row["building"])
	return furnitureRead{label: na.AsString(ref["label"]), stuff: na.AsString(row["stuff"]), rotation: strings.ToLower(na.AsString(row["rotation"])),
		hp: int(na.AsNumber(row["hitPoints"])), cells: cellsOf(row["occupiedCells"])}, nil
}

func tidyFurniture(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	origin, _ := na.AsMap(prepared["hutOrigin"])
	size := int32(na.AsNumber(prepared["hutSize"]))
	if size < 5 {
		return fmt.Errorf("fixture hut size %d: %#v", size, prepared)
	}
	interior := policy.Rectangle{X: int32(na.AsNumber(origin["x"])) + 1, Z: int32(na.AsNumber(origin["z"])) + 1, Width: size - 2, Height: size - 2}
	var bed string
	var before furnitureRead
	var doors []domain.Cell
	var tidies []store.LayoutTidy
	var after furnitureRead
	var role string
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: tidyFurnitureWindow, Goal: policy.TidyLayout, Until: furnitureMoved},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			audit, err := h.Call(ctx, "hut-audit", gridAudit, map[string]any{})
			if err != nil {
				return err
			}
			for _, raw := range na.AsSlice(audit["walls"]) {
				w, _ := na.AsMap(raw)
				if strings.Contains(na.AsString(w["def"]), "door") || strings.Contains(na.AsString(w["def"]), "Door") {
					doors = append(doors, domain.Cell{X: int32(na.AsNumber(w["x"])), Z: int32(na.AsNumber(w["z"]))})
				}
			}
			if len(doors) == 0 {
				return fmt.Errorf("the fixture hut has no door: %#v", audit)
			}
			// The interior corner farthest from the door, the bed turned
			// east along the wall so it blocks no threshold.
			var corner domain.Cell
			best := int32(-1)
			for _, c := range []domain.Cell{{X: interior.X, Z: interior.Z}, {X: interior.X, Z: interior.Z + interior.Height - 1}, {X: interior.X + interior.Width - 2, Z: interior.Z}, {X: interior.X + interior.Width - 2, Z: interior.Z + interior.Height - 1}} {
				d := abs32(c.X-doors[0].X) + abs32(c.Z-doors[0].Z)
				if d > best {
					corner, best = c, d
				}
			}
			// A 1x2 bed turned east occupies its anchor and the cell east
			// of it (GenAdj.OccupiedRect).
			id, spawned, err := na.LabSpawn(ctx, h, na.LabThing{Def: "Bed", Stuff: "WoodLog", X: int(corner.X), Z: int(corner.Z), Rotation: 1})
			if err != nil {
				return err
			}
			report["bed_spawn"] = spawned
			bed = id
			if before, err = readFurniture(ctx, s, h, "bed-before", bed); err != nil {
				return err
			}
			report["bed_before"] = fmt.Sprintf("%+v", before)
			return nil
		},
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
			report["layout_review"] = review.Layout
			if tidies, err = journal.LayoutTidies(ctx, review.Snapshot, review.Tick); err != nil {
				return err
			}
			report["tidies"] = tidies
			if after, err = readFurniture(ctx, s, h, "bed-after", bed); err != nil {
				return err
			}
			report["bed_after"] = fmt.Sprintf("%+v", after)
			reply, err := h.Wire(ctx, "rooms", "observations_list_rooms", map[string]any{
				"scope": map[string]any{"expectedIdentity": s.Identity()}, "includeOutdoors": false, "includeBoundary": false, "includeCells": true, "page": map[string]any{"limit": 256},
			})
			if err != nil {
				return err
			}
			_, observed, err := na.Outcome(reply, "observed")
			if err != nil {
				return err
			}
			for _, raw := range na.AsSlice(observed["rooms"]) {
				row, _ := na.AsMap(raw)
				for _, c := range cellsOf(row["cells"]) {
					if len(after.cells) > 0 && c == after.cells[0] {
						role = na.AsString(row["role"])
					}
				}
			}
			report["room_role"] = role
			return nil
		},
	})
	if err != nil {
		return err
	}
	var done *store.LayoutTidy
	for i := range tidies {
		if tidies[i].Kind == policy.TidyFurniture && tidies[i].Item == bed {
			done = &tidies[i]
		}
	}
	if done == nil || done.Status == store.LayoutTidyAbandoned {
		return fmt.Errorf("the journal records no finished furniture tidy of bed %s: %+v", bed, tidies)
	}
	if after.label != before.label || after.stuff != before.stuff || after.hp != before.hp {
		return fmt.Errorf("the move lost the bed's quality, stuff or hit points: before %+v, after %+v", before, after)
	}
	if role != string(policy.RoomRoleBedroom) {
		return fmt.Errorf("the room holding the moved bed reads role %q, not a bedroom", role)
	}
	// Regularity: the bed stands exactly on its plan slot, with the plan's
	// rotation, in the plan derived from the room as it stands now.
	plan, ok := policy.PlanInterior(policy.InteriorRoom{Role: policy.RoomRoleBedroom, Interior: interior, Doors: doors}, policy.InteriorPieceDef{})
	if !ok {
		return fmt.Errorf("no bedroom plan for interior %+v with doors %v", interior, doors)
	}
	rect := bounding(after.cells)
	for _, p := range plan.Pieces {
		if p.Def == "Bed" && p.Rect == rect && string(p.Rot) == after.rotation {
			report["slot"] = p.Slot
			return nil
		}
	}
	return fmt.Errorf("the moved bed %+v (%s) is on no Bed slot of the plan %+v", rect, after.rotation, plan.Pieces)
}

func abs32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// furnitureMoved reports a tidy plan whose move actions all completed.
func furnitureMoved(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		kinds, _ := plan["kinds"].(map[string]int)
		if strings.HasPrefix(id, "routine-tidy-") && actions > 0 && kinds[string(domain.MoveBuildingAction)] == actions && stages["completed"] == actions {
			return true
		}
	}
	return false
}
