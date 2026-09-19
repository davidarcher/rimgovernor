// Package power holds the EnsureBasicPower reliability vertical (issue #6
// slice 1, milestone A; #413/#418 for the wind, geothermal and storage
// scenarios): a live game and a live rimgovernor Go player-control service
// composed with the power family, one case per scenario:
//
//	fuel       -- a wood-fired generator is out of fuel and its one consumer
//	              unpowered, with unforbidden wood nearby. Refuelling is
//	              ordinary colonist work, so the service must hold
//	              (waiting_for_refuel) and commit no generator or conduit
//	              method; the native colonists refuel it, and an independent
//	              native read then shows the generator fuelled and the
//	              consumer powered.
//	reserve    -- every consumer is powered right now, but the network drains
//	              a partly charged battery faster than its one generator
//	              supplies, so the reserve runway is under a day. The service
//	              must admit one more generator (a plan whose actions are a
//	              single generator definition) and the colonists must build
//	              it.
//	rain       -- the controller encloses an exposed battery and replaces
//	              ordinary conduits, then a full day of forced rain leaves no
//	              short circuits, fires or equipment damage (#405).
//	battery    -- a solar generator, a lamp in a roofed room and no bank at
//	              all (#418): the day covers the draw but nothing carries the
//	              night, so the service must add storage (one plan whose
//	              single action is a Battery, sited indoors) rather than
//	              another generator. Once built, the independent read shows
//	              the controller's battery as the only one, connected, and a
//	              night driven at speed finds it charged with the lamp still
//	              powered.
//	wind       -- a lamp in a cleared field, no generator, Batteries
//	              researched and no wood in stock: the service must raise a
//	              WindTurbine (one generator plan) on a site whose native
//	              catch zone is entirely clear, then connect it. The read
//	              shows the turbine as the only generator, connected, with no
//	              blocked catch-zone cell, and the lamp powered whenever the
//	              turbine is producing.
//	geothermal -- a lamp, a free steam geyser in reach and GeothermalPower
//	              researched: the service must raise a GeothermalGenerator on
//	              the geyser ahead of every other generator, then connect it.
//	              The read shows the generator as the only one, standing on
//	              the geyser, connected, and the lamp powered.
//
// Uses the private disposable test/power_prepare and test/power_observe
// fixtures (PowerFixture.cs). The case's own bridge session and the
// service's are used sequentially (one GABP client per game).
package power

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const prefix = "power-accept"

// budgets are ~2x the measured healthy runs (403eebbb): the refuel hold
// plays ~4 minutes, the reserve generator is admitted in under one and
// built in about two. The storage and site scenarios add a bounded window
// for follow-up conduit plans and, for the battery, a night driven at
// Superfast.
var budgets = map[string]time.Duration{
	"fuel": 10 * time.Minute, "reserve": 5 * time.Minute, "rain": 14 * time.Minute,
	"battery": 15 * time.Minute, "wind": 12 * time.Minute, "geothermal": 12 * time.Minute,
}

// firstAction is the definition the scenario's first admitted power plan
// must build; generator scenarios accept any generator.
var firstAction = map[string]string{"battery": policy.BatteryDefinition, "wind": policy.WindTurbineDefinition, "geothermal": policy.GeothermalDefinition}

