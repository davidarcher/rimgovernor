package shelter

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The shell helpers shelter/bunks-first runs on: the durable shell plan's
// geometry and lineage, the service starts, the furnished bed and the
// native room checks. The staged-ring shelter cases are
// snapshot tests since #745 (buildingruntime.TestShelterSitingSnapshots).
// families: the work family is left out because its planner refuses
// the tribal8 baseline's work priorities and one failing planner
// cancels the whole step, and the acquisition family because its food
// planner times out under load.
var families = []routinefamily.Family{routinefamily.Shelter, routinefamily.Sleeping}

const (
	// buildWait is the wall-clock ceiling for each construction phase,
	// furnishWait for a bed completed inside the finished hut.
	buildWait   = 30 * time.Minute
	furnishWait = 15 * time.Minute
	// builtTicks is the game-time budget for native construction to finish
	// once the plan has placed its blueprints.
	builtTicks = 60000
)

// shell is the shelter's planned ring as the recorded layout plan holds it.
type shell struct {
	footprint domain.RoomFootprint
	cells     map[domain.Cell]bool // the ring: every wall cell and the door
	// pattern is the method glob of the ring's build waves
	// ("shelter-shell-<x>-<z>-build-<hash>", plannedRoomMethod).
	pattern string
}

// waits are the run's wall-clock ceilings and its stall budget; each wait
// is also ended by the service exiting on its own.
type waits struct {
	build, furnish, stall time.Duration
}

func (w waits) wait(ceiling time.Duration, service *na.ServiceProcess) na.Wait {
	return na.Wait{Ceiling: ceiling, Stall: w.stall, Terminal: service.Exited}
}

// describe is the shell's geometry for the report.
func (sh *shell) describe() map[string]any {
	return map[string]any{
		"pattern": sh.pattern, "door": sh.footprint.Door(), "entrance": string(sh.footprint.Entrance()),
		"interior_cells": len(sh.footprint.Interior()), "wall_cells": len(sh.footprint.Walls()),
		"bounds": sh.footprint.Bounds(),
	}
}

// start launches the service (previous nil) or restarts it on the same
// durable state, acquires player authority and keeps it granted until Stop.
func start(ctx context.Context, s cases.Session, previous *na.ServiceProcess) (*na.ServiceProcess, error) {
	var proc *na.ServiceProcess
	var err error
	if previous == nil {
		proc, err = s.Serve(ctx, s.Spec())
	} else {
		proc, err = previous.Restart(ctx)
	}
	if err != nil {
		return nil, err
	}
	if _, err := proc.Acquire(); err != nil {
		proc.Stop()
		return nil, err
	}
	proc.KeepAuthority(ctx)
	return proc, nil
}

// lineage is the ring's build waves the controller has issued. A world
// interruption or a restart cancels a live wave and the next review issues a
// successor for the cells still missing, so the ring's history is a chain of
// waves; the union of their completed cells is the ring placed.
type lineage struct {
	plans     int
	ordered   int
	completed map[domain.Cell]bool
}

// shellLineage reads the ring's waves from the plan history, retired ones
// included. A wave ordering anything but a wall or door on the ring is a second
// shell and fails the run.
func shellLineage(ctx context.Context, st *store.Store, sh *shell) (lineage, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, sh.pattern)
	if err != nil {
		return lineage{}, err
	}
	l := lineage{plans: len(plans), completed: map[domain.Cell]bool{}}
	for _, plan := range plans {
		for i, a := range plan.Spec.Actions() {
			b, ok := a.Building()
			if !ok || b.Definition() != "Wall" && b.Definition() != "Door" {
				return lineage{}, fmt.Errorf("ring wave %s holds a non-ring action", plan.Spec.ID())
			}
			if !sh.cells[b.Cell()] {
				return lineage{}, fmt.Errorf("ring wave %s orders %v off the planned ring (a second shell)", plan.Spec.ID(), b.Cell())
			}
			v := plan.Progress[i].View()
			if v.Stage == domain.Completed {
				l.completed[b.Cell()] = true
			}
			if v.Attempt > 0 {
				l.ordered++
			}
		}
	}
	return l, nil
}

