// The settlement/gift case exercises the GiftCaravanSilver vertical
// (G01.07f) end to end against a live game: a real player caravan carrying
// Silver, a real non-hostile trading settlement, and native gift execution
// (an actual gift-mode TradeSession/TradeDeal via TradeSession.SetupWith
// and TradeDeal.TryExecute) invoked through the same
// rimgovernor/operations_execute wire contract Go's
// buildingruntime.SettlementGiftBoundary drives, and its observation/replay
// semantics. Uses a private disposable fixture
// (test/settlement_gift_prepare, test/settlement_gift_control) since gifting
// requires an exact caravan/settlement pair that native random colony
// generation cannot deterministically produce, mirroring
// questfulfillaccept's own fixture-first pattern.
package settlement

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-settlement-gift-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "settlement/gift",
		Scope: "Native GiftCaravanSilver vertical: a real gift-mode TradeSession/TradeDeal " +
			"execution against an actual visited settlement, exact caravan-position/faction-token CAS and " +
			"visit-first refusal, replay idempotency and a post-gift stale-faction-token refusal.",
		Start:  cases.Fixture{Op: "test/settlement_gift_prepare", Args: map[string]any{"silverCount": 100}},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	for _, required := range []string{"rimgovernor/observations_read_world_progression", "rimgovernor/observations_read_world"} {
		if !na.Contains(names, required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}

	caravanID := na.AsString(prepared["caravanId"])
	factionID := na.AsString(prepared["factionId"])
	settlementTile := na.AsNumber(prepared["settlementTile"])
	homeTile := na.AsNumber(prepared["homeTile"])
	silverCount := int32(na.AsNumber(prepared["silverCount"]))
	var pawnIDs []string
	for _, raw := range na.AsSlice(prepared["pawnIds"]) {
		pawnIDs = append(pawnIDs, fmt.Sprint(raw))
	}
	if caravanID == "" || factionID == "" || len(pawnIDs) != 2 {
		return fmt.Errorf("settlement_gift_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// journey reads the world-progression census for the fixture's exact
	// caravan, mirroring bridge.ReadSettlementGiftTarget's own extraction.
	journey := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_world_progression", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false, "page": map[string]any{"limit": 64},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
			return nil, fmt.Errorf("%s: incomplete or unreadable world progression page: %#v", label, completeness)
		}
		var caravanRow map[string]any
		for _, raw := range na.AsSlice(observed["caravans"]) {
			row, _ := na.AsMap(raw)
			caravan, _ := na.AsMap(row["caravan"])
			if na.AsString(caravan["id"]) == caravanID {
				caravanRow = row
			}
		}
		if caravanRow == nil {
			return nil, fmt.Errorf("%s: fixture caravan not found in world progression: %#v", label, observed)
		}
		return caravanRow, nil
	}

	// settlement reads the world census for whatever settlement sits at
	// tile, mirroring bridge.ReadWorld/ReadSettlementGiftTarget.
	settlement := func(label string, tile float64) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_world", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "tile": tile, "settlementRadius": 0,
			"page": map[string]any{"limit": 256},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete {
			return nil, fmt.Errorf("%s: incomplete world page: %#v", label, completeness)
		}
		var settlementRow map[string]any
		for _, raw := range na.AsSlice(observed["settlements"]) {
			row, _ := na.AsMap(raw)
			if na.AsNumber(row["tile"]) == tile {
				settlementRow = row
			}
		}
		if settlementRow == nil {
			return nil, fmt.Errorf("%s: no settlement observed at tile %v: %#v", label, tile, observed)
		}
		return settlementRow, nil
	}

	caravanRowBefore, err := journey("journey-before")
	if err != nil {
		return err
	}
	if na.AsNumber(caravanRowBefore["tile"]) != homeTile {
		return fmt.Errorf("journey-before: expected the caravan still at the home tile, got %#v", caravanRowBefore)
	}
	if moving, _ := na.AsBool(caravanRowBefore["moving"]); moving {
		return fmt.Errorf("journey-before: expected a stationary caravan, got %#v", caravanRowBefore)
	}
	staleCaravanToken, err := caravanGiftToken(caravanRowBefore, pawnIDs)
	if err != nil {
		return err
	}
	var beforeSilver int64
	for _, raw := range na.AsSlice(caravanRowBefore["inventory"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["defName"]) == "Silver" {
			beforeSilver = int64(na.AsNumber(row["units"]))
		}
	}
	if beforeSilver < int64(silverCount) {
		return fmt.Errorf("journey-before: expected the caravan to carry at least %d Silver, got %d", silverCount, beforeSilver)
	}

	// The home tile carries the player's own colony as a world object too, so
	// this only confirms the caravan has not somehow already reached the
	// fixture's target settlement, not that the tile is empty.
	if settlementRowBefore, err := settlement("settlement-before", homeTile); err == nil {
		if na.AsString(settlementRowBefore["factionId"]) == factionID {
			return fmt.Errorf("settlement-before: unexpectedly found the target settlement's faction at the home tile: %#v", settlementRowBefore)
		}
	}

	buildOperation := func(caravanTok, factionTok string) map[string]any {
		return map[string]any{"giftCaravanSilver": map[string]any{
			"caravan":         map[string]any{"entityId": caravanID, "expectedSnapshotToken": caravanTok},
			"faction":         map[string]any{"entityId": factionID, "expectedSnapshotToken": factionTok},
			"expectedPawnIds": pawnIDs, "silver": silverCount,
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

	// Refusal 1: the caravan has not yet visited any settlement.
	notYetThereRequest := buildRequest("settlement-gift-not-there", "1", buildOperation(staleCaravanToken, "faction-token-placeholder"))
	if code, err := failureCode(ctx, h, "not-at-settlement", notYetThereRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("not-at-settlement: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	teleportResult, err := h.Call(ctx, "teleport-to-settlement", "test/settlement_gift_control", map[string]any{
		"operation": "teleport-settlement", "colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(teleportResult["success"]); !success {
		return fmt.Errorf("teleport-to-settlement refused: %#v", teleportResult)
	}

	caravanRowAfter, err := journey("journey-after-teleport")
	if err != nil {
		return err
	}
	if na.AsNumber(caravanRowAfter["tile"]) != settlementTile {
		return fmt.Errorf("journey-after-teleport: expected the caravan at the settlement tile, got %#v", caravanRowAfter)
	}
	freshCaravanToken, err := caravanGiftToken(caravanRowAfter, pawnIDs)
	if err != nil {
		return err
	}
	if freshCaravanToken == staleCaravanToken {
		return fmt.Errorf("journey-after-teleport: expected the caravan token to change with position")
	}

	settlementRow, err := settlement("settlement-after-teleport", settlementTile)
	if err != nil {
		return err
	}
	if na.AsString(settlementRow["factionId"]) != factionID {
		return fmt.Errorf("settlement-after-teleport: unexpected faction id: %#v", settlementRow)
	}
	factionSnapshot, _ := na.AsMap(settlementRow["factionSnapshot"])
	freshFactionToken := na.AsString(factionSnapshot["token"])
	if freshFactionToken == "" {
		return fmt.Errorf("settlement-after-teleport: missing faction snapshot token: %#v", settlementRow)
	}
	beforeGoodwill := na.AsNumber(settlementRow["goodwill"])

	// Refusal 2: an exact caravan-position CAS token captured before the
	// caravan moved must be refused now that the caravan really is at the
	// settlement, proving the position token is load-bearing rather than a
	// no-op.
	staleTokenRequest := buildRequest("settlement-gift-stale", "1", buildOperation(staleCaravanToken, freshFactionToken))
	if code, err := failureCode(ctx, h, "stale-caravan-token", staleTokenRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-caravan-token: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: accepted, but never mutates the live TradeSession.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(freshCaravanToken, freshFactionToken),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the gift to be accepted, got %#v", evaluated)
	}
	projected, _ := na.AsMap(evaluated["projected"])
	projectedTrade, _ := na.AsMap(projected["trade"])
	if na.AsString(projectedTrade["factionId"]) != factionID {
		return fmt.Errorf("preview: unexpected projected faction id: %#v", projectedTrade)
	}

	// Execute: the real native gift-mode TradeSession/TradeDeal.
	giftRequest := buildRequest("settlement-gift", "1", buildOperation(freshCaravanToken, freshFactionToken))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", giftRequest)
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
	if na.AsString(appliedTrade["factionId"]) != factionID {
		return fmt.Errorf("execute: unexpected applied faction id: %#v", appliedTrade)
	}
	if executed, _ := na.AsBool(appliedTrade["executed"]); !executed {
		return fmt.Errorf("execute: expected the gift to execute, got %#v", appliedTrade)
	}
	if actuallyTraded, _ := na.AsBool(appliedTrade["actuallyTraded"]); !actuallyTraded {
		return fmt.Errorf("execute: expected actuallyTraded=true, got %#v", appliedTrade)
	}
	if closed, _ := na.AsBool(appliedTrade["closed"]); !closed {
		return fmt.Errorf("execute: expected the gift session to close, got %#v", appliedTrade)
	}
	afterSilver := na.AsNumber(appliedTrade["afterSilver"])
	beforeSilverReported := na.AsNumber(appliedTrade["beforeSilver"])
	if afterSilver >= beforeSilverReported {
		return fmt.Errorf("execute: expected silver to decrease, before=%v after=%v", beforeSilverReported, afterSilver)
	}
	afterGoodwill := na.AsNumber(appliedTrade["afterGoodwill"])
	if afterGoodwill <= beforeGoodwill {
		return fmt.Errorf("execute: expected goodwill to increase, before=%v after=%v", beforeGoodwill, afterGoodwill)
	}

	// Observe: durable progress lookup reports the same completed evidence.
	precondition, _ := na.AsMap(giftRequest["precondition"])
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
	completed, ok := na.AsMap(progress["completed"])
	if !ok {
		return fmt.Errorf("observe: expected a completed outcome, got %#v", progress)
	}
	completedEvidence, _ := na.AsMap(completed["evidence"])
	completedTrade, _ := na.AsMap(completedEvidence["trade"])
	if na.AsString(completedTrade["factionId"]) != factionID {
		return fmt.Errorf("observe: unexpected completed faction id: %#v", completedTrade)
	}
	if closed, _ := na.AsBool(completedTrade["closed"]); !closed {
		return fmt.Errorf("observe: expected closed=true, got %#v", completedTrade)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt a second native gift.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", giftRequest)
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

	// Refusal 3: a brand-new attempt reusing the now-stale (pre-gift)
	// faction token is refused rather than silently re-admitted, since the
	// gift changed the faction's goodwill and therefore its token.
	postGiftRequest := buildRequest("settlement-gift-again", "1", buildOperation(freshCaravanToken, freshFactionToken))
	if code, err := failureCode(ctx, h, "post-gift-retry", postGiftRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("post-gift-retry: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questfulfillaccept's/movementaccept's helper of
// the same name.
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

// caravanGiftToken reproduces NativeSettlementGiftOperations.CaravanToken/
// Go's unexported caravanToken (bridge/settlement_gift.go) exactly (same
// joined-string SHA256 hex, lowercase booleans, ordinally sorted pawn ids),
// from a world-progression caravan row alone, mirroring questfulfillaccept's
// own caravanToken helper (which uses the same shape with a different
// "caravan-fulfill-" prefix).
func caravanGiftToken(caravanRow map[string]any, expectedPawnIDs []string) (string, error) {
	caravan, ok := na.AsMap(caravanRow["caravan"])
	if !ok {
		return "", fmt.Errorf("caravanGiftToken: missing caravan entity ref: %#v", caravanRow)
	}
	id := na.AsString(caravan["id"])
	if id == "" {
		return "", fmt.Errorf("caravanGiftToken: missing caravan id: %#v", caravanRow)
	}
	tile := int64(na.AsNumber(caravanRow["tile"]))
	moving, _ := na.AsBool(caravanRow["moving"])
	var pawnIDs []string
	for _, raw := range na.AsSlice(caravanRow["pawns"]) {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		pawnIDs = append(pawnIDs, na.AsString(pawn["id"]))
	}
	if len(pawnIDs) != len(expectedPawnIDs) {
		return "", fmt.Errorf("caravanGiftToken: caravan crew %v does not match expected crew %v", pawnIDs, expectedPawnIDs)
	}
	sorted := append([]string(nil), pawnIDs...)
	sort.Strings(sorted)
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s|%s", id, tile, movingText, strings.Join(sorted, ","))))
	return "caravan-gift-" + hex.EncodeToString(sum[:]), nil
}
