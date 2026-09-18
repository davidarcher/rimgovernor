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
//  3. run 2: the same state path; run 1's plan resumes and the cancelled
//     cell settles unsuccessful in it; the routine must reissue exactly that
//     cell (and nothing that stands) in a repair plan under the same goal,
//     complete the shell and, under the sleeping family, furnish it.
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
	"strings"
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
	families := flag.String("families", "shelter,sleeping,supply", "RIMGOVERNOR_ROUTINE_FAMILIES for the service (empty = every family): the shell needs the supply family to unforbid starting supplies; the work family is left out because its planner refuses the tribal8 baseline's work priorities and one failing planner cancels the whole step, and the acquisition family is left out because its food planner times out under load, so WoodLog comes from -designate-wood instead")
	buildWait := flag.Duration("build-wait", 30*time.Minute, "wall-clock budget for each construction phase")
	furnishWait := flag.Duration("furnish-wait", 15*time.Minute, "wall-clock budget for a bed to be completed inside the finished hut")
	stall := flag.Duration("stall", na.StallBudget(), "fail a wait once its progress signature (shell lineage stages, shelter goal binding, bed plan stage) has not changed for this long; "+na.StallEnv+" sets the default")
	timeout := flag.Duration("timeout", 100*time.Minute, "overall run timeout")
	nativeTimeout := flag.Duration("native-timeout", 60*time.Second, "serve subprocess's own --timeout (the shelter planner previews the whole shell per cell natively; shorter budgets time out under load; serve caps this at 1m)")
	clockSpeed := flag.String("clock-speed", "Fast", "serve's --clock-speed (Normal, Fast or Superfast; Superfast starves the bridge calls inside the worker's 8s step budget)")
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
	if err := run(ctx, cfg, waits{build: *buildWait, furnish: *furnishWait, stall: *stall}, report); err != nil {
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
	seen      map[domain.PlanID]bool // every shell plan the store has listed
}

// bearing reports a wall the enclosure depends on: one orthogonally between
// an interior cell and open ground. A diagonal ring is two cells thick in
// places and the game keeps the room enclosed without the redundant cell,
// in which case the routine rightly leaves a player's cancel of it alone
// and the repair path is never exercised (runs 3 and 4 of this harness).
func (sh *shell) bearing(c domain.Cell) bool {
	interior := map[domain.Cell]bool{}
	for _, i := range sh.footprint.Interior() {
		interior[i] = true
	}
	in, out := false, false
	for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
		_, onRing := sh.cells[n]
		in = in || interior[n]
		out = out || !interior[n] && !onRing
	}
	return in && out
}

// waits are the run's wall-clock ceilings and its stall budget; each wait
// is also ended by the service exiting on its own.
type waits struct{ build, furnish, stall time.Duration }

func (w waits) wait(ceiling time.Duration, service *liveservice.Service) na.Wait {
	return na.Wait{Ceiling: ceiling, Stall: w.stall, Terminal: service.Exited}
}