// waitLineage polls the ring's waves until done; the progress signature is
// the wave count and the cells placed.
func waitLineage(ctx context.Context, st *store.Store, sh *shell, w na.Wait, done func(lineage) bool) error {
	var last lineage
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		l, err := shellLineage(ctx, st, sh)
		if err != nil {
			return "", false, err
		}
		last = l
		if done(l) {
			return "", true, nil
		}
		return na.Signature(l.plans, l.ordered, len(l.completed)), false, nil
	})
	if err != nil {
		return fmt.Errorf("ring waves=%d ordered=%d placed=%d of %d: %w", last.plans, last.ordered, len(last.completed), len(sh.cells), err)
	}
	return nil
}

// waitShell polls the store until the recorded layout plan holds the shelter
// and reads its ring: the interior and door the planner raises the walls on.
func waitShell(ctx context.Context, st *store.Store, w na.Wait) (*shell, error) {
	var found *shell
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		record, ok, err := st.LayoutPlan(ctx, review.Snapshot, review.Tick)
		if err != nil || !ok {
			return na.Signature("no-plan", err), false, nil
		}
		rooms := roomsOf(record.Plan, policy.PlannedShelter)
		if len(rooms) == 0 {
			return na.Signature("no-shelter", record.Tick), false, nil
		}
		room := rooms[0]
		footprint, err := room.Footprint()
		if err != nil {
			return "", false, fmt.Errorf("the planned shelter %+v is no room footprint: %w", room.Interior, err)
		}
		cells := map[domain.Cell]bool{footprint.Door(): true}
		for _, c := range footprint.Walls() {
			cells[c] = true
		}
		found = &shell{footprint: footprint, cells: cells,
			pattern: fmt.Sprintf("%s-shell-%d-%d-build-*", room.Role, room.Interior.X, room.Interior.Z)}
		return "", true, nil
	})
	if err != nil {
		return nil, fmt.Errorf("no layout plan holds a shelter: %w", err)
	}
	return found, nil
}

// allowSupplies has test/hut_shell_fixture unforbid the starting supplies
// a fresh load drops forbidden, so the shell's WoodLog stock admits its
// plan without the supply family: that family cost one worker dispatch
// (3-4s under peer load) per stack, fifteen of them in run 2 (#193).
func allowSupplies(ctx context.Context, h *na.Harness, label string, report na.Report) error {
	result, err := h.Call(ctx, label, "test/hut_shell_fixture", map[string]any{"action": "allow"})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(result["success"]); !success {
		return fmt.Errorf("hut_shell_fixture allow refused: %#v", result)
	}
	report[strings.ReplaceAll(label, "-", "_")] = result["allowed"]
	return nil
}

