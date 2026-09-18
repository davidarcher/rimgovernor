// Command poweraccept exercises the EnsureBasicPower reliability vertical
// (issue #6 slice 1, milestone A) end to end against a live game and a live
// rimgovernor Go player-control service composed with the power family:
//
//	fuel    -- a wood-fired generator is out of fuel and its one consumer
//	           unpowered, with unforbidden wood nearby. Refuelling is
//	           ordinary colonist work, so the service must hold
//	           (waiting_for_refuel) and commit no generator or conduit
//	           method; the native colonists refuel it, and an independent
//	           native read then shows the generator fuelled and the consumer
//	           powered.
//	reserve -- every consumer is powered right now, but the network drains
//	           a partly charged battery faster than its one generator
//	           supplies, so the reserve runway is under a day. The service
//	           must admit one more generator (a plan whose actions are a
//	           single generator definition) and the colonists must build it.
//
// Uses the private disposable test/power_prepare and test/power_observe
// fixtures (PowerFixture.cs). As with routinehaulaccept, the harness's own
// bridge session and the service's are used sequentially (one GABP client
// per game).
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

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const prefix = "power-accept"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-power-acceptance-<scenario>)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	scenario := flag.String("scenario", "fuel", "fuel or reserve")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	na.BudgetFlag((30 * time.Minute) / 2)
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *scenario != "fuel" && *scenario != "reserve" {
		fmt.Fprintln(os.Stderr, "-scenario must be fuel or reserve")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-power-acceptance-" + *scenario
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native EnsureBasicPower reliability vertical ("+*scenario+"): an out-of-fuel generator holds the "+
		"live Go power family until native colonists refuel it, or a draining battery under a day of reserve has the "+
		"family admit one more generator that the colonists build; both confirmed by an independent native read.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *binary, *scenario, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, binary, scenario string, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	var service *na.ServiceProcess
	var s *na.Session
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
		if _, err := s.Reattach(stopCtx); err != nil {
			report["stop_error"] = "reopen session for games_stop: " + err.Error()
			return
		}
		s.Close()
	}

	s, err := na.OpenSession(ctx, cfg, report, na.Fixture{Op: "test/power_prepare", Args: map[string]any{"scenario": scenario}}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer stopGame()
	h, identity, prepared := s.Harness, s.Identity, s.Prepared
	if !na.Contains(s.Names, "test/power_observe") {
		return fmt.Errorf("missing test/power_observe in discovery; rebuild the native mod with -Fixture PowerFixture")
	}
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
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
	}
	generatorID := na.AsString(prepared["generator"])
	spare, _ := na.AsMap(prepared["spareCell"])
	var ids []string
	ids = append(ids, generatorID)
	for _, raw := range na.AsSlice(prepared["consumers"]) {
		ids = append(ids, fmt.Sprint(raw))
	}
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
	if scenario == "fuel" {
		if out, _ := na.AsBool(generatorBefore["outOfFuel"]); !out {
			return fmt.Errorf("power-before: fixture generator is not out of fuel: %#v", generatorBefore)
		}
		for _, id := range ids[1:] {
			if on, _ := na.AsBool(rows[id]["powerOn"]); on {
				return fmt.Errorf("power-before: consumer %s already powered: %#v", id, rows[id])
			}
		}
	} else {
		for _, id := range ids[1:] {
			if row := rows[id]; row["storedWattDays"] == nil {
				if on, _ := na.AsBool(row["powerOn"]); !on {
					return fmt.Errorf("power-before: consumer %s should be powered on battery reserve: %#v", id, row)
				}
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
	if err := s.Release(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	service, err = na.LaunchService(ctx, cfg, s.GABS, na.ServiceLaunch{Binary: binary, Families: []string{"power", "work"}, Extra: na.ClockSpeedArgs()}, report)
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
	case "reserve":
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
	rows = indexRows(after)
	generators := na.AsSlice(after["generators"])
	switch scenario {
	case "fuel":
		if out, _ := na.AsBool(rows[generatorID]["outOfFuel"]); out {
			return fmt.Errorf("power-after: generator still out of fuel after the hold window; colonists never refuelled it: %#v", rows[generatorID])
		}
		// A refuel landing just before the pause leaves the generator
		// fuelled while the power net has not ticked its consumers back on;
		// let the game run a few seconds and read again before judging.
		for attempt := 1; attempt <= 3 && !allPowered(rows, ids[1:]); attempt++ {
			if _, err := h.Call(ctx, fmt.Sprintf("resume-after-%d", attempt), "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
				return err
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(3 * time.Second):
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
		for _, id := range ids[1:] {
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
	}
	topology, err = readPowerFacts(ctx, h, identity, "colony-facts-after")
	if err != nil {
		return err
	}
	report["colony_power_after"] = topology
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
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
