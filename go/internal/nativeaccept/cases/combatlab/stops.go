package combatlab

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	// stopsFirstWindow bounds the window the raiders close in: 20 cells of
	// walking is well under this at Superfast.
	stopsFirstWindow = 2000
	// stopsBackstop is the combat backstop: a window with no armed
	// event stops on its budget.
	stopsBackstop = 300
	// stopsWindows bounds the fight after contact: 20 backstops.
	stopsWindows = 20
)

func init() {
	cases.Register(cases.Case{
		Name:        "combatlab/stops",
		Scope:       "Event-triggered combat stops (#849): on lab-open a combat window armed with downed and entered-range stops on the exact tick a raider's weapon reaches a colonist or a colonist's reaches a raider (the stop event's tick equals its occurrence tick), once per raider and direction per combat, later windows without an event stop on the 300-tick backstop, and a colonist going down stops on its tick.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runStops,
	})
}

func runStops(ctx context.Context, s cases.Session) error {
	h, identity := s.Harness(), s.Identity()
	staged, err := Stage(ctx, h, "lab-open")
	if err != nil {
		return err
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "combat-stops-acquire", identity)
	if err != nil {
		return err
	}
	report := s.Report()
	clock := &na.ScenarioClock{
		Wire:     h.WireFunc(),
		Identity: identity, Owner: na.Controller, Report: map[string]any{}, Grant: grant,
		// Acknowledged, the raiders' approach is not a hostile stop; the
		// health thresholds are off so only armed events and the budget stop.
		CombatTargets: staged.Hostiles(),
	}
	clock.PolicyEdit = func(policy map[string]any) {
		policy["combatStopEvents"] = []any{"COMBAT_EVENT_DOWNED", "COMBAT_EVENT_ENTERED_RANGE"}
		policy["healthDropFraction"] = 1
		policy["minHealthFraction"] = 0.01
	}
	var stops []map[string]any
	sawRange, sawBackstop, sawDowned := false, false, false
	entered := map[string]bool{}
	for window := 0; window <= stopsWindows && !sawDowned; window++ {
		budget := uint64(stopsBackstop)
		if window == 0 {
			budget = stopsFirstWindow
		}
		started, err := clock.Change(ctx, "Superfast", budget)
		if err != nil {
			return fmt.Errorf("window %d start: %w", window, err)
		}
		clock.SeekEvents(started)
		stop, err := awaitStop(ctx, clock)
		if err != nil {
			return fmt.Errorf("window %d: %w", window, err)
		}
		startTick, _ := started["startTick"].(uint64)
		stop["window"], stop["startTick"] = window, startTick
		stops = append(stops, stop)
		report["stops"] = stops
		switch stop["kind"] {
		case "combat_event":
			if stop["tick"] != stop["occurrenceTick"] || stop["tick"] != stop["detectedTick"] {
				return fmt.Errorf("window %d: combat stop off its event's tick: %v", window, stop)
			}
			switch stop["event"] {
			case "COMBAT_EVENT_ENTERED_RANGE":
				// First time only: the combat remembers a raider across windows.
				// Keyed by hostile and direction ("reaches" or "reached by").
				key := fmt.Sprint(stop["thingId"], strings.Contains(fmt.Sprint(stop["detail"]), "reached by"))
				if entered[key] {
					return fmt.Errorf("window %d: %s entered range a second time: %v", window, key, stop)
				}
				entered[key] = true
				sawRange = true
			case "COMBAT_EVENT_DOWNED":
				sawDowned = true
			default:
				return fmt.Errorf("window %d: unarmed combat event stopped the clock: %v", window, stop)
			}
		case "tick_budget":
			if window == 0 {
				return fmt.Errorf("raiders never came within weapon range in %d ticks: %v", stopsFirstWindow, stop)
			}
			if tick, _ := stop["tick"].(uint64); tick-startTick != stopsBackstop {
				return fmt.Errorf("window %d: backstop at %d ticks, want %d: %v", window, tick-startTick, stopsBackstop, stop)
			}
			sawBackstop = true
		default:
			return fmt.Errorf("window %d: unexpected stop: %v", window, stop)
		}
		if window == 0 && !sawRange {
			return fmt.Errorf("first window stopped on %v before entered range", stop)
		}
	}
	report["enteredRange"], report["backstop"], report["downed"] = sawRange, sawBackstop, sawDowned
	if !sawBackstop && !sawDowned {
		return fmt.Errorf("neither a backstop nor a downed stop after contact: %v", stops)
	}
	return nil
}

// awaitStop polls until the running window stops, then returns its stop
// event: kind, the armed event, and the event's context, detected and
// occurrence ticks.
func awaitStop(ctx context.Context, clock *na.ScenarioClock) (map[string]any, error) {
	deadline := time.Now().Add(90 * time.Second)
	for time.Now().Before(deadline) {
		status, err := clock.Call(ctx, "status", nil)
		if err != nil {
			return nil, err
		}
		if na.AsString(status["stopReason"]) != "" {
			events, err := clock.Poll(ctx)
			if err != nil {
				return nil, err
			}
			for _, e := range events {
				row, _ := na.AsMap(e)
				native, _ := na.AsMap(row["native"])
				stopped, ok := na.AsMap(native["stopped"])
				if !ok {
					continue
				}
				out := map[string]any{"kind": row["kind"], "detail": native["detail"]}
				ctxRow, _ := na.AsMap(native["context"])
				out["tick"], _ = na.ScenarioInteger(ctxRow["tick"])
				out["detectedTick"], _ = na.ScenarioInteger(stopped["detectedTick"])
				out["occurrenceTick"], _ = na.ScenarioInteger(stopped["occurrenceTick"])
				if combat, ok := na.AsMap(stopped["combat"]); ok {
					out["event"], out["thingId"] = na.AsString(combat["event"]), na.AsString(combat["thingId"])
				}
				return out, nil
			}
			return nil, fmt.Errorf("stopped (%v) with no stop event", status["stopReason"])
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(100 * time.Millisecond):
		}
	}
	return nil, fmt.Errorf("window did not stop in 90 s")
}