func init() {
	for _, scenario := range []string{"fuel", "reserve", "battery", "rain", "wind", "geothermal"} {
		scenario := scenario
		cases.Register(cases.Case{
			Name: "power/" + scenario,
			Scope: "Native EnsureBasicPower reliability vertical (" + scenario + "): an out-of-fuel generator holds the " +
				"live Go power family until native colonists refuel it, a draining battery under a day of reserve has the " +
				"family admit one more generator that the colonists build, a solar-only network has it bank the night in a " +
				"battery it sites indoors, a cleared field has it raise a wind turbine on a clear catch zone, or a free " +
				"steam geyser has it raise a geothermal generator there; the rain case encloses a battery and replaces " +
				"unsafe wiring before a full day of rain. Each is confirmed by an independent native read.",
			Start:   cases.Fixture{Op: "test/power_prepare", Args: map[string]any{"scenario": scenario}},
			Service: true,
			Budget:  budgets[scenario],
			Run:     func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	if !na.Contains(s.Names(), "test/power_observe") {
		return fmt.Errorf("missing test/power_observe in discovery; rebuild the native mod with -Fixture PowerFixture")
	}
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	generatorID := na.AsString(prepared["generator"])
	spare, _ := na.AsMap(prepared["spareCell"])
	var ids, consumers []string
	if generatorID != "" {
		ids = append(ids, generatorID)
	}
	for _, raw := range na.AsSlice(prepared["consumers"]) {
		consumers = append(consumers, fmt.Sprint(raw))
	}
	ids = append(ids, consumers...)
	if battery := na.AsString(prepared["battery"]); battery != "" {
		ids = append(ids, battery)
	}
	observe := func(label string) (map[string]any, error) {
		reply, err := h.Call(ctx, label, "test/power_observe", map[string]any{"ids": strings.Join(ids, ",")})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(reply["success"]); !success {
			return nil, fmt.Errorf("%s: power_observe refused: %#v", label, reply)
		}
		return reply, nil
	}
	before, err := observe("power-before")
	if err != nil {
		return err
	}
	report["power_before"] = before
	rows := indexRows(before)
	generatorBefore := rows[generatorID]
	switch scenario {
	case "fuel":
		if out, _ := na.AsBool(generatorBefore["outOfFuel"]); !out {
			return fmt.Errorf("power-before: fixture generator is not out of fuel: %#v", generatorBefore)
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); on {
				return fmt.Errorf("power-before: consumer %s already powered: %#v", id, rows[id])
			}
		}
	case "reserve":
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-before: consumer %s should be powered on battery reserve: %#v", id, rows[id])
			}
		}
	case "battery":
		if na.AsString(generatorBefore["defName"]) != "SolarGenerator" || len(na.AsSlice(before["generators"])) != 1 {
			return fmt.Errorf("power-before: the battery fixture should hold one solar generator: %#v / %#v", generatorBefore, before["generators"])
		}
		if n := len(na.AsSlice(before["batteries"])); n != 0 {
			return fmt.Errorf("power-before: the battery fixture spawned %d batteries: %#v", n, before["batteries"])
		}
	case "wind", "geothermal":
		if generatorID != "" || len(na.AsSlice(before["generators"])) != 0 {
			return fmt.Errorf("power-before: the %s fixture spawned a generator: %#v", scenario, before["generators"])
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); on {
				return fmt.Errorf("power-before: consumer %s powered with no generator: %#v", id, rows[id])
			}
		}
		if scenario == "geothermal" && na.AsString(prepared["geyser"]) == "" {
			return fmt.Errorf("power-before: the geothermal fixture reported no geyser: %#v", prepared)
		}
	}
	// The typed colony facts the Go family reads must carry the fuel and
	// network facts milestone A added (and, for geothermal, the geyser).
	topology, err := readPowerFacts(ctx, h, identity, "colony-facts-before")
	if err != nil {
		return err
	}
	report["colony_power_before"] = topology
	if scenario == "geothermal" && len(na.AsSlice(topology["geysers"])) == 0 {
		return fmt.Errorf("colony-facts-before: the development census lists no steam geyser")
	}

	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"power", "work", "naming"}, Extra: na.ClockSpeedArgs()})
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
	rootPlanID, err := service.SubmitAndResume(prefix, identity, map[string]any{
		"defName": "Wall", "x": int(na.AsNumber(spare["x"])), "z": int(na.AsNumber(spare["z"])), "rotation": "north", "stuff": "WoodLog",
	}, token, report)
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

	switch scenario {
	case "rain":
		deadline := time.Now().Add(8 * time.Minute)
		shelter, wiring, safe := false, false, false
		for time.Now().Before(deadline) {
			r, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return err
			}
			open, covered := false, false
			for _, binding := range r.Goals {
				if binding.Need != policy.EnsureBasicPower {
					continue
				}
				g, err := journal.LoadGoal(ctx, binding.Goal)
				if err != nil {
					return err
				}
				covered = g.Goal.Need == domain.NeedRecovered
				for _, m := range g.Methods {
					p, err := journal.LoadPlan(ctx, m.Plan)
					if err != nil {
						return err
					}
					for _, progress := range p.Progress {
						b, ok := progress.Action().Building()
						if ok {
							shelter = shelter || b.Definition() == "Wall"
							wiring = wiring || b.Definition() == "HiddenConduit"
						}
						open = open || progress.View().Stage != domain.Completed
					}
				}
			}
			if shelter && wiring && !open && covered {
				safe = true
				break
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(time.Second):
			}
		}
		if !safe {
			return fmt.Errorf("power protection did not complete: shelter=%v wiring=%v", shelter, wiring)
		}
	case "fuel":
		// Hold for a bounded window of Fast-speed simulation: the power goal
		// may bind (the consumer is unpowered) but no method may be committed
		// while the generator merely wants refuelling.
		deadline := time.Now().Add(4 * time.Minute)
		sawGoal := false
		for time.Now().Before(deadline) {
			r, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return err
			}
			for _, binding := range r.Goals {
				if binding.Need != policy.EnsureBasicPower {
					continue
				}
				sawGoal = true
				goal, err := journal.LoadGoal(ctx, binding.Goal)
				if err != nil {
					return err
				}
				if len(goal.Methods) != 0 {
					return fmt.Errorf("power family committed %d methods while the generator was only out of fuel: %#v", len(goal.Methods), goal.Methods)
				}
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(3 * time.Second):
			}
		}
		report["power_goal_bound"] = sawGoal
	default:
		methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
		goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.EnsureBasicPower, nil)
		methodCancel()
		if err != nil {
			return fmt.Errorf("first power method: %w", err)
		}
		report["goal_id"] = string(goalID)
		for renewals := 0; ; renewals++ {
			plan, err := journal.LoadPlan(ctx, method.Plan)
			if err != nil {
				return err
			}
			actions := plan.Spec.Actions()
			if len(actions) != 1 {
				return fmt.Errorf("first power plan %s has %d actions, expected 1", method.Plan, len(actions))
			}
			b, ok := actions[0].Building()
			if !ok {
				return fmt.Errorf("first power plan action is not a build: %#v", actions[0])
			}
			switch want := firstAction[scenario]; {
			case want != "" && b.Definition() != want:
				return fmt.Errorf("first power plan builds %s, expected %s: %#v", b.Definition(), want, actions[0])
			case want == "" && !na.Contains(policy.GeneratorDefinitions, b.Definition()):
				return fmt.Errorf("power plan action is not a generator build: %#v", actions[0])
			}
			report["first_definition"] = b.Definition()
			report["first_center"] = fmt.Sprint(b.Cell())
			doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
			state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
			doneCancel()
			if err != nil {
				return fmt.Errorf("first power plan: %w", err)
			}
			if !incidental {
				report["first_plan"] = string(method.Plan)
				report["first_completed_tick"] = int64(state.Progress[0].View().Tick)
				report["incidental_renewals"] = renewals
				break
			}
			renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
			_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.EnsureBasicPower, &method)
			renewCancel()
			if err != nil {
				return fmt.Errorf("renewed first power method after incidental cancellation #%d: %w", renewals+1, err)
			}
		}
		if scenario != "reserve" {
			// The new building may stand off the conduit line, so the family's
			// next methods route conduits to it; a turbine or geothermal
			// network may then bank its surplus. Follow-up plans are bounded
			// in number and in the wait for the next one, and none may raise
			// another generator.
			seen := map[domain.PlanID]bool{method.Plan: true}
			for followUps := 0; followUps < 4; followUps++ {
				waitCtx, waitCancel := context.WithTimeout(ctx, 2*time.Minute)
				_, next, err := na.WaitGoalMethodExcluding(waitCtx, journal, policy.EnsureBasicPower, seen)
				waitCancel()
				if err != nil {
					// A satisfied goal admits nothing more: the bounded wait
					// running out or the journal going quiet ends the
					// follow-ups, and the native read below judges the net.
					var stalled *na.WaitError
					if ctx.Err() == nil && (errors.Is(err, context.DeadlineExceeded) || errors.As(err, &stalled)) {
						report["follow_up_wait_ended"] = err.Error()
						break
					}
					return fmt.Errorf("follow-up power method #%d: %w", followUps+1, err)
				}
				seen[next.Plan] = true
				plan, err := journal.LoadPlan(ctx, next.Plan)
				if err != nil {
					return err
				}
				for _, action := range plan.Spec.Actions() {
					b, ok := action.Building()
					if !ok || b.Definition() != "HiddenConduit" && !(scenario != "battery" && b.Definition() == policy.BatteryDefinition) {
						return fmt.Errorf("power family committed an unexpected action after the %s: %#v", report["first_definition"], action)
					}
				}
				doneCtx, doneCancel := context.WithTimeout(ctx, 5*time.Minute)
				state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, next.Plan)
				doneCancel()
				if err != nil {
					return fmt.Errorf("follow-up power plan %s: %w", next.Plan, err)
				}
				report[fmt.Sprintf("follow_up_plan_%d", followUps+1)] = map[string]any{
					"plan": string(next.Plan), "actions": len(plan.Spec.Actions()), "incidental": incidental,
					"completed_tick": int64(state.Progress[0].View().Tick),
				}
			}
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
	after, err := observe("power-after")
	if err != nil {
		return err
	}
	report["power_after"] = after
	if scenario == "rain" {
		return checkRain(ctx, s, h, after, observe)
	}
	rows = indexRows(after)
	generators := na.AsSlice(after["generators"])
	if scenario != "fuel" && scenario != "reserve" {
		// The built generator or battery joins the read so its connection
		// (and fuel, catch zone or charge) is in evidence alongside the
		// consumer it should power.
		for _, raw := range append(generators, na.AsSlice(after["batteries"])...) {
			if id := fmt.Sprint(raw); !na.Contains(ids, id) {
				ids = append(ids, id)
			}
		}
		if after, err = observe("power-after-built"); err != nil {
			return err
		}
		report["power_after"] = after
		rows = indexRows(after)
	}
	if scenario == "fuel" {
		if out, _ := na.AsBool(rows[generatorID]["outOfFuel"]); out {
			return fmt.Errorf("power-after: generator still out of fuel after the hold window; colonists never refuelled it: %#v", rows[generatorID])
		}
	}
	// step runs the paused game for a bounded window at the given speed
	// and reads the network again.
	step := func(label, speed string, hold time.Duration) error {
		if _, err := h.Call(ctx, "resume-"+label, "rimworld/set_time_speed", map[string]any{"speed": speed, "ultraSpeedBoost": false}); err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(hold):
		}
		if _, err := h.Call(ctx, "pause-"+label, "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		if after, err = observe("power-" + label); err != nil {
			return err
		}
		report["power_after"] = after
		rows = indexRows(after)
		generators = na.AsSlice(after["generators"])
		return nil
	}
	switch scenario {
	case "fuel", "wind", "geothermal":
		// A refuel landing just before the pause leaves the generator
		// fuelled while the power net has not ticked its consumers back on;
		// let the game run a few seconds and read again before judging. A
		// turbine only powers the lamp while the wind blows, so its window
		// waits for output as well.
		attempts := 3
		if scenario == "wind" {
			attempts = 12
		}
		for attempt := 1; attempt <= attempts && !allPowered(rows, consumers); attempt++ {
			if err := step(fmt.Sprintf("after-%d", attempt), "Normal", 5*time.Second); err != nil {
				return err
			}
		}
	case "battery":
		// Drive the clock in owned windows (a letter pause stopped a plain
		// Superfast run for good, 2026-09-19) until a night read (sky glow
		// under 0.3) finds the bank charged; the lamp must then be powered
		// from it. A bank that once held charge and reads empty on a later
		// night has been drained without a day to refill it: the storage
		// sizing failed. Forty windows of 5000 ticks span over three days.
		charged := false
		for attempt := 1; ; attempt++ {
			if _, err := s.Advance(ctx, 5000); err != nil {
				return fmt.Errorf("night window %d: %w", attempt, err)
			}
			if after, err = observe(fmt.Sprintf("power-night-%d", attempt)); err != nil {
				return err
			}
			report["power_after"] = after
			rows = indexRows(after)
			generators = na.AsSlice(after["generators"])
			stored := bankStored(rows, na.AsSlice(after["batteries"]))
			night := na.AsNumber(after["skyGlow"]) < 0.3
			if stored > 0 {
				charged = true
			}
			if night && charged && stored == 0 {
				return fmt.Errorf("power-night-%d: the bank charged earlier but reads empty at night (hour %v): %#v", attempt, after["hour"], after["batteries"])
			}
			if night && stored > 0 {
				report["night_read"] = attempt
				break
			}
			if attempt == 40 {
				return fmt.Errorf("no night with a charged bank within %d clock windows (last hour %v, glow %v, stored %.1f Wd)", attempt, after["hour"], after["skyGlow"], stored)
			}
		}
	}
	switch scenario {
	case "fuel":
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-after: consumer %s still unpowered after refuelling: %#v", id, rows[id])
			}
		}
		if len(generators) != 1 {
			return fmt.Errorf("power-after: expected the single fixture generator, observed %d: %#v", len(generators), generators)
		}
	case "reserve":
		if len(generators) != 2 {
			return fmt.Errorf("power-after: expected the fixture generator plus one built by the controller, observed %d: %#v", len(generators), generators)
		}
	case "battery":
		batteries := na.AsSlice(after["batteries"])
		if len(batteries) != 1 {
			return fmt.Errorf("power-after: expected exactly the battery the controller built, observed %d: %#v", len(batteries), batteries)
		}
		if len(generators) != 1 {
			return fmt.Errorf("power-after: expected only the fixture solar generator, observed %d: %#v", len(generators), generators)
		}
		built := rows[fmt.Sprint(batteries[0])]
		if connected, _ := na.AsBool(built["connected"]); !connected {
			return fmt.Errorf("power-after: the controller's battery is not connected to a power net: %#v", built)
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-after: consumer %s unpowered at night with %.1f Wd banked: %#v", id, na.AsNumber(built["storedWattDays"]), rows[id])
			}
		}
	case "wind", "geothermal":
		if len(generators) != 1 {
			return fmt.Errorf("power-after: expected exactly the generator the controller built, observed %d: %#v", len(generators), generators)
		}
		built := rows[fmt.Sprint(generators[0])]
		if def := na.AsString(built["defName"]); def != firstAction[scenario] {
			return fmt.Errorf("power-after: the controller's generator is a %s, expected %s: %#v", def, firstAction[scenario], built)
		}
		if connected, _ := na.AsBool(built["connected"]); !connected {
			return fmt.Errorf("power-after: the controller's generator is not connected to a power net: %#v", built)
		}
		if scenario == "wind" {
			if blocked := na.AsNumber(built["windBlockedCells"]); blocked != 0 {
				return fmt.Errorf("power-after: the turbine's catch zone has %v blocked cells: %#v", blocked, built)
			}
			if generatorOutput(rows, generators) == 0 {
				report["wind_calm_at_read"] = true
				break
			}
		} else {
			cell, _ := na.AsMap(prepared["geyserCell"])
			if na.AsNumber(built["x"]) != na.AsNumber(cell["x"]) || na.AsNumber(built["z"]) != na.AsNumber(cell["z"]) {
				return fmt.Errorf("power-after: the geothermal generator stands at (%v,%v), not on the geyser %#v", built["x"], built["z"], cell)
			}
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-after: consumer %s still unpowered after the controller's %s: %#v", id, firstAction[scenario], rows[id])
			}
		}
	}
	topology, err = readPowerFacts(ctx, h, identity, "colony-facts-after")
	if err != nil {
		return err
	}
	report["colony_power_after"] = topology
	return checkStartupLog(s)
}

