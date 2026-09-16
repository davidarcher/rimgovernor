// hutaccept is the native acceptance harness for issue #7: a tribal
// (Neolithic) colony's first shelter under the live autopilot is a
// circular/oval hut (or, in constrained terrain, an irregular grown
// footprint) that the game itself reports as one proper, fully roofed room
// whose cells are exactly the planned interior, furnished without blocking
// the entrance aisle, surviving a controller restart and a player edit
// mid-construction without duplicate or missing orders.
//
// Sequence, all against one loaded save:
//  1. run 1: the shelter routine admits the shell plan; the harness
//     classifies its geometry from the durable plan and waits until
//     construction is under way (some walls dispatched, not all completed).
//  2. the service is stopped; through a private bridge session the harness
//     cancels one pending wall blueprint/frame in-game (a player edit while
//     the controller is down).
//  3. run 2: the same state path; the routine must reissue exactly the
//     cancelled cell (attempt 2) and nothing else (attempt 1), then complete
//     the shell and, under the sleeping family, furnish it.
//  4. the service is stopped; the bridge reads observations_list_rooms with
//     cells: the hut is a proper room, cells == interior, open_roof_count 0,
//     the door cell is a doorway room, beds sit inside and off the aisle.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/liveservice"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-hut-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "save name to load (default: the tribal8 baseline)")
	families := flag.String("families", "shelter,sleeping,haul,supply,acquisition", "RIMGOVERNOR_ROUTINE_FAMILIES for the service (empty = every family): the shell needs the supply family to unforbid starting supplies and the acquisition family to fell trees for WoodLog; the work family is left out because its planner refuses the tribal8 baseline's work priorities and one failing planner cancels the whole step")
	buildWait := flag.Duration("build-wait", 12*time.Minute, "wall-clock budget for each construction phase")
	furnishWait := flag.Duration("furnish-wait", 8*time.Minute, "wall-clock budget for a bed to be completed inside the finished hut")
	timeout := flag.Duration("timeout", 45*time.Minute, "overall run timeout")
	nativeTimeout := flag.Duration("native-timeout", 45*time.Second, "serve subprocess's own --timeout")
	clockSpeed := flag.String("clock-speed", "Superfast", "serve's --clock-speed (Normal, Fast or Superfast)")
	designateWood := flag.Int("designate-wood", 400, "before the service starts, designate the nearest wild trees for cutting until their estimated WoodLog yield reaches this amount (0 = leave wood supply entirely to the acquisition family)")
	debug := flag.Bool("debug", false, "trace the service's scheduler steps (RIMGOVERNOR_CLOCK_DEBUG=1) into the service stderr log")
	flag.Parse()
	if *root == "" || !filepath.IsAbs(*root) {
		fmt.Fprintln(os.Stderr, "-root must be an absolute path")
		os.Exit(2)
	}
	if *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *output == "" {
		*output = filepath.Join(*root, "native-hut-acceptance")
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Issue #7: the live autopilot raises a natively enclosed, roofed and furnished oval hut "+
		"(or grown irregular shell) for the tribal "+*save+" colony, reissuing exactly one cancelled wall "+
		"across a controller restart.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	cfg := liveservice.Config{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		Binary: *binary, Save: *save, NativeTimeout: *nativeTimeout, ClockSpeed: *clockSpeed,
		Families: *families, Prefix: "hut", Debug: *debug,
	}
	if *designateWood > 0 {
		cfg.BeforeService = func(ctx context.Context, h *na.Harness, identity, facts map[string]any) error {
			return designateTrees(ctx, h, identity, facts, *designateWood, report)
		}
	}
	if err := run(ctx, cfg, *buildWait, *furnishWait, report); err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

// shell is the shelter plan's geometry as recovered from the durable plan.
type shell struct {
	planID    domain.PlanID
	goalID    domain.GoalID
	footprint domain.RoomFootprint
	cells     map[domain.Cell]int // shell cell -> action index
	shape     string
}

