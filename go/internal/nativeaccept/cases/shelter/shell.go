package shelter

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The shell helpers shelter/bunks-first runs on: the durable shell plan's
// geometry and lineage, the service starts, the furnished bed and the
// native room checks. The staged-ring shelter cases are
// snapshot tests since #745 (buildingruntime.TestShelterSitingSnapshots).
const (
	// families: the work family is left out because its planner refuses
	// the tribal8 baseline's work priorities and one failing planner
	// cancels the whole step, and the acquisition family because its food
	// planner times out under load.
	families = "shelter,sleeping"
	// buildWait is the wall-clock ceiling for each construction phase,
	// furnishWait for a bed completed inside the finished hut.
	buildWait   = 30 * time.Minute
	furnishWait = 15 * time.Minute
)

// shell is the shelter plan's geometry as recovered from the durable plan.
type shell struct {
	planID    domain.PlanID
	goalID    domain.GoalID
	footprint domain.RoomFootprint
	cells     map[domain.Cell]int // shell cell -> action index
	shape     string
	seen      map[domain.PlanID]bool // every shell plan the store has listed
	ignore    map[domain.PlanID]bool // run 0's plan, from the world before the reload
	staged    map[domain.Cell]bool   // ring cells the fixture spawned finished
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
		"plan": string(sh.planID), "goal": string(sh.goalID), "shape": sh.shape,
		"door": sh.footprint.Door(), "entrance": string(sh.footprint.Entrance()),
		"interior_cells": len(sh.footprint.Interior()), "wall_cells": len(sh.footprint.Walls()),
		"roof_supported": sh.footprint.RoofSupported(), "bounds": sh.footprint.Bounds(),
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

// lineage is every shell plan the controller has issued on run 1's ring. A
// world interruption or a restart invalidates the shelter goal and cancels
// its plan; the next review adopts the standing walls and issues a successor
// for the missing cells, so the shell's history is a chain of plans, at most
// one of them live. Cells ordered natively are the union of every dispatch
// attempt; a dispatch whose receipt never arrived, or whose effect was never
// observed, may or may not stand in the game and is undecided; a cell
// whose receipt arrived but whose completion has not is acknowledged.
type lineage struct {
	plans        map[domain.PlanID]bool
	byID         map[domain.PlanID]store.PlanState
	ordered      map[domain.Cell]bool
	undecided    map[domain.Cell]bool
	acknowledged map[domain.Cell]bool
	completed    map[domain.Cell]bool
	live         *store.PlanState
}

// shellLineage reads the lineage from the store. Any shell plan with a cell
// off run 1's ring is a second shell and fails the run.
func shellLineage(ctx context.Context, st *store.Store, sh *shell) (lineage, error) {
	// The catalog lists live plans only; a plan retired by an interruption
	// stays part of the lineage, so every shell plan ever seen is reloaded.
	live, err := st.LoadPlans(ctx, 256)
	if err != nil {
		return lineage{}, err
	}
	for _, plan := range live {
		if buildingruntime.IsShellMethod(plan.Method) {
			sh.seen[plan.Spec.ID()] = true
		}
	}
	var plans []store.PlanState
	for id := range sh.seen {
		if sh.ignore[id] {
			continue
		}
		plan, err := st.LoadPlan(ctx, id)
		if err != nil {
			return lineage{}, err
		}
		plans = append(plans, plan)
	}
	l := lineage{plans: map[domain.PlanID]bool{}, byID: map[domain.PlanID]store.PlanState{}, ordered: map[domain.Cell]bool{}, undecided: map[domain.Cell]bool{}, acknowledged: map[domain.Cell]bool{}, completed: map[domain.Cell]bool{}}
	for _, plan := range plans {
		cancelled, gap := false, false
		for i, a := range plan.Spec.Actions() {
			b, ok := a.Building()
			if !ok || b.Stuff() != "WoodLog" || (b.Definition() != "Wall" && b.Definition() != "Door") {
				return lineage{}, fmt.Errorf("shell plan %s holds a non-shell action", plan.Spec.ID())
			}
			if _, onRing := sh.cells[b.Cell()]; !onRing {
				return lineage{}, fmt.Errorf("shell plan %s orders %v off the sited ring (a second shell)", plan.Spec.ID(), b.Cell())
			}
			if sh.staged[b.Cell()] {
				return lineage{}, fmt.Errorf("shell plan %s orders %v, which stands staged", plan.Spec.ID(), b.Cell())
			}
			v := plan.Progress[i].View()
			if v.Stage == domain.Cancelled {
				cancelled = true
			}
			if v.Stage == domain.Completed {
				l.completed[b.Cell()] = true
			} else if !domain.GoalWorkOpen([]domain.Progress{plan.Progress[i]}) {
				gap = true
			}
			if v.Attempt == 0 {
				continue
			}
			l.ordered[b.Cell()] = true
			_, known := v.Receipt.Value()
			if !known || v.Unresolved || v.Stage != domain.Completed {
				l.undecided[b.Cell()] = true
			}
			if known && v.Stage != domain.Completed {
				l.acknowledged[b.Cell()] = true
			}
		}
		l.plans[plan.Spec.ID()] = true
		l.byID[plan.Spec.ID()] = plan
		// A plan settled with a gap (a cell unsuccessful) is history: the
		// repair that closes it is the live plan.
		if !plan.Retired && !cancelled && !(gap && !domain.GoalWorkOpen(plan.Progress)) {
			if l.live != nil {
				return lineage{}, fmt.Errorf("two live shell plans: %s and %s", l.live.Spec.ID(), plan.Spec.ID())
			}
			p := plan
			l.live = &p
		}
	}
	return l, nil
}

// waitLineage polls the shell lineage until done; the progress signature is
// the live plan and its stage counts.
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
		live := "none"
		if l.live != nil {
			live = fmt.Sprintf("%s %v", l.live.Spec.ID(), stagesOf(ctx, st, l.live.Spec.ID()))
		}
		return na.Signature(len(l.plans), len(l.ordered), live), false, nil
	})
	if err != nil {
		live := "none"
		if last.live != nil {
			live = fmt.Sprintf("%s %+v", last.live.Spec.ID(), stagesOf(ctx, st, last.live.Spec.ID()))
		}
		return fmt.Errorf("plans=%d ordered=%d live=%s: %w", len(last.plans), len(last.ordered), live, err)
	}
	return nil
}

