// Command lightaccept exercises the MaintainLighting vertical (issue #6
// slice 3) end to end against a live game and a live rimgovernor Go
// player-control service composed with the lighting family:
//
//	dark   -- an enclosed roofed room holds a fuelled stove whose interaction
//	          cell native measures dark, with no lamp in reach. The service
//	          must latch the bench from the measured glow, admit exactly one
//	          affordable lamp (a TorchLamp: the colony has no power source)
//	          on a free cell of the room within the placement radius, the
//	          colonists build it, and the next measured census must release
//	          the latch. An independent native read then confirms the cell
//	          reads lit and the lamp stands where it was admitted.
//	outage -- the same room with an unpowered StandingLamp in reach. The
//	          lamp does not glow, so the cell stays dark, but the service
//	          must hold for the power family (lamp_power_needed) rather than
//	          double up with a torch: no lighting method may be committed.
//
// Uses the private disposable test/lighting_prepare fixture
// (LightingFixture.cs). As with the other native harnesses, the harness's
// own bridge session and the service's are used sequentially, never
// concurrently (one GABP client per game).
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "light-accept"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-lighting-acceptance-<scenario>)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	scenario := flag.String("scenario", "dark", "dark or outage")
	hold := flag.Duration("hold", 4*time.Minute, "outage: how long the service must hold without committing a lighting method")
	timeout := flag.Duration("timeout", 25*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *scenario != "dark" && *scenario != "outage" {
		fmt.Fprintln(os.Stderr, "-scenario must be dark or outage")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-lighting-acceptance-" + *scenario
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native MaintainLighting vertical ("+*scenario+"): a measured-dark stove interaction cell in an enclosed room "+
		"drives the live Go routine reviewer/planner to admit one affordable lamp beside it (dark) or to hold for the power "+
		"family behind an unpowered lamp already in reach (outage); the measured glow, not the receipt, releases the latch, "+
		"confirmed by an independent native read.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *binary, *scenario, *hold, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, binary, scenario string, hold time.Duration, report na.Report) error {
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
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 120*time.Second)
	if err != nil {
		return err
	}
	h := na.NewHarness(client, output)
	sessionOpen := true
	var service *na.ServiceProcess
	var postmortem map[string]any
	stopped := false
	stopGame := func() {
		if stopped {
			return
		}
		stopped = true
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		if !sessionOpen {
			reopened, err := na.ReopenSession(stopCtx, gabsExecutable, cfg.Configuration, gameID)
			if err != nil {
				report["stop_error"] = "reopen session for games_stop: " + err.Error()
				return
			}
			client, sessionOpen = reopened, true
			if postmortem != nil {
				if _, hasAfter := report["lighting_after"]; !hasAfter {
					ph := na.NewHarness(client, output)
					if after, err := readLighting(stopCtx, ph, postmortem, "lighting-postmortem"); err == nil {
						report["lighting_postmortem"] = after.evidence()
					} else {
						report["lighting_postmortem_error"] = err.Error()
					}
				}
			}
		}
		if s, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(s.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}
	defer stopGame()

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := confirmColonyNames(ctx, h, report); err != nil {
		return err
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
	postmortem = identity
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "test/lighting_prepare") {
		return fmt.Errorf("missing test/lighting_prepare in discovery; rebuild the native mod with -Fixture LightingFixture")
	}

	prepared, err := h.Call(ctx, "prepare", "test/lighting_prepare", map[string]any{"scenario": scenario})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success || !na.MatchesIdentity(prepared, identity) {
		return fmt.Errorf("lighting_prepare refused or identity mismatch: %#v", prepared)
	}
	report["prepared"] = prepared
	stoveID := na.AsString(prepared["stove"])
	lampID := na.AsString(prepared["lamp"])
	interior, _ := na.AsMap(prepared["interior"])
	workCellMap, _ := na.AsMap(prepared["workCell"])
	workCell := domain.Cell{X: int32(na.AsNumber(workCellMap["x"])), Z: int32(na.AsNumber(workCellMap["z"]))}
	inside := func(c domain.Cell) bool {
		return float64(c.X) >= na.AsNumber(interior["minX"]) && float64(c.X) <= na.AsNumber(interior["maxX"]) &&
			float64(c.Z) >= na.AsNumber(interior["minZ"]) && float64(c.Z) <= na.AsNumber(interior["maxZ"])
	}

	// Before: the typed lighting census must list the stove's interaction
	// cell roofed and dark -- the exact facts the review latches on -- and,
	// for outage, the fixture lamp unlit and unpowered.
	lighting := policy.DefaultLightingPolicy()
	before, err := readLighting(ctx, h, identity, "lighting-before")
	if err != nil {
		return err
	}
	report["lighting_before"] = before.evidence()
	stove, ok := before.cells[stoveID]
	if !ok || !stove.roofed || stove.glow >= lighting.LitGlow || stove.cell != workCell {
		return fmt.Errorf("lighting-before: fixture stove is not a roofed dark work cell: %+v", stove)
	}
	if scenario == "outage" {
		lamp, ok := before.lamps[lampID]
		if !ok || lamp.lit || lamp.powered {
			return fmt.Errorf("lighting-before: fixture lamp is not an unlit unpowered lamp: %+v", lamp)
		}
	} else if len(before.lamps) != 0 {
		return fmt.Errorf("lighting-before: %d lamps present before the controller acts", len(before.lamps))
	}
	if err := client.Close(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}
	sessionOpen = false

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment, which only the work family applies.
	service, err = na.LaunchService(ctx, cfg, gabsExecutable, na.ServiceLaunch{Binary: binary, Families: []string{"lighting", "work"}, Extra: []string{"--clock-speed", na.ClockSpeed()}}, report)
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return err
	}
	report["service_state_attached"] = attached
	// No anchor plan: a player construction project would commit the
	// colony's builder and starve the ranked lamp of construction labor.
	rootPlanID, err := service.Resume(prefix, identity, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()

	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)

	// The review must latch the stove and bind MaintainLighting.
	waitCtx, waitCancel := context.WithTimeout(ctx, 3*time.Minute)
	latched, err := waitLatch(waitCtx, journal, service, stoveID)
	waitCancel()
	if err != nil {
		return fmt.Errorf("lighting latch: %w", err)
	}
	report["latched_review_revision"] = latched.Revision

	if scenario == "outage" {
		// Hold: the lamp in reach is unpowered, so the lighting family must
		// report lamp_power_needed and commit nothing for the whole window.
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(hold):
		}
		methods, err := lightingMethods(ctx, journal)
		if err != nil {
			return err
		}
		if len(methods) != 0 {
			return fmt.Errorf("lighting committed %d methods behind an unpowered lamp: %#v", len(methods), methods)
		}
		still, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return err
		}
		if !latchedOn(still, stoveID) {
			return fmt.Errorf("lighting latch dropped during the hold (revision %d, latches %+v)", still.Revision, still.Latches.Lighting)
		}
		report["held_review_revision"] = still.Revision
		stderr, err := os.ReadFile(filepath.Join(output, "service", "stderr.log"))
		if err != nil {
			return fmt.Errorf("read service stderr: %w", err)
		}
		reasons := stepReasons(string(stderr))
		report["lighting_step_reasons"] = reasons
		if reasons[string(policy.LightingPowerNeeded)] == 0 {
			return fmt.Errorf("service never reported %s; observed step reasons %v", policy.LightingPowerNeeded, reasons)
		}
		for reason := range reasons {
			if reason == string(policy.LightingBuild) || reason == "admitted" {
				return fmt.Errorf("service reported a lamp build behind an unpowered lamp: %v", reasons)
			}
		}
		if err := na.AssertRoutineRunning(service.Get); err != nil {
			return err
		}
		journal.Close()
		service.Stop()
		client, err = na.ReopenSession(ctx, gabsExecutable, cfg.Configuration, gameID)
		if err != nil {
			return fmt.Errorf("reopen harness session after service stop: %w", err)
		}
		sessionOpen = true
		h = na.NewHarness(client, output)
		if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		after, err := readLighting(ctx, h, identity, "lighting-after")
		if err != nil {
			return err
		}
		report["lighting_after"] = after.evidence()
		if len(after.lamps) != 1 {
			return fmt.Errorf("lighting-after: expected only the fixture lamp, observed %d lamps", len(after.lamps))
		}
		if s := after.cells[stoveID]; s.glow >= lighting.LitGlow {
			return fmt.Errorf("lighting-after: stove cell reads lit (%.2f) with no power; the fixture lamp must not glow", s.glow)
		}
		logData, err := os.ReadFile(cfg.StartupLogPath())
		if err != nil {
			return fmt.Errorf("read startup log: %w", err)
		}
		return na.CheckStartupLog(string(logData), headless)
	}

	// The lighting method: one lamp build on a free interior cell within
	// the placement radius of the interaction cell, never on it.
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainLighting, nil)
	methodCancel()
	if err != nil {
		return fmt.Errorf("lighting method: %w", err)
	}
	report["goal_id"] = string(goalID)
	var builtCell domain.Cell
	var builtDefinition string
	for renewals := 0; ; renewals++ {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("lighting plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok {
			return fmt.Errorf("lighting plan action is not a building: %#v", actions[0])
		}
		builtCell, builtDefinition = b.Cell(), b.Definition()
		if builtDefinition != "TorchLamp" {
			return fmt.Errorf("lighting admitted %s; a colony without a power source must choose the TorchLamp", builtDefinition)
		}
		if builtCell == workCell || !inside(builtCell) || max(abs(builtCell.X-workCell.X), abs(builtCell.Z-workCell.Z)) > lighting.PlacementRadius {
			return fmt.Errorf("lamp placed at %v: not a free interior cell within %d of the work cell %v", builtCell, lighting.PlacementRadius, workCell)
		}
		report["lamp_cell"] = map[string]any{"x": builtCell.X, "z": builtCell.Z, "definition": builtDefinition}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("lighting plan: %w", err)
		}
		if !incidental {
			report["lighting_plan"] = string(method.Plan)
			report["lighting_completed_tick"] = int64(state.Progress[0].View().Tick)
			report["incidental_renewals"] = renewals
			break
		}
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.MaintainLighting, &method)
		renewCancel()
		if err != nil {
			return fmt.Errorf("renewed lighting method after incidental cancellation #%d: %w", renewals+1, err)
		}
	}

	// The measured census releases the latch once the cell reads lit.
	releaseCtx, releaseCancel := context.WithTimeout(ctx, 5*time.Minute)
	released, err := waitRelease(releaseCtx, journal, service, stoveID)
	releaseCancel()
	if err != nil {
		return fmt.Errorf("lighting release: %w", err)
	}
	report["released_review_revision"] = released.Revision
	methods, err := lightingMethods(ctx, journal)
	if err != nil {
		return err
	}
	report["lighting_methods"] = len(methods)
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	service.Stop()
	client, err = na.ReopenSession(ctx, gabsExecutable, cfg.Configuration, gameID)
	if err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	sessionOpen = true
	h = na.NewHarness(client, output)
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readLighting(ctx, h, identity, "lighting-after")
	if err != nil {
		return err
	}
	report["lighting_after"] = after.evidence()
	s, ok := after.cells[stoveID]
	if !ok || s.glow < lighting.LitGlow {
		return fmt.Errorf("lighting-after: stove cell glow %.2f is still under %.2f", s.glow, lighting.LitGlow)
	}
	if len(after.lamps) != 1 {
		return fmt.Errorf("expected exactly one lamp after the run, observed %d: %+v", len(after.lamps), after.lamps)
	}
	for _, l := range after.lamps {
		if l.cell != builtCell || l.definition != builtDefinition || !l.lit {
			return fmt.Errorf("the surviving lamp %+v is not the lit %s admitted at %v", l, builtDefinition, builtCell)
		}
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

// confirmColonyNames dismisses the fresh debug game's naming dialog, which
// RankDevelopment otherwise treats as a global emergency (see routinehaulaccept).
func confirmColonyNames(ctx context.Context, h *na.Harness, report na.Report) error {
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	naming, ok := na.AsMap(facts["colonyNaming"])
	if !ok || naming == nil {
		report["confirmed_colony_names"] = "no pending naming dialog"
		return nil
	}
	confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
		"windowId": int(na.AsNumber(naming["windowId"])), "factionName": na.AsString(naming["factionName"]),
		"settlementName": na.AsString(naming["settlementName"]), "dryRun": false,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(confirmed["success"]); !success {
		return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
	}
	report["confirmed_colony_names"] = confirmed
	return nil
}

type workCellRow struct {
	cell   domain.Cell
	glow   float64
	roofed bool
}
type lampRow struct {
	definition string
	cell       domain.Cell
	radius     float64
	lit        bool
	powered    bool
}
type lightingSummary struct {
	cells map[string]workCellRow
	lamps map[string]lampRow
}

func (s lightingSummary) evidence() map[string]any {
	cells := map[string]any{}
	for id, c := range s.cells {
		cells[id] = map[string]any{"x": c.cell.X, "z": c.cell.Z, "glow": c.glow, "roofed": c.roofed}
	}
	lamps := map[string]any{}
	for id, l := range s.lamps {
		lamps[id] = map[string]any{"definition": l.definition, "x": l.cell.X, "z": l.cell.Z, "glow_radius": l.radius, "lit": l.lit, "powered": l.powered}
	}
	return map[string]any{"work_cells": cells, "lamps": lamps}
}

// readLighting decodes the typed upkeep lighting section the way the Go
// projection does: work cells keyed by bench ID, lamps keyed by building ID.
func readLighting(ctx context.Context, h *na.Harness, identity map[string]any, label string) (lightingSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return lightingSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return lightingSummary{}, err
	}
	upkeepSection, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return lightingSummary{}, fmt.Errorf("%s: upkeep unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "lighting" {
			return lightingSummary{}, fmt.Errorf("%s: lighting census unavailable: %#v", label, issue)
		}
	}
	section, _ := na.AsMap(upkeep["lighting"])
	_, facts, err := na.Outcome(section, "observed")
	if err != nil {
		return lightingSummary{}, fmt.Errorf("%s: lighting section unavailable: %w", label, err)
	}
	s := lightingSummary{cells: map[string]workCellRow{}, lamps: map[string]lampRow{}}
	for _, raw := range na.AsSlice(facts["workCells"]) {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		cell, _ := na.AsMap(row["cell"])
		roofed, _ := na.AsBool(row["roofed"])
		s.cells[na.AsString(bench["id"])] = workCellRow{cell: domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}, glow: na.AsNumber(row["glow"]), roofed: roofed}
	}
	for _, raw := range na.AsSlice(facts["lamps"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		ref, _ := na.AsMap(building["building"])
		position, _ := na.AsMap(ref["position"])
		service, _ := na.AsMap(building["service"])
		lit, _ := na.AsBool(row["lit"])
		powered, _ := na.AsBool(service["powerOn"])
		s.lamps[na.AsString(ref["id"])] = lampRow{definition: na.AsString(ref["defName"]), cell: domain.Cell{X: int32(na.AsNumber(position["x"])), Z: int32(na.AsNumber(position["z"]))}, radius: na.AsNumber(row["glowRadius"]), lit: lit, powered: powered}
	}
	return s, nil
}

