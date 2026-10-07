package shelter

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// shelter/retirement (#2076): the whole shelter lifecycle's last act on a real
// game. Everyone sleeps in a built bedroom (the layout grid fixture's
// expansion start), the planned workshop and laboratory stand (staged on the
// plan the controller recorded) and the shelter still holds a research table,
// a sleeping spot, a crafting spot and a butcher spot: ShelterRetirable holds,
// the shelter leaves the plan (#2046), and its footprint, table and spots
// included, is cleared through LayoutPlan.RetiredGround (#2075) until the entry
// drops; the table is packed, and the laboratory installs it from stock.
const (
	retireTableDef = "SimpleResearchBench"
	retireWait     = 35 * time.Minute
)

// retireSpots are the shelter's loose spots, each of which the clearance of
// the retired ground must remove.
var retireSpots = []string{"SleepingSpot", "CraftingSpot", "ButcherSpot"}

func init() {
	cases.Register(cases.Case{
		Name: "shelter/retirement",
		Scope: "Issue #2076: from the tribal " + sustained.BaselineSave + " baseline with every colonist asleep in a built bedroom, the planned " +
			"workshop and laboratory staged standing and a research table, sleeping spot, crafting spot and butcher spot left in the " +
			"shelter hut, the shelter retires: the plan drops it with its footprint as RetiredGround, and the table (packed, then installed " +
			"in a laboratory from stock), spots and hut walls are cleared before the entry leaves the plan. " +
			"A snapshot test cannot cover it: the clearance is pawns packing and deconstructing, and the " +
			"gate reads the native room census and sleeping facts.",
		Start: cases.Fixture{Op: "test/layout_grid_prepare", ArgsFrom: startersite.BedroomArgs,
			Args: map[string]any{"sleepingSpots": 8, "builders": true, "expansion": true},
			On:   cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{"test/layout_grid_prepare", "test/layout_grid_audit", stageRoomsOp, na.LabSpawnTool},
		Keep:        []string{string(na.NeedFood)},
		Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Sleeping, routinefamily.Clearance}, NativeTimeout: 30 * time.Second, Prefix: "shelter-retirement"},
		Budget:      50 * time.Minute,
		Reason:      "three stages on one journal: the plan the controller records, the rooms staged on it, then pawn work (a Reinstall and a deconstruction of the hut) to the retirement's end",
		Run:         retirement,
	})
}

func retirement(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	// 1. The first plan holding the shelter, workshop and a demand-grown
	// laboratory.
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	record, err := waitPlan(ctx, service, planWait, func(p policy.LayoutPlan) bool {
		return len(roomsOf(p, policy.PlannedShelter)) == 1 && len(roomsOf(p, policy.PlannedWorkshop)) > 0 && len(roomsOf(p, policy.PlannedLab)) > 0
	})
	report["keepalive_plan"] = service.Stop()
	if err != nil {
		return fmt.Errorf("wait for a plan with shelter, workshop and laboratory: %w", err)
	}
	plan := record.Plan
	shelter := roomsOf(plan, policy.PlannedShelter)[0]
	labs := roomsOf(plan, policy.PlannedLab)
	report["plan"] = map[string]any{"tick": record.Tick, "shelter": shelter.Interior, "workshops": len(roomsOf(plan, policy.PlannedWorkshop)), "labs": len(labs)}

	// 2. Stage the work rooms and leave a table and spots in the hut.
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	work := append(append(roomsOf(plan, policy.PlannedWorkshop), labs...), shelter)
	staged, err := stageRooms(ctx, h, "stage-work-rooms", work...)
	if err != nil {
		return err
	}
	report["staged_rooms"] = staged
	table, spots, err := furnishShelter(ctx, s, h, prepared, shelter.Interior)
	if err != nil {
		return err
	}
	report["table"], report["spots"] = table.id, spots

	// 3. Retirement, to the entry's end.
	service, err = start(ctx, s, service)
	if err != nil {
		return err
	}
	journal, err := service.Store(ctx)
	if err != nil {
		service.Stop()
		return err
	}
	var ground []policy.Rectangle
	var final policy.LayoutPlan
	var readErr error
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: retireWait}, func(r store.Rounds) bool {
		// A review before the first plan carries no plan identity yet.
		if r.Snapshot.Validate() != nil {
			return false
		}
		rec, ok, err := journal.LayoutPlan(ctx, r.Snapshot, r.Tick)
		if err != nil {
			readErr = err
			return true
		}
		if !ok {
			return false
		}
		final = rec.Plan
		if len(ground) == 0 && len(roomsOf(final, policy.PlannedShelter)) == 0 && len(final.RetiredGround) > 0 {
			ground = append(ground, final.RetiredGround...)
		}
		return len(ground) > 0 && len(final.RetiredGround) == 0
	})
	report["retired_ground"] = ground
	report["keepalive_retire"] = service.Stop()
	if readErr != nil {
		return readErr
	}
	if err != nil {
		return fmt.Errorf("the shelter did not retire and clear (retired ground %v, shelter planned %d, ground left %v): %w",
			ground, len(roomsOf(final, policy.PlannedShelter)), final.RetiredGround, err)
	}

	// 4. The game agrees: the table is not left in the hut (packed, or
	// installed in a laboratory), the spots and the hut's walls are gone.
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	cells, found, err := buildingCells(ctx, h, s.Identity(), "table-after", table.id)
	if err != nil {
		return err
	}
	report["table_after"] = cells
	if found {
		for _, c := range cells {
			if inside(shelter.Interior, c) {
				return fmt.Errorf("research table %s still stands in the retired shelter at %v", table.id, cells)
			}
		}
	}
	for i, id := range spots {
		if _, found, err := buildingCells(ctx, h, s.Identity(), "spot-after", id); err != nil {
			return err
		} else if found {
			return fmt.Errorf("%s %s still stands after the shelter's ground was cleared", retireSpots[i], id)
		}
	}
	audit, err := h.Call(ctx, "hut-audit", "test/layout_grid_audit", map[string]any{})
	if err != nil {
		return err
	}
	var left []domain.Cell
	for _, raw := range na.AsSlice(audit["walls"]) {
		w, _ := na.AsMap(raw)
		c := domain.Cell{X: int32(na.AsNumber(w["x"])), Z: int32(na.AsNumber(w["z"]))}
		for _, g := range ground {
			if inside(g, c) && !keptByRoom(final, c) {
				left = append(left, c)
			}
		}
	}
	report["walls_left_on_retired_ground"] = left
	if len(left) > 0 {
		return fmt.Errorf("%d wall or door cells of the retired shelter still stand: %v", len(left), left)
	}
	return nil
}

