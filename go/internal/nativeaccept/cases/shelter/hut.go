package shelter

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The hut case is the native acceptance for issue #7: a tribal
// (Neolithic) colony's first shelter under the live autopilot is a
// circular/oval hut (or, in constrained terrain, an irregular grown
// footprint) that the game itself reports as one proper, fully roofed room
// whose cells are exactly the planned interior, furnished without blocking
// the entrance aisle, surviving a controller restart and a player edit
// mid-construction without duplicate or missing orders.
//
// Sequence, all against the loaded tribal8 baseline (staged, #174):
//  1. run 0: the shelter routine admits the shell plan; the case
//     classifies its geometry from the durable plan and stops the service
//     at once. The ring is now on the journal.
//  2. the save is reloaded over the running game (a world change: the
//     routine goals of run 0 are invalidated, the journal stays) and
//     test/hut_shell_fixture spawns the ring finished but for a few
//     load-bearing walls near the door, with wood beside it.
//  3. run 1: the routine adopts the standing ring from its journal under a
//     fresh goal and orders exactly the missing cells; the case stops at
//     the first the game acknowledges and has not completed.
//  4. the service is stopped; through a private bridge session the case
//     cancels one pending wall in-game (a player edit while the controller
//     is down).
//  5. run 2: the same state path; run 1's plan resumes and the cancelled
//     cell settles unsuccessful in it; the routine must reissue exactly that
//     cell (and nothing that stands) in a repair plan under the same goal,
//     complete the shell and, under the sleeping family, furnish it.
//  6. the service is stopped; the bridge reads observations_list_rooms with
//     cells: the hut is a proper room, cells == interior, open_roof_count 0,
//     the door cell is a doorway room, beds sit inside and off the aisle.
const (
	// families: the work family is left out because its planner refuses
	// the tribal8 baseline's work priorities and one failing planner
	// cancels the whole step, and the acquisition family because its food
	// planner times out under load, so WoodLog comes from the staged ring's
	// spawnWood (unforbidden) instead. The supply family used to unforbid
	// the starting supplies for the grown shell; with the ring staged it
	// only cost the run one worker dispatch (3-4s under peer load) per
	// starting stack, fifteen of them in run 2 (#193).
	families = "shelter,sleeping"
	// stagePending is how many load-bearing walls nearest the door the
	// fixture leaves for the builders, the one the case cancels among them;
	// spawnWood the WoodLog it drops beside the staged door for them.
	stagePending = 3
	spawnWood    = 150
	// orderPoll is run 1's store poll while it waits for the first
	// acknowledged wall order: every poll interval is game time the
	// builders spend on that wall before the service stops.
	orderPoll = 250 * time.Millisecond
	// buildWait is the wall-clock ceiling for each construction phase,
	// furnishWait for a bed completed inside the finished hut.
	buildWait   = 30 * time.Minute
	furnishWait = 15 * time.Minute
)

// corridorFixture raises granite rows every sixth cell around the
// colonists (one walkway each) so no hut template or 9x9 rectangle fits
// and the shelter routine's only shell is a grown one (needs the mod built
// with -Fixture CorridorTerrainFixture).
const corridorFixture = "test/corridor_terrain_fixture"

