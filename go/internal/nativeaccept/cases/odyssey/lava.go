// Package odyssey holds the Odyssey acceptance cases (epic #1707).
//
// The odyssey/lava-field case (#1719) proves a colony starts beside a lava
// field and builds clear of it: the harm is the game's own burn terrain, the
// colony's layout and field planning read it as a hazard (#1710), and the
// native postcondition is that no field, pasture or building stands on it
// and no colonist stands in it.
package odyssey

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// floorCellsTool lays any terrain def on exact cells (UpkeepFixture.cs).
	floorCellsTool = "test/floor_cells"
	// fieldHour is the ticks the field planner is given after the layout
	// plan is recorded (the hourly layout trigger).
	fieldHour = 2500
	// The hazard patch is the 16x16 block (the op's 256-cell bound) whose
	// south-west corner is patchOffset cells east and patchHalf south of the
	// lab's centre: beside the colonists' start, inside the plan's reach.
	patchSize   = 16
	patchOffset = 5
	patchHalf   = 8
)

func init() {
	cases.Register(cases.Case{
		Name: "odyssey/lava-field",
		Scope: "Issue #1719: on the blank lab with Odyssey, a 16x16 block of the game's own burn terrain (the catalog terrain def " +
			"whose burnDamage or heatPerTick is positive) lies beside the colonists; the served colony plans a layout and fields, and " +
			"natively every patch cell reads as a hazard in the map survey, no growing or pasture zone and no player building stands on " +
			"a hazard cell, and no colonist stands on one. A Go snapshot test cannot cover it: the hazard bit is a native read of the " +
			"terrain now at the cell (NativeOdysseyColony.IsHazard on the live TerrainGrid), and the zones and buildings are the game's " +
			"own state after the controller's writes; the planner's refusal of hazard cells replays offline in " +
			"policy TestZoningKeepsHazardOffCoreFieldPasture. No eruption is fired: no existing test op fires one.",
		Start: cases.Fixture{Op: floorCellsTool, ArgsFrom: patchArgs, On: cases.LabStart()},
		// The Odyssey profile needs its own process.
		Expansions:   []string{"ludeon.rimworld.odyssey"},
		NoKeep:       true,
		NoCheckpoint: true,
		Keep:         []string{string(na.NeedFood)},
		Serve:        &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion, routinefamily.Field}, NativeTimeout: 30 * time.Second, Prefix: "odyssey-lava-field"},
		Budget:       8 * time.Minute,
		Crew:         cases.Crew{Size: 3}, Reason: "one serve until the layout plan is recorded and the field planner has had an in-game hour",
		Run: runLavaField,
	})
}