func isShellPlan(p store.PlanState) bool {
	actions := p.Spec.Actions()
	if len(actions) < 9 {
		return false
	}
	for i, a := range actions {
		b, ok := a.Building()
		if !ok || b.Stuff() != "WoodLog" || (i == 0) != (b.Definition() == "Door") || (i > 0 && b.Definition() != "Wall") {
			return false
		}
	}
	return true
}

// waitShell polls the store until the shelter goal binds a shell plan and
// reconstructs the footprint from its placements. The progress signature is
// the shelter goal's binding and how many methods it has tried.
func waitShell(ctx context.Context, st *store.Store, w na.Wait) (*shell, error) {
	var found *shell
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRoutineReview(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.MaintainHousing {
				continue
			}
			goal, err := st.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			for _, m := range goal.Methods {
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil || plan.Retired || !isShellPlan(plan) {
					continue
				}
				sh, err := classify(plan)
				if err != nil {
					return "", false, err
				}
				sh.planID, sh.goalID = m.Plan, binding.Goal
				sh.seen = map[domain.PlanID]bool{m.Plan: true}
				found = sh
				return "", true, nil
			}
			return na.Signature(binding.Goal, len(goal.Methods)), false, nil
		}
		return na.Signature("unbound", len(review.Goals)), false, nil
	})
	if err != nil {
		return nil, fmt.Errorf("no shell plan admitted for MaintainHousing: %w", err)
	}
	return found, nil
}

