// The trade/open case exercises the OpenTrade vertical (G01.07f) end to end
// against a live game: a real spawned trader-caravan incident, a real
// eligible colonist negotiator standing adjacent to the trader, and native
// TradeSession admission (an actual TradeSession.SetupWith, mirroring
// HomeTradeTools' own opening path) invoked through the same
// rimgovernor/operations_execute wire contract Go's bridge.TradeWriter
// drives, and its observation/replay/refusal semantics.
//
// Per go/README.md's documented limitation, only OpenTrade is exercised
// here: SetTradeLines, AcceptTrade and EndTrade cannot be driven through a
// direct single-action submission the way every other vertical in this
// package is, because native requires an ActionDependency on a sibling
// OpenTrade action within the same plan -- a constraint this repo's
// buildingruntime layer enforces above the wire, not something a raw wire
// harness like this one can honestly bypass without fabricating a
// plan-level dependency the native mod was never asked to honor. This
// harness therefore closes with a live, still-open TradeSession; nothing
// beyond OpenTrade's own admission, evidence, replay and refusal behavior
// is claimed as verified.
//
// Uses a private disposable fixture (test/trade_fixture) since a specific
// eligible trader-caravan incident and an adjacent negotiator cannot be
// reliably produced by native random generation alone. The fixture's
// "state" action reads back the exact raw fields
// (position, CanTradeNow, traderDismissed, Downed, Dead, InMentalState,
// WorkTagIsDisabled) that NativeTradeOperations.TraderToken/NegotiatorToken
// hash, so this harness can self-compute the same CAS tokens client side --
// mirroring every other self-computed-token vertical in this package --
// without needing to reproduce RimWorld's pawn altitude-layer constant.
package trade

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
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
		Scope: "Native OpenTrade vertical only (per go/README.md's documented TradeSetLines/" +
			"TradeAccept/TradeEnd limitation): a real TradeSession admission against an actual spawned trader " +
			"caravan, exact trader/negotiator-position CAS self-computed client side, owner-conflict and " +
			"stale-identity refusals, and replay idempotency.",
		Start:  cases.DebugStart{},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, names := s.Harness(), s.Identity(), s.Names()
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/operations_preview", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress"} {
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
	var negotiatorID string
	for _, raw := range na.AsSlice(snapshot["colonists"]) {
		row, _ := na.AsMap(raw)
		if na.AsNumber(row["social"]) < 0 {
			continue
		}
		negotiatorID = na.AsString(row["id"])
		break
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

	// state reads back the exact raw fields NativeTradeOperations.
	// TraderToken/NegotiatorToken hash, so tradeToken below can self-compute
	// the same CAS token client side.
	state := func(label string) (map[string]any, error) {
		result, err := h.Call(ctx, label, "test/trade_fixture", map[string]any{
			"action": "state", "traderId": traderID, "pawnId": negotiatorID,
		})
		if err != nil {
			return nil, err
		}
		return result, nil
	}

	stateBefore, err := state("state-before")
	if err != nil {
		return err
	}
	traderTok, negotiatorTok, err := tradeTokens(traderID, negotiatorID, stateBefore)
	if err != nil {
		return err
	}
	report["state_before"] = stateBefore

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	buildOperation := func(traderToken, negotiatorToken string) map[string]any {
		return map[string]any{"openTrade": map[string]any{
			"trader":     map[string]any{"entityId": traderID, "expectedSnapshotToken": traderToken},
			"negotiator": map[string]any{"entityId": negotiatorID, "expectedSnapshotToken": negotiatorToken},
			"giftMode":   false,
		}}
	}
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// Refusal 1: a corrupted trader token must be refused.
	badTraderRequest := buildRequest("trade-open-bad-trader", "1", buildOperation(flip(traderTok), negotiatorTok))
	if code, err := failureCode(ctx, h, "bad-trader-token", badTraderRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("bad-trader-token: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Refusal 2: a corrupted negotiator token must be refused.
	badNegotiatorRequest := buildRequest("trade-open-bad-negotiator", "1", buildOperation(traderTok, flip(negotiatorTok)))
	if code, err := failureCode(ctx, h, "bad-negotiator-token", badNegotiatorRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("bad-negotiator-token: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: accepted, but never opens the live TradeSession.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(traderTok, negotiatorTok),
	})
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

	stateAfterPreview, err := state("state-after-preview")
	if err != nil {
		return err
	}
	report["state_after_preview"] = stateAfterPreview
	if !na.DeepEqual(stateAfterPreview["trader"], stateBefore["trader"]) || !na.DeepEqual(stateAfterPreview["negotiator"], stateBefore["negotiator"]) {
		return fmt.Errorf("state-after-preview: expected preview to leave trader/negotiator state unchanged")
	}

	// Execute: the real native TradeSession admission.
	openRequest := buildRequest("trade-open", "1", buildOperation(traderTok, negotiatorTok))
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
	if na.AsString(appliedTrade["sessionId"]) == "" {
		return fmt.Errorf("execute: expected a non-empty session id, got %#v", appliedTrade)
	}
	if executed, _ := na.AsBool(appliedTrade["executed"]); executed {
		return fmt.Errorf("execute: expected executed=false for a bare open, got %#v", appliedTrade)
	}
	if closed, _ := na.AsBool(appliedTrade["closed"]); closed {
		return fmt.Errorf("execute: expected closed=false for a bare open, got %#v", appliedTrade)
	}
	if na.AsString(appliedTrade["factionId"]) == "" {
		return fmt.Errorf("execute: expected a non-empty faction id, got %#v", appliedTrade)
	}
	snapshotEvidence, _ := na.AsMap(appliedTrade["snapshot"])
	if na.AsString(snapshotEvidence["entityId"]) == "" || na.AsString(snapshotEvidence["afterToken"]) == "" {
		return fmt.Errorf("execute: expected a non-empty session snapshot, got %#v", snapshotEvidence)
	}

	// Observe: durable progress lookup reports the same pending evidence
	// (a bare open never reaches "completed" -- it stays a live session).
	precondition, _ := na.AsMap(openRequest["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	progressReply, err := h.Wire(ctx, "observe", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, progress, err := na.Outcome(progressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(progress["completeInspection"]); !complete {
		return fmt.Errorf("observe: expected completeInspection=true, got %#v", progress)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt a second native open.
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

	// Durable lookup: receipts_lookup independently returns the same receipt.
	lookupReply, err := h.Wire(ctx, "lookup", "receipts_lookup", attempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, receipt) {
		return fmt.Errorf("lookup: expected the same receipt as execute, got %#v", lookup)
	}

	// Refusal 3: a brand-new attempt against the same trader/negotiator
	// while the session from execute is still open is refused as an owner
	// conflict, not silently admitted as a second session.
	stateAfterOpen, err := state("state-after-open")
	if err != nil {
		return err
	}
	freshTraderTok, freshNegotiatorTok, err := tradeTokens(traderID, negotiatorID, stateAfterOpen)
	if err != nil {
		return err
	}
	conflictRequest := buildRequest("trade-open-again", "1", buildOperation(freshTraderTok, freshNegotiatorTok))
	if code, err := failureCode(ctx, h, "session-already-open", conflictRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("session-already-open: expected FAILURE_CODE_OWNER_CONFLICT, got %q", code)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questacceptaccept's/questfulfillaccept's helper
// of the same name.
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

// tradeTokens reproduces NativeTradeOperations.TraderToken/NegotiatorToken
// exactly (same joined-string SHA256 hex, IntVec3.ToString()'s
// "(x, y, z)" position format, lowercase booleans) from a test/trade_fixture
// "state" reply alone. The fixture's "state" action does not echo back the
// ids it was asked for, so the caller supplies them (the same traderId/
// pawnId already threaded through every other test/trade_fixture call).
func tradeTokens(traderID, negotiatorID string, state map[string]any) (traderToken, negotiatorToken string, err error) {
	trader, ok := na.AsMap(state["trader"])
	if !ok {
		return "", "", fmt.Errorf("tradeTokens: missing trader state: %#v", state)
	}
	negotiator, ok := na.AsMap(state["negotiator"])
	if !ok {
		return "", "", fmt.Errorf("tradeTokens: missing negotiator state: %#v", state)
	}
	traderPos := position(trader)
	traderCanTrade, _ := na.AsBool(trader["canTradeNow"])
	traderDismissed, _ := na.AsBool(trader["dismissed"])
	negotiatorPos := position(negotiator)
	negotiatorDowned, _ := na.AsBool(negotiator["downed"])
	negotiatorDead, _ := na.AsBool(negotiator["dead"])
	negotiatorMental, _ := na.AsBool(negotiator["mental"])
	negotiatorSocialDisabled, _ := na.AsBool(negotiator["socialDisabled"])

	traderInput := traderID + "|cantrade=" + boolText(traderCanTrade) + "|dismissed=" + boolText(traderDismissed) + "|pos=" + traderPos
	negotiatorInput := negotiatorID + "|pos=" + negotiatorPos + "|downed=" + boolText(negotiatorDowned) +
		"|dead=" + boolText(negotiatorDead) + "|mental=" + boolText(negotiatorMental) + "|socialDisabled=" + boolText(negotiatorSocialDisabled)
	return "trade-trader-" + hash(traderInput), "trade-negotiator-" + hash(negotiatorInput), nil
}

func position(m map[string]any) string {
	return fmt.Sprintf("(%d, %d, %d)", int64(na.AsNumber(m["x"])), int64(na.AsNumber(m["y"])), int64(na.AsNumber(m["z"])))
}

// boolText mirrors C#'s bool.ToString() interpolation ("True"/"False"), the
// exact text NativeTradeOperations' token hash inputs embed.
func boolText(v bool) string {
	if v {
		return "True"
	}
	return "False"
}

func hash(text string) string {
	sum := sha256.Sum256([]byte(text))
	return hex.EncodeToString(sum[:])
}

// flip corrupts a hex token's last character so it no longer matches the
// native hash, for exercising stale-identity refusal paths.
func flip(token string) string {
	if token == "" {
		return "0"
	}
	last := token[len(token)-1]
	replacement := byte('0')
	if last == '0' {
		replacement = '1'
	}
	return token[:len(token)-1] + string(replacement)
}