func verifyNative(ctx context.Context, h *na.Harness, expected map[string]any, sh *shell, bedCells []domain.Cell, report na.Report) error {
	if _, err := h.Call(ctx, "pause-for-verify", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	if !na.MatchesIdentity(identity, expected) {
		return fmt.Errorf("identity changed during the run: %#v", identity)
	}
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	door := sh.footprint.Door()
	var hut, doorway map[string]any
	readHut := func(label string) error {
		reply, err := h.Wire(ctx, label, "observations_list_rooms", scope)
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		grid, err := h.MapCells(ctx, label+"-cells", identity)
		if err != nil {
			return err
		}
		gridRooms := na.RoomCells(grid)
		hut, doorway = nil, nil
		for _, raw := range na.AsSlice(observed["rooms"]) {
			row, _ := na.AsMap(raw)
			cells := gridRooms[na.AsString(row["gridRoom"])]
			if len(cells) == 1 && cells[0] == door {
				doorway = row
				continue
			}
			if len(cells) != len(inside) {
				continue
			}
			match := true
			for _, c := range cells {
				match = match && inside[c]
			}
			if match {
				hut = row
			}
		}
		if hut == nil {
			return fmt.Errorf("no native room whose cells equal the planned interior (%d cells)", len(inside))
		}
		return nil
	}
	if err := readHut("rooms-after"); err != nil {
		return err
	}
	// Roofing is the game's own work after the ring closes: builders roof an
	// enclosed room over the following hours. The shell completing last (a
	// repaired ring) can leave that in progress when the bed lands, so give
	// it the same four in-game hours the roofing budget allows, stepping
	// the paused game and re-reading the room. Steps stay short: the bridge
	// gives one step ten seconds, and an all-DLC game on a CI runner
	// advanced 474 ticks in that time.
	const roofStep, roofBudget = 200, 10000
	waited := 0
	for na.AsNumber(hut["openRoofCount"]) != 0 && waited < roofBudget {
		if _, err := h.Call(ctx, fmt.Sprintf("roof-step-%d", waited/roofStep), "rimgovernor/step_game_ticks", map[string]any{"ticks": roofStep}); err != nil {
			return err
		}
		waited += roofStep
		if err := readHut(fmt.Sprintf("rooms-after-roof-%d", waited)); err != nil {
			return err
		}
	}
	report["roof_wait_ticks"] = waited
	// Doorway rooms are excluded from the typed census unless outdoors rooms
	// are included; a second census without cells (the outdoors mega-room
	// would otherwise list the whole map) finds the door's one-tile room by
	// its extents.
	if doorway == nil {
		reply, err := h.Wire(ctx, "doorways-after", "observations_list_rooms", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "includeOutdoors": true})
		if err != nil {
			return err
		}
		_, all, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		for _, raw := range na.AsSlice(all["rooms"]) {
			row, _ := na.AsMap(raw)
			extents, _ := na.AsMap(row["extents"])
			lo, _ := na.AsMap(extents["minimum"])
			hi, _ := na.AsMap(extents["maximum"])
			at := func(m map[string]any) domain.Cell {
				return domain.Cell{X: int32(na.AsNumber(m["x"])), Z: int32(na.AsNumber(m["z"]))}
			}
			if boolOf(row["doorway"]) && at(lo) == door && at(hi) == door {
				doorway = row
			}
		}
	}
	summary := map[string]any{
		"id": hut["id"], "role": hut["role"], "properRoom": hut["properRoom"], "outdoors": hut["outdoors"],
		"openRoofCount": hut["openRoofCount"], "cellCount": hut["cellCount"], "beds": len(na.AsSlice(hut["beds"])),
		"pawns": len(na.AsSlice(hut["pawns"])),
	}
	report["native_hut"] = summary
	if !boolOf(hut["properRoom"]) || boolOf(hut["outdoors"]) {
		return fmt.Errorf("hut is not a proper indoor room: %#v", summary)
	}
	if na.AsNumber(hut["openRoofCount"]) != 0 {
		return fmt.Errorf("hut has %v unroofed cells", hut["openRoofCount"])
	}
	if doorway == nil || !boolOf(doorway["doorway"]) {
		return fmt.Errorf("door cell %v is not a native doorway room", door)
	}
	report["native_doorway"] = map[string]any{"id": doorway["id"], "doorway": doorway["doorway"]}
	// Aisle: the interior cell straight inside the door must stay free of
	// beds, and every native bed must lie inside the hut.
	aisle := map[domain.Cell]bool{}
	for _, c := range policy.DoorwayAisles(policy.Bounds{Width: 1 << 30, Height: 1 << 30}, []policy.SiteCell{{Cell: door, Doorway: domain.Known(true)}}) {
		aisle[c] = true
	}
	beds := na.AsSlice(hut["beds"])
	if len(beds) == 0 {
		return errors.New("native hut lists no beds")
	}
	rows, err := h.BuildingRows(ctx, "beds", expected)
	if err != nil {
		return err
	}
	var bedReport []map[string]any
	for _, raw := range beds {
		ref, _ := na.AsMap(raw)
		bed, ok := rows[na.AsString(ref["id"])]
		if !ok {
			return fmt.Errorf("hut bed %v is not in the building table", ref["id"])
		}
		var occupied []domain.Cell
		for _, c := range na.RectCells(bed["occupied"]) {
			occupied = append(occupied, c)
			if !inside[c] {
				return fmt.Errorf("bed cell %v lies outside the hut", c)
			}
			if aisle[c] {
				return fmt.Errorf("bed cell %v blocks the doorway aisle", c)
			}
		}
		building, _ := na.AsMap(bed["building"])
		bedReport = append(bedReport, map[string]any{"id": building["id"], "cells": occupied, "status": bed["status"]})
	}
	report["native_beds"] = bedReport
	report["planned_bed_cells"] = bedCells
	// No second shell: every player wall or door anywhere near this hut
	// stands on its ring, one per cell, and none is still a blueprint or
	// frame.
	b := sh.footprint.Bounds()
	reply, err := h.Wire(ctx, "walls-after", "observations_list_buildings", map[string]any{
		"scope": map[string]any{"expectedIdentity": expected}, "statuses": []any{"all"}, "playerOnly": true,
	})
	if err != nil {
		return err
	}
	_, listed, err := na.Outcome(reply, "observed")
	if err != nil {
		return fmt.Errorf("walls-after: %w", err)
	}
	centerX, centerZ := b.X+b.Width/2, b.Z+b.Height/2
	ring := map[domain.Cell]bool{}
	for _, w := range sh.footprint.Walls() {
		ring[w] = true
	}
	standing := map[domain.Cell]string{}
	for _, raw := range na.AsSlice(listed["buildings"]) {
		row, _ := na.AsMap(raw)
		def := na.AsString(row["buildDefName"])
		if def == "" {
			building, _ := na.AsMap(row["building"])
			def = na.AsString(building["defName"])
		}
		if def != "Wall" && def != "Door" {
			continue
		}
		building, _ := na.AsMap(row["building"])
		pos, _ := na.AsMap(building["position"])
		c := domain.Cell{X: int32(na.AsNumber(pos["x"])), Z: int32(na.AsNumber(pos["z"]))}
		if dx, dz := int(c.X)-int(centerX), int(c.Z)-int(centerZ); dx*dx+dz*dz > 64*64 {
			continue
		}
		if !ring[c] {
			return fmt.Errorf("player %s at %v stands off the hut ring: a second shell was ordered", def, c)
		}
		if na.AsString(row["status"]) != o.BuildingStatus_BUILDING_STATUS_BUILT.String() {
			return fmt.Errorf("%s at %v is still %s", def, c, row["status"])
		}
		if _, dup := standing[c]; dup {
			return fmt.Errorf("two structures at %v", c)
		}
		standing[c] = def
	}
	if len(standing) != len(ring) {
		return fmt.Errorf("%d walls and doors stand on a ring of %d cells", len(standing), len(ring))
	}
	report["native_ring"] = map[string]any{"cells": len(ring), "built": len(standing)}
	return nil
}

