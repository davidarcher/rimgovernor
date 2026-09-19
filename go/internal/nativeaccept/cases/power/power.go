// Package power holds the EnsureBasicPower reliability vertical (issue #6
// slice 1, milestone A): a live game and a live rimgovernor Go
// player-control service composed with the power family, one case per
// scenario:
//
//	fuel    -- a wood-fired generator is out of fuel and its one consumer
//	           unpowered, with unforbidden wood nearby. Refuelling is
//	           ordinary colonist work, so the service must hold
//	           (waiting_for_refuel) and commit no generator or conduit
//	           method; the native colonists refuel it, and an independent
//	           native read then shows the generator fuelled and the consumer
//	           powered.
//	rain    -- the controller encloses an exposed battery and replaces ordinary
//	           conduits, then a full day of forced rain leaves no short circuits,
//	           fires or equipment damage.
//	reserve -- every consumer is powered right now, but the network drains
//	           a partly charged battery faster than its one generator
//	           supplies, so the reserve runway is under a day. The service
//	           must admit one more generator (a plan whose actions are a
//	           single generator definition) and the colonists must build it.
//	battery -- an exhausted battery bank and no generator at all (#160): the
//	           consumer is off and nothing on the network wants refuelling or
//	           repair, so the service must add generation (one generator
//	           plan) rather than wait on the bank, then connect it (a conduit
//	           plan: the generator is placed by the consumer, which does not
//	           transmit). Once both are built the independent read shows the
//	           built generator as the only one, connected, and -- after the
//	           colonists fuel it in a bounded native window -- the consumer
//	           powered.
//
// Uses the private disposable test/power_prepare and test/power_observe
// fixtures (PowerFixture.cs). The case's own bridge session and the
// service's are used sequentially (one GABP client per game).
package power

