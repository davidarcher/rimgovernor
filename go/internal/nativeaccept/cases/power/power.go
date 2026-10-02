// Package power holds the EnsureBasicPower reliability vertical (issue #6
// slice 1, milestone A) on the lab contract (#747): a live game and a live
// rimgovernor Go player-control service composed with the power family.
//
//	fuel -- a wood-fired generator on the blank lab is out of fuel and its
//	        one consumer unpowered, with unforbidden wood nearby.
//	        Refuelling is ordinary colonist work, so the service must hold
//	        (waiting_for_refuel) and commit no generator or conduit method;
//	        the native colonists refuel it, and an independent native read
//	        then shows the generator fuelled and the consumer powered.
//
// The reserve, battery, wind and geothermal decisions are snapshot tests
// over their recorded reviews (internal/buildingruntime, #747).
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

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const prefix = "power-accept"

func init() {
	cases.Register(cases.Case{
		Name: "power/fuel",
		Scope: "Native EnsureBasicPower reliability vertical on the lab: an out-of-fuel generator holds the live Go power family " +
			"until native colonists refuel it, confirmed by an independent native read.",
		Start:   cases.Fixture{Op: "test/power_prepare", Args: map[string]any{"scenario": "fuel"}, On: cases.LabStart()},
		Service: true,
		// ~2x the measured healthy run (403eebbb): the refuel hold plays ~4
		// minutes.
		Budget: 10 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
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
	if out, _ := na.AsBool(generatorBefore["outOfFuel"]); !out {
		return fmt.Errorf("power-before: fixture generator is not out of fuel: %#v", generatorBefore)
	}
	for _, id := range consumers {
		if on, _ := na.AsBool(rows[id]["powerOn"]); on {
			return fmt.Errorf("power-before: consumer %s already powered: %#v", id, rows[id])
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
	if out, _ := na.AsBool(rows[generatorID]["outOfFuel"]); out {
		return fmt.Errorf("power-after: generator still out of fuel after the hold window; colonists never refuelled it: %#v", rows[generatorID])
	}
	// A refuel landing just before the pause leaves the generator fuelled
	// while the power net has not ticked its consumers back on; let the
	// game run a few seconds and read again before judging.
	for attempt := 1; attempt <= 3 && !allPowered(rows, consumers); attempt++ {
		label := fmt.Sprintf("after-%d", attempt)
		if _, err := h.Call(ctx, "resume-"+label, "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Second):
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
	}
	for _, id := range consumers {
		if on, _ := na.AsBool(rows[id]["powerOn"]); !on {
			return fmt.Errorf("power-after: consumer %s still unpowered after refuelling: %#v", id, rows[id])
		}
	}
	if len(generators) != 1 {
		return fmt.Errorf("power-after: expected the single fixture generator, observed %d: %#v", len(generators), generators)
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

// readPowerFacts returns the typed development power rows, network
// summaries and steam geysers the Go power family decodes, so the report
// carries the exact fuel/battery/network facts the decision was made on.
func readPowerFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false,
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
	rows, err := h.BuildingRows(ctx, label+"-buildings", identity)
	if err != nil {
		return nil, err
	}
	for i, raw := range power {
		row, _ := na.AsMap(raw)
		entity, _ := na.AsMap(row["building"])
		building, ok := rows[na.AsString(entity["id"])]
		if !ok {
			return nil, fmt.Errorf("%s: power row %d (%v) is not in the building table", label, i, entity["id"])
		}
		service, _ := na.AsMap(building["service"])
		if _, ok := service["outOfFuel"]; !ok {
			if na.AsString(entity["defName"]) == "WoodFiredGenerator" {
				return nil, fmt.Errorf("%s: power row %d (WoodFiredGenerator) lacks the refuelable service facts", label, i)
			}
		}
	}
	return map[string]any{"power": power, "networks": development["networks"], "geysers": development["geysers"]}, nil
}