func run(ctx context.Context, cfg liveservice.Config, w waits, report na.Report) error {
	prepared, err := liveservice.Prepare(ctx, cfg, report)
	if err != nil {
		return err
	}
	finished := false
	defer func() {
		// A failed run still ends its hold on the game (with the report's
		// stop fields); the success path's Finish also checks the log.
		if !finished {
			_ = prepared.Finish(ctx, report)
		}
	}()
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
	sh, err := waitShell(ctx, st, w.wait(w.build, service))
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
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		// Stop only while a load-bearing wall is ordered but not completed,
		// so the in-game cancel below has one to take.
		if len(l.ordered) < 4 || l.live == nil || l.liveComplete() {
			return false
		}
		for c := range l.undecided {
			if sh.bearing(c) {
				return true
			}
		}
		return false
	}); err != nil {
		service.Stop()
		return fmt.Errorf("run 1 did not reach mid-construction: %w", err)
	}
	report["run1_keepalive"] = service.Stop()
	before, err := shellLineage(ctx, st, sh)
	if err != nil {
		return err
	}
	report["run1_stages"] = stagesOf(ctx, st, before.live.Spec.ID())
	report["run1_shell_plans"] = before.planIDs()
	report["run1_ordered_cells"] = len(before.ordered)
	report["run1_undecided_cells"] = cellList(before.undecided)

	// Player edit while the controller is down: cancel one pending wall.
	cancelled, err := cancelOneWall(ctx, prepared, sh, report)
	if err != nil {
		return err
	}

	// Run 2: a paired restart suspends and resumes the routine goals with
	// their plans open (#65), so run 1's plan carries on and the cancelled
	// wall settles unsuccessful in it. The controller must then close the
	// gap from the ring on record: a further shell plan under the same goal
	// epoch that reissues exactly the cells not standing -- the cancelled
	// wall and any cell run 1 never decided -- and nothing that stands. A
	// world interruption (a letter pause) instead invalidates the goal and
	// the ring is adopted the same way under a fresh goal.
	service, err = prepared.Start(ctx, report)
	if err != nil {
		return err
	}
	var repair store.PlanState
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		for id, plan := range l.byID {
			if before.plans[id] {
				continue
			}
			for _, a := range plan.Spec.Actions() {
				if b, _ := a.Building(); b.Cell() == cancelled {
					repair = plan
					return true
				}
			}
		}
		return false
	}); err != nil {
		service.Stop()
		return fmt.Errorf("run 2 did not reissue the cancelled wall: %w", err)
	}
	reissued := map[domain.Cell]bool{}
	for _, a := range repair.Spec.Actions() {
		b, _ := a.Building()
		if before.ordered[b.Cell()] && b.Cell() != cancelled && !before.undecided[b.Cell()] {
			service.Stop()
			return fmt.Errorf("run 2 reissued %s at %v, which run 1 already ordered", b.Definition(), b.Cell())
		}
		reissued[b.Cell()] = true
	}
	report["run2_shell"] = map[string]any{"plan": string(repair.Spec.ID()), "reissued_cells": len(reissued), "cancelled_reissued": true}
	// Completion: every ring cell completed exactly once across the lineage.
	var final lineage
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		for _, w := range sh.footprint.Walls() {
			if !l.completed[w] {
				return false
			}
		}
		final = l
		return true
	}); err != nil {
		service.Stop()
		return fmt.Errorf("run 2 did not complete the shell: %w", err)
	}
	report["run2_shell_plans"] = final.planIDs()
	report["run2_interruptions"] = len(final.plans) - len(before.plans) - 1
	attempts := map[string]int{}
	for id, plan := range final.byID {
		for i, p := range plan.Progress {
			v := p.View()
			b, _ := plan.Spec.Actions()[i].Building()
			key := fmt.Sprintf("%d,%d", b.Cell().X, b.Cell().Z)
			if v.Stage == domain.Completed {
				if _, twice := attempts[key]; twice {
					service.Stop()
					return fmt.Errorf("shell cell %v completed twice", b.Cell())
				}
				attempts[key] = int(v.Attempt)
			}
			if v.Attempt > 1 || v.Stage != domain.Completed && !(id == before.live.Spec.ID() && reissued[b.Cell()]) {
				service.Stop()
				return fmt.Errorf("shell action %s/%d at %v: attempt %d stage %s, want one completed attempt", id, i, b.Cell(), v.Attempt, v.Stage)
			}
		}
	}
	report["run2_shell_attempts"] = attempts
	old, err := st.LoadGoal(ctx, sh.goalID)
	if err != nil {
		service.Stop()
		return err
	}
	report["run1_goal_status_after_restart"] = string(old.Goal.Status)
	// Furnishing: a bed completed inside the hut.
	bedPlan, bedCells, err := waitBed(ctx, st, sh, w.wait(w.furnish, service))
	report["run2_keepalive"] = service.Stop()
	if err != nil {
		return err
	}
	report["bed"] = map[string]any{"plan": string(bedPlan), "cells": bedCells}

	// Native verification through a fresh bridge session.
	_, h, err := prepared.Open(ctx)
	if err != nil {
		return err
	}
	if err := verifyNative(ctx, h, prepared, sh, bedCells, report); err != nil {
		return err
	}
	finished = true
	return prepared.Finish(ctx, report)
}

// lineage is every shell plan the controller has issued on run 1's ring. A
// world interruption or a restart invalidates the shelter goal and cancels
// its plan; the next review adopts the standing walls and issues a successor
// for the missing cells, so the shell's history is a chain of plans, at most
// one of them live. Cells ordered natively are the union of every dispatch
// attempt; a dispatch whose receipt never arrived, or whose effect was never
// observed, may or may not stand in the game and is undecided.
type lineage struct {
	plans     map[domain.PlanID]bool
	byID      map[domain.PlanID]store.PlanState
	ordered   map[domain.Cell]bool
	undecided map[domain.Cell]bool
	completed map[domain.Cell]bool
	live      *store.PlanState
}

func (l lineage) planIDs() []string {
	out := make([]string, 0, len(l.plans))
	for id := range l.plans {
		out = append(out, string(id))
	}
	sort.Strings(out)
	return out
}

