// The temperature/heatwave case is EnsureTemperatureSafety's powered cooler
// method (#406) against a live game: a fully ramped heat wave on the
// baseline colony, an enclosed sleeping room, Cooler research and a fuelled
// generator with spare capacity. The live service composed with the
// temperature, power and work families must admit exactly one Cooler on a
// wall cell of that room with its cold side inside and its hot side
// outdoors, the colonists build it, native cooling takes the room under the
// review's hot exit and the goal recovers; the game then runs on to three
// days after the fixture and no colonist carries a Heatstroke hediff at any
// sample. An independent native read after the service releases the game
// confirms the cooler stands powered in the wall and the room reads under
// the hot exit while the outdoors stays above the hot entry.
package temperature

import (
	"context"
	"encoding/json"
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

const (
	heatwavePrefix = "temperature-heatwave"
	// heatwaveDays is the window after the fixture over which no colonist
	// may carry a Heatstroke hediff.
	heatwaveDays = 3
)

func init() {
	cases.Register(cases.Case{
		Name: "temperature/heatwave",
		Scope: "EnsureTemperatureSafety's powered cooler (#406): a fully ramped heat wave on the " + sustained.BaselineSave +
			" save with an enclosed sleeping room, Cooler research and a fuelled generator drives the live service (temperature, power, work) " +
			"to admit one Cooler through a vented wall of the room, cold side in; native cooling recovers the room under the hot exit, " +
			"the game runs to three days after the fixture with no Heatstroke hediff on any colonist, and an independent native read " +
			"confirms the powered wall cooler and the cool room under a hot sky.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Serve:  &cases.ServeSpec{Families: []string{"temperature", "power", "work"}, Prefix: heatwavePrefix},
		Budget: cases.MaxBudget,
		Run:    runHeatwave,
	})
}

// heatwaveRoom is the fixture's geometry: the room's interior cells and
// the wall cells around it.
type heatwaveRoom struct {
	interior, walls map[domain.Cell]bool
}

func heatwaveCells(rows []any) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		out[domain.Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))}] = true
	}
	return out
}

