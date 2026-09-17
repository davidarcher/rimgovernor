// Command questacceptaccept exercises the AcceptQuest vertical (G01.07f) end
// to end against a live game: a real not-yet-accepted quest carrying a
// two-option native reward-choice part, native acceptance (an actual
// Quest.Accept settings write and its QuestPart_Choice.Choose call)
// invoked through the same rimgovernor/operations_execute wire contract
// Go's buildingruntime.QuestAcceptBoundary drives, and its observation/
// replay semantics. Uses a private disposable fixture
// (test/quest_accept_prepare) since a minimal quest with an exact
// reward-choice shape cannot be produced deterministically through native
// random quest generation. The fixture quest carries no native
// QuestPart_RequirementsToAccept part (the vanilla mechanism that needs an
// accepter, QuestPart_RequirementsToAcceptColonistWithTitle, requires a
// held Royalty title), so this harness instead exercises the "this quest
// does not accept an accepter" refusal branch by supplying one anyway.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-quest-accept-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-quest-accept-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-quest-accept-acceptance"
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
	report := na.NewReport("Native AcceptQuest vertical: an actual Quest.Accept settings write and "+
		"QuestPart_Choice reward selection, exact CAS/stale-token refusal, an accepter-not-required "+
		"refusal, replay idempotency and a post-acceptance stale-identity refusal.", !*rendered)
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
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
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

	prepared, err := h.Call(ctx, "prepare", "test/quest_accept_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("quest_accept_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) ||
		na.AsNumber(prepared["mapId"]) != na.AsNumber(identity["mapId"]) {
		return fmt.Errorf("quest_accept_prepare identity does not match the fresh debug game")
	}
	report["prepared"] = prepared
	questID := na.AsString(prepared["questId"])
	var pawnIDs []string
	for _, raw := range na.AsSlice(prepared["pawnIds"]) {
		pawnIDs = append(pawnIDs, fmt.Sprint(raw))
	}
	if questID == "" || len(pawnIDs) < 1 {
		return fmt.Errorf("quest_accept_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	statusReply, err := h.Wire(ctx, "authority-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return err
	}
	statusContext, _ := na.AsMap(status["context"])
	grantReply, err := h.Wire(ctx, "acquire", "authority_control", map[string]any{"acquire": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"],
		"owner": map[string]any{"controllerSessionId": sessionOwner, "playerDirection": "1"}, "leaseMs": 30000,
	}})
	if err != nil {
		return err
	}
	_, grant, err := na.Outcome(grantReply, "granted")
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])

	// target reads the world-progression census for the fixture's exact
	// quest, mirroring bridge.ReadQuestAcceptTarget's own extraction from
	// the same census (NativeWorldProgressionObservation.cs's Quests()), so
	// the token and facts used below are exactly what the Go boundary
	// itself would compute from this call.
	target := func(label string) (map[string]any, error) {
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
		var questRow map[string]any
		for _, raw := range na.AsSlice(observed["quests"]) {
			row, _ := na.AsMap(raw)
			if na.AsString(row["id"]) == questID {
				questRow = row
			}
		}
		if questRow == nil {
			return nil, fmt.Errorf("%s: fixture quest not found in world progression: %#v", label, observed)
		}
		return questRow, nil
	}

	questRow, err := target("target-before")
	if err != nil {
		return err
	}
	if na.AsString(questRow["state"]) != "QUEST_STATE_NOT_YET_ACCEPTED" && na.AsString(questRow["state"]) != "NotYetAccepted" {
		return fmt.Errorf("target-before: expected a not-yet-accepted quest, got %#v", questRow)
	}
	if requiresAccepter, _ := na.AsBool(questRow["requiresAccepter"]); requiresAccepter {
		return fmt.Errorf("target-before: expected the fixture quest to not require an accepter, got %#v", questRow)
	}
	if canAccept, _ := na.AsBool(questRow["canAccept"]); !canAccept {
		return fmt.Errorf("target-before: expected the fixture quest to be acceptable, got %#v", questRow)
	}
	questSnapshot, _ := na.AsMap(questRow["snapshot"])
	questToken := na.AsString(questSnapshot["token"])
	if questToken == "" {
		return fmt.Errorf("target-before: missing quest snapshot token: %#v", questRow)
	}

	buildOperation := func(qToken, accepterPawn string, rewardChoice int) map[string]any {
		op := map[string]any{"quest": map[string]any{"entityId": questID, "expectedSnapshotToken": qToken}, "rewardChoice": rewardChoice}
		if accepterPawn != "" {
			op["accepterPawnId"] = accepterPawn
		}
		return map[string]any{"acceptQuest": op}
	}
	buildRequest := func(actionID, attemptID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": grantContext["nativeGeneration"], "leaseId": grant["leaseId"],
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// Refusal 1: an obviously stale token is refused rather than silently
	// admitted, the same CAS enforcement every other vertical proves.
	staleTokenRequest := buildRequest("quest-accept-stale", "1", buildOperation("quest-cas-stale-00000000000000000000000000000000000000000000000000000000000000", "", 0))
	if code, err := failureCode(ctx, h, "stale-token", staleTokenRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-token: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 2: this quest does not require an accepter; supplying one
	// anyway is refused rather than silently ignored.
	unwantedAccepterRequest := buildRequest("quest-accept-unwanted-accepter", "1", buildOperation(questToken, pawnIDs[0], 0))
	if code, err := failureCode(ctx, h, "unwanted-accepter", unwantedAccepterRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("unwanted-accepter: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Preview: accepted, but never mutates the live quest.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(questToken, "", 0),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the quest acceptance to be accepted, got %#v", evaluated)
	}
	projected, _ := na.AsMap(evaluated["projected"])
	projectedQuest, _ := na.AsMap(projected["quest"])
	if na.AsString(projectedQuest["questId"]) != questID {
		return fmt.Errorf("preview: unexpected projected quest id: %#v", projectedQuest)
	}
	if acceptedAlready, _ := na.AsBool(projectedQuest["accepted"]); acceptedAlready {
		return fmt.Errorf("preview: expected the dry-run projection to report unaccepted")
	}
	afterPreviewRow, err := target("target-after-preview")
	if err != nil {
		return err
	}
	if na.AsString(afterPreviewRow["state"]) != "QUEST_STATE_NOT_YET_ACCEPTED" && na.AsString(afterPreviewRow["state"]) != "NotYetAccepted" {
		return fmt.Errorf("target-after-preview: expected the quest to survive a dry-run preview: %#v", afterPreviewRow)
	}

	// Execute: the real native Quest.Accept settings write and its
	// QuestPart_Choice.Choose call.
	acceptRequest := buildRequest("quest-accept", "1", buildOperation(questToken, "", 0))
	receiptReply, err := h.Wire(ctx, "execute", "operations_execute", acceptRequest)
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
	if accepted, _ := na.AsBool(appliedQuest["accepted"]); !accepted {
		return fmt.Errorf("execute: expected the quest to be accepted, got %#v", appliedQuest)
	}
	appliedSnapshot, _ := na.AsMap(appliedQuest["snapshot"])
	if na.AsString(appliedSnapshot["beforeToken"]) != questToken {
		return fmt.Errorf("execute: unexpected beforeToken: %#v", appliedSnapshot)
	}

	questRowAccepted, err := target("target-after-execute")
	if err != nil {
		return err
	}
	if na.AsString(questRowAccepted["state"]) == "QUEST_STATE_NOT_YET_ACCEPTED" || na.AsString(questRowAccepted["state"]) == "NotYetAccepted" {
		return fmt.Errorf("target-after-execute: expected the quest to have left NotYetAccepted, got %#v", questRowAccepted)
	}
	if canAccept, _ := na.AsBool(questRowAccepted["canAccept"]); canAccept {
		return fmt.Errorf("target-after-execute: expected canAccept=false for an already-accepted quest, got %#v", questRowAccepted)
	}

	// Observe: durable progress lookup reports the same completed evidence.
	precondition, _ := na.AsMap(acceptRequest["precondition"])
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
	if accepted, _ := na.AsBool(completedQuest["accepted"]); !accepted {
		return fmt.Errorf("observe: expected accepted=true, got %#v", completedQuest)
	}

	// Replay: the exact same attempt returns an identical receipt, and does
	// not attempt the native settings write a second time.
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", acceptRequest)
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

	// Refusal 3: a brand-new attempt against the now-accepted quest is
	// refused as not found rather than silently re-admitted or
	// double-accepted (NativeQuestOperations.Prepare requires
	// State==NotYetAccepted).
	postAcceptRequest := buildRequest("quest-accept-again", "1", buildOperation(questToken, "", 0))
	if code, err := failureCode(ctx, h, "post-acceptance-retry", postAcceptRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("post-acceptance-retry: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
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
