// Package shelter holds the shelter planner's serve-driven cases: the
// staged excavation vertical (#8 B06f) and the hut (initial shelter shell).
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
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The excavation case exercises the staged excavation vertical end to end
// against a live game and a live rimgovernor service: a disposable fixture
// (test/mountain_fixture) raises a fogged granite block beside the
// colonists, and the shelter planner must choose to dig in, drive ordinary
// pawn mining through bounded stage plans, keep the rock roof supported
// throughout, close the room with a door, furnish it, and have a colonist
// actually sleep in it. The service is restarted after the first stage
// completes to prove the project is rediscovered from the durable journal
// without re-designating cleared cells.
//
// The precondition is staged (#129): the fixture opens the corridor and
// the nearest predig room columns itself, as finished mining would have
// left them, so the planner binds the same target through its sunk-work
// credit and the run's own stages dig only the columns left standing.
//
// Two colonies run it (#64, #116): the debug quick-start (Industrial) digs
// the 7x7 rectangle; the tribal8 baseline (Neolithic) digs the radius-4
// round room, whose 49 cells are the ellipse target key
// "...e.4.4.north_south" every stage plan and the paired restart must
// rebuild.
func init() {
	register := func(name, colony string, start cases.Start, shape excavationShape) {
		prefix := strings.TrimPrefix(name, "shelter/")
		cases.Register(cases.Case{
			Name: name,
			Scope: "Staged excavation (" + colony + "): the shelter planner digs a corridor and a " + shape.describe + " into a fogged " +
				"granite block through ordinary pawn mining in bounded stages, survives a service restart without " +
				"re-designating cleared cells, keeps the rock roof supported, closes the room with a door, furnishes it " +
				"and a colonist sleeps inside.",
			Start: start,
			// A colonist must sleep in the room at the end: Rest stays live.
			Keep: []string{string(na.NeedRest)},
			// Random debug colonies may start with wounds; tend and rescue clear
			// the medical emergency that would otherwise suspend every
			// development goal, defense answers the hostile threats that
			// otherwise hold the clock, and naming confirms the colony name the
			// game asks for a few days in (STOP_REASON_COLONY_NAMING otherwise
			// stops the clock for good). Fifty-odd granite cells under one or two
			// miners span several game days; Superfast packs them into the
			// budget unless RIMGOVERNOR_ACCEPT_CLOCK_SPEED says otherwise.
			Serve: &cases.ServeSpec{
				Families: []string{"shelter", "tend", "rescue", "defense", "naming"},
				Env:      []string{"RIMGOVERNOR_CLOCK_DEBUG=1"}, Prefix: prefix,
			},
			// Fifty-odd granite cells mined by hand across a service restart ran
			// 35 minutes on the 2026-09-17 baseline; staged, a healthy run takes
			// about four minutes (#129).
			Budget: 15 * time.Minute,
			Run:    func(ctx context.Context, s cases.Session) error { return excavation(ctx, s, shape) },
		})
	}
	register("shelter/excavation", "Industrial debug colony", rectangle.fixture(nil), rectangle)
	register("shelter/excavation-round", "Neolithic "+sustained.BaselineSave, round.fixture(cases.Save{Name: sustained.BaselineSave}), round)
}

// excavationShape is the room the colony's tech level makes the planner dig
// (buildingruntime.excavationShapes) and how the fixture stages it: predig
// is how many room columns the fixture opens before the run, after the
// two-cell corridor, leaving about a dozen cells (two stages) for the run;
// 0 would dig the whole room live (the original 4-5 game-day run).
type excavationShape struct {
	name     string // the fixture's shape argument
	describe string
	kind     policy.ExcavationShapeKind
	suffix   string // the target key's shape suffix
	interior int    // interior cells
	predig   int
}

