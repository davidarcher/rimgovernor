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
//	repair -- the dark scenario played through its release, after which
//	          the fixture removes the admitted lamp (issue #161: repair
//	          after a layout change). The controller, restarted on the same
//	          journal, must re-latch the bench from the measured dark
//	          census, admit a replacement lamp within the placement radius
//	          and release again; an independent read confirms the cell lit
//	          and exactly the replacement standing.
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
// repair plays dark twice around a service restart.
var budgets = map[string]time.Duration{
	"dark": 5 * time.Minute, "partial": 5 * time.Minute,
	"outage": 10 * time.Minute, "fungus": 10 * time.Minute,
	"repair": 12 * time.Minute,
}

// fixtureScenario is the test/lighting_prepare scenario a case starts
// from; repair starts from the dark room.
func fixtureScenario(scenario string) string {
	if scenario == "repair" {
		return "dark"
	}
	return scenario
}

func init() {
	for _, scenario := range []string{"dark", "outage", "partial", "fungus", "repair"} {
		scenario := scenario
		cases.Register(cases.Case{
			Name: "light/" + scenario,
			Scope: "Native MaintainLighting vertical (" + scenario + "): a measured-dark stove interaction cell in an enclosed room " +
				"drives the live Go routine reviewer/planner to admit one affordable lamp beside it (dark, partial) or to hold for the power " +
				"family behind an unpowered lamp already in reach (outage), a room growing a light-killed cave plant is never latched (fungus), " +
				"and a lit room whose lamp is removed is re-latched and relit with a replacement (repair); " +
				"the measured glow, not the receipt, releases the latch, " +
				"confirmed by an independent native read.",
			Start:   cases.Fixture{Op: "test/lighting_prepare", Args: map[string]any{"scenario": fixtureScenario(scenario)}},
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
	// stopped is what each keep-alive counted once its service was stopped;
	// repair runs a second one for the restarted controller.
	stopped := map[string]any{}
	defer func() {
		if stopKeepAlive != nil {
			stopped["running"] = stopKeepAlive()
		}
		report["authority_reacquisitions"] = stopped
	}()

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

	lamp, err := admitAndRelease(ctx, admission{journal: journal, service: service, stove: stoveID, work: workCell, inside: inside, radius: lighting.PlacementRadius, report: report})
	if err != nil {
		return err
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	stopped["first"], stopKeepAlive = stopKeepAlive(), nil
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if scenario != "repair" {
		after, err := readLighting(ctx, h, identity, "lighting-after")
		if err != nil {
			return err
		}
		report["lighting_after"] = after.evidence()
		if err := checkLit(after, scenario, stoveID, lampID, lamp, lighting.LitGlow); err != nil {
			return fmt.Errorf("lighting-after: %w", err)
		}
		return finish(s)
	}

	// repair: the room reads lit with the admitted lamp, then the fixture
	// removes that lamp (the layout change) and the room must read dark
	// again with no lamp standing, all before the controller returns.
	lit, err := readLighting(ctx, h, identity, "lighting-lit")
	if err != nil {
		return err
	}
	report["lighting_lit"] = lit.evidence()
	if err := checkLit(lit, scenario, stoveID, lampID, lamp, lighting.LitGlow); err != nil {
		return fmt.Errorf("lighting-lit: %w", err)
	}
	disrupted, err := h.Call(ctx, "lighting-disrupt", "test/lighting_disrupt", map[string]any{"x": lamp.cell.X, "z": lamp.cell.Z, "workX": workCell.X, "workZ": workCell.Z})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(disrupted["success"]); !success {
		return fmt.Errorf("lighting-disrupt: lighting_disrupt refused: %#v", disrupted)
	}
	report["lighting_disrupted"] = disrupted
	dark, err := readLighting(ctx, h, identity, "lighting-dark-again")
	if err != nil {
		return err
	}
	report["lighting_dark_again"] = dark.evidence()
	if c := dark.cells[stoveID]; c.glow >= lighting.LitGlow || len(dark.lamps) != 0 {
		return fmt.Errorf("lighting-dark-again: the work cell still reads %.2f with %d lamps after the lamp was removed", c.glow, len(dark.lamps))
	}

	// The controller returns on the same journal: its released review must
	// re-latch the bench from the measured dark census, admit a replacement
	// lamp and release again. Only one GABP client may hold the game, so
	// the harness session is released first; the restart reuses the first
	// launch's spec and state under report["service_2"].
	if err := s.Release(); err != nil {
		return err
	}
	service.Identity = identity
	restarted, err := service.Restart(ctx)
	if err != nil {
		return fmt.Errorf("restart the controller after the layout change: %w", err)
	}
	service = restarted
	defer service.Stop()
	const repairPrefix = prefix + "-repair"
	token = service.Token
	if _, err := service.Resume(repairPrefix, identity, token, report); err != nil {
		return fmt.Errorf("resume after the layout change: %w", err)
	}
	keepAlive = &na.AuthorityKeepAlive{Service: service, Prefix: repairPrefix, Identity: identity, Token: token}
	stopKeepAlive = keepAlive.Start(ctx)
	journal, err = na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	relatchCtx, relatchCancel := context.WithTimeout(ctx, 3*time.Minute)
	relatched, err := waitLatch(relatchCtx, journal, service, stoveID)
	relatchCancel()
	if err != nil {
		return fmt.Errorf("lighting re-latch after the layout change: %w", err)
	}
	if relatched.Revision <= lamp.released {
		return fmt.Errorf("re-latched review revision %d is not past the released revision %d", relatched.Revision, lamp.released)
	}
	report["repair_latched_review_revision"] = relatched.Revision
	replacement, err := admitAndRelease(ctx, admission{journal: journal, service: service, stove: stoveID, work: workCell, inside: inside, radius: lighting.PlacementRadius, report: report, previous: &lamp.method, keys: "repair_"})
	if err != nil {
		return fmt.Errorf("repair: %w", err)
	}
	if replacement.method.Plan == lamp.method.Plan {
		return fmt.Errorf("repair reused the first lamp's plan %s", lamp.method.Plan)
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}
	journal.Close()
	stopped["repair"], stopKeepAlive = stopKeepAlive(), nil
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after the repair: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after-repair", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readLighting(ctx, h, identity, "lighting-after")
	if err != nil {
		return err
	}
	report["lighting_after"] = after.evidence()
	if err := checkLit(after, scenario, stoveID, lampID, replacement, lighting.LitGlow); err != nil {
		return fmt.Errorf("lighting-after: %w", err)
	}
	return finish(s)
}

// checkLit is the independent read every lit scenario ends on: the stove
// cell lit and exactly the admitted lamp standing lit where it was placed
// (dark, repair), or that lamp beside the fixture torch, both lit and
// nothing else doubled up (partial).
func checkLit(after lightingSummary, scenario, stoveID, lampID string, lamp admitted, litGlow float64) error {
	stoveAfter, ok := after.cells[stoveID]
	if !ok || stoveAfter.glow < litGlow {
		return fmt.Errorf("stove cell glow %.2f is still under %.2f", stoveAfter.glow, litGlow)
	}
	expectedLamps := 1
	if scenario == "partial" {
		expectedLamps = 2
	}
	if len(after.lamps) != expectedLamps {
		return fmt.Errorf("expected exactly %d lamps, observed %d: %+v", expectedLamps, len(after.lamps), after.lamps)
	}
	found := false
	for id, l := range after.lamps {
		if scenario == "partial" && id == lampID {
			if !l.lit {
				return fmt.Errorf("the fixture torch %+v went out during the run", l)
			}
			continue
		}
		if l.cell != lamp.cell || l.definition != lamp.definition || !l.lit {
			return fmt.Errorf("the surviving lamp %+v is not the lit %s admitted at %v", l, lamp.definition, lamp.cell)
		}
		found = true
	}
	if !found {
		return fmt.Errorf("the admitted %s at %v does not stand: %+v", lamp.definition, lamp.cell, after.lamps)
	}
	return nil
}

// admission is what admitAndRelease needs from the running case: the
// service's journal, the latched stove, the room and where to report.
type admission struct {
	journal *store.Store
	service *na.ServiceProcess
	stove   string
	work    domain.Cell
	inside  func(domain.Cell) bool
	radius  int32
	report  na.Report
	// previous, when set, is the plan of a lamp already admitted on the
	// same journal: the repair wait must see a newer method. keys prefixes
	// the report keys ("" on the first pass, "repair_" on the second).
	previous *domain.GoalMethod
	keys     string
}

// admitted is the lamp admitAndRelease saw built: its cell, definition
// and the method that placed it.
type admitted struct {
	cell       domain.Cell
	definition string
	method     domain.GoalMethod
	// released is the review revision that let go of the latch.
	released uint64
}

// admitAndRelease follows one lighting method from commitment through the
// build to the measured census releasing the latch: one lamp build on a
// free interior cell within the placement radius of the interaction cell,
// never on it, renewed across incidental cancellations.
func admitAndRelease(ctx context.Context, a admission) (admitted, error) {
	journal, service, report := a.journal, a.service, a.report
	key := func(name string) string { return a.keys + name }
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainLighting, a.previous)
	methodCancel()
	if err != nil {
		return admitted{}, fmt.Errorf("lighting method: %w", err)
	}
	report[key("goal_id")] = string(goalID)
	var builtCell domain.Cell
	var builtDefinition string
	for renewals := 0; ; renewals++ {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return admitted{}, err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return admitted{}, fmt.Errorf("lighting plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok {
			return admitted{}, fmt.Errorf("lighting plan action is not a building: %#v", actions[0])
		}
		builtCell, builtDefinition = b.Cell(), b.Definition()
		if builtDefinition != "TorchLamp" {
			return admitted{}, fmt.Errorf("lighting admitted %s; a colony without a power source must choose the TorchLamp", builtDefinition)
		}
		if builtCell == a.work || !a.inside(builtCell) || max(abs(builtCell.X-a.work.X), abs(builtCell.Z-a.work.Z)) > a.radius {
			return admitted{}, fmt.Errorf("lamp placed at %v: not a free interior cell within %d of the work cell %v", builtCell, a.radius, a.work)
		}
		report[key("lamp_cell")] = map[string]any{"x": builtCell.X, "z": builtCell.Z, "definition": builtDefinition}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return admitted{}, fmt.Errorf("lighting plan: %w", err)
		}
		if !incidental {
			report[key("lighting_plan")] = string(method.Plan)
			report[key("lighting_completed_tick")] = int64(state.Progress[0].View().Tick)
			report[key("incidental_renewals")] = renewals
			break
		}
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.MaintainLighting, &method)
		renewCancel()
		if err != nil {
			return admitted{}, fmt.Errorf("renewed lighting method after incidental cancellation #%d: %w", renewals+1, err)
		}
	}

	// The measured census releases the latch once the cell reads lit.
	releaseCtx, releaseCancel := context.WithTimeout(ctx, 5*time.Minute)
	released, err := waitRelease(releaseCtx, journal, service, a.stove)
	releaseCancel()
	if err != nil {
		return admitted{}, fmt.Errorf("lighting release: %w", err)
	}
	report[key("released_review_revision")] = released.Revision
	methods, err := lightingMethods(ctx, journal)
	if err != nil {
		return admitted{}, err
	}
	report[key("lighting_methods")] = len(methods)
	return admitted{cell: builtCell, definition: builtDefinition, method: method, released: released.Revision}, nil
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