func latchedOn(review store.RoutineReview, bench string) bool {
	for _, id := range review.Latches.Lighting {
		if id == bench {
			return true
		}
	}
	return false
}

// storeWait bounds the journal polls below: the shared stall budget, and
// the service exiting on its own ends a wait at once.
func storeWait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}
}

func waitLatch(ctx context.Context, s *store.Store, service *na.ServiceProcess, bench string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool {
		if !latchedOn(r, bench) {
			return false
		}
		for _, binding := range r.Goals {
			if binding.Need == policy.MaintainLighting {
				return true
			}
		}
		return false
	})
	if err != nil {
		return review, fmt.Errorf("review never latched %s with a bound MaintainLighting goal (revision %d, latches %+v): %w", bench, review.Revision, review.Latches.Lighting, err)
	}
	return review, nil
}

func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess, bench string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool { return !latchedOn(r, bench) })
	if err != nil {
		return review, fmt.Errorf("review never released the lighting latch on %s (revision %d): %w", bench, review.Revision, err)
	}
	return review, nil
}

// lightingMethods lists every method ever committed on a MaintainLighting
// goal, across goal epochs, from the journal.
func lightingMethods(ctx context.Context, s *store.Store) ([]domain.GoalMethod, error) {
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		return nil, err
	}
	var out []domain.GoalMethod
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainLighting {
			continue
		}
		goal, err := s.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return nil, err
		}
		out = append(out, goal.Methods...)
	}
	return out, nil
}

// stepReasons counts the lighting planner's step reasons from the service's
// debug log (RIMGOVERNOR_CLOCK_DEBUG), so a hold can be attributed.
func stepReasons(stderr string) map[string]int {
	out := map[string]int{}
	for _, line := range strings.Split(stderr, "\n") {
		i := strings.Index(line, "Lighting.step result: reason=")
		if i < 0 {
			continue
		}
		rest := line[i+len("Lighting.step result: reason="):]
		if j := strings.IndexByte(rest, ' '); j >= 0 {
			rest = rest[:j]
		}
		out[rest]++
	}
	return out
}