func init() {
	// The shelter planner previews the whole shell per cell natively;
	// shorter native budgets time out under load (serve caps this at 1m).
	// RIMGOVERNOR_ACCEPT_CLOCK_SPEED sets the clock (#128); Superfast is the
	// shared default.
	spec := func(prefix string) *cases.ServeSpec {
		return &cases.ServeSpec{Families: []string{families}, NativeTimeout: 60 * time.Second, Prefix: prefix}
	}
	cases.Register(cases.Case{
		Name: "shelter/hut",
		Scope: "Issue #7: the live autopilot raises a natively enclosed, roofed and furnished oval hut " +
			"(or grown irregular shell) for the tribal " + sustained.BaselineSave + " colony, reissuing exactly one cancelled wall " +
			"across a controller restart.",
		Start: cases.Save{Name: sustained.BaselineSave},
		// Every need is frozen (#131): the assertion is the placement of the
		// bed, not a sleeper in it, and with Rest live the whole colony
		// sleeps through a night on the ground mid-run 2, which the stall
		// budget reads as no progress (run141e, tick 48k).
		Serve:  spec("hut"),
		Budget: 15 * time.Minute,
		Run:    func(ctx context.Context, s cases.Session) error { return hut(ctx, s, "open") },
	})
	cases.Register(cases.Case{
		Name: "shelter/hut-corridor",
		Scope: "Issue #7 under corridor terrain: granite rows leave five-cell corridors in which no hut template or " +
			"9x9 rectangle fits, so the shelter routine must grow an irregular shell confined to a corridor, " +
			"reissuing exactly one cancelled wall across a controller restart.",
		Start:  cases.Fixture{Op: corridorFixture, Args: map[string]any{"action": "setup"}, On: cases.Save{Name: sustained.BaselineSave}},
		Serve:  spec("hut-corridor"),
		Budget: 15 * time.Minute,
		Run:    func(ctx context.Context, s cases.Session) error { return hut(ctx, s, "corridor") },
	})
}

// shell is the shelter plan's geometry as recovered from the durable plan.
type shell struct {
	planID    domain.PlanID
	goalID    domain.GoalID
	footprint domain.RoomFootprint
	cells     map[domain.Cell]int // shell cell -> action index
	shape     string
	seen      map[domain.PlanID]bool // every shell plan the store has listed
	solid     map[domain.Cell]bool   // impassable ground beside the ring (corridor rock)
	ignore    map[domain.PlanID]bool // run 0's plan, from the world before the reload
	staged    map[domain.Cell]bool   // ring cells the fixture spawned finished
	expect    map[domain.Cell]bool   // ring cells the controller must complete
}

// bearing reports a wall the enclosure depends on: one orthogonally between
// an interior cell and open ground. A diagonal ring is two cells thick in
// places and the game keeps the room enclosed without the redundant cell,
// in which case the routine rightly leaves a player's cancel of it alone
// and the repair path is never exercised (runs 3 and 4 of this harness).
// Impassable ground (corridor rock) encloses like a wall: a ring cell whose
// only outside neighbour is rock is redundant too (run 16).
func (sh *shell) bearing(c domain.Cell) bool {
	interior := map[domain.Cell]bool{}
	for _, i := range sh.footprint.Interior() {
		interior[i] = true
	}
	in, out := false, false
	for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
		_, onRing := sh.cells[n]
		in = in || interior[n]
		out = out || !interior[n] && !onRing && !sh.solid[n]
	}
	return in && out
}

// waits are the run's wall-clock ceilings and its stall budget; each wait
// is also ended by the service exiting on its own.
type waits struct {
	build, furnish, stall time.Duration
	terrain               string
}

func (w waits) wait(ceiling time.Duration, service *na.ServiceProcess) na.Wait {
	return na.Wait{Ceiling: ceiling, Stall: w.stall, Terminal: service.Exited}
}

