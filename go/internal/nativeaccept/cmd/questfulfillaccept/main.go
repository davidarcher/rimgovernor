// Command questfulfillaccept exercises the FulfillQuest vertical (G01.07f)
// end to end against a live game: an ongoing quest carrying a single native
// settlement trade-request objective, a real player caravan carrying the
// exact requested resource, native fulfillment (an actual TradeRequestComp
// caravan gizmo callback and its Dialog_MessageBox confirmation) invoked
// through the same rimgovernor/operations_execute wire contract Go's
// buildingruntime.QuestFulfillBoundary drives, and its observation/replay
// semantics. Uses a private disposable fixture (test/quest_fulfill_prepare,
// test/quest_fulfill_control) since fulfillment requires an exact quest/
// caravan/settlement triple that native random quest generation cannot
// deterministically produce.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-quest-fulfill-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-quest-fulfill-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-quest-fulfill-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native FulfillQuest vertical: settlement trade-request fulfillment through an "+
		"actual TradeRequestComp caravan gizmo callback and confirmation dialog, exact CAS/stale-identity "+
		"refusal, replay idempotency and durable lookup.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
	h := na.NewHarness(client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/observations_read_world_progression") {
		return fmt.Errorf("missing rimgovernor/observations_read_world_progression in discovery")
	}

	prepared, err := h.Call(ctx, "prepare", "test/quest_fulfill_prepare", map[string]any{"requestedCount": 40})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("quest_fulfill_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) ||
		na.AsNumber(prepared["mapId"]) != na.AsNumber(identity["mapId"]) {
		return fmt.Errorf("quest_fulfill_prepare identity does not match the fresh debug game")
	}
	report["prepared"] = prepared
	questID := na.AsString(prepared["questId"])
	caravanID := na.AsString(prepared["caravanId"])
	settlementTile := na.AsNumber(prepared["settlementTile"])
	homeTile := na.AsNumber(prepared["homeTile"])
	var pawnIDs []string
	for _, raw := range na.AsSlice(prepared["pawnIds"]) {
		pawnIDs = append(pawnIDs, fmt.Sprint(raw))
	}
	if questID == "" || caravanID == "" || len(pawnIDs) != 2 {
		return fmt.Errorf("quest_fulfill_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// target reads the world-progression census for the fixture's exact quest
	// and caravan, mirroring bridge.ReadQuestFulfillTarget's own extraction
	// from the same census (NativeWorldProgressionObservation.cs's Quests()/
	// Caravans()), so the tokens and facts used below are exactly what the
	// Go boundary itself would compute from this call.
	target := func(label string) (questRow, caravanRow map[string]any, err error) {
		reply, err := h.Wire(ctx, label, "observations_read_world_progression", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false, "page": map[string]any{"limit": 64},
		})
		if err != nil {
			return nil, nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, nil, err
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
			return nil, nil, fmt.Errorf("%s: incomplete or unreadable world progression page: %#v", label, completeness)
		}
		for _, raw := range na.AsSlice(observed["quests"]) {
			row, _ := na.AsMap(raw)
			if na.AsString(row["id"]) == questID {
				questRow = row
			}
		}
		for _, raw := range na.AsSlice(observed["caravans"]) {
			row, _ := na.AsMap(raw)
			caravan, _ := na.AsMap(row["caravan"])
			if na.AsString(caravan["id"]) == caravanID {
				caravanRow = row
			}
		}
		if questRow == nil || caravanRow == nil {
			return nil, nil, fmt.Errorf("%s: fixture quest/caravan not found in world progression: %#v", label, observed)
		}
		return questRow, caravanRow, nil
	}

	questRow, caravanRow, err := target("target-before")
	if err != nil {
		return err
	}
	if na.AsString(questRow["state"]) != "QUEST_STATE_ONGOING" && na.AsString(questRow["state"]) != "Ongoing" {
		return fmt.Errorf("target-before: expected an ongoing quest, got %#v", questRow)
	}
	tradeRequests := na.AsSlice(questRow["tradeRequests"])
	if len(tradeRequests) != 1 {
		return fmt.Errorf("target-before: expected exactly one trade request, got %#v", questRow)
	}
	if na.AsNumber(caravanRow["tile"]) != homeTile {
		return fmt.Errorf("target-before: expected the caravan still at the home tile, got %#v", caravanRow)
	}
	if moving, _ := na.AsBool(caravanRow["moving"]); moving {
		return fmt.Errorf("target-before: expected a stationary caravan, got %#v", caravanRow)
	}
	questSnapshot, _ := na.AsMap(questRow["snapshot"])
	questToken := na.AsString(questSnapshot["token"])
	staleCaravanToken, err := caravanToken(caravanRow, pawnIDs)
	if err != nil {
		return err
	}
	if questToken == "" {
		return fmt.Errorf("target-before: missing quest snapshot token: %#v", questRow)
	}

	buildOperation := func(qToken, cToken string) map[string]any {
		return map[string]any{"fulfillQuest": map[string]any{
			"quest":           map[string]any{"entityId": questID, "expectedSnapshotToken": qToken},
			"caravan":         map[string]any{"entityId": caravanID, "expectedSnapshotToken": cToken},
			"expectedPawnIds": pawnIDs,
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

	// Refusal 1: the caravan has not yet visited the quest's settlement.
	notYetThereRequest := buildRequest("quest-fulfill-not-there", "1", buildOperation(questToken, staleCaravanToken))
	if code, err := failureCode(ctx, h, "not-at-settlement", notYetThereRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("not-at-settlement: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	teleportResult, err := h.Call(ctx, "teleport-to-settlement", "test/quest_fulfill_control", map[string]any{
		"operation": "teleport-settlement", "colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(teleportResult["success"]); !success {
		return fmt.Errorf("teleport-to-settlement refused: %#v", teleportResult)
	}

	questRowAfter, caravanRowAfter, err := target("target-after-teleport")
	if err != nil {
		return err
	}
	if na.AsNumber(caravanRowAfter["tile"]) != settlementTile {
		return fmt.Errorf("target-after-teleport: expected the caravan at the settlement tile, got %#v", caravanRowAfter)
	}
	freshCaravanToken, err := caravanToken(caravanRowAfter, pawnIDs)
	if err != nil {
		return err
	}
	if freshCaravanToken == staleCaravanToken {
		return fmt.Errorf("target-after-teleport: expected the caravan token to change with position")
	}
	questSnapshotAfter, _ := na.AsMap(questRowAfter["snapshot"])
	freshQuestToken := na.AsString(questSnapshotAfter["token"])

	// Refusal 2: an exact caravan-position CAS token captured before the
	// caravan moved must be refused now that the caravan really is at the
	// settlement, proving the position token is load-bearing rather than a
	// no-op.
	staleTokenRequest := buildRequest("quest-fulfill-stale", "1", buildOperation(freshQuestToken, staleCaravanToken))
	if code, err := failureCode(ctx, h, "stale-caravan-token", staleTokenRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-caravan-token: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: accepted, but never mutates the live TradeRequestComp.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(freshQuestToken, freshCaravanToken),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the fulfillment to be accepted, got %#v", evaluated)
	}
	projected, _ := na.AsMap(evaluated["projected"])
	projectedQuest, _ := na.AsMap(projected["quest"])
	if na.AsString(projectedQuest["questId"]) != questID {
		return fmt.Errorf("preview: unexpected projected quest id: %#v", projectedQuest)
	}
	if fulfilledAlready, _ := na.AsBool(projectedQuest["accepted"]); fulfilledAlready {
		return fmt.Errorf("preview: expected the dry-run projection to report unfulfilled")
	}
	afterPreviewRow, _, err := target("target-after-preview")
	if err != nil {
		return err
	}
	if tradeRequestsAfterPreview := na.AsSlice(afterPreviewRow["tradeRequests"]); len(tradeRequestsAfterPreview) != 1 {
		return fmt.Errorf("target-after-preview: expected the trade request to survive a dry-run preview: %#v", afterPreviewRow)
	}

	// Execute: the real native gizmo callback and its confirmation dialog.
	fulfillRequest := buildRequest("quest-fulfill", "1", buildOperation(freshQuestToken, freshCaravanToken))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", fulfillRequest)
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
	appliedQuest, _ := na.AsMap(appliedObserved["quest"])
	if na.AsString(appliedQuest["questId"]) != questID {
		return fmt.Errorf("execute: unexpected applied quest id: %#v", appliedQuest)
	}
	if fulfilled, _ := na.AsBool(appliedQuest["accepted"]); !fulfilled {
		return fmt.Errorf("execute: expected the trade request to be fulfilled, got %#v", appliedQuest)
	}
	appliedSnapshot, _ := na.AsMap(appliedQuest["snapshot"])
	if na.AsString(appliedSnapshot["beforeToken"]) != freshQuestToken {
		return fmt.Errorf("execute: unexpected beforeToken: %#v", appliedSnapshot)
	}

	questRowFulfilled, _, err := target("target-after-execute")
	if err != nil {
		return err
	}
	if tradeRequestsAfter := na.AsSlice(questRowFulfilled["tradeRequests"]); len(tradeRequestsAfter) != 0 {
		return fmt.Errorf("target-after-execute: expected the native trade request to be cleared, got %#v", questRowFulfilled)
	}

	// Observe: durable progress lookup reports the same completed evidence.
	precondition, _ := na.AsMap(fulfillRequest["precondition"])
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
	completedQuest, _ := na.AsMap(completedEvidence["quest"])
	if na.AsString(completedQuest["questId"]) != questID {
		return fmt.Errorf("observe: unexpected completed quest id: %#v", completedQuest)
	}
	if fulfilled, _ := na.AsBool(completedQuest["accepted"]); !fulfilled {
		return fmt.Errorf("observe: expected accepted=true, got %#v", completedQuest)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt the native gizmo callback a second time.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", fulfillRequest)
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

	// Refusal 3: a brand-new attempt against the now-cleared trade request is
	// refused as stale rather than silently re-admitted or double-fulfilled.
	postFulfillRequest := buildRequest("quest-fulfill-again", "1", buildOperation(freshQuestToken, freshCaravanToken))
	if code, err := failureCode(ctx, h, "post-fulfillment-retry", postFulfillRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("post-fulfillment-retry: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// failureCode wires request through operations_execute and returns the failure
// code, mirroring guardedconstructionaccept's/movementaccept's helper of the
// same name.
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

// caravanToken reproduces NativeQuestFulfillOperations.CaravanToken/Go's
// unexported questFulfillCaravanToken exactly (same joined-string SHA256
// hex, lowercase booleans, ordinally sorted pawn ids), from a world-
// progression caravan row alone, so this acceptance binary can self-compute
// the same CAS token the native contract and the Go boundary both use
// without needing to export that helper from internal/bridge.
func caravanToken(caravanRow map[string]any, expectedPawnIDs []string) (string, error) {
	caravan, ok := na.AsMap(caravanRow["caravan"])
	if !ok {
		return "", fmt.Errorf("caravanToken: missing caravan entity ref: %#v", caravanRow)
	}
	id := na.AsString(caravan["id"])
	if id == "" {
		return "", fmt.Errorf("caravanToken: missing caravan id: %#v", caravanRow)
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
		return "", fmt.Errorf("caravanToken: caravan crew %v does not match expected crew %v", pawnIDs, expectedPawnIDs)
	}
	sorted := append([]string(nil), pawnIDs...)
	sort.Strings(sorted)
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s|%s", id, tile, movingText, strings.Join(sorted, ","))))
	return "caravan-fulfill-" + hex.EncodeToString(sum[:]), nil
}