// patchCells are the hazard patch's cells.
func patchCells() []domain.Cell {
	centre := int32(na.LabMapSize / 2)
	var out []domain.Cell
	for z := centre - patchHalf; z < centre-patchHalf+patchSize; z++ {
		for x := centre + patchOffset; x < centre+patchOffset+patchSize; x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

// patchArgs is the fixture op's arguments: the patch's cells and the burn
// terrain def, read from the definition catalog by the def's own flags.
func patchArgs(ctx context.Context, h *na.Harness) (map[string]any, error) {
	identity, err := na.ReadIdentity(ctx, h, "lava-identity")
	if err != nil {
		return nil, err
	}
	reply, err := h.Wire(ctx, "lava-catalog", "observations_read_definition_catalog", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	var burning []string
	for _, raw := range na.AsSlice(observed["terrainDefs"]) {
		row, _ := na.AsMap(raw)
		if na.AsNumber(row["burnDamage"]) > 0 || na.AsNumber(row["heatPerTick"]) > 0 {
			burning = append(burning, na.AsString(row["defName"]))
		}
	}
	if len(burning) == 0 {
		return nil, fmt.Errorf("the definition catalog holds no terrain with burnDamage or heatPerTick above zero (Odyssey inactive?)")
	}
	sort.Strings(burning)
	var cells []string
	for _, c := range patchCells() {
		cells = append(cells, fmt.Sprintf("%d,%d", c.X, c.Z))
	}
	return map[string]any{"cells": strings.Join(cells, ";"), "def": burning[0]}, nil
}

func runLavaField(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", floorCellsTool, prepared)
	}
	report["fixture"] = prepared
	if err := serveFields(ctx, s, report); err != nil {
		return err
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	facts, survey, err := startersite.Survey(ctx, h)
	if err != nil {
		return err
	}
	hazard := map[domain.Cell]bool{}
	for _, c := range survey.Cells {
		if c.Hazard {
			hazard[c.Cell] = true
		}
	}
	var unread []domain.Cell
	for _, c := range patchCells() {
		if !hazard[c] {
			unread = append(unread, c)
		}
	}
	if len(unread) > 0 {
		return fmt.Errorf("%d of %d laid patch cells do not read as hazard in the map survey, first %v", len(unread), patchSize*patchSize, unread[0])
	}
	var built []domain.Cell
	for _, c := range survey.Cells {
		if c.Hazard && c.Built {
			built = append(built, c.Cell)
		}
	}
	grid, err := h.MapCells(ctx, "lava-cells", s.Identity())
	if err != nil {
		return err
	}
	zoned, onHazard := 0, []domain.Cell{}
	for _, cells := range na.ZoneCells(grid) {
		zoned += len(cells)
		for _, c := range cells {
			if hazard[c] {
				onHazard = append(onHazard, c)
			}
		}
	}
	standing, err := colonistsOnHazard(ctx, h, s.Identity(), hazard)
	if err != nil {
		return err
	}
	report["lava"] = map[string]any{"hazard_cells": len(hazard), "zone_cells": zoned, "zone_cells_on_hazard": len(onHazard),
		"built_on_hazard": len(built), "colonists_on_hazard": standing, "tick": facts.Identity.Tick}
	switch {
	case len(onHazard) > 0:
		return fmt.Errorf("%d zone cells lie on hazard terrain, first %v", len(onHazard), onHazard[0])
	case len(built) > 0:
		return fmt.Errorf("%d player buildings stand on hazard terrain, first %v", len(built), built[0])
	case len(standing) > 0:
		return fmt.Errorf("colonists stand on hazard terrain: %v", standing)
	case zoned == 0:
		return fmt.Errorf("the colony laid no zone by tick %d: the hazard check proved nothing", facts.Identity.Tick)
	}
	return nil
}

// serveFields serves the controller until the journal holds a layout plan
// and the field planner has had an in-game hour, then stops it.
func serveFields(ctx context.Context, s cases.Session, report na.Report) error {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer journal.Close()
	_, err = service.WaitReview(ctx, na.Wait{Stall: na.StallBudget(), Ceiling: 5 * time.Minute}, func(r store.Rounds) bool {
		record, ok, err := journal.LayoutPlan(ctx, r.Snapshot, r.Tick)
		if err != nil || !ok {
			return false
		}
		report["plan"] = map[string]any{"tick": record.Tick, "summary": record.Plan.Summary()}
		return r.Tick >= record.Tick+fieldHour
	})
	if err != nil {
		return fmt.Errorf("wait for a layout plan and its first field hour: %w", err)
	}
	return nil
}

// colonistsOnHazard lists the colonists whose cell is a hazard cell.
func colonistsOnHazard(ctx context.Context, h *na.Harness, identity map[string]any, hazard map[domain.Cell]bool) ([]string, error) {
	reply, err := h.Wire(ctx, "lava-colonists", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	rows := na.AsSlice(observed["pawns"])
	if len(rows) == 0 {
		return nil, fmt.Errorf("no colonist rows to check against the hazard terrain: %v", observed)
	}
	var standing []string
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		at, _ := na.AsMap(pawn["position"])
		cell := domain.Cell{X: int32(na.AsNumber(at["x"])), Z: int32(na.AsNumber(at["z"]))}
		if hazard[cell] {
			standing = append(standing, fmt.Sprintf("%s@%d,%d", na.AsString(pawn["id"]), cell.X, cell.Z))
		}
	}
	return standing, nil
}