func run(ctx context.Context, cfg liveservice.Config, buildWait, furnishWait time.Duration, report na.Report) error {
	prepared, err := liveservice.Prepare(ctx, cfg, report)
	if err != nil {
		return err
	}
	// Run 1: admission and the first walls.
	service, err := prepared.Start(ctx, report)
	if err != nil {
		return err
	}
	st, err := prepared.OpenStore(ctx)
	if err != nil {
		service.Stop()
		return err
	}
	defer st.Close()
	sh, err := waitShell(ctx, st, buildWait)
	if err != nil {
		service.Stop()
		return err
	}
	report["shell"] = map[string]any{
		"plan": string(sh.planID), "goal": string(sh.goalID), "shape": sh.shape,
		"door": sh.footprint.Door(), "entrance": string(sh.footprint.Entrance()),
		"interior_cells": len(sh.footprint.Interior()), "wall_cells": len(sh.footprint.Walls()),
		"roof_supported": sh.footprint.RoofSupported(), "bounds": sh.footprint.Bounds(),
	}
	if err := waitStages(ctx, st, sh.planID, buildWait, func(s stageCount) bool {
		return s.dispatchedOrLater >= 4 && s.completed < s.total
	}); err != nil {
		service.Stop()
		return fmt.Errorf("run 1 did not reach mid-construction: %w", err)
	}
	report["run1_keepalive"] = service.Stop()
	report["run1_stages"] = stagesOf(ctx, st, sh.planID)

	// Player edit while the controller is down: cancel one pending wall.
	cancelled, err := cancelOneWall(ctx, prepared, sh, report)
	if err != nil {
		return err
	}

	// Run 2: reconcile, reissue only the cancelled cell, finish and furnish.
	service, err = prepared.Start(ctx, report)
	if err != nil {
		return err
	}
	if err := waitStages(ctx, st, sh.planID, buildWait, func(s stageCount) bool { return s.completed == s.total }); err != nil {
		service.Stop()
		return fmt.Errorf("run 2 did not complete the shell: %w", err)
	}
	plan, err := st.LoadPlan(ctx, sh.planID)
	if err != nil {
		service.Stop()
		return err
	}
	attempts := map[string]int{}
	for i, p := range plan.Progress {
		v := p.View()
		b, _ := plan.Spec.Actions()[i].Building()
		attempts[fmt.Sprintf("%d,%d", b.Cell().X, b.Cell().Z)] = int(v.Attempt)
		want := domain.AttemptID(1)
		if b.Cell() == cancelled {
			want = 2
		}
		if v.Attempt != want || v.Stage != domain.Completed {
			service.Stop()
			return fmt.Errorf("action %d at %v: attempt %d stage %s, want attempt %d completed", i, b.Cell(), v.Attempt, v.Stage, want)
		}
	}
	report["shell_attempts"] = attempts
	goal, err := st.LoadGoal(ctx, sh.goalID)
	if err != nil {
		service.Stop()
		return err
	}
	shellPlans := 0
	for _, m := range goal.Methods {
		if p, err := st.LoadPlan(ctx, m.Plan); err == nil && isShellPlan(p) {
			shellPlans++
		}
	}
	if shellPlans != 1 {
		service.Stop()
		return fmt.Errorf("shelter goal holds %d shell plans after restart, want exactly one", shellPlans)
	}
	// Furnishing: a bed completed inside the hut.
	bedPlan, bedCells, err := waitBed(ctx, st, sh, furnishWait)
	report["run2_keepalive"] = service.Stop()
	if err != nil {
		return err
	}
	report["bed"] = map[string]any{"plan": string(bedPlan), "cells": bedCells}

	// Native verification through a fresh bridge session.
	client, h, err := prepared.Open(ctx)
	if err != nil {
		return err
	}
	if err := verifyNative(ctx, h, prepared, sh, bedCells, report); err != nil {
		client.Close()
		return err
	}
	client.Close()
	return prepared.Finish(ctx, report)
}

