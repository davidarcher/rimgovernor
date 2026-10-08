// The trade/open case exercises the OpenTrade intent end to end against a
// live game: a real spawned trader-caravan incident, a real eligible
// colonist negotiator teleported adjacent to the trader (the immediate open
// path), and native TradeSession admission (an actual TradeSession.SetupWith,
// mirroring HomeTradeTools' own opening path) invoked through the same
// rimgovernor/operations_apply wire contract Go's bridge.ActionsWriter
// drives.
//
// Trade operations are idempotent intents naming the session's trader and
// negotiator. This case checks that an unknown trader is refused, that open
// applies, that a fresh open attempt for the same pair reuses the live
// session instead of conflicting, and that a different pair is refused while
// it is held. It closes with the session still open; SetTradeLines,
// AcceptTrade and EndTrade are driven by the routine, not claimed here.
//
// Uses a private disposable fixture (test/trade_fixture) since a specific
// eligible trader-caravan incident and an adjacent negotiator cannot be
// reliably produced by native random generation alone.
package trade

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "trade/open",
		Scope: "Native OpenTrade intent: a real TradeSession admission against an actual spawned trader " +
			"caravan, unknown-trader and other-pair refusals, open-or-reuse under a fresh key, a resent key replayed, and the session in the trade-session read.",
		Start:  cases.LabStart(),
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, names := s.Harness(), s.Identity(), s.Names()
	for _, required := range []string{"rimgovernor/operations_apply", "rimgovernor/observations_read_trade_session"} {
		if !na.Contains(names, required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}

	incident, err := h.Call(ctx, "spawn-trader", "test/trade_fixture", map[string]any{"action": "incident"})
	if err != nil {
		return err
	}
	if eligible, _ := na.AsBool(incident["eligible"]); !eligible {
		return fmt.Errorf("spawn-trader: incident was not eligible to fire: %#v", incident)
	}
	if applied, _ := na.AsBool(incident["applied"]); !applied {
		return fmt.Errorf("spawn-trader: incident did not apply: %#v", incident)
	}
	var traderID string
	for _, raw := range na.AsSlice(incident["traderIds"]) {
		traderID = fmt.Sprint(raw)
		break
	}
	if traderID == "" {
		return fmt.Errorf("spawn-trader: no trader pawn was spawned: %#v", incident)
	}
	report["incident"] = incident

	snapshot, err := h.Call(ctx, "snapshot-colonists", "test/trade_fixture", map[string]any{"action": "snapshot"})
	if err != nil {
		return err
	}
	var negotiatorID, otherID string
	for _, raw := range na.AsSlice(snapshot["colonists"]) {
		row, _ := na.AsMap(raw)
		if na.AsNumber(row["social"]) < 0 {
			continue
		}
		if negotiatorID == "" {
			negotiatorID = na.AsString(row["id"])
		} else if otherID == "" {
			otherID = na.AsString(row["id"])
		}
	}
	if negotiatorID == "" {
		return fmt.Errorf("snapshot-colonists: no socially-eligible colonist available: %#v", snapshot)
	}

	teleport, err := h.Call(ctx, "teleport-adjacent", "test/trade_fixture", map[string]any{
		"action": "teleport_adjacent", "traderId": traderID, "pawnId": negotiatorID,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(teleport["success"]); !success {
		return fmt.Errorf("teleport-adjacent refused: %#v", teleport)
	}

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	// apply sends one open intent under key and returns its result.
	apply := func(label, key, trader, negotiator string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, "trade": map[string]any{"traderId": trader, "negotiatorId": negotiator, "open": map[string]any{"giftMode": false}},
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	refusedCode := func(result map[string]any) string {
		refused, _ := na.AsMap(result["refused"])
		return na.AsString(refused["code"])
	}
	openTrade := func(label string, result map[string]any) (map[string]any, error) {
		receipt, _ := na.AsMap(result["applied"])
		applied, ok := na.AsMap(receipt["applied"])
		if !ok {
			return nil, fmt.Errorf("%s: expected an applied outcome, got %#v", label, result)
		}
		observed, _ := na.AsMap(applied["observed"])
		trade, _ := na.AsMap(observed["trade"])
		return trade, nil
	}

	// Refusal 1: a trader that is not on the map.
	unknown, err := apply("unknown-trader", "trade-open-unknown", "Thing_NoSuchTrader", negotiatorID)
	if err != nil {
		return err
	}
	if code := refusedCode(unknown); code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("unknown-trader: expected a FAILURE_CODE_NOT_FOUND refusal, got %#v", unknown)
	}

	// Apply: the real native TradeSession admission.
	first, err := apply("open", "trade-open", traderID, negotiatorID)
	if err != nil {
		return err
	}
	appliedTrade, err := openTrade("open", first)
	if err != nil {
		return err
	}
	if na.AsString(appliedTrade["sessionId"]) == "" || na.AsString(appliedTrade["factionId"]) == "" {
		return fmt.Errorf("open: expected session and faction ids, got %#v", appliedTrade)
	}
	if executed, _ := na.AsBool(appliedTrade["executed"]); executed {
		return fmt.Errorf("open: expected executed=false for a bare open, got %#v", appliedTrade)
	}
	if closed, _ := na.AsBool(appliedTrade["closed"]); closed {
		return fmt.Errorf("open: expected closed=false for a bare open, got %#v", appliedTrade)
	}

	// The applied result is the whole outcome (trade is an intent-mode
	// kind); the live session shows in the trade-session read.
	sessionReply, err := h.Wire(ctx, "session", "observations_read_trade_session", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, session, err := na.Outcome(sessionReply, "observed")
	if err != nil {
		return err
	}
	if open, _ := na.AsBool(session["open"]); !open || na.AsString(session["traderId"]) != traderID || na.AsString(session["negotiatorId"]) != negotiatorID {
		return fmt.Errorf("session: expected %s trading with %s in an open session, got %#v", negotiatorID, traderID, session)
	}

	// Resend: the same key returns the identical result.
	replay, err := apply("resend", "trade-open", traderID, negotiatorID)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, first) {
		return fmt.Errorf("resend: the same key returned a different result")
	}

	// Open-or-reuse: a fresh key for the same pair applies against the
	// live session rather than conflicting.
	again, err := apply("reuse", "trade-open-again", traderID, negotiatorID)
	if err != nil {
		return err
	}
	reuseTrade, err := openTrade("reuse", again)
	if err != nil {
		return err
	}
	if na.AsString(reuseTrade["sessionId"]) != na.AsString(appliedTrade["sessionId"]) {
		return fmt.Errorf("reuse: expected the live session %q, got %#v", na.AsString(appliedTrade["sessionId"]), again)
	}

	// Refusal 2: a different negotiator while the session is held.
	if otherID != "" {
		other, err := apply("other-pair", "trade-open-other", traderID, otherID)
		if err != nil {
			return err
		}
		if code := refusedCode(other); code != "FAILURE_CODE_STALE_IDENTITY" {
			return fmt.Errorf("other-pair: expected a FAILURE_CODE_STALE_IDENTITY refusal, got %#v", other)
		}
	}

	return nil
}