// checkStartupLog is the run's last assertion: no native error in the
// game's startup log.
func checkStartupLog(s cases.Session) error {
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// allPowered reports whether every listed consumer row reads powerOn.
func allPowered(rows map[string]map[string]any, ids []string) bool {
	for _, id := range ids {
		if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
			return false
		}
	}
	return true
}

// generatorOutput sums the listed generators' current output in watts.
func generatorOutput(rows map[string]map[string]any, generators []any) float64 {
	total := 0.0
	for _, raw := range generators {
		total += na.AsNumber(rows[fmt.Sprint(raw)]["powerOutputW"])
	}
	return total
}

// bankStored sums the listed batteries' stored watt-days.
func bankStored(rows map[string]map[string]any, batteries []any) float64 {
	total := 0.0
	for _, raw := range batteries {
		total += na.AsNumber(rows[fmt.Sprint(raw)]["storedWattDays"])
	}
	return total
}

func indexRows(reply map[string]any) map[string]map[string]any {
	out := map[string]map[string]any{}
	for _, raw := range na.AsSlice(reply["buildings"]) {
		row, _ := na.AsMap(raw)
		out[na.AsString(row["id"])] = row
	}
	return out
}

// readPowerFacts returns the typed development power rows, network
// summaries and steam geysers the Go power family decodes, so the report
// carries the exact fuel/battery/network facts the decision was made on.
func readPowerFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
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
	section, _ := na.AsMap(observed["development"])
	_, development, err := na.Outcome(section, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: development section unavailable: %w", label, err)
	}
	power := na.AsSlice(development["power"])
	if len(power) == 0 {
		return nil, fmt.Errorf("%s: no development power rows observed", label)
	}
	for i, raw := range power {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		service, _ := na.AsMap(building["service"])
		if _, ok := service["outOfFuel"]; !ok {
			if entity, _ := na.AsMap(building["building"]); na.AsString(entity["defName"]) == "WoodFiredGenerator" {
				return nil, fmt.Errorf("%s: power row %d (WoodFiredGenerator) lacks the refuelable service facts", label, i)
			}
		}
	}
	return map[string]any{"power": power, "networks": development["networks"], "geysers": development["geysers"]}, nil
}