func (l lineage) liveComplete() bool {
	for _, p := range l.live.Progress {
		if p.View().Stage != domain.Completed {
			return false
		}
	}
	return true
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
		if strings.HasPrefix(string(plan.Spec.ID()), "routine-shell-") {
			sh.seen[plan.Spec.ID()] = true
		}
	}
	var plans []store.PlanState
	for id := range sh.seen {
		plan, err := st.LoadPlan(ctx, id)
		if err != nil {
			return lineage{}, err
		}
		plans = append(plans, plan)
	}
	l := lineage{plans: map[domain.PlanID]bool{}, byID: map[domain.PlanID]store.PlanState{}, ordered: map[domain.Cell]bool{}, undecided: map[domain.Cell]bool{}, completed: map[domain.Cell]bool{}}
	for _, plan := range plans {
		cancelled, gap := false, false
		for i, a := range plan.Spec.Actions() {
			b, ok := a.Building()
			if !ok || b.Stuff() != "WoodLog" || (b.Definition() != "Wall" && b.Definition() != "Door") {
				return lineage{}, fmt.Errorf("shell plan %s holds a non-shell action", plan.Spec.ID())
			}
			if _, onRing := sh.cells[b.Cell()]; !onRing {
				return lineage{}, fmt.Errorf("shell plan %s orders %v off run 1's ring (a second shell)", plan.Spec.ID(), b.Cell())
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
			if _, known := v.Receipt.Value(); !known || v.Unresolved || v.Stage != domain.Completed {
				l.undecided[b.Cell()] = true
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

func cellList(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Z < out[j].Z || out[i].Z == out[j].Z && out[i].X < out[j].X })
	return out
}

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
			if binding.Need != policy.EnsureInitialShelter {
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
		return nil, fmt.Errorf("no shell plan admitted for EnsureInitialShelter: %w", err)
	}
	return found, nil
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

// cancelOneWall cancels the pending wall order nearest the door through the
// game's own cancellation path and returns its cell.
func cancelOneWall(ctx context.Context, p *liveservice.Prepared, sh *shell, report na.Report) (domain.Cell, error) {
	_, h, err := p.Open(ctx)
	if err != nil {
		return domain.Cell{}, err
	}
	defer p.Release()
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
	// Prefer a load-bearing wall; run 1 was stopped while one was pending.
	bearing := sh.bearing
	door := sh.footprint.Door()
	sort.Slice(candidates, func(i, j int) bool {
		if bi, bj := bearing(candidates[i].cell), bearing(candidates[j].cell); bi != bj {
			return bi
		}
		di := abs(candidates[i].cell.X-door.X) + abs(candidates[i].cell.Z-door.Z)
		dj := abs(candidates[j].cell.X-door.X) + abs(candidates[j].cell.Z-door.Z)
		if di != dj {
			return di < dj
		}
		return na.AsString(candidates[i].row["thingId"]) < na.AsString(candidates[j].row["thingId"])
	})
	target := candidates[0]
	if !bearing(target.cell) {
		return domain.Cell{}, fmt.Errorf("no pending load-bearing wall to cancel (%d pending, nearest %v)", len(candidates), target.cell)
	}
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
	report["cancelled_wall"] = map[string]any{"cell": target.cell, "pending_walls": len(candidates), "load_bearing": bearing(target.cell), "result": result}
	return target.cell, nil
}

func boolOf(v any) bool { b, _ := na.AsBool(v); return b }
func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// waitBed polls every plan for a completed sleeping placement inside the
// hut: the initial-shelter routine furnishes with sleeping spots, later
// comfort work with beds; native lists both as beds.
func waitBed(ctx context.Context, st *store.Store, sh *shell, w na.Wait) (domain.PlanID, []domain.Cell, error) {
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	var seen string
	// Settled plans retire at the next review and leave the live catalog, so
	// every sleeping plan listed, and every plan bound to a shelter goal, is
	// remembered and reloaded by id.
	known := map[domain.PlanID]bool{}
	var bedPlan domain.PlanID
	var bedCells []domain.Cell
	// The progress signature is the last sleeping placement's stage plus how
	// many sleeping plans are known.
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		if plans, err := st.LoadPlans(ctx, 256); err == nil {
			for _, plan := range plans {
				if strings.HasPrefix(string(plan.Spec.ID()), "routine-sleep-") {
					known[plan.Spec.ID()] = true
				}
			}
		}
		if review, err := st.LoadRoutineReview(ctx); err == nil {
			for _, binding := range review.Goals {
				if binding.Need != policy.EnsureInitialShelter {
					continue
				}
				if goal, err := st.LoadGoal(ctx, binding.Goal); err == nil {
					for _, m := range goal.Methods {
						if strings.HasPrefix(string(m.Plan), "routine-sleep-") {
							known[m.Plan] = true
						}
					}
				}
			}
		}
		for id := range known {
			plan, err := st.LoadPlan(ctx, id)
			if err != nil {
				continue
			}
			actions := plan.Spec.Actions()
			var cells []domain.Cell
			complete := len(actions) > 0
			for i, a := range actions {
				b, ok := a.Building()
				if !ok || b.Definition() != "SleepingSpot" && b.Definition() != "Bed" || i >= len(plan.Progress) {
					complete = false
					break
				}
				v := plan.Progress[i].View()
				seen = fmt.Sprintf("%s at %v stage %s", plan.Spec.ID(), b.Cell(), v.Stage)
				if v.Stage != domain.Completed || !inside[b.Cell()] {
					complete = false
					break
				}
				cells = append(cells, b.Cell())
			}
			if complete {
				bedPlan, bedCells = plan.Spec.ID(), cells
				return "", true, nil
			}
		}
		return na.Signature(len(known), seen), false, nil
	})
	if err != nil {
		return "", nil, fmt.Errorf("no completed sleeping plan inside the hut (last seen: %s): %w", seen, err)
	}
	return bedPlan, bedCells, nil
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