// classify recovers the interior as the cells the wall ring encloses and
// classify recovers the interior as the cells the wall ring encloses; every
// shell stands on a planned room (#1231).
func classify(plan store.PlanState) (*shell, error) {
	actions := plan.Spec.Actions()
	door, _ := actions[0].Building()
	cells := map[domain.Cell]int{}
	minX, minZ, maxX, maxZ := door.Cell().X, door.Cell().Z, door.Cell().X, door.Cell().Z
	for i, a := range actions {
		b, _ := a.Building()
		if _, dup := cells[b.Cell()]; dup {
			return nil, fmt.Errorf("duplicate shell cell %v", b.Cell())
		}
		cells[b.Cell()] = i
		minX, minZ = min(minX, b.Cell().X), min(minZ, b.Cell().Z)
		maxX, maxZ = max(maxX, b.Cell().X), max(maxZ, b.Cell().Z)
	}
	// Flood the exterior from outside the bounding box; what remains is inside.
	outside := map[domain.Cell]bool{}
	queue := []domain.Cell{{X: minX - 1, Z: minZ - 1}}
	outside[queue[0]] = true
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if n.X < minX-1 || n.X > maxX+1 || n.Z < minZ-1 || n.Z > maxZ+1 || outside[n] {
				continue
			}
			if _, wall := cells[n]; wall {
				continue
			}
			outside[n] = true
			queue = append(queue, n)
		}
	}
	var interior []domain.Cell
	for x := minX; x <= maxX; x++ {
		for z := minZ; z <= maxZ; z++ {
			c := domain.Cell{X: x, Z: z}
			if _, wall := cells[c]; !wall && !outside[c] {
				interior = append(interior, c)
			}
		}
	}
	footprint, err := domain.NewRoomFootprint(interior, door.Cell(), door.Rotation())
	if err != nil {
		return nil, fmt.Errorf("shell placements do not form a room footprint: %w", err)
	}
	if len(footprint.Walls()) != len(cells) {
		return nil, fmt.Errorf("plan places %d shell cells but the footprint needs %d", len(cells), len(footprint.Walls()))
	}
	return &shell{footprint: footprint, cells: cells, shape: "planned"}, nil
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
	if _, err := h.Call(ctx, "pause-for-verify", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
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
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "includeCells": true}
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
		hut, doorway = nil, nil
		for _, raw := range na.AsSlice(observed["rooms"]) {
			row, _ := na.AsMap(raw)
			cells := roomCells(row)
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
	// gives one step ten seconds, and a loaded box advances well under a
	// thousand ticks in that time.
	const roofStep, roofBudget = 500, 10000
	waited := 0
	for na.AsNumber(hut["openRoofCount"]) != 0 && waited < roofBudget {
		if _, err := h.Call(ctx, fmt.Sprintf("roof-step-%d", waited/roofStep), "rimworld/step_game_ticks", map[string]any{"ticks": roofStep}); err != nil {
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
	var bedReport []map[string]any
	for _, raw := range beds {
		bed, _ := na.AsMap(raw)
		var occupied []domain.Cell
		for _, rc := range na.AsSlice(bed["occupiedCells"]) {
			m, _ := na.AsMap(rc)
			c := domain.Cell{X: int32(na.AsNumber(m["x"])), Z: int32(na.AsNumber(m["z"]))}
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
	listed, err := h.Call(ctx, "walls-after", "home/list_buildings", map[string]any{
		"status": "all", "playerOnly": true, "aggregate": false,
		"x": b.X + b.Width/2, "z": b.Z + b.Height/2, "radius": 64,
	})
	if err != nil {
		return err
	}
	ring := map[domain.Cell]bool{}
	for _, w := range sh.footprint.Walls() {
		ring[w] = true
	}
	standing := map[domain.Cell]string{}
	for _, raw := range na.AsSlice(listed["buildings"]) {
		row, _ := na.AsMap(raw)
		def := na.AsString(row["buildDefName"])
		if def == "" {
			def = na.AsString(row["defName"])
		}
		if def != "Wall" && def != "Door" {
			continue
		}
		pos, _ := na.AsMap(row["position"])
		c := domain.Cell{X: int32(na.AsNumber(pos["x"])), Z: int32(na.AsNumber(pos["z"]))}
		if !ring[c] {
			return fmt.Errorf("player %s at %v stands off the hut ring: a second shell was ordered", def, c)
		}
		if na.AsString(row["status"]) != "built" {
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

func roomCells(row map[string]any) []domain.Cell {
	var cells []domain.Cell
	for _, rc := range na.AsSlice(row["cells"]) {
		m, _ := na.AsMap(rc)
		cells = append(cells, domain.Cell{X: int32(na.AsNumber(m["x"])), Z: int32(na.AsNumber(m["z"]))})
	}
	return cells
}

func stagesOf(ctx context.Context, st *store.Store, id domain.PlanID) map[string]int {
	out := map[string]int{}
	plan, err := st.LoadPlan(ctx, id)
	if err != nil {
		return out
	}
	for _, p := range plan.Progress {
		out[string(p.View().Stage)]++
	}
	return out
}

func boolOf(v any) bool { b, _ := na.AsBool(v); return b }