func runHeatwave(ctx context.Context, s cases.Session) (err error) {
	report, h, identity := s.Report(), s.Harness(), s.Identity()
	for _, fixture := range []string{"test/routine_sleeping_prepare", "test/heatwave_prepare"} {
		if !na.Contains(s.Names(), fixture) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture ForecastFixture,RoutineSleepingFixture", fixture)
		}
	}
	limits := policy.DefaultRoutinePolicy()
	room, err := h.Call(ctx, "prepare-room", "test/routine_sleeping_prepare", map[string]any{"outdoorSite": false})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(room["success"]); !success {
		return fmt.Errorf("routine_sleeping_prepare refused: %#v", room)
	}
	center, _ := na.AsMap(room["center"])
	prepared, err := h.Call(ctx, "prepare-heatwave", "test/heatwave_prepare", map[string]any{
		"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "hotEnterC": limits.HotEnter, "hotExitC": limits.HotExit,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("heatwave_prepare refused: %#v", prepared)
	}
	report["prepared"] = prepared
	geometry := heatwaveRoom{interior: heatwaveCells(na.AsSlice(prepared["cells"])), walls: heatwaveCells(na.AsSlice(prepared["walls"]))}
	if len(geometry.interior) != 25 || len(geometry.walls) < 20 {
		return fmt.Errorf("fixture geometry: %d interior cells, %d wall cells", len(geometry.interior), len(geometry.walls))
	}
	startTick, err := h.Tick(ctx)
	if err != nil {
		return err
	}
	report["fixture_tick"] = startTick
	deadline := startTick + heatwaveDays*na.TicksPerDay

	// Before: the review's own facts must show the sleeping room hot and the
	// colonists free of heatstroke.
	facts, err := readHeatwaveFacts(ctx, h, identity, "facts-before")
	if err != nil {
		return err
	}
	report["facts_before"] = facts
	if max, ok := facts["sleepingTemperatureMaxC"]; !ok || na.AsNumber(max) <= limits.HotEnter {
		return fmt.Errorf("facts-before: sleeping maximum %v is not above the hot entry %.1f C", max, limits.HotEnter)
	}
	if err := auditHeatstroke(ctx, h, "heatstroke-before", report); err != nil {
		return err
	}

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	defer func() {
		if err == nil {
			return
		}
		service.Stop()
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		ph, reattachErr := s.Reattach(stopCtx)
		if reattachErr != nil {
			report["postmortem_error"] = reattachErr.Error()
			return
		}
		if after, readErr := readHeatwaveFacts(stopCtx, ph, identity, "facts-postmortem"); readErr == nil {
			report["facts_postmortem"] = after
		} else {
			report["postmortem_error"] = readErr.Error()
		}
		if coolers, readErr := readWallCoolers(stopCtx, ph, identity); readErr == nil {
			evidence := make([]map[string]any, 0, len(coolers))
			for _, c := range coolers {
				evidence = append(evidence, c.evidence())
			}
			report["coolers_postmortem"] = evidence
		}
	}()
	rootPlanID, err := service.Acquire()
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)
	for _, row := range review.Development.Rows {
		if row.Reason == policy.DevelopmentEmergency {
			return fmt.Errorf("first review holds development as an emergency (patients %v); the fixture roll is unusable", review.MedicalCare.Patients)
		}
	}

	// Deficit, then the one method: a Cooler on a wall cell, cold side in.
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	goal, err := waitHeatwaveNeed(deficitCtx, journal, domain.NeedDeficit)
	deficitCancel()
	if err != nil {
		return err
	}
	report["goal_id"] = string(goal.Goal.ID)
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	_, method, err := na.WaitGoalMethod(methodCtx, journal, policy.EnsureTemperatureSafety, nil)
	methodCancel()
	if err != nil {
		return fmt.Errorf("temperature method: %w", err)
	}
	var builtCell domain.Cell
	var builtRotation domain.Rotation
	for renewals := 0; ; renewals++ {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("temperature plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok || b.Definition() != "Cooler" {
			return fmt.Errorf("temperature plan action is not a Cooler build (a passive cooler means research, power or the wall search fell through): %#v", actions[0])
		}
		builtCell, builtRotation = b.Cell(), b.Rotation()
		if !geometry.walls[builtCell] {
			return fmt.Errorf("cooler placed at %v, not on a wall cell of the sleeping room", builtCell)
		}
		placed := policy.RefrigerationCooler{Position: builtCell, Rotation: builtRotation}
		if cold, hot := placed.Cold(), placed.Hot(); !geometry.interior[cold] || geometry.interior[hot] || geometry.walls[hot] {
			return fmt.Errorf("cooler at %v facing %s has cold side %v / hot side %v; expected cold inside and hot outdoors", builtCell, builtRotation, cold, hot)
		}
		report["cooler_cell"] = map[string]any{"x": builtCell.X, "z": builtCell.Z, "rotation": string(builtRotation)}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("temperature plan: %w", err)
		}
		if !incidental {
			report["temperature_plan"] = string(method.Plan)
			report["cooler_completed_tick"] = int64(state.Progress[0].View().Tick)
			report["incidental_renewals"] = renewals
			break
		}
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.EnsureTemperatureSafety, &method)
		renewCancel()
		if err != nil {
			return fmt.Errorf("renewed temperature method after incidental cancellation #%d: %w", renewals+1, err)
		}
	}

	// Native cooling: the review releases only once every sleeping room
	// measures at or under the hot exit.
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	recovered, err := waitHeatwaveNeed(recoverCtx, journal, domain.NeedRecovered)
	recoverCancel()
	if err != nil {
		if final, loadErr := journal.LoadRoutineReview(ctx); loadErr == nil {
			data, _ := json.Marshal(final)
			report["routine_review_at_failure"] = json.RawMessage(data)
		}
		return err
	}
	report["temperature_recovered_tick"] = int64(recovered.Goal.Tick)
	methods, err := journal.LoadGoalMethods(ctx, recovered.Goal.ID, recovered.Goal.Epoch)
	if err != nil {
		return err
	}
	report["temperature_methods"] = len(methods)
	if len(methods) != 1 {
		return fmt.Errorf("the goal bound %d methods; one cooler must recover the room", len(methods))
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot,
	// then the rest of the three-day window under native simulation alone,
	// sampling every colonist's hediffs as it runs.
	report["authority_reacquisitions"] = service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := auditHeatstroke(ctx, h, "heatstroke-after-service", report); err != nil {
		return err
	}
	if err := verifyHeatwaveCooler(ctx, h, identity, builtCell, limits, report, "after-service"); err != nil {
		return err
	}
	now, err := h.Tick(ctx)
	if err != nil {
		return err
	}
	if now < deadline {
		var samples int
		advanced, err := na.RunUntil(ctx, h, "three-days", deadline-now+na.TicksPerHour, na.Wait{Stall: na.StallBudget(), Interval: 5 * time.Second}, func(ctx context.Context) (string, bool, error) {
			tick, err := h.Tick(ctx)
			if err != nil {
				return "", false, err
			}
			samples++
			if err := auditHeatstroke(ctx, h, fmt.Sprintf("heatstroke-sample-%d", samples), report); err != nil {
				return "", false, err
			}
			return na.Signature(tick / na.TicksPerHour), tick >= deadline, nil
		})
		report["three_days_ticks_run"] = advanced
		report["heatstroke_samples"] = samples
		if err != nil {
			return fmt.Errorf("three days after the fixture: %w", err)
		}
	}
	if err := auditHeatstroke(ctx, h, "heatstroke-after", report); err != nil {
		return err
	}
	return verifyHeatwaveCooler(ctx, h, identity, builtCell, limits, report, "after")
}