// keptByRoom reports whether c is on a planned room's interior or wall ring.
func keptByRoom(plan policy.LayoutPlan, c domain.Cell) bool {
	for _, r := range plan.AllRooms() {
		in := r.Interior
		if inside(policy.Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}, c) {
			return true
		}
	}
	return false
}

// shelterTable is the research table left in the hut.
type shelterTable struct {
	id    string
	cells []domain.Cell
}

// furnishShelter spawns a research table and one of each loose spot inside
// the planned shelter interior (staged with the work rooms; the plan sites it
// wherever it likes, not on the fixture hut), clear of the fixture campfire, and returns the
// table and the spots' ids in retireSpots order.
func furnishShelter(ctx context.Context, s cases.Session, h *na.Harness, prepared map[string]any, planned policy.Rectangle) (shelterTable, []string, error) {
	// The plan's shelter need not overlap the fixture hut: furnish the planned
	// interior the stage raised.
	room := planned
	expansion, _ := na.AsMap(prepared["expansion"])
	var fire domain.Cell
	var y int32
	if _, err := fmt.Sscanf(strings.TrimSpace(na.AsString(expansion["campfire"])), "(%d, %d, %d)", &fire.X, &y, &fire.Z); err != nil {
		return shelterTable{}, nil, fmt.Errorf("fixture campfire cell %q: %w", na.AsString(expansion["campfire"]), err)
	}
	taken := map[domain.Cell]bool{fire: true}
	// The table is 3x2, anchored (SimpleResearchBench, north) at its middle
	// column and bottom row.
	var anchor domain.Cell
	placed := false
	for z := room.Z; z+1 < room.Z+room.Height && !placed; z++ {
		for x := room.X; x+2 < room.X+room.Width && !placed; x++ {
			clear := true
			for dz := int32(0); dz < 2; dz++ {
				for dx := int32(0); dx < 3; dx++ {
					clear = clear && !taken[domain.Cell{X: x + dx, Z: z + dz}]
				}
			}
			if clear {
				anchor, placed = domain.Cell{X: x + 1, Z: z}, true
			}
		}
	}
	if !placed {
		return shelterTable{}, nil, fmt.Errorf("no 3x2 floor for the research table in the planned shelter %+v", planned)
	}
	id, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: retireTableDef, X: int(anchor.X), Z: int(anchor.Z)})
	if err != nil {
		return shelterTable{}, nil, err
	}
	cells, found, err := buildingCells(ctx, h, s.Identity(), "table-before", id)
	if err != nil {
		return shelterTable{}, nil, err
	}
	if !found || len(cells) != 6 {
		return shelterTable{}, nil, fmt.Errorf("research table %s occupies %v, want six cells", id, cells)
	}
	for _, c := range cells {
		taken[c] = true
		if !inside(planned, c) {
			return shelterTable{}, nil, fmt.Errorf("research table cell %v lies outside the planned shelter %+v", c, planned)
		}
	}
	var ids []string
	for _, def := range retireSpots {
		var spawned bool
		for z := room.Z; z < room.Z+room.Height && !spawned; z++ {
			for x := room.X; x < room.X+room.Width && !spawned; x++ {
				c := domain.Cell{X: x, Z: z}
				if taken[c] {
					continue
				}
				spotID, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: def, X: int(x), Z: int(z)})
				if err != nil {
					return shelterTable{}, nil, err
				}
				taken[c], spawned = true, true
				ids = append(ids, spotID)
			}
		}
		if !spawned {
			return shelterTable{}, nil, fmt.Errorf("no free shelter cell for a %s", def)
		}
	}
	return shelterTable{id: id, cells: cells}, ids, nil
}