// hut runs the case on open terrain (the save's own ground, where a hut
// template fits) or corridor terrain (the fixture op the case opened on
// raised the rock rows; the routine must grow an irregular shell).
func hut(ctx context.Context, s cases.Session, terrain string) error {
	report := s.Report()
	w := waits{build: buildWait, furnish: furnishWait, stall: na.StallBudget(), terrain: terrain}
	if terrain == "corridor" {
		prepared := s.Prepared()
		if success, _ := na.AsBool(prepared["success"]); !success {
			return fmt.Errorf("%s setup refused: %#v", corridorFixture, prepared)
		}
		if na.AsNumber(prepared["raised"]) < 100 {
			return fmt.Errorf("%s raised only %v rock cells", corridorFixture, prepared["raised"])
		}
		report["corridor_terrain"] = prepared
	}
	if err := allowSupplies(ctx, s.Harness(), "allow-supplies", report); err != nil {
		return err
	}
	// Run 0: admission.
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	// The verification store outlives the service handles (Stop closes a
	// handle's own).
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
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
	if w.terrain == "corridor" && sh.shape != "irregular" {
		service.Stop()
		return fmt.Errorf("corridor terrain: the routine sited %s, want an irregular grown shell", sh.shape)
	}
	if w.terrain == "corridor" && sh.footprint.Bounds().Height > 5 && sh.footprint.Bounds().Width > 5 {
		service.Stop()
		return fmt.Errorf("corridor terrain: the shell's bounds %v are not confined to a corridor", sh.footprint.Bounds())
	}
	if w.terrain == "corridor" {
		sh.solid = corridorRock(report)
	}
	// The ring is on the journal; the world it was sited in is discarded,
	// and the next world holds it nearly finished.
	report["run0_keepalive"] = service.Stop()
	sh.ignore = map[domain.PlanID]bool{sh.planID: true}
	corridor := report["corridor_terrain"]
	h, err := s.Reload(ctx)
	if err != nil {
		return err
	}
	if w.terrain == "corridor" {
		report["corridor_terrain"] = s.Prepared()
		if !sameCorridor(corridor, report["corridor_terrain"]) {
			return fmt.Errorf("the reloaded corridor terrain differs from run 0's: %v vs %v", report["corridor_terrain"], corridor)
		}
	}
	if err := allowSupplies(ctx, h, "allow-supplies-reloaded", report); err != nil {
		return err
	}
	open, err := openGround(ctx, h)
	if err != nil {
		return err
	}
	candidates := missingCells(sh, len(sh.cells), open)
	if len(candidates) < stagePending {
		return fmt.Errorf("%d load-bearing ring cells on open ground to leave for the builders, want %d", len(candidates), stagePending)
	}
	missing := candidates[:stagePending]
	if err := stageRing(ctx, h, sh, missing, spawnWood, report); err != nil {
		return err
	}
	missing, err = unpocketedGaps(ctx, h, sh, candidates, missing, report)
	if err != nil {
		return err
	}
	sh.staged = map[domain.Cell]bool{}
	sh.expect = map[domain.Cell]bool{}
	for _, c := range sh.footprint.Walls() {
		sh.staged[c] = true
	}
	for _, c := range missing {
		delete(sh.staged, c)
		sh.expect[c] = true
	}
	report["staged_missing_cells"] = missing
	// Run 1: the missing cells.
	service, err = start(ctx, s, nil)
	if err != nil {
		return err
	}
	run1 := w.wait(w.build, service)
	run1.Interval = orderPoll
	if err := waitLineage(ctx, st, sh, run1, func(l lineage) bool {
		// Stop at the first load-bearing wall the game has acknowledged and
		// not completed, so the in-game cancel below has one to take. The
		// builders raise a wall beside staged wood in a few hundred ticks
		// and the dispatcher paces orders seconds apart, so waiting for the
		// rest of the missing cells leaves the first standing before the
		// service is down (run 2 tolerates cells run 1 never ordered); a
		// dispatch still in flight may never reach the game (undecided).
		if l.live == nil || l.liveComplete() {
			return false
		}
		for c := range l.acknowledged {
			if sh.bearing(c) {
				return true
			}
		}
		return false
	}); err != nil {
		service.Stop()
		postmortem(ctx, s, unsettledCells(ctx, st, sh), report)
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
	cancelled, err := cancelOneWall(ctx, s, sh, report)
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
	service, err = start(ctx, s, service)
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
		postmortem(ctx, s, unsettledCells(ctx, st, sh), report)
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
	// Completion: every ring cell left to the controller completed exactly
	// once across the lineage.
	var final lineage
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		for c := range sh.expect {
			if !l.completed[c] {
				return false
			}
		}
		final = l
		return true
	}); err != nil {
		service.Stop()
		postmortem(ctx, s, unsettledCells(ctx, st, sh), report)
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
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	return verifyNative(ctx, h, s.Identity(), sh, bedCells, report)
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

func cellList(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Z < out[j].Z || out[i].Z == out[j].Z && out[i].X < out[j].X })
	return out
}

