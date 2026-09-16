// Command excavationaccept exercises the staged excavation vertical (#8
// B06f) end to end against a live game and a live rimgovernor Go player
// service: a disposable fixture (test/mountain_fixture) raises a fogged
// granite block beside the colonists, and the shelter planner must choose
// to dig in, drive ordinary pawn mining through bounded stage plans, keep
// the rock roof supported throughout, close the room with a door, furnish
// it, and have a colonist actually sleep in it. The service is restarted
// after the first stage completes to prove the project is rediscovered from
// the durable journal without re-designating cleared cells.
//
// Session discipline follows routinehaulaccept: only one GABP client can be
// attached to the game at a time, so this harness prepares the fixture,
// closes its own bridge session (without games_stop), lets the service run
// its own session, stops the service, and reopens a fresh harness session
// for the independent native checks. games_stop is called exactly once, at
// the very end.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root")
	output := flag.String("output", "", "fresh output directory (default <root>/native-excavation-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	timeout := flag.Duration("timeout", 90*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-excavation-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Staged excavation: the shelter planner digs a corridor and room into a fogged "+
		"granite block through ordinary pawn mining in bounded stages, survives a service restart without "+
		"re-designating cleared cells, keeps the rock roof supported, closes the room with a door, furnishes it "+
		"and a colonist sleeps inside.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *rimgovernorBinary, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, rimgovernorBinary string, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	binarySHA, err := sha256File(rimgovernorBinary)
	if err != nil {
		return fmt.Errorf("hash rimgovernor binary: %w", err)
	}
	report["rimgovernor_binary"] = map[string]string{"path": rimgovernorBinary, "sha256": binarySHA}

	openHarness := func() (*bridge.Client, *na.Harness, error) {
		c, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
		if err != nil {
			return nil, nil, err
		}
		return c, na.NewHarness(c, output), nil
	}
	stopGame := func(c *bridge.Client) {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := c.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
	}

	client, h, err := openHarness()
	if err != nil {
		return err
	}
	// Whatever path this run takes, the game it started is stopped exactly
	// once before returning: through the session that is open at the time,
	// or through a fresh one when the service held the slot at failure.
	var open *bridge.Client = client
	var svc *service
	defer func() {
		if svc != nil {
			svc.stop()
		}
		if open == nil {
			reopenCtx, reopenCancel := context.WithTimeout(context.Background(), 60*time.Second)
			defer reopenCancel()
			deadline := time.Now().Add(30 * time.Second)
			for open == nil {
				c, err := na.OpenSession(reopenCtx, gabsExecutable, cfg.Configuration, gameID, 30*time.Second)
				if err == nil {
					open = c
					break
				}
				if time.Now().After(deadline) {
					report["stop_error"] = "reopen for stop: " + err.Error()
					return
				}
				time.Sleep(time.Second)
			}
		}
		stopGame(open)
		_ = open.Close()
	}()
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// The colony-naming dialog is a priority-0 emergency that blocks every
	// other goal; dismiss it as a player would.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	}

	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	matchesIdentity := func(v map[string]any) bool {
		return na.AsString(v["colonyId"]) == na.AsString(identity["colonyId"]) &&
			na.AsString(v["loadToken"]) == na.AsString(identity["loadToken"]) &&
			na.AsNumber(v["mapId"]) == na.AsNumber(identity["mapId"])
	}

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	for _, want := range []string{"test/mountain_fixture", "rimgovernor/observations_read_excavation_site"} {
		if !na.Contains(names, want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture MountainFixture", want)
		}
	}

	prepared, err := h.Call(ctx, "mountain-setup", "test/mountain_fixture", map[string]any{"action": "setup"})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("mountain_fixture setup refused: %#v", prepared)
	}
	report["prepared"] = prepared
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

	// Before any dispatch: the face is visible rock under a rock roof and no
	// cell of the block is designated.
	faceX, faceZ := int(na.AsNumber(prepared["faceX"])), int(na.AsNumber(prepared["faceZ"]))
	direction, _ := na.AsMap(prepared["direction"])
	var probe map[string]any
	if na.AsNumber(direction["x"]) != 0 {
		probe = map[string]any{"action": "inspect", "x": faceX, "z": int(blockMinZ) + 6}
	} else {
		probe = map[string]any{"action": "inspect", "x": int(blockMinX) + 6, "z": faceZ}
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

	if err := client.Close(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}
	open = nil

	svc = &service{ctx: ctx, binary: rimgovernorBinary, output: output, gabs: gabsExecutable, configuration: cfg.Configuration, gameID: gameID, report: report}
	if err := svc.launch(); err != nil {
		return err
	}
	if err := svc.attach(matchesIdentity); err != nil {
		return err
	}

	// The one player-submitted plan the authority grant is bound to.
	submission, status, err := svc.api("POST", "/api/buildings/plans", map[string]any{
		"requestId": "excavation-player-plan-1",
		"expected":  identity,
		"building":  map[string]any{"defName": "Wall", "x": spareX, "z": spareZ, "rotation": "north", "stuff": "WoodLog"},
	}, svc.token())
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
	acquired, status, err := svc.api("POST", "/api/player/control/resume", map[string]any{
		"requestId": "excavation-resume-1", "expected": identity,
	}, svc.token())
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected resume status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "running" {
		return fmt.Errorf("resume was not running: %#v", acquired)
	}
	report["resumed"] = acquired

	keepAlive := &authorityKeepAlive{svc: svc, identity: identity}
	keepAliveCtx, stopKeepAlive := context.WithCancel(ctx)
	var keepAliveWG sync.WaitGroup
	keepAliveWG.Add(1)
	go func() { defer keepAliveWG.Done(); keepAlive.run(keepAliveCtx) }()
	defer func() {
		stopKeepAlive()
		keepAliveWG.Wait()
		report["authority_reacquisitions"] = keepAlive.snapshot()
	}()

	verifyStore, err := openStoreWithRetry(ctx, svc.statePath())
	if err != nil {
		return fmt.Errorf("open verification store: %w", err)
	}
	defer verifyStore.Close()

	// Stage 0 binds the project: its plan identity carries the target.
	// A random debug colony can start under a standing emergency (injured
	// colonists, hostiles) that suspends every development goal; fail with
	// the ranking instead of waiting out the whole run.
	stage0Ctx, stage0Cancel := context.WithTimeout(ctx, 12*time.Minute)
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
		report[label] = map[string]any{"key": t.Key(), "access": t.Access, "door": t.Door, "corridor": t.Corridor, "interior": t.Interior}
		for _, c := range targetCells {
			if !inBlock(c) {
				return fmt.Errorf("target cell %v lies outside the fixture block", c)
			}
		}
		if inBlock(t.Access) {
			return fmt.Errorf("access cell %v lies inside the block", t.Access)
		}
		return nil
	}
	if err := bindTarget(stage0.Plan, "target"); err != nil {
		return err
	}

	var stages []map[string]any
	restarted := false
	stage := 0
	// A world-changing review invalidates every routine goal (a resume
	// alone keeps them). The next review then re-plans from the geometry
	// the pawns actually opened: the same target is re-adopted with its
	// cleared cells, and no cleared cell is ever re-designated. The
	// harness follows that lineage whenever it happens.
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
			// A player-issued manual release precedes the restart: native
			// authority is a live grant that only its holder can revoke, so
			// the old process must hand it back before the new one can
			// acquire it. The keep-alive pauses across the restart so it
			// does not race the release.
			keepAlive.pause(true)
			// A letter pause may have revoked automation at the same
			// moment, which makes the hand-back uncertain; automation
			// already off is the state the release was after.
			var released map[string]any
			for attempt := 1; ; attempt++ {
				state, stateStatus, err := svc.api("GET", "/api/state", nil, "")
				if err == nil && stateStatus == 200 && na.AsString(state["mode"]) != "automate" {
					released = map[string]any{"already_manual": true, "state": state}
					break
				}
				var status int
				released, status, err = svc.api("POST", "/api/player/control/pause", map[string]any{
					"requestId": fmt.Sprintf("excavation-release-before-restart-%d", attempt), "expected": identity,
				}, svc.token())
				if err != nil {
					return fmt.Errorf("release before restart: %w", err)
				}
				if status == 200 {
					break
				}
				if attempt >= 10 {
					return fmt.Errorf("release before restart status=%d body=%#v", status, released)
				}
				select {
				case <-ctx.Done():
					return ctx.Err()
				case <-time.After(3 * time.Second):
				}
			}
			report["released_before_restart"] = released
			svc.stop()
			if err := svc.launch(); err != nil {
				return fmt.Errorf("relaunch service: %w", err)
			}
			if err := svc.attach(matchesIdentity); err != nil {
				return fmt.Errorf("reattach after restart: %w", err)
			}
			verifyStore, err = openStoreWithRetry(ctx, svc.statePath())
			if err != nil {
				return fmt.Errorf("reopen verification store: %w", err)
			}
			keepAlive.pause(false)
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
		v, status, err := svc.api(method, path, nil, "")
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
	stopKeepAlive()
	svc.stop()
	var finalClient *bridge.Client
	var finalHarness *na.Harness
	reopenDeadline := time.Now().Add(30 * time.Second)
	for {
		finalClient, finalHarness, err = openHarness()
		if err == nil {
			break
		}
		if time.Now().After(reopenDeadline) {
			return fmt.Errorf("reopen bridge session for final native check: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
	open = finalClient
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
	center := domain.Cell{X: target.Interior.X + target.Interior.Width/2, Z: target.Interior.Z + target.Interior.Height/2}
	room, err := inspect("room", center)
	if err != nil {
		return err
	}
	proper, _ := na.AsBool(room["properRoom"])
	outdoors, _ := na.AsBool(room["outdoors"])
	if !proper || outdoors {
		return fmt.Errorf("interior is not a proper enclosed room: %#v", room)
	}
	if int(na.AsNumber(room["roomCells"])) != int(target.Interior.Width*target.Interior.Height) {
		return fmt.Errorf("room spans %v cells, want the %d-cell interior", room["roomCells"], target.Interior.Width*target.Interior.Height)
	}
	if int(na.AsNumber(room["bedsInRoom"])) < 1 {
		return fmt.Errorf("no bed inside the excavated room: %#v", room)
	}
	report["room"] = room

	// Functional use: run the game and wait for a colonist to sleep inside.
	if _, err := finalHarness.Call(ctx, "run-fast", "rimworld/set_time_speed", map[string]any{"speed": "Superfast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	sleepDeadline := time.Now().Add(12 * time.Minute)
	var slept map[string]any
	for {
		row, err := inspect("sleepers", center)
		if err != nil {
			return err
		}
		if len(na.AsSlice(row["sleepers"])) > 0 {
			slept = row
			break
		}
		if time.Now().After(sleepDeadline) {
			return fmt.Errorf("no colonist slept in the excavated room: %#v", row)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Second):
		}
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// service owns one rimgovernor serve subprocess at a time; launch after stop
// reuses the same state file so the restart exercises journal recovery.
type service struct {
	ctx           context.Context
	binary        string
	output        string
	gabs          string
	configuration string
	gameID        string
	report        na.Report

	mu       sync.Mutex
	launches int
	url      string
	tok      string
	cmd      *exec.Cmd
	done     chan error
	files    []*os.File
	requests int
	client   *http.Client
}

func (s *service) statePath() string { return filepath.Join(s.output, "service.sqlite") }

func (s *service) token() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.tok
}

func (s *service) launch() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.launches++
	profileDir := filepath.Join(s.output, "service-profile")
	serviceDir := filepath.Join(s.output, fmt.Sprintf("service-%d", s.launches))
	for _, dir := range []string{profileDir, serviceDir} {
		if err := os.MkdirAll(dir, 0755); err != nil {
			return err
		}
	}
	argv := []string{
		"serve", "--profile", profileDir, "--gabs", s.gabs, "--config", s.configuration, "--game", s.gameID,
		"--state", s.statePath(), "--listen", "127.0.0.1:0", "--refresh", "1s", "--timeout", "15s",
		// Fifty-odd granite cells under one or two miners span several game
		// days; Superfast packs them into the run's wall-clock budget.
		"--clock-speed", "Superfast",
	}
	s.report[fmt.Sprintf("service_argv_%d", s.launches)] = append([]string{s.binary}, argv...)
	cmd := exec.CommandContext(s.ctx, s.binary, argv...)
	// Random debug colonies may start with wounds; tend and rescue clear the
	// medical emergency that would otherwise suspend every development goal,
	// defense answers the hostile threats that otherwise hold the clock, and
	// naming confirms the colony name the game asks for a few days in
	// (STOP_REASON_COLONY_NAMING otherwise stops the clock for good).
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_CLOCK_DEBUG=1", "RIMGOVERNOR_ROUTINE_FAMILIES=shelter,tend,rescue,defense,naming")
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return err
	}
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start rimgovernor serve: %w", err)
	}
	s.report[fmt.Sprintf("service_pid_%d", s.launches)] = cmd.Process.Pid
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		return fmt.Errorf("read service startup line: %w", err)
	}
	const prefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, prefix) {
		return fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	stdoutFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		return err
	}
	go func() { _, _ = io.Copy(stdoutFile, reader) }()
	s.url = strings.TrimPrefix(firstLine, prefix)
	s.report[fmt.Sprintf("service_url_%d", s.launches)] = s.url
	s.cmd, s.done = cmd, done
	s.files = []*os.File{stderrFile, stdoutFile}
	if s.client == nil {
		s.client = &http.Client{Timeout: 20 * time.Second}
	}
	return nil
}

