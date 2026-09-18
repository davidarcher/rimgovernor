// Package light holds the MaintainLighting vertical (issue #6 slice 3): a
// live game and a live rimgovernor Go player-control service composed with
// the lighting family, one case per scenario:
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
//	partial -- a wider room with a lit torch at the far end whose glow
//	          radius reaches the interaction cell but whose light has fallen
//	          off below lit by then (the issue's "partially lit bench"). The
//	          service must not defer to the far torch: it admits a lamp of
//	          its own beside the cell, the census releases on measured glow,
//	          and both lamps stand lit afterwards.
//	fungus -- the dark room grows a cave plant that dies to light (the
//	          issue's "protected fungus room"). The census must mark the
//	          work cell light-sensitive, the review must never latch it,
//	          no lighting method may be committed over the hold, and the
//	          plant must still be alive on an independent native read.
//
// Uses the private disposable test/lighting_prepare fixture
// (LightingFixture.cs). The case's own bridge session and the service's
// are used sequentially, never concurrently (one GABP client per game).
package light

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "light-accept"

// hold is how long the outage and fungus cases require the service to
// hold without committing a lighting method.
const hold = 4 * time.Minute

// budgets are ~2x the measured healthy runs (403eebbb): dark and partial
// admit a lamp in under a minute; outage and fungus play a power hold or a
// day of plant growth for ~4 minutes.
var budgets = map[string]time.Duration{
	"dark": 5 * time.Minute, "partial": 5 * time.Minute,
	"outage": 10 * time.Minute, "fungus": 10 * time.Minute,
}