var (
	// The 7x7 rectangle: 5 of 7 columns staged, 14 cells left.
	rectangle = excavationShape{name: "rectangle", describe: "7x7 room", kind: policy.ExcavationRectangle, interior: 49, predig: 5}
	// The radius-4 round room: 6 of 9 columns staged, 13 cells (7+5+1) left.
	round = excavationShape{name: "round", describe: "radius-4 round room", kind: policy.ExcavationEllipse, suffix: ".e.4.4." + string(domain.EllipseNorthSouth), interior: 49, predig: 6}
)

func (e excavationShape) fixture(on cases.Start) cases.Start {
	return cases.Fixture{Op: "test/mountain_fixture", Args: map[string]any{"action": "setup", "predig": e.predig, "shape": e.name}, On: on}
}

func excavation(ctx context.Context, s cases.Session, shape excavationShape) error {
	report, h := s.Report(), s.Harness()
	report["predig"], report["shape"] = shape.predig, shape.name
	for _, want := range []string{"rimgovernor/observations_read_excavation_site"} {
		if !na.Contains(s.Names(), want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture MountainFixture", want)
		}
	}
	prepared := s.Prepared()
	if success, _ := na.AsBool(prepared["success"]); !success || na.AsString(prepared["shape"]) != shape.name {
		return fmt.Errorf("mountain_fixture setup for the %s room refused: %#v", shape.name, prepared)
	}
	block, _ := na.AsMap(prepared["block"])
	blockMinX, blockMinZ := int32(na.AsNumber(block["minX"])), int32(na.AsNumber(block["minZ"]))
	blockMaxX, blockMaxZ := int32(na.AsNumber(block["maxX"])), int32(na.AsNumber(block["maxZ"]))
	inBlock := func(c domain.Cell) bool {
		return c.X >= blockMinX && c.X <= blockMaxX && c.Z >= blockMinZ && c.Z <= blockMaxZ
	}
	spare, ok := na.AsMap(prepared["spare"])
	if !ok || spare == nil {
		return fmt.Errorf("mountain_fixture returned no spare cell for the player plan: %#v", prepared)
	}
	spareX, spareZ := int(na.AsNumber(spare["x"])), int(na.AsNumber(spare["z"]))

	// Before any dispatch: the face (off the centre line, where a staged
	// corridor may already be open) is visible rock under a rock roof and no
	// cell of the block is designated.
	faceX, faceZ := int(na.AsNumber(prepared["faceX"])), int(na.AsNumber(prepared["faceZ"]))
	direction, _ := na.AsMap(prepared["direction"])
	var probe map[string]any
	if na.AsNumber(direction["x"]) != 0 {
		probe = map[string]any{"action": "inspect", "x": faceX, "z": int(blockMinZ) + 1}
	} else {
		probe = map[string]any{"action": "inspect", "x": int(blockMinX) + 1, "z": faceZ}
	}
	before, err := h.Call(ctx, "face-before", "test/mountain_fixture", probe)
	if err != nil {
		return err
	}
	if na.AsString(before["mineable"]) != "Granite" || na.AsString(before["roof"]) != "RoofRockThick" {
		return fmt.Errorf("fixture face is not visible granite under a rock roof: %#v", before)
	}
	if designated, _ := na.AsBool(before["designated"]); designated {
		return fmt.Errorf("fixture face already designated before any dispatch: %#v", before)
	}
	if fogged, _ := na.AsBool(before["fogged"]); fogged {
		return fmt.Errorf("fixture face unexpectedly fogged: %#v", before)
	}

	identity := s.Identity()
	svc, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer func() { svc.Stop() }()
	// The one player-submitted plan the authority grant is bound to.
	submission, status, err := svc.API("POST", "/api/buildings/plans", map[string]any{
		"requestId": "excavation-player-plan-1",
		"expected":  identity,
		"building":  map[string]any{"defName": "Wall", "x": spareX, "z": spareZ, "rotation": "north", "stuff": "WoodLog"},
	}, svc.Token)
	if err != nil {
		return err
	}
	if status != 200 && status != 201 {
		return fmt.Errorf("unexpected building submission status=%d body=%#v", status, submission)
	}
	planID, revision := na.AsString(submission["planId"]), na.AsString(submission["revision"])
	if planID == "" || revision == "" {
		return fmt.Errorf("unexpected building submission: %#v", submission)
	}
	report["submission"] = submission
	// Resume enters automate mode under the world's root plan (#55).
	if _, err := svc.Acquire(); err != nil {
		return err
	}
	report["resumed"] = svc.Entry()["resumed"]

	// The keep-alive re-acquires bounded native player authority whenever
	// the service leaves automate mode (acknowledging any hold first); each
	// launch's counters land under its report entry ("keepalive").
	svc.KeepAuthority(ctx)

	verifyStore, err := na.OpenStoreWithRetry(ctx, svc.StatePath)
	if err != nil {
		return fmt.Errorf("open verification store: %w", err)
	}
	defer func() { verifyStore.Close() }()

	// Stage 0 binds the project: its plan identity carries the target.
	// A random debug colony can start under a standing emergency (injured
	// colonists, hostiles) that suspends every development goal; fail with
	// the ranking instead of waiting out the whole run.
	stage0Ctx, stage0Cancel := context.WithTimeout(ctx, 5*time.Minute)
	goalID, stage0, err := waitMethod(stage0Ctx, verifyStore, "", buildingruntime.ExcavationStageMethod(0))
	stage0Cancel()
	if err != nil {
		if review, reviewErr := verifyStore.LoadRoutineReview(ctx); reviewErr == nil {
			report["routine_review_at_failure"] = review
		}
		return fmt.Errorf("stage 0 method: %w", err)
	}
	report["goal_id"] = string(goalID)
	var target policy.ExcavationTarget
	var targetCells []domain.Cell
	// Every stage: ≤8 excavation actions, all target cells, none repeated
	// across stages, each plan Completed by the executor's own observation.
	dug := map[domain.Cell]int{}
	bindTarget := func(plan domain.PlanID, label string) error {
		t, err := buildingruntime.ExcavationPlanTarget(plan)
		if err != nil {
			return fmt.Errorf("stage 0 plan %s: %w", plan, err)
		}
		target, targetCells = t, t.Cells()
		report[label] = map[string]any{"key": t.Key(), "access": t.Access, "door": t.Door, "corridor": t.Corridor, "shape": t.Shape, "interior": t.Interior, "interior_cells": len(t.InteriorCells())}
		for _, c := range targetCells {
			if !inBlock(c) {
				return fmt.Errorf("target cell %v lies outside the fixture block", c)
			}
		}
		if inBlock(t.Access) {
			return fmt.Errorf("access cell %v lies inside the block", t.Access)
		}
		// The colony's tech level chose the room shape (#64): the key carries
		// it, so every later stage plan and the restart rebuild the same cells.
		if t.Shape.Kind != shape.kind || !strings.HasSuffix(t.Key(), shape.suffix) || len(t.InteriorCells()) != shape.interior {
			return fmt.Errorf("target %s is not the %s the colony's tech level selects (%d interior cells, want %d, key suffix %q)", t.Key(), shape.describe, len(t.InteriorCells()), shape.interior, shape.suffix)
		}
		return nil
	}
	if err := bindTarget(stage0.Plan, "target"); err != nil {
		return err
	}

	var stages []map[string]any
	restarted := false
	stage := 0
	// Only a world change (colony/load/map, tick rewind) invalidates a
	// routine goal; letter pauses, the keep-alive's resumes and the paired
	// restart suspend and reactivate the same goal (#65). If a successor
	// goal does appear, the next review re-plans from the geometry the
	// pawns actually opened (same target, cleared cells adopted, no cleared
	// cell re-designated); the harness follows that lineage so the room
	// still completes, then fails on the lineage at the end.
	var lineage []map[string]any
	followLineage := func() error {
		var next domain.GoalID
		var nextStage0 domain.GoalMethod
		var err error
		for next == "" || next == goalID {
			if next, nextStage0, err = waitMethod(ctx, verifyStore, "", buildingruntime.ExcavationStageMethod(0)); err != nil {
				return fmt.Errorf("stage 0 after goal %s was invalidated: %w", goalID, err)
			}
		}
		previous := target.Key()
		goalID = next
		if err := bindTarget(nextStage0.Plan, "target"); err != nil {
			return err
		}
		lineage = append(lineage, map[string]any{"goal": string(next), "target": target.Key(), "replaced": previous, "dug_before": len(dug)})
		report["goal_lineage"] = lineage
		stage = 0
		return nil
	}
	invalidated := func() bool {
		goal, err := verifyStore.LoadGoal(ctx, goalID)
		return err == nil && goal.Goal.Status == domain.GoalInvalidated
	}
	for {
		method, err := waitStageMethod(ctx, verifyStore, goalID, stage)
		if errors.Is(err, errGoalInvalidated) {
			if err := followLineage(); err != nil {
				return err
			}
			continue
		}
		if err != nil {
			return fmt.Errorf("stage %d: %w", stage, err)
		}
		if method == nil {
			break
		}
		// Every stage plan, before and after the restart, resumes the key.
		if t, err := buildingruntime.ExcavationPlanTarget(method.Plan); err != nil || t.Key() != target.Key() {
			return fmt.Errorf("stage %d plan %s carries target %q, want %q (%v)", stage, method.Plan, t.Key(), target.Key(), err)
		}
		cells, err := waitStageCompleted(ctx, verifyStore, method.Plan)
		if err != nil && invalidated() {
			if err := followLineage(); err != nil {
				return err
			}
			continue
		}
		if err != nil {
			return fmt.Errorf("stage %d: %w", stage, err)
		}
		if len(cells) == 0 || len(cells) > 8 {
			return fmt.Errorf("stage %d dug %d cells, want 1..8", stage, len(cells))
		}
		for _, c := range cells {
			if prev, seen := dug[c]; seen {
				return fmt.Errorf("stage %d re-designated %v already cleared by stage %d", stage, c, prev)
			}
			if !containsCell(targetCells, c) {
				return fmt.Errorf("stage %d dug %v outside the target", stage, c)
			}
			dug[c] = stage
		}
		stages = append(stages, map[string]any{"stage": stage, "plan": string(method.Plan), "cells": cells})
		report["stages"] = stages
		// Paired restart after the first stage: the project must be picked
		// up from the journal, not re-planned from scratch.
		if stage == 0 && !restarted {
			restarted = true
			verifyStore.Close()
			// The kill is ungraceful: the old process never hands native
			// authority back, and the relaunch must reclaim the stale Auto
			// grant itself (#67). The keep-alive ends with the old handle
			// and starts again on the new one once it has attached.
			svc.Stop()
			if svc, err = svc.Restart(ctx); err != nil {
				return fmt.Errorf("relaunch service: %w", err)
			}
			svc.KeepAuthority(ctx)
			verifyStore, err = na.OpenStoreWithRetry(ctx, svc.StatePath)
			if err != nil {
				return fmt.Errorf("reopen verification store: %w", err)
			}
			report["restarted_after_stage"] = stage
		}
		stage++
	}
	// A stage cancelled by a goal invalidation leaves its designations
	// with the pawns, who finish them anyway; the successor goal adopts
	// those cells as cleared and never plans them again, so they belong
	// to no completed stage. The native inspection below is the ground
	// truth for every target cell; here only the attribution is recorded.
	var adopted []domain.Cell
	for _, c := range targetCells {
		if _, ok := dug[c]; !ok {
			adopted = append(adopted, c)
		}
	}
	report["stage_count"] = stage
	report["cleared_outside_completed_stages"] = adopted

	// The door and furnishing follow the same lineage: whichever shelter
	// goal is current commits them.
	doorGoal, door, err := waitMethod(ctx, verifyStore, "", buildingruntime.ExcavationDoorMethod())
	if err != nil {
		return fmt.Errorf("door method: %w", err)
	}
	if doorGoal != goalID {
		lineage = append(lineage, map[string]any{"goal": string(doorGoal), "target": target.Key(), "phase": "door"})
		report["goal_lineage"] = lineage
		goalID = doorGoal
	}
	if err := waitPlanCompleted(ctx, verifyStore, door.Plan); err != nil {
		return fmt.Errorf("door plan: %w", err)
	}
	report["door_plan"] = string(door.Plan)
	if len(lineage) != 0 {
		return fmt.Errorf("room needed %d successor goals; pause/resume must keep the routine goal (#65): %v", len(lineage), lineage)
	}

	// The shell alternative must never have been committed under this goal.
	goal, err := verifyStore.LoadGoal(ctx, goalID)
	if err != nil {
		return err
	}
	for _, m := range goal.Methods {
		if strings.HasPrefix(string(m.Plan), "routine-shell-") {
			return fmt.Errorf("the open-site shell was committed alongside the excavation: %v", m)
		}
	}

	// Furnishing: the sleeping planner places a spot inside the new room.
	spot, err := waitFurnishing(ctx, verifyStore, goalID, target.Interior)
	if err != nil {
		return fmt.Errorf("furnishing: %w", err)
	}
	report["furnishing_plan"] = string(spot.Plan)

	if err := na.AssertRoutineRunning(func(method, path string) (map[string]any, error) {
		v, status, err := svc.API(method, path, nil, "")
		if err != nil {
			return nil, err
		}
		if status != 200 {
			return nil, fmt.Errorf("%s %s: status %d", method, path, status)
		}
		return v, nil
	}); err != nil {
		return err
	}

	verifyStore.Close()
	report["authority_reacquisitions"] = svc.Stop()
	finalHarness, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := finalHarness.Call(ctx, "pause-final", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	// Independent native evidence: every target cell is cleared under an
	// intact rock roof with no collapse pending; the door stands at the
	// target's door cell; the interior is a proper indoor room with a bed.
	inspect := func(label string, c domain.Cell) (map[string]any, error) {
		return finalHarness.Call(ctx, label, "test/mountain_fixture", map[string]any{"action": "inspect", "x": int(c.X), "z": int(c.Z)})
	}
	for i, c := range targetCells {
		row, err := inspect(fmt.Sprintf("cell-%02d", i), c)
		if err != nil {
			return err
		}
		if na.AsString(row["mineable"]) != "" {
			return fmt.Errorf("target cell %v still holds %s", c, row["mineable"])
		}
		if na.AsString(row["roof"]) != "RoofRockThick" {
			return fmt.Errorf("target cell %v lost its rock roof: %#v", c, row)
		}
		if int(na.AsNumber(row["collapsing"])) != 0 {
			return fmt.Errorf("collapse pending near %v: %#v", c, row)
		}
		isDoor, _ := na.AsBool(row["door"])
		if isDoor != (c == target.Door) {
			return fmt.Errorf("door presence at %v: %v, want %v", c, isDoor, c == target.Door)
		}
	}
	center := target.Center()
	room, err := inspect("room", center)
	if err != nil {
		return err
	}
	proper, _ := na.AsBool(room["properRoom"])
	outdoors, _ := na.AsBool(room["outdoors"])
	if !proper || outdoors {
		return fmt.Errorf("interior is not a proper enclosed room: %#v", room)
	}
	if want := len(target.InteriorCells()); int(na.AsNumber(room["roomCells"])) != want {
		return fmt.Errorf("room spans %v cells, want the %d-cell interior", room["roomCells"], want)
	}
	if int(na.AsNumber(room["bedsInRoom"])) < 1 {
		return fmt.Errorf("no bed inside the excavated room: %#v", room)
	}
	report["room"] = room

	// Functional use: exhaust the colonists (the assertion is that the room
	// is slept in, not when their schedule says so), run the game and wait
	// for one of them to sleep inside.
	tired, err := finalHarness.Call(ctx, "tire", "test/mountain_fixture", map[string]any{"action": "tire"})
	if err != nil {
		return err
	}
	report["tired"] = tired
	if _, err := finalHarness.Call(ctx, "run-fast", "rimworld/set_time_speed", map[string]any{"speed": "Superfast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// Game time has to pass here, so the progress signature is the game tick
	// (the inspect reply's tick): a clock held by a letter stalls the wait
	// instead of running out the ceiling.
	var slept map[string]any
	sleepWait := storeWait()
	sleepWait.Ceiling, sleepWait.Interval = 5*time.Minute, 5*time.Second
	if err := na.WaitProgress(ctx, sleepWait, func(ctx context.Context) (string, bool, error) {
		row, err := inspect("sleepers", center)
		if err != nil {
			return "", false, err
		}
		slept = row
		return na.Signature(row["tick"], len(na.AsSlice(row["sleepers"]))), len(na.AsSlice(row["sleepers"])) > 0, nil
	}); err != nil {
		return fmt.Errorf("no colonist slept in the excavated room (%#v): %w", slept, err)
	}
	report["slept"] = slept
	if _, err := finalHarness.Call(ctx, "pause-end", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// The roof still stands after the room was lived in.
	after, err := inspect("room-after-sleep", center)
	if err != nil {
		return err
	}
	if na.AsString(after["roof"]) != "RoofRockThick" || int(na.AsNumber(after["collapsing"])) != 0 {
		return fmt.Errorf("roof changed after use: %#v", after)
	}

	return nil
}

// storeWait is the wait every store poll below uses: the shared stall
// budget (RIMGOVERNOR_ACCEPT_STALL or the runner's -stall) and a 1s poll.
func storeWait() na.Wait { return na.Wait{Stall: na.StallBudget(), Interval: time.Second} }

// waitMethod polls the routine review for the EnsureInitialShelter binding
// and the named committed method under it.

func waitMethod(ctx context.Context, s *store.Store, knownGoal domain.GoalID, method domain.MethodID) (domain.GoalID, domain.GoalMethod, error) {
	var foundGoal domain.GoalID
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var goalID domain.GoalID
		for _, binding := range review.Goals {
			if binding.Need == policy.EnsureInitialShelter {
				goalID = binding.Goal
				break
			}
		}
		if goalID == "" || knownGoal != "" && goalID != knownGoal {
			return na.Signature("goal", goalID), false, nil
		}
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		for _, m := range goal.Methods {
			if m.Method == method {
				foundGoal, found = goalID, m
				return "", true, nil
			}
		}
		if err == nil {
			if m, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, method); err == nil {
				foundGoal, found = goalID, m
				return "", true, nil
			}
		}
		return na.Signature("goal", goalID, goal.Goal.Epoch, len(goal.Methods)), false, nil
	})
	if err != nil {
		return "", domain.GoalMethod{}, fmt.Errorf("method %s: %w", method, err)
	}
	return foundGoal, found, nil
}

var errGoalInvalidated = errors.New("goal invalidated")

// waitStageMethod waits until either stage n is committed or the door method
// exists (nil: no more stages).
func waitStageMethod(ctx context.Context, s *store.Store, goalID domain.GoalID, stage int) (*domain.GoalMethod, error) {
	var found *domain.GoalMethod
	err := na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil {
			return "", false, err
		}
		if goal.Goal.Status == domain.GoalInvalidated {
			return "", false, errGoalInvalidated
		}
		if m, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, buildingruntime.ExcavationStageMethod(stage)); err == nil {
			found = &m
			return "", true, nil
		}
		if _, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, buildingruntime.ExcavationDoorMethod()); err == nil {
			return "", true, nil
		}
		for _, m := range goal.Methods {
			if !buildingruntime.IsExcavationPlan(m.Plan) {
				return "", false, fmt.Errorf("a non-excavation method %s was committed before the project finished", m.Plan)
			}
		}
		return na.Signature(goal.Goal.Status, goal.Goal.Epoch, len(goal.Methods)), false, nil
	})
	if err != nil {
		return nil, fmt.Errorf("stage %d method: %w", stage, err)
	}
	return found, nil
}