// postmortem records every colonist's health and needs after a run-2 wait
// fails, through a reattached bridge session, so a clock stopped on a
// downed colonist (run 11 under corridor terrain) names its cause in the
// report; under corridor terrain it also inspects the given shell cells
// (what stands or lies on each, who reserves it) through the fixture.
func postmortem(ctx context.Context, s cases.Session, cells []domain.Cell, report na.Report) {
	h, err := s.Reattach(ctx)
	if err != nil {
		report["postmortem_error"] = err.Error()
		return
	}
	defer s.Release()
	pawns, err := h.Call(ctx, "postmortem-pawns", "home/list_pawns", map[string]any{"health": true, "needs": true, "withinOfColonists": 40})
	if err != nil {
		report["postmortem_error"] = err.Error()
		return
	}
	report["postmortem_pawns"] = pawns["pawns"]
	if cells == nil || !na.Contains(s.Names(), corridorFixture) {
		return
	}
	inspected := map[string]any{}
	for _, c := range cells {
		row, err := h.Call(ctx, "postmortem-cell", corridorFixture, map[string]any{"action": "inspect", "x": int(c.X), "z": int(c.Z)})
		if err != nil {
			inspected[fmt.Sprintf("%d,%d", c.X, c.Z)] = err.Error()
			continue
		}
		inspected[fmt.Sprintf("%d,%d", c.X, c.Z)] = row
	}
	report["postmortem_cells"] = inspected
}

// unsettledCells are the ring cells no shell plan has completed.
func unsettledCells(ctx context.Context, st *store.Store, sh *shell) []domain.Cell {
	l, err := shellLineage(ctx, st, sh)
	if err != nil {
		return nil
	}
	var cells []domain.Cell
	for _, w := range sh.footprint.Walls() {
		if sh.expect[w] && !l.completed[w] {
			cells = append(cells, w)
		}
	}
	return cells
}