// waitHeatwaveNeed polls the routine review until EnsureTemperatureSafety's
// goal reports state.
func waitHeatwaveNeed(ctx context.Context, journal *store.Store, state domain.NeedState) (store.GoalState, error) {
	for {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return store.GoalState{}, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureTemperatureSafety {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return store.GoalState{}, err
			}
			if err == nil && goal.Goal.Need == state {
				return goal, nil
			}
		}
		select {
		case <-ctx.Done():
			return store.GoalState{}, fmt.Errorf("%s never reported %s: %w", policy.EnsureTemperatureSafety, state, ctx.Err())
		case <-time.After(time.Second):
		}
	}
}

func readHeatwaveFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	return map[string]any{"sleepingTemperatureMinC": observed["sleepingTemperatureMinC"], "sleepingTemperatureMaxC": observed["sleepingTemperatureMaxC"], "outdoorTemperatureC": observed["outdoorTemperatureC"], "colonistCount": observed["colonistCount"]}, nil
}

// auditHeatstroke lists every colonist's hediffs and fails on any Heatstroke;
// the report keeps the worst severity seen per label.
func auditHeatstroke(ctx context.Context, h *na.Harness, label string, report na.Report) error {
	listed, err := h.Call(ctx, label, "home/list_pawns", map[string]any{"colonistsOnly": true, "health": true})
	if err != nil {
		return err
	}
	var struck []string
	colonists := 0
	for _, raw := range na.AsSlice(listed["pawns"]) {
		row, _ := na.AsMap(raw)
		colonists++
		health, _ := na.AsMap(row["health"])
		for _, entry := range na.AsSlice(health["hediffs"]) {
			hediff, _ := na.AsMap(entry)
			if na.AsString(hediff["defName"]) == "Heatstroke" {
				struck = append(struck, fmt.Sprintf("%s %.3f", na.AsString(row["thingId"]), na.AsNumber(hediff["severity"])))
			}
		}
	}
	sort.Strings(struck)
	if colonists == 0 {
		return fmt.Errorf("%s: no colonists listed", label)
	}
	if !strings.HasPrefix(label, "heatstroke-sample-") {
		report[strings.ReplaceAll(label, "-", "_")] = map[string]any{"colonists": colonists, "heatstroke": struck}
	}
	if len(struck) > 0 {
		report["heatstroke_failure"] = map[string]any{"label": label, "colonists": colonists, "heatstroke": struck}
		return fmt.Errorf("%s: %d colonist(s) carry Heatstroke: %v", label, len(struck), struck)
	}
	return nil
}

type wallCooler struct {
	id                 string
	x, z               int32
	target             float64
	powerOn, connected bool
}