func boolOf(v any) bool { b, _ := na.AsBool(v); return b }

// waitBuilt runs the game until a built (not blueprint or frame) Wall or
// Door stands on every shell cell and a built Bed covers every bed cell.
// An intent-mode BuildingAction's Completed stage means its blueprint was
// placed, so construction is proven natively.
func waitBuilt(ctx context.Context, h *na.Harness, expected map[string]any, sh *shell, bedCells []domain.Cell, report na.Report) error {
	scope := map[string]any{"expectedIdentity": expected}
	ticks, err := na.RunUntil(ctx, h, "shell-built", builtTicks, na.Wait{Ceiling: buildWait, Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Wire(ctx, "shell-built", "observations_list_buildings", map[string]any{
			"scope": scope, "defNames": []any{"Wall", "Door", "Bed"}, "statuses": []any{"built"}, "playerOnly": true,
		})
		if err != nil {
			return "", false, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", false, err
		}
		built := map[domain.Cell]string{}
		for _, raw := range na.AsSlice(observed["buildings"]) {
			row, _ := na.AsMap(raw)
			building, _ := na.AsMap(row["building"])
			for _, cell := range na.RectCells(row["occupied"]) {
				built[cell] = na.AsString(building["defName"])
			}
		}
		missing := 0
		for c := range sh.cells {
			if d := built[c]; d != "Wall" && d != "Door" {
				missing++
			}
		}
		for _, c := range bedCells {
			if built[c] != "Bed" {
				missing++
			}
		}
		return fmt.Sprintf("missing=%d", missing), missing == 0, nil
	})
	report["built_wait_ticks"] = ticks
	return err
}