// designateTrees marks the nearest mature wild trees for cutting through the
// game's own designator until their estimated yield covers target WoodLog.
// The tribal baseline starts with less wood than a hut shell costs; felling
// is the acquisition family's job, which this harness leaves out to keep the
// per-step native budget on the shell, so the wood order is placed here as
// an explicit player edit before the controller starts.
func designateTrees(ctx context.Context, h *na.Harness, identity, facts map[string]any, target int, report na.Report) error {
	center, _ := na.AsMap(facts["center"])
	cx, cz := na.AsNumber(center["x"]), na.AsNumber(center["z"])
	type tree struct {
		row      map[string]any
		distance float64
	}
	var trees []tree
	for _, raw := range na.AsSlice(facts["acquisition"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["resource"]) != "WoodLog" || !boolOf(row["tree"]) || boolOf(row["designated"]) {
			continue
		}
		dx, dz := na.AsNumber(row["x"])-cx, na.AsNumber(row["z"])-cz
		trees = append(trees, tree{row, dx*dx + dz*dz})
	}
	sort.Slice(trees, func(i, j int) bool {
		if trees[i].distance != trees[j].distance {
			return trees[i].distance < trees[j].distance
		}
		return na.AsString(trees[i].row["id"]) < na.AsString(trees[j].row["id"])
	})
	total := 0
	var designated []map[string]any
	for _, t := range trees {
		if total >= target {
			break
		}
		result, err := h.Call(ctx, "designate-tree", "home/acquire_resource", map[string]any{
			"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
			"thingId": na.AsString(t.row["id"]), "resource": "WoodLog",
			"x": int(na.AsNumber(t.row["x"])), "z": int(na.AsNumber(t.row["z"])), "dryRun": false,
		})
		if err != nil {
			return err
		}
		if !boolOf(result["success"]) {
			report["designate_tree_refused"] = result
			continue
		}
		total += int(na.AsNumber(t.row["yield"]))
		designated = append(designated, map[string]any{"id": t.row["id"], "x": t.row["x"], "z": t.row["z"], "yield": t.row["yield"]})
	}
	report["designated_trees"] = map[string]any{"target": target, "estimated_yield": total, "trees": designated}
	if total < target {
		return fmt.Errorf("only %d WoodLog of %d could be designated from %d visible trees", total, target, len(trees))
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
// reconstructs the footprint from its placements.
func waitShell(ctx context.Context, st *store.Store, wait time.Duration) (*shell, error) {
	deadline := time.Now().Add(wait)
	for {
		review, err := st.LoadRoutineReview(ctx)
		if err == nil {
			for _, binding := range review.Goals {
				if binding.Need != policy.EnsureInitialShelter {
					continue
				}
				goal, err := st.LoadGoal(ctx, binding.Goal)
				if err != nil && !errors.Is(err, store.ErrNotFound) {
					return nil, err
				}
				for _, m := range goal.Methods {
					plan, err := st.LoadPlan(ctx, m.Plan)
					if err != nil || plan.Retired || !isShellPlan(plan) {
						continue
					}
					sh, err := classify(plan)
					if err != nil {
						return nil, err
					}
					sh.planID, sh.goalID = m.Plan, binding.Goal
					return sh, nil
				}
			}
		}
		if time.Now().After(deadline) {
			return nil, errors.New("no shell plan admitted for EnsureInitialShelter in time")
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
}

// classify recovers the interior as the cells the wall ring encloses and
// names the shape: one of the hut templates, or an irregular grown shell.
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
	sh := &shell{footprint: footprint, cells: cells, shape: "irregular"}
	b := footprint.Bounds()
	for x := b.X; x < b.X+b.Width; x++ {
		for z := b.Z; z < b.Z+b.Height; z++ {
			for i, t := range policy.HutTemplateShells(domain.Cell{X: x, Z: z}) {
				if domain.SameRoomFootprint(t, footprint) {
					sh.shape = fmt.Sprintf("hut-template-%d", i)
					return sh, nil
				}
			}
		}
	}
	return sh, nil
}

type stageCount struct{ total, completed, dispatchedOrLater int }

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

func waitStages(ctx context.Context, st *store.Store, id domain.PlanID, wait time.Duration, done func(stageCount) bool) error {
	deadline := time.Now().Add(wait)
	var last stageCount
	for {
		plan, err := st.LoadPlan(ctx, id)
		if err == nil {
			var s stageCount
			s.total = len(plan.Spec.Actions())
			for _, p := range plan.Progress {
				switch p.View().Stage {
				case domain.Completed:
					s.completed++
					s.dispatchedOrLater++
				case domain.Dispatched, domain.AwaitingObservation:
					s.dispatchedOrLater++
				case domain.Cancelled:
					return fmt.Errorf("shell action cancelled: %v", p.View())
				}
			}
			last = s
			if done(s) {
				return nil
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("timed out: %+v", last)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
}

// cancelOneWall cancels the pending wall order nearest the door through the
// game's own cancellation path and returns its cell.
func cancelOneWall(ctx context.Context, p *liveservice.Prepared, sh *shell, report na.Report) (domain.Cell, error) {
	client, h, err := p.Open(ctx)
	if err != nil {
		return domain.Cell{}, err
	}
	defer client.Close()
	if _, err := h.Call(ctx, "pause-for-edit", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return domain.Cell{}, err
	}
	b := sh.footprint.Bounds()
	listed, err := h.Call(ctx, "pending-walls", "home/list_buildings", map[string]any{
		"status": "pending", "playerOnly": true, "aggregate": false,
		"x": b.X + b.Width/2, "z": b.Z + b.Height/2, "radius": max(b.Width, b.Height),
	})
	if err != nil {
		return domain.Cell{}, err
	}
	type pending struct {
		row  map[string]any
		cell domain.Cell
	}
	var candidates []pending
	for _, raw := range na.AsSlice(listed["buildings"]) {
		row, _ := na.AsMap(raw)
		pos, _ := na.AsMap(row["position"])
		c := domain.Cell{X: int32(na.AsNumber(pos["x"])), Z: int32(na.AsNumber(pos["z"]))}
		if na.AsString(row["buildDefName"]) != "Wall" {
			continue
		}
		if _, ok := sh.cells[c]; ok {
			candidates = append(candidates, pending{row, c})
		}
	}
	if len(candidates) == 0 {
		return domain.Cell{}, errors.New("no pending wall order found on the shell to cancel")
	}
	door := sh.footprint.Door()
	sort.Slice(candidates, func(i, j int) bool {
		di := abs(candidates[i].cell.X-door.X) + abs(candidates[i].cell.Z-door.Z)
		dj := abs(candidates[j].cell.X-door.X) + abs(candidates[j].cell.Z-door.Z)
		if di != dj {
			return di < dj
		}
		return na.AsString(candidates[i].row["thingId"]) < na.AsString(candidates[j].row["thingId"])
	})
	target := candidates[0]
	args := map[string]any{
		"colonyId": p.Identity["colonyId"], "loadToken": p.Identity["loadToken"], "mapId": p.Identity["mapId"],
		"thing": na.AsString(target.row["thingId"]), "expectedDef": "Wall",
		"x": target.cell.X, "z": target.cell.Z, "expectedStuff": na.AsString(target.row["stuff"]), "dryRun": false,
	}
	result, err := h.Call(ctx, "cancel-wall", "home/cancel_construction", args)
	if err != nil {
		return domain.Cell{}, err
	}
	if ok, _ := na.AsBool(result["success"]); !ok || !boolOf(result["removed"]) {
		return domain.Cell{}, fmt.Errorf("cancel_construction refused: %#v", result)
	}
	report["cancelled_wall"] = map[string]any{"cell": target.cell, "pending_walls": len(candidates), "result": result}
	return target.cell, nil
}

func boolOf(v any) bool { b, _ := na.AsBool(v); return b }
func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// waitBed polls every plan for a completed Bed placement inside the hut.
func waitBed(ctx context.Context, st *store.Store, sh *shell, wait time.Duration) (domain.PlanID, []domain.Cell, error) {
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	deadline := time.Now().Add(wait)
	var seen string
	for {
		plans, err := st.LoadPlans(ctx, 512)
		if err == nil {
			for _, plan := range plans {
				actions := plan.Spec.Actions()
				for i, a := range actions {
					b, ok := a.Building()
					if !ok || b.Definition() != "Bed" || i >= len(plan.Progress) {
						continue
					}
					v := plan.Progress[i].View()
					seen = fmt.Sprintf("%s at %v stage %s", plan.Spec.ID(), b.Cell(), v.Stage)
					if v.Stage != domain.Completed || !inside[b.Cell()] {
						continue
					}
					return plan.Spec.ID(), []domain.Cell{b.Cell()}, nil
				}
			}
		}
		if time.Now().After(deadline) {
			return "", nil, fmt.Errorf("no completed bed inside the hut in time (last seen: %s)", seen)
		}
		select {
		case <-ctx.Done():
			return "", nil, ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
}

func verifyNative(ctx context.Context, h *na.Harness, p *liveservice.Prepared, sh *shell, bedCells []domain.Cell, report na.Report) error {
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
	if !p.SameIdentity(identity) {
		return fmt.Errorf("identity changed during the run: %#v", identity)
	}
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "includeCells": true}
	reply, err := h.Wire(ctx, "rooms-after", "observations_list_rooms", scope)
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	door := sh.footprint.Door()
	var hut, doorway map[string]any
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