func init() {
	for _, scenario := range []string{"dark", "outage", "partial", "fungus"} {
		scenario := scenario
		cases.Register(cases.Case{
			Name: "light/" + scenario,
			Scope: "Native MaintainLighting vertical (" + scenario + "): a measured-dark stove interaction cell in an enclosed room " +
				"drives the live Go routine reviewer/planner to admit one affordable lamp beside it (dark, partial) or to hold for the power " +
				"family behind an unpowered lamp already in reach (outage), and a room growing a light-killed cave plant is never latched (fungus); " +
				"the measured glow, not the receipt, releases the latch, " +
				"confirmed by an independent native read.",
			Start:   cases.Fixture{Op: "test/lighting_prepare", Args: map[string]any{"scenario": scenario}},
			Service: true,
			Budget:  budgets[scenario],
			Run:     func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	// Failure evidence: the same independent lighting read a pass ends
	// with, taken once the service has let go of the game.
	defer func() {
		if _, hasAfter := report["lighting_after"]; hasAfter {
			return
		}
		if service != nil {
			service.Stop()
			na.ReportPhases(report, s.Config().Output, true)
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		ph, err := s.Reattach(stopCtx)
		if err != nil {
			report["postmortem_error"] = "reopen session for the postmortem: " + err.Error()
			return
		}
		if after, err := readLighting(stopCtx, ph, identity, "lighting-postmortem"); err == nil {
			report["lighting_postmortem"] = after.evidence()
		} else {
			report["lighting_postmortem_error"] = err.Error()
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	stoveID := na.AsString(prepared["stove"])
	lampID := na.AsString(prepared["lamp"])
	plantCellMap, _ := na.AsMap(prepared["plantCell"])
	plantCell := domain.Cell{X: int32(na.AsNumber(plantCellMap["x"])), Z: int32(na.AsNumber(plantCellMap["z"]))}
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
	switch scenario {
	case "outage":
		lamp, ok := before.lamps[lampID]
		if !ok || lamp.lit || lamp.powered {
			return fmt.Errorf("lighting-before: fixture lamp is not an unlit unpowered lamp: %+v", lamp)
		}
	case "partial":
		// The far torch is lit and its radius reaches the cell, yet the
		// cell measures dark: partial coverage, beyond the placement radius.
		lamp, ok := before.lamps[lampID]
		reach := math.Hypot(float64(lamp.cell.X-workCell.X), float64(lamp.cell.Z-workCell.Z))
		if !ok || !lamp.lit || reach > lamp.radius || max(abs(lamp.cell.X-workCell.X), abs(lamp.cell.Z-workCell.Z)) <= lighting.PlacementRadius {
			return fmt.Errorf("lighting-before: fixture torch is not a lit lamp reaching the cell from beyond the placement radius: %+v (reach %.1f)", lamp, reach)
		}
	case "fungus":
		if !stove.lightSensitive {
			return fmt.Errorf("lighting-before: the census does not mark the fungus room's work cell light-sensitive: %+v", stove)
		}
		if len(before.lamps) != 0 {
			return fmt.Errorf("lighting-before: %d lamps present before the controller acts", len(before.lamps))
		}
		plant, err := inspectPlant(ctx, h, plantCell, "plant-before")
		if err != nil {
			return err
		}
		report["plant_before"] = plant
		if alive, _ := na.AsBool(plant["alive"]); !alive {
			return fmt.Errorf("plant-before: no live cave plant at %v: %#v", plantCell, plant)
		}
	default:
		if len(before.lamps) != 0 {
			return fmt.Errorf("lighting-before: %d lamps present before the controller acts", len(before.lamps))
		}
	}
	if scenario != "fungus" && stove.lightSensitive {
		return fmt.Errorf("lighting-before: work cell marked light-sensitive without a cave plant: %+v", stove)
	}

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment, which only the work family applies.
	// The outage and fungus holds are attributed from the scheduler's
	// step-reason trace, which only RIMGOVERNOR_CLOCK_DEBUG prints.
	service, err = s.Launch(ctx, na.ServiceLaunch{
		Families: []string{"lighting", "work"},
		Extra:    append(na.ClockSpeedArgs(), na.FlightRecorderArgs(s.Config().Output, true)...),
		Env:      []string{"RIMGOVERNOR_CLOCK_DEBUG=1"},
	})
	if err != nil {
		return err
	}
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

	if scenario == "fungus" {
		// Hold: the protected room must never latch, whatever the glow.
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
			return fmt.Errorf("lighting committed %d methods in a protected fungus room: %#v", len(methods), methods)
		}
		still, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return err
		}
		if latchedOn(still, stoveID) || len(still.Latches.Lighting) != 0 {
			return fmt.Errorf("lighting latched a protected fungus room (revision %d, latches %+v)", still.Revision, still.Latches.Lighting)
		}
		report["held_review_revision"] = still.Revision
		stderr, err := os.ReadFile(service.StderrPath())
		if err != nil {
			return fmt.Errorf("read service stderr: %w", err)
		}
		reasons := stepReasons(string(stderr))
		report["lighting_step_reasons"] = reasons
		// The planner's own reason for an inactive goal, not a policy method.
		const noDeficit = "no_active_deficit"
		if reasons[noDeficit] == 0 {
			return fmt.Errorf("service never reported %s; observed step reasons %v", noDeficit, reasons)
		}
		for reason := range reasons {
			if reason != noDeficit {
				return fmt.Errorf("service reported %s in a protected fungus room: %v", reason, reasons)
			}
		}
		if err := na.AssertRoutineRunning(service.Get); err != nil {
			return err
		}
		journal.Close()
		service.Stop()
		if h, err = s.Reattach(ctx); err != nil {
			return fmt.Errorf("reopen harness session after service stop: %w", err)
		}
		if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		after, err := readLighting(ctx, h, identity, "lighting-after")
		if err != nil {
			return err
		}
		report["lighting_after"] = after.evidence()
		if len(after.lamps) != 0 {
			return fmt.Errorf("lighting-after: %d lamps stand in the protected fungus room", len(after.lamps))
		}
		if s := after.cells[stoveID]; s.glow >= lighting.LitGlow || !s.lightSensitive {
			return fmt.Errorf("lighting-after: work cell no longer a dark light-sensitive cell: %+v", s)
		}
		plant, err := inspectPlant(ctx, h, plantCell, "plant-after")
		if err != nil {
			return err
		}
		report["plant_after"] = plant
		if alive, _ := na.AsBool(plant["alive"]); !alive {
			return fmt.Errorf("plant-after: the cave plant at %v did not survive: %#v", plantCell, plant)
		}
		return finish(s)
	}

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
		stderr, err := os.ReadFile(service.StderrPath())
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
		if h, err = s.Reattach(ctx); err != nil {
			return fmt.Errorf("reopen harness session after service stop: %w", err)
		}
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
		return finish(s)
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
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readLighting(ctx, h, identity, "lighting-after")
	if err != nil {
		return err
	}
	report["lighting_after"] = after.evidence()
	stoveAfter, ok := after.cells[stoveID]
	if !ok || stoveAfter.glow < lighting.LitGlow {
		return fmt.Errorf("lighting-after: stove cell glow %.2f is still under %.2f", stoveAfter.glow, lighting.LitGlow)
	}
	// dark: only the admitted lamp; partial: the fixture torch and the
	// admitted lamp, both lit, nothing else doubled up.
	expectedLamps := 1
	if scenario == "partial" {
		expectedLamps = 2
	}
	if len(after.lamps) != expectedLamps {
		return fmt.Errorf("expected exactly %d lamps after the run, observed %d: %+v", expectedLamps, len(after.lamps), after.lamps)
	}
	admitted := false
	for id, l := range after.lamps {
		if scenario == "partial" && id == lampID {
			if !l.lit {
				return fmt.Errorf("the fixture torch %+v went out during the run", l)
			}
			continue
		}
		if l.cell != builtCell || l.definition != builtDefinition || !l.lit {
			return fmt.Errorf("the surviving lamp %+v is not the lit %s admitted at %v", l, builtDefinition, builtCell)
		}
		admitted = true
	}
	if !admitted {
		return fmt.Errorf("the admitted %s at %v does not stand after the run: %+v", builtDefinition, builtCell, after.lamps)
	}
	return finish(s)
}

// finish summarizes the stopped service's flight recording and checks the
// game's startup log, the way every scenario ends.
func finish(s cases.Session) error {
	na.ReportPhases(s.Report(), s.Config().Output, true)
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

type workCellRow struct {
	cell           domain.Cell
	glow           float64
	roofed         bool
	lightSensitive bool
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
		cells[id] = map[string]any{"x": c.cell.X, "z": c.cell.Z, "glow": c.glow, "roofed": c.roofed, "light_sensitive": c.lightSensitive}
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
		sensitive, _ := na.AsBool(row["lightSensitive"])
		s.cells[na.AsString(bench["id"])] = workCellRow{cell: domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}, glow: na.AsNumber(row["glow"]), roofed: roofed, lightSensitive: sensitive}
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

// inspectPlant reads the fixture's own account of the plant on a cell (alive,
// growth, dying) and the measured glow there, independent of the census.
func inspectPlant(ctx context.Context, h *na.Harness, cell domain.Cell, label string) (map[string]any, error) {
	reply, err := h.Call(ctx, label, "test/lighting_inspect", map[string]any{"x": cell.X, "z": cell.Z})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(reply["success"]); !success {
		return nil, fmt.Errorf("%s: lighting_inspect refused: %#v", label, reply)
	}
	return reply, nil
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