// readWallCoolers reads every built Cooler: position and setpoint through
// the typed building read, whose service field is still unimplemented, and
// the power state (CompPowerTrader powered/connected) from the detailed rows
// of home/list_buildings.
func readWallCoolers(ctx context.Context, h *na.Harness, identity map[string]any) ([]wallCooler, error) {
	reply, err := h.Wire(ctx, "coolers", "observations_list_buildings", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "defNames": []string{"Cooler"}, "statuses": []string{"built"}, "page": map[string]any{"limit": 64},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	listed, err := h.Call(ctx, "coolers-power", "home/list_buildings", map[string]any{"match": "Cooler", "status": "built", "playerOnly": true, "aggregate": false})
	if err != nil {
		return nil, err
	}
	// Keyed by anchor cell: the detailed row's thingId is RimWorld's bare
	// ThingID, the typed read's id the bridge's Thing_-prefixed form.
	power := map[domain.Cell]map[string]any{}
	for _, raw := range na.AsSlice(listed["buildings"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["defName"]) != "Cooler" {
			continue
		}
		block, _ := na.AsMap(row["power"])
		position, _ := na.AsMap(row["position"])
		power[domain.Cell{X: int32(na.AsNumber(position["x"])), Z: int32(na.AsNumber(position["z"]))}] = block
	}
	var coolers []wallCooler
	for _, raw := range na.AsSlice(observed["buildings"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["defName"]) != "Cooler" || na.AsString(row["status"]) != "built" {
			continue
		}
		position, _ := na.AsMap(building["position"])
		settings, _ := na.AsMap(row["settings"])
		cell := domain.Cell{X: int32(na.AsNumber(position["x"])), Z: int32(na.AsNumber(position["z"]))}
		block, listed := power[cell]
		if !listed {
			return nil, fmt.Errorf("home/list_buildings lists no Cooler at %v with a power block: %#v", cell, power)
		}
		on, _ := na.AsBool(block["powered"])
		connected, _ := na.AsBool(block["connected"])
		coolers = append(coolers, wallCooler{id: na.AsString(building["id"]), x: int32(na.AsNumber(position["x"])), z: int32(na.AsNumber(position["z"])), target: na.AsNumber(settings["targetTemperatureC"]), powerOn: on, connected: connected})
	}
	return coolers, nil
}

func (c wallCooler) evidence() map[string]any {
	return map[string]any{"id": c.id, "x": c.x, "z": c.z, "target_c": c.target, "power_on": c.powerOn, "connected": c.connected}
}

// verifyHeatwaveCooler is the independent native postcondition: exactly one
// Cooler, at the admitted cell, connected and powered, with a setpoint under
// the hot exit; the sleeping room measured at or under the hot exit while
// the outdoors still reads above the hot entry (the heat wave, not the
// weather, explains the cool room).
func verifyHeatwaveCooler(ctx context.Context, h *na.Harness, identity map[string]any, builtCell domain.Cell, limits policy.RoutinePolicy, report na.Report, label string) error {
	coolers, err := readWallCoolers(ctx, h, identity)
	if err != nil {
		return err
	}
	evidence := make([]map[string]any, 0, len(coolers))
	for _, c := range coolers {
		evidence = append(evidence, c.evidence())
	}
	report["coolers_"+strings.ReplaceAll(label, "-", "_")] = evidence
	if len(coolers) != 1 {
		return fmt.Errorf("%s: expected exactly one Cooler, observed %d: %v", label, len(coolers), evidence)
	}
	c := coolers[0]
	if c.x != builtCell.X || c.z != builtCell.Z {
		return fmt.Errorf("%s: the cooler stands at (%d,%d), not the admitted cell %v", label, c.x, c.z, builtCell)
	}
	if !c.connected || !c.powerOn {
		return fmt.Errorf("%s: the cooler is not connected and powered: %v", label, c.evidence())
	}
	if c.target > limits.HotExit {
		return fmt.Errorf("%s: cooler target %.1f C is above the hot exit %.1f C", label, c.target, limits.HotExit)
	}
	facts, err := readHeatwaveFacts(ctx, h, identity, "facts-"+label)
	if err != nil {
		return err
	}
	report["facts_"+strings.ReplaceAll(label, "-", "_")] = facts
	max, ok := facts["sleepingTemperatureMaxC"]
	if !ok {
		return fmt.Errorf("%s: native sleeping temperature unknown", label)
	}
	outdoors := na.AsNumber(facts["outdoorTemperatureC"])
	// Under the wave the outdoors swings +-7 C around a mean above the hot
	// entry; the room must read under the hot exit whatever the hour, and
	// the sky must still be hot enough that weather alone cannot explain it.
	if na.AsNumber(max) > limits.HotExit {
		return fmt.Errorf("%s: sleeping maximum %.1f C is above the hot exit %.1f C (outdoors %.1f C)", label, na.AsNumber(max), limits.HotExit, outdoors)
	}
	if outdoors <= limits.HotExit {
		return fmt.Errorf("%s: outdoors %.1f C is no warmer than the hot exit %.1f C; the heat wave did not hold", label, outdoors, limits.HotExit)
	}
	return nil
}