// corridorRock recovers the rock rows the fixture raised from its setup
// result: every cell of a row within reach of the centre except the row's
// walkway cells. Cells the fixture skipped (already impassable, occupied)
// are counted as rock too, which only makes bearing more conservative.
func corridorRock(report na.Report) map[domain.Cell]bool {
	setup, _ := na.AsMap(report["corridor_terrain"])
	center, _ := na.AsMap(setup["center"])
	cx, reach := int32(na.AsNumber(center["x"])), int32(na.AsNumber(setup["reach"]))
	walkway := int32(na.AsNumber(setup["walkwayPeriod"]))
	rock := map[domain.Cell]bool{}
	for _, raw := range na.AsSlice(setup["rows"]) {
		row, _ := na.AsMap(raw)
		z, phase := int32(na.AsNumber(row["z"])), int32(na.AsNumber(row["walkwayPhase"]))
		for x := cx - reach; x <= cx+reach; x++ {
			if walkway > 0 && ((x-cx)%walkway+walkway)%walkway == phase {
				continue
			}
			rock[domain.Cell{X: x, Z: z}] = true
		}
	}
	return rock
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
func cancelOneWall(ctx context.Context, s cases.Session, sh *shell, report na.Report) (domain.Cell, error) {
	h, err := s.Reattach(ctx)
	if err != nil {
		return domain.Cell{}, err
	}
	defer s.Release()
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
		"colonyId": s.Identity()["colonyId"], "loadToken": s.Identity()["loadToken"], "mapId": s.Identity()["mapId"],
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

// missingCells picks the count load-bearing ring cells nearest the door
// (never the door) that the fixture leaves to the builders: each is one the
// enclosure depends on, so the cancel among them exercises the repair path.
// The map's own rock beside a cell seals the gap as a wall would (run 4 of
// this harness under #174), so a cell counts only when every outside
// neighbour is open ground.
func missingCells(sh *shell, count int, open func(domain.Cell) bool) []domain.Cell {
	door := sh.footprint.Door()
	interior := map[domain.Cell]bool{}
	for _, i := range sh.footprint.Interior() {
		interior[i] = true
	}
	var bearing []domain.Cell
	for _, c := range sh.footprint.Walls() {
		if c == door || !sh.bearing(c) {
			continue
		}
		sealed := false
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if _, onRing := sh.cells[n]; !onRing && !interior[n] && !sh.solid[n] && !open(n) {
				sealed = true
			}
		}
		if !sealed {
			bearing = append(bearing, c)
		}
	}
	sort.Slice(bearing, func(i, j int) bool {
		di := abs(bearing[i].X-door.X) + abs(bearing[i].Z-door.Z)
		dj := abs(bearing[j].X-door.X) + abs(bearing[j].Z-door.Z)
		if di != dj {
			return di < dj
		}
		return bearing[i].Z < bearing[j].Z || bearing[i].Z == bearing[j].Z && bearing[i].X < bearing[j].X
	})
	return bearing[:min(count, len(bearing))]
}

// unpocketedGaps moves a staged gap whose outside neighbour lies in an
// enclosed native room. The game roofs such a room through the gap once the
// other cells stand (a pocket sealed by natural rock or ruin walls beside
// the ring, #193 run r1), the hut then furnishes as a roofed room with the
// gap open and the repair the case exercises is never owed. Each pocketed
// gap is filled by the fixture and the next candidate cleared, until every
// gap opens onto ground the game counts as outdoors; without the corridor
// fixture's inspect the gaps stay where they are.
func unpocketedGaps(ctx context.Context, h *na.Harness, sh *shell, candidates, missing []domain.Cell, report na.Report) ([]domain.Cell, error) {
	names, err := h.Discovery(ctx)
	if err != nil {
		return nil, err
	}
	if !na.Contains(names, corridorFixture) {
		return missing, nil
	}
	interior := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		interior[c] = true
	}
	outside := func(c domain.Cell) []domain.Cell {
		var out []domain.Cell
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if _, onRing := sh.cells[n]; !onRing && !interior[n] && !sh.solid[n] {
				out = append(out, n)
			}
		}
		return out
	}
	probe := 0
	pocketed := func(c domain.Cell) (bool, error) {
		for _, n := range outside(c) {
			probe++
			row, err := h.Call(ctx, fmt.Sprintf("gap-room-%d", probe), corridorFixture, map[string]any{"action": "inspect", "x": int(n.X), "z": int(n.Z)})
			if err != nil {
				return false, err
			}
			if room, ok := na.AsMap(row["room"]); ok && boolOf(room["proper"]) {
				return true, nil
			}
		}
		return false, nil
	}
	used := map[domain.Cell]bool{}
	for _, c := range missing {
		used[c] = true
	}
	var moves []map[string]any
	for round := 0; round < len(candidates); round++ {
		var filled, cleared []domain.Cell
		for _, c := range missing {
			pocket, err := pocketed(c)
			if err != nil {
				return nil, err
			}
			if !pocket {
				continue
			}
			filled = append(filled, c)
			for _, next := range candidates {
				if !used[next] {
					used[next] = true
					cleared = append(cleared, next)
					break
				}
			}
		}
		if len(filled) == 0 {
			break
		}
		if len(cleared) < len(filled) {
			return nil, fmt.Errorf("every candidate gap opens onto an enclosed pocket: %v", filled)
		}
		var kept []domain.Cell
		for _, c := range missing {
			if !contains(filled, c) {
				kept = append(kept, c)
			}
		}
		missing = append(kept, cleared...)
		moves = append(moves, map[string]any{"filled": filled, "cleared": cleared})
		if _, err := h.Call(ctx, fmt.Sprintf("fill-gaps-%d", round), "test/hut_shell_fixture", map[string]any{
			"action": "stage", "walls": cellArg(filled), "door": cellArg([]domain.Cell{sh.footprint.Door()}), "doorRotation": string(sh.footprint.Entrance()),
		}); err != nil {
			return nil, err
		}
		if _, err := h.Call(ctx, fmt.Sprintf("clear-gaps-%d", round), "test/hut_shell_fixture", map[string]any{"action": "clear", "walls": cellArg(cleared)}); err != nil {
			return nil, err
		}
	}
	report["staged_gap_moves"] = moves
	return missing, nil
}

