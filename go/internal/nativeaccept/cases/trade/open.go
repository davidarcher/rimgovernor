// The trade/open case exercises the OpenTrade intent end to end against a
// live game: a real spawned trader-caravan incident, a real eligible
// colonist negotiator teleported adjacent to the trader (the immediate open
// path), and native TradeSession admission (an actual TradeSession.SetupWith,
// mirroring HomeTradeTools' own opening path) invoked through the same
// rimgovernor/operations_execute wire contract Go's bridge.TradeWriter
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
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-trade-accept-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "trade/open",
		Scope: "Native OpenTrade intent: a real TradeSession admission against an actual spawned trader " +
			"caravan, unknown-trader and other-pair refusals, open-or-reuse on a fresh attempt, replay idempotency, and the session in the trade-session read.",
		Start:  cases.LabStart(),
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, names := s.Harness(), s.Identity(), s.Names()
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/operations_preview", "rimgovernor/observations_read_trade_session"} {
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

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	openIntent := func(trader, negotiator string) map[string]any {
		return map[string]any{"openTrade": map[string]any{"traderId": trader, "negotiatorId": negotiator, "giftMode": false}}
	}
	buildRequest := func(actionID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": "1"},
			},
			"operation": operation,
		}
	}

	// Refusal 1: a trader that is not on the map.
	if code, err := failureCode(ctx, h, "unknown-trader", buildRequest("trade-open-unknown", openIntent("Thing_NoSuchTrader", negotiatorID))); err != nil {
		return err
	} else if code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("unknown-trader: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}

	// Preview: accepted, but never opens the live TradeSession.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{"identity": identity, "operation": openIntent(traderID, negotiatorID)})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the open to be accepted, got %#v", evaluated)
	}
	previewTrade, _ := na.AsMap(evaluated["trade"])
	if na.AsString(previewTrade["settlementId"]) != traderID {
		return fmt.Errorf("preview: unexpected preview settlementId (expected trader entity id): %#v", previewTrade)
	}

	// Execute: the real native TradeSession admission.
	openRequest := buildRequest("trade-open", openIntent(traderID, negotiatorID))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", openRequest)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	applied, ok := na.AsMap(receipt["applied"])
	if !ok {
		return fmt.Errorf("execute: expected an applied outcome, got %#v", receipt)
	}
	appliedObserved, _ := na.AsMap(applied["observed"])
	appliedTrade, _ := na.AsMap(appliedObserved["trade"])
	if na.AsString(appliedTrade["sessionId"]) == "" || na.AsString(appliedTrade["factionId"]) == "" {
		return fmt.Errorf("execute: expected session and faction ids, got %#v", appliedTrade)
	}
	if executed, _ := na.AsBool(appliedTrade["executed"]); executed {
		return fmt.Errorf("execute: expected executed=false for a bare open, got %#v", appliedTrade)
	}
	if closed, _ := na.AsBool(appliedTrade["closed"]); closed {
		return fmt.Errorf("execute: expected closed=false for a bare open, got %#v", appliedTrade)
	}

	// The applied receipt is the whole outcome (trade is an intent-mode
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

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", openRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, receipt) {
		return fmt.Errorf("replay: replay of the same attempt returned a different receipt")
	}

	// Open-or-reuse: a fresh attempt for the same pair applies against the
	// live session rather than conflicting.
	reuseReply, err := h.Wire(ctx, "reuse", "operations_execute", buildRequest("trade-open-again", openIntent(traderID, negotiatorID)))
	if err != nil {
		return err
	}
	_, reuse, err := na.Outcome(reuseReply, "receipt")
	if err != nil {
		return err
	}
	reuseApplied, _ := na.AsMap(reuse["applied"])
	reuseObserved, _ := na.AsMap(reuseApplied["observed"])
	reuseTrade, _ := na.AsMap(reuseObserved["trade"])
	if na.AsString(reuseTrade["sessionId"]) != na.AsString(appliedTrade["sessionId"]) {
		return fmt.Errorf("reuse: expected the live session %q, got %#v", na.AsString(appliedTrade["sessionId"]), reuse)
	}

	// Refusal 2: a different negotiator while the session is held.
	if otherID != "" {
		if code, err := failureCode(ctx, h, "other-pair", buildRequest("trade-open-other", openIntent(traderID, otherID))); err != nil {
			return err
		} else if code != "FAILURE_CODE_STALE_IDENTITY" {
			return fmt.Errorf("other-pair: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
		}
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// failureCode wires request through operations_execute and returns the
// failure code.
func failureCode(ctx context.Context, h *na.Harness, label string, request map[string]any) (string, error) {
	reply, err := h.Wire(ctx, label, "operations_execute", request)
	if err != nil {
		return "", err
	}
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return "", err
	}
	return na.AsString(failure["code"]), nil
}