// waitStageCompleted polls one stage plan until every excavation action is
// Completed, returning the cells it dug in plan order.
func waitStageCompleted(ctx context.Context, s *store.Store, planID domain.PlanID) ([]domain.Cell, error) {
	if err := waitPlanCompleted(ctx, s, planID); err != nil {
		return nil, err
	}
	state, err := s.LoadPlan(ctx, planID)
	if err != nil {
		return nil, err
	}
	var cells []domain.Cell
	for _, action := range state.Spec.Actions() {
		x, ok := action.Excavation()
		if !ok {
			return nil, fmt.Errorf("stage plan %s carries a non-excavation action %s", planID, action.ID())
		}
		if x.Definition() != "Granite" {
			return nil, fmt.Errorf("stage plan %s targets %s, want Granite", planID, x.Definition())
		}
		cells = append(cells, x.Cell())
	}
	return cells, nil
}

// waitPlanCompleted polls until every action is Completed; the progress
// signature is each action's stage and attempt.
func waitPlanCompleted(ctx context.Context, s *store.Store, planID domain.PlanID) error {
	err := na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		state, err := s.LoadPlan(ctx, planID)
		if err != nil {
			return "", false, err
		}
		completed := len(state.Progress) > 0
		var signature []any
		for _, p := range state.Progress {
			v := p.View()
			signature = append(signature, v.Stage, v.Attempt)
			switch v.Stage {
			case domain.Completed:
			case domain.Unsuccessful, domain.Cancelled:
				return "", false, fmt.Errorf("plan %s action %s reached %s instead of completed", planID, v.Action, v.Stage)
			default:
				completed = false
			}
		}
		return na.Signature(signature...), completed, nil
	})
	if err != nil {
		return fmt.Errorf("plan %s: %w", planID, err)
	}
	return nil
}