func contains(cells []domain.Cell, c domain.Cell) bool {
	for _, o := range cells {
		if o == c {
			return true
		}
	}
	return false
}

func cellArg(cells []domain.Cell) string {
	parts := make([]string, 0, len(cells))
	for _, c := range cells {
		parts = append(parts, fmt.Sprintf("%d,%d", c.X, c.Z))
	}
	return strings.Join(parts, ";")
}

// openGround reports, through test/corridor_terrain_fixture's inspect when
// the mod carries it, whether a cell is walkable ground; without the
// fixture every cell counts as open.
func openGround(ctx context.Context, h *na.Harness) (func(domain.Cell) bool, error) {
	names, err := h.Discovery(ctx)
	if err != nil {
		return nil, err
	}
	if !na.Contains(names, "test/corridor_terrain_fixture") {
		return func(domain.Cell) bool { return true }, nil
	}
	known := map[domain.Cell]bool{}
	return func(c domain.Cell) bool {
		if v, ok := known[c]; ok {
			return v
		}
		row, err := h.Call(ctx, "inspect-ground", "test/corridor_terrain_fixture", map[string]any{"action": "inspect", "x": int(c.X), "z": int(c.Z)})
		known[c] = err == nil && boolOf(row["success"]) && boolOf(row["walkable"])
		return known[c]
	}, nil
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

// stageRing has test/hut_shell_fixture spawn the sited ring finished (the
// door with the plan's rotation, every wall but the missing cells) with wood
// beside the door, in the reloaded world the service is about to see. The
// missing cells are cleared of plants and items: a wall under brambles
// waits on plant cutting the baseline's work priorities never assign.
func stageRing(ctx context.Context, h *na.Harness, sh *shell, missing []domain.Cell, wood int, report na.Report) error {
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	if !na.Contains(names, "test/hut_shell_fixture") {
		return errors.New("missing test/hut_shell_fixture in discovery; rebuild the native mod with -Fixture HutShellFixture (or run with -stage=false)")
	}
	skip := map[domain.Cell]bool{}
	for _, c := range missing {
		skip[c] = true
	}
	door := sh.footprint.Door()
	var walls []domain.Cell
	for _, c := range sh.footprint.Walls() {
		if c != door && !skip[c] {
			walls = append(walls, c)
		}
	}
	staged, err := h.Call(ctx, "stage-ring", "test/hut_shell_fixture", map[string]any{
		"action": "stage", "walls": cellArg(walls), "gaps": cellArg(missing),
		"door": fmt.Sprintf("%d,%d", door.X, door.Z), "doorRotation": string(sh.footprint.Entrance()), "wood": wood,
	})
	if err != nil {
		return err
	}
	report["staged_ring"] = staged
	if ok, _ := na.AsBool(staged["success"]); !ok {
		return fmt.Errorf("hut_shell_fixture refused: %#v", staged)
	}
	if int(na.AsNumber(staged["spawned"])) != len(walls)+1 {
		return fmt.Errorf("hut_shell_fixture spawned %v ring cells, want %d", staged["spawned"], len(walls)+1)
	}
	return nil
}

// sameCorridor reports whether two corridor_terrain_fixture setup results
// raised the same rows around the same centre.
func sameCorridor(a, b any) bool {
	am, _ := na.AsMap(a)
	bm, _ := na.AsMap(b)
	if am == nil || bm == nil {
		return false
	}
	return fmt.Sprint(am["center"], am["rows"]) == fmt.Sprint(bm["center"], bm["rows"])
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
