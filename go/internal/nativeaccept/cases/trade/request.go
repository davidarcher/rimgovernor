package trade

import (
	"context"
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"time"
)

func init() {
	for _, channel := range []string{"caravan", "orbital"} {
		c := cases.Case{Name: "trade/request-" + channel, Scope: "Pinned real UseCommsConsole request: interruption has no charge; unavailable console, alliance cost and cooldown refuse. Native goodwill debit, cooldown tick and incident queue precede actual trader arrival. Go snapshots cannot prove the comms contact toil, goodwill mutation, vanilla queue or arrival physics. Setup provides only eligible participants and power.", Start: cases.Fixture{On: cases.LabStart(), Op: "test/trade_fixture", Args: map[string]any{"action": "request_setup", "channel": channel}}, RequiredOps: []string{"test/trade_fixture"}, Quiet: na.QuietRequired, Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3}, NoCheckpoint: true, Run: func(ctx context.Context, s cases.Session) error { return runRequest(ctx, s, channel) }}
		if channel == "orbital" {
			c.Expansions = []string{"ludeon.rimworld.odyssey"}
			c.NoKeep = true
		}
		cases.Register(c)
	}
}

func runRequest(ctx context.Context, s cases.Session, channel string) error {
	h, identity, p := s.Harness(), s.Identity(), s.Prepared()
	if ok, _ := na.AsBool(p["success"]); !ok {
		return fmt.Errorf("request setup: %v", p)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "request-authority", identity); err != nil {
		return err
	}
	state := func(action string) (map[string]any, error) {
		return h.Call(ctx, action, "test/trade_fixture", map[string]any{"action": action, "channel": channel, "pawnId": p["pawnId"], "traderId": p["factionId"]})
	}
	read := func() (map[string]any, error) {
		r, e := h.Wire(ctx, "request-facts", "observations_read_trade_acquisition", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
		if e != nil {
			return nil, e
		}
		_, o, e := na.Outcome(r, "observed")
		return o, e
	}
	kind := "TRADE_REQUEST_KIND_CARAVAN"
	if channel == "orbital" {
		kind = "TRADE_REQUEST_KIND_ORBITAL"
	}
	apply := func(label string, tick any) (map[string]any, error) {
		r, e := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": label, "commsTradeRequest": map[string]any{"kind": kind, "factionId": p["factionId"], "traderKind": p["traderKind"], "consoleId": p["consoleId"], "negotiatorId": p["pawnId"], "expectedLastRequestTick": tick}}}})
		if e != nil {
			return nil, e
		}
		rows := na.AsSlice(r["results"])
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: %v", label, r)
		}
		row, _ := na.AsMap(rows[0])
		return row, nil
	}
	unchanged := func(label string, before map[string]any) error {
		after, e := state("request_state")
		if e != nil {
			return e
		}
		if num(after["goodwill"]) != num(before["goodwill"]) || num(after["lastRequestTick"]) != num(before["lastRequestTick"]) {
			return fmt.Errorf("%s changed payment: %v -> %v", label, before, after)
		}
		return nil
	}
	refusal := func(label string, tick any) error {
		r, e := apply(label, tick)
		if e != nil {
			return e
		}
		f, _ := na.AsMap(r["refused"])
		if na.AsString(f["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s expected refusal: %v", label, r)
		}
		s.Report()[label] = f
		return nil
	}
	queued := func(label string) error {
		r, e := apply(label, p["lastRequestTick"])
		if e != nil {
			return e
		}
		receipt, _ := na.AsMap(r["applied"])
		outcome, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(outcome["observed"])
		effect, _ := na.AsMap(observed["commsTradeRequest"])
		if ok, _ := na.AsBool(effect["workQueued"]); !ok {
			return fmt.Errorf("not queued: %v", r)
		}
		if e := unchanged(label, p); e != nil {
			return e
		}
		facts, e := read()
		if e != nil {
			return e
		}
		if len(na.AsSlice(facts["commsWork"])) == 0 || len(na.AsSlice(facts["arrivals"])) != 0 {
			return fmt.Errorf("queued work misclassified: %v", facts)
		}
		return nil
	}
	if e := queued("interrupted-request"); e != nil {
		return e
	}
	if _, e := state("request_interrupt"); e != nil {
		return e
	}
	if e := unchanged("interrupted", p); e != nil {
		return e
	}
	if _, e := state("request_console_off"); e != nil {
		return e
	}
	if e := refusal("console-unavailable", p["lastRequestTick"]); e != nil {
		return e
	}
	if _, e := state("request_console_on"); e != nil {
		return e
	}
	low, e := state("request_low_goodwill")
	if e != nil {
		return e
	}
	if ok, _ := na.AsBool(low["ally"]); !ok {
		return fmt.Errorf("low goodwill control must retain Ally: %v", low)
	}
	if e := refusal("alliance-cost", p["lastRequestTick"]); e != nil {
		return e
	}
	if e := unchanged("alliance-cost", low); e != nil {
		return e
	}
	if _, e := state("request_restore_goodwill"); e != nil {
		return e
	}
	if e := queued("paid-request"); e != nil {
		return e
	}
	var applied map[string]any
	var arrival map[string]any
	_, e = na.RunUntil(ctx, h, "request-contact", 2500, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		var e error
		facts, e := read()
		if e != nil {
			return "", false, e
		}
		for _, raw := range na.AsSlice(facts["arrivals"]) {
			a, _ := na.AsMap(raw)
			if na.AsString(a["factionId"]) == na.AsString(p["factionId"]) && na.AsString(a["traderKind"]) == na.AsString(p["traderKind"]) {
				arrival = a
			}
		}
		// Fixture state reads need a paused game; authoritative request rows supply the application facts during a wait.
		for _, raw := range na.AsSlice(facts["requests"]) {
			r, _ := na.AsMap(raw)
			if na.AsString(r["factionId"]) == na.AsString(p["factionId"]) && na.AsString(r["traderKind"]) == na.AsString(p["traderKind"]) {
				applied = r
			}
		}
		return fmt.Sprint(applied), arrival != nil && num(applied["lastRequestTick"]) > num(p["lastRequestTick"]), nil
	})
	if e != nil {
		return e
	}
	if num(applied["goodwill"]) != num(p["goodwill"])-num(p["cost"]) || num(applied["cooldownRemainingTicks"]) <= 0 {
		return fmt.Errorf("native payment/cooldown mismatch: %v before %v", applied, p)
	}
	delay := num(arrival["arrivalTick"]) - num(applied["lastRequestTick"])
	min, max := 120000, 120000
	if channel == "orbital" {
		min, max = 2500, 5000
	}
	if delay < float64(min) || delay > float64(max) {
		return fmt.Errorf("native arrival outside bounds: %v", arrival)
	}
	if e := refusal("cooldown", applied["lastRequestTick"]); e != nil {
		return e
	}
	s.Report()["applied"], s.Report()["queuedArrival"] = applied, arrival
	// RunUntil pauses at the observed boundary. Tick budget follows the native queue; no incident is injected.
	_, e = na.RunUntil(ctx, h, "native-arrival", uint64(max+3000), na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		facts, e := read()
		if e != nil {
			return "", false, e
		}
		if channel == "orbital" {
			for _, raw := range na.AsSlice(facts["passingShips"]) {
				ship, _ := na.AsMap(raw)
				if na.AsString(ship["traderKind"]) == na.AsString(p["traderKind"]) {
					s.Report()["arrived"] = ship
					return fmt.Sprint(ship), true, nil
				}
			}
		}
		return fmt.Sprint(facts["arrivals"]), channel == "caravan" && len(na.AsSlice(facts["arrivals"])) == 0, e
	})
	if e != nil {
		return e
	}
	if channel == "caravan" {
		final, e := state("request_state")
		if e != nil {
			return e
		}
		if num(final["traders"]) < 1 {
			return fmt.Errorf("queue removal without native caravan: %v", final)
		}
		s.Report()["arrived"] = final
	}
	return nil
}