// waitFurnishing waits for a committed non-excavation method under the goal
// whose plan places a building inside the interior and completes.
func waitFurnishing(ctx context.Context, s *store.Store, goalID domain.GoalID, interior policy.Rectangle) (domain.GoalMethod, error) {
	inside := func(c domain.Cell) bool {
		return c.X >= interior.X && c.Z >= interior.Z && c.X < interior.X+interior.Width && c.Z < interior.Z+interior.Height
	}
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		// The furnishing satisfies the shelter goal, after which the review
		// stops binding it; the goal that committed the door stays the one
		// to read unless the lineage moved on in between.
		for _, binding := range review.Goals {
			if binding.Need == policy.EnsureInitialShelter {
				goalID = binding.Goal
			}
		}
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		for _, m := range goal.Methods {
			if buildingruntime.IsExcavationPlan(m.Plan) {
				continue
			}
			state, err := s.LoadPlan(ctx, m.Plan)
			if err != nil {
				return "", false, err
			}
			for _, action := range state.Spec.Actions() {
				b, ok := action.Building()
				if !ok || !inside(b.Cell()) {
					return "", false, fmt.Errorf("post-door method %s places %s outside the excavated interior", m.Plan, action.ID())
				}
			}
			if err := waitPlanCompleted(ctx, s, m.Plan); err != nil {
				return "", false, err
			}
			found = m
			return "", true, nil
		}
		return na.Signature(goalID, goal.Goal.Epoch, len(goal.Methods)), false, nil
	})
	if err != nil {
		return domain.GoalMethod{}, fmt.Errorf("furnishing: %w", err)
	}
	return found, nil
}

func containsCell(cells []domain.Cell, c domain.Cell) bool {
	for _, x := range cells {
		if x == c {
			return true
		}
	}
	return false
}