import (
	"context"
	"encoding/json"
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
// plays ~4 minutes, the reserve and battery generators are admitted in
// under one and built in about two.
var budgets = map[string]time.Duration{"fuel": 10 * time.Minute, "reserve": 5 * time.Minute, "battery": 5 * time.Minute, "rain": 14 * time.Minute}

func init() {
	for _, scenario := range []string{"fuel", "reserve", "battery", "rain"} {
		scenario := scenario
		cases.Register(cases.Case{
			Name: "power/" + scenario,
			Scope: "Native EnsureBasicPower reliability vertical (" + scenario + "): an out-of-fuel generator holds the " +
				"live Go power family until native colonists refuel it, a draining battery under a day of reserve has the " +
				"family admit one more generator that the colonists build, or an exhausted bank with no generator has it add " +
				"generation that powers the consumer; the rain case encloses a battery and replaces unsafe wiring " +
				"before a full day of rain. Each is confirmed by an independent native read.",
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
		if generatorID != "" || len(na.AsSlice(before["generators"])) != 0 {
			return fmt.Errorf("power-before: the battery fixture spawned a generator: %#v", before["generators"])
		}
		if stored := na.AsNumber(rows[na.AsString(prepared["battery"])]["storedWattDays"]); stored != 0 {
			return fmt.Errorf("power-before: fixture battery holds %.1f Wd, expected an exhausted bank", stored)
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); on {
				return fmt.Errorf("power-before: consumer %s powered with no generator and an empty bank: %#v", id, rows[id])
			}
		}
	}
	// The typed colony facts the Go family reads must carry the fuel and
	// network facts milestone A added.
	topology, err := readPowerFacts(ctx, h, identity, "colony-facts-before")
	if err != nil {
		return err
	}
	report["colony_power_before"] = topology

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
	case "reserve", "battery":
		methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
		goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.EnsureBasicPower, nil)
		methodCancel()
		if err != nil {
			return fmt.Errorf("generator method: %w", err)
		}
		report["goal_id"] = string(goalID)
		for renewals := 0; ; renewals++ {
			plan, err := journal.LoadPlan(ctx, method.Plan)
			if err != nil {
				return err
			}
			actions := plan.Spec.Actions()
			if len(actions) != 1 {
				return fmt.Errorf("generator plan %s has %d actions, expected 1", method.Plan, len(actions))
			}
			b, ok := actions[0].Building()
			if !ok || !na.Contains(policy.GeneratorDefinitions, b.Definition()) {
				return fmt.Errorf("power plan action is not a generator build: %#v", actions[0])
			}
			report["generator_definition"] = b.Definition()
			doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
			state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
			doneCancel()
			if err != nil {
				return fmt.Errorf("generator plan: %w", err)
			}
			if !incidental {
				report["generator_plan"] = string(method.Plan)
				report["generator_completed_tick"] = int64(state.Progress[0].View().Tick)
				report["incidental_renewals"] = renewals
				break
			}
			renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
			_, method, err = na.WaitGoalMethod(renewCtx, journal, policy.EnsureBasicPower, &method)
			renewCancel()
			if err != nil {
				return fmt.Errorf("renewed generator method after incidental cancellation #%d: %w", renewals+1, err)
			}
		}
		if scenario == "battery" {
			// The generator stands beside the consumer, off the conduit
			// line, so the family's next method routes conduits to it.
			seen := map[domain.PlanID]bool{method.Plan: true}
			connectCtx, connectCancel := context.WithTimeout(ctx, 5*time.Minute)
			defer connectCancel()
			for renewals := 0; ; renewals++ {
				_, connect, err := na.WaitGoalMethodExcluding(connectCtx, journal, policy.EnsureBasicPower, seen)
				if err != nil {
					return fmt.Errorf("connect method after the generator build: %w", err)
				}
				seen[connect.Plan] = true
				plan, err := journal.LoadPlan(ctx, connect.Plan)
				if err != nil {
					return err
				}
				for _, action := range plan.Spec.Actions() {
					b, ok := action.Building()
					if !ok || b.Definition() != "HiddenConduit" {
						return fmt.Errorf("power family committed a non-conduit action after the generator: %#v", action)
					}
				}
				state, incidental, err := na.WaitPlanTerminal(connectCtx, journal, connect.Plan)
				if err != nil {
					return fmt.Errorf("conduit plan: %w", err)
				}
				if !incidental {
					report["connect_plan"] = string(connect.Plan)
					report["connect_conduits"] = len(plan.Spec.Actions())
					report["connect_completed_tick"] = int64(state.Progress[0].View().Tick)
					report["connect_incidental_renewals"] = renewals
					break
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
	if scenario == "battery" {
		// The built generator joins the read so its fuel and connection are
		// in evidence alongside the consumer it should power.
		for _, raw := range generators {
			if id := fmt.Sprint(raw); !na.Contains(ids, id) {
				ids = append(ids, id)
			}
		}
		if after, err = observe("power-after-generators"); err != nil {
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
	if scenario != "reserve" {
		// A refuel landing just before the pause leaves the generator
		// fuelled while the power net has not ticked its consumers back on;
		// let the game run a few seconds and read again before judging. The
		// controller's new generator is built empty and, with its plan done,
		// no family asks for another clock window, so the battery run gives
		// the colonists a bounded Fast window (about four game hours) to
		// fuel it before the consumer is judged.
		attempts, speed := 3, "Normal"
		if scenario == "battery" {
			attempts, speed = 12, "Fast"
		}
		for attempt := 1; attempt <= attempts && !allPowered(rows, consumers); attempt++ {
			if _, err := h.Call(ctx, fmt.Sprintf("resume-after-%d", attempt), "rimworld/set_time_speed", map[string]any{"speed": speed, "ultraSpeedBoost": false}); err != nil {
				return err
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(5 * time.Second):
			}
			if _, err := h.Call(ctx, fmt.Sprintf("pause-after-%d", attempt), "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
				return err
			}
			if after, err = observe(fmt.Sprintf("power-after-%d", attempt)); err != nil {
				return err
			}
			report["power_after"] = after
			rows = indexRows(after)
			generators = na.AsSlice(after["generators"])
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
		if len(generators) != 1 {
			return fmt.Errorf("power-after: expected exactly the generator the controller built, observed %d: %#v", len(generators), generators)
		}
		built := rows[fmt.Sprint(generators[0])]
		if connected, _ := na.AsBool(built["connected"]); !connected {
			return fmt.Errorf("power-after: the controller's generator is not connected to a power net: %#v", built)
		}
		for _, id := range consumers {
			if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
				return fmt.Errorf("power-after: consumer %s still unpowered after the controller's generator (fuelled by native colonists?): %#v", id, rows[id])
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

func indexRows(reply map[string]any) map[string]map[string]any {
	out := map[string]map[string]any{}
	for _, raw := range na.AsSlice(reply["buildings"]) {
		row, _ := na.AsMap(raw)
		out[na.AsString(row["id"])] = row
	}
	return out
}

// readPowerFacts returns the typed development power rows and network
// summaries the Go power family decodes, so the report carries the exact
// fuel/battery/network facts the decision was made on.
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
	return map[string]any{"power": power, "networks": development["networks"]}, nil
}