func (s *service) stop() {
	s.mu.Lock()
	cmd, done, files := s.cmd, s.done, s.files
	s.cmd, s.done, s.files = nil, nil, nil
	s.mu.Unlock()
	if cmd == nil {
		return
	}
	if cmd.ProcessState == nil {
		_ = cmd.Process.Kill()
	}
	<-done
	for _, f := range files {
		_ = f.Close()
	}
}

// attach waits for health, a player session token and the service's own
// bridge session to see the fixture's identity.
func (s *service) attach(matchesIdentity func(map[string]any) bool) error {
	health, status, err := s.api("GET", "/api/health", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || na.AsString(health["backend"]) != "go" {
		return fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := s.api("GET", "/api/player/session", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(session["mode"]) != "explicit-player" || na.AsString(session["token"]) == "" {
		return fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	s.mu.Lock()
	s.tok = na.AsString(session["token"])
	s.mu.Unlock()
	deadline := time.Now().Add(90 * time.Second)
	for {
		state, status, err := s.api("GET", "/api/state", nil, "")
		if err != nil {
			return err
		}
		if status != 200 {
			return fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && matchesIdentity(svcIdentity) {
				s.report[fmt.Sprintf("service_state_attached_%d", s.launches)] = state
				return nil
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("service did not attach to the fixture's identity in time: %#v", state)
		}
		select {
		case <-s.ctx.Done():
			return s.ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func (s *service) api(method, path string, body map[string]any, token string) (map[string]any, int, error) {
	s.mu.Lock()
	url, client := s.url, s.client
	s.mu.Unlock()
	if url == "" {
		return nil, 0, errors.New("service is not running")
	}
	var reqBody io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			return nil, 0, err
		}
		reqBody = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(s.ctx, method, url+path, reqBody)
	if err != nil {
		return nil, 0, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		req.Header.Set("X-RimGovernor-Player", token)
	}
	req.Header.Set("Origin", url)
	resp, err := client.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, 0, err
	}
	s.mu.Lock()
	s.requests++
	n := s.requests
	s.mu.Unlock()
	_ = os.WriteFile(filepath.Join(s.output, fmt.Sprintf("http-%04d.json", n)), mustJSON(map[string]any{
		"method": method, "path": path, "request": body, "status": resp.StatusCode, "response": json.RawMessage(data),
	}), 0644)
	var out map[string]any
	if len(data) > 0 {
		if err := json.Unmarshal(data, &out); err != nil {
			return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
		}
	}
	return out, resp.StatusCode, nil
}

// authorityKeepAlive re-acquires bounded native player authority whenever
// the service leaves automate mode (acknowledging any hold first), including
// after the paired restart, exactly as routinehaulaccept does.
type authorityKeepAlive struct {
	svc      *service
	identity map[string]any

	mu                sync.Mutex
	paused            bool
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	lastError         string
}

func (k *authorityKeepAlive) snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{
		"attempts": k.attempts, "reacquired": k.reacquired,
		"acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed,
	}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

func (k *authorityKeepAlive) pause(paused bool) {
	k.mu.Lock()
	defer k.mu.Unlock()
	k.paused = paused
}

func (k *authorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
		k.mu.Lock()
		paused := k.paused
		k.mu.Unlock()
		if paused {
			continue
		}
		state, status, err := k.svc.api("GET", "/api/state", nil, "")
		if err != nil || status != 200 {
			continue
		}
		if na.AsString(state["mode"]) == "automate" {
			continue
		}
		if connected, _ := na.AsBool(state["connected"]); !connected {
			continue
		}
		token := k.svc.token()
		if clk, clkStatus, clkErr := k.svc.api("GET", "/api/player/clock", nil, ""); clkErr == nil && clkStatus == 200 {
			if holds := na.AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("excavation-ack-%d", time.Now().UnixNano()),
					"expectedRevision": na.AsString(clk["revision"]),
					"throughCursor":    na.AsString(clk["inboxCursor"]),
				}
				k.mu.Lock()
				if _, ackStatus, ackErr := k.svc.api("POST", "/api/player/clock/acknowledge", ackBody, token); ackErr != nil || ackStatus != 200 {
					if ackErr != nil {
						k.lastError = "acknowledge: " + ackErr.Error()
					} else {
						k.lastError = fmt.Sprintf("acknowledge status=%d", ackStatus)
					}
					k.acknowledgeFailed++
				} else {
					k.acknowledged++
				}
				k.mu.Unlock()
			}
		}
		k.mu.Lock()
		k.attempts++
		k.mu.Unlock()
		body := map[string]any{
			"requestId": fmt.Sprintf("excavation-resume-%d", time.Now().UnixNano()),
			"expected":  k.identity,
		}
		acquired, status, err := k.svc.api("POST", "/api/player/control/resume", body, token)
		if err != nil {
			k.mu.Lock()
			k.lastError = err.Error()
			k.mu.Unlock()
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		k.mu.Lock()
		switch {
		case status != 200:
			k.lastError = fmt.Sprintf("resume status=%d body=%#v", status, acquired)
		case na.AsString(record["phase"]) != "running":
			k.lastError = fmt.Sprintf("resume not running: %#v", acquired)
		default:
			k.reacquired++
			k.lastError = ""
		}
		k.mu.Unlock()
	}
}

// waitMethod polls the routine review for the EnsureInitialShelter binding
// and the named committed method under it.
func waitMethod(ctx context.Context, s *store.Store, knownGoal domain.GoalID, method domain.MethodID) (domain.GoalID, domain.GoalMethod, error) {
	for {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", domain.GoalMethod{}, err
		}
		var goalID domain.GoalID
		for _, binding := range review.Goals {
			if binding.Need == policy.EnsureInitialShelter {
				goalID = binding.Goal
				break
			}
		}
		if goalID != "" && (knownGoal == "" || goalID == knownGoal) {
			goal, err := s.LoadGoal(ctx, goalID)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", domain.GoalMethod{}, err
			}
			for _, m := range goal.Methods {
				if m.Method == method {
					return goalID, m, nil
				}
			}
			if err == nil {
				if m, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, method); err == nil {
					return goalID, m, nil
				}
			}
		}
		select {
		case <-ctx.Done():
			return "", domain.GoalMethod{}, ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

var errGoalInvalidated = errors.New("goal invalidated")

// waitStageMethod waits until either stage n is committed or the door method
// exists (nil: no more stages).
func waitStageMethod(ctx context.Context, s *store.Store, goalID domain.GoalID, stage int) (*domain.GoalMethod, error) {
	for {
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil {
			return nil, err
		}
		if goal.Goal.Status == domain.GoalInvalidated {
			return nil, errGoalInvalidated
		}
		if m, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, buildingruntime.ExcavationStageMethod(stage)); err == nil {
			return &m, nil
		}
		if _, err := s.LoadGoalMethod(ctx, goalID, goal.Goal.Epoch, buildingruntime.ExcavationDoorMethod()); err == nil {
			return nil, nil
		}
		for _, m := range goal.Methods {
			if !buildingruntime.IsExcavationPlan(m.Plan) {
				return nil, fmt.Errorf("a non-excavation method %s was committed before the project finished", m.Plan)
			}
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(time.Second):
		}
	}
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

func waitPlanCompleted(ctx context.Context, s *store.Store, planID domain.PlanID) error {
	for {
		state, err := s.LoadPlan(ctx, planID)
		if err != nil {
			return err
		}
		completed := len(state.Progress) > 0
		for _, p := range state.Progress {
			switch p.View().Stage {
			case domain.Completed:
			case domain.Unsuccessful, domain.Cancelled:
				return fmt.Errorf("plan %s action %s reached %s instead of completed", planID, p.View().Action, p.View().Stage)
			default:
				completed = false
			}
		}
		if completed {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

// waitFurnishing waits for a committed non-excavation method under the goal
// whose plan places a building inside the interior and completes.
func waitFurnishing(ctx context.Context, s *store.Store, goalID domain.GoalID, interior policy.Rectangle) (domain.GoalMethod, error) {
	inside := func(c domain.Cell) bool {
		return c.X >= interior.X && c.Z >= interior.Z && c.X < interior.X+interior.Width && c.Z < interior.Z+interior.Height
	}
	for {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return domain.GoalMethod{}, err
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
			return domain.GoalMethod{}, err
		}
		for _, m := range goal.Methods {
			if buildingruntime.IsExcavationPlan(m.Plan) {
				continue
			}
			state, err := s.LoadPlan(ctx, m.Plan)
			if err != nil {
				return domain.GoalMethod{}, err
			}
			for _, action := range state.Spec.Actions() {
				b, ok := action.Building()
				if !ok || !inside(b.Cell()) {
					return domain.GoalMethod{}, fmt.Errorf("post-door method %s places %s outside the excavated interior", m.Plan, action.ID())
				}
			}
			if err := waitPlanCompleted(ctx, s, m.Plan); err != nil {
				return domain.GoalMethod{}, err
			}
			return m, nil
		}
		select {
		case <-ctx.Done():
			return domain.GoalMethod{}, ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

func containsCell(cells []domain.Cell, c domain.Cell) bool {
	for _, x := range cells {
		if x == c {
			return true
		}
	}
	return false
}

func openStoreWithRetry(ctx context.Context, path string) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, path)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func sha256File(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func mustJSON(v any) []byte {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return []byte("{}")
	}
	return data
}
