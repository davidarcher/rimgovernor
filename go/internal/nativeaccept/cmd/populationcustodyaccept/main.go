// Command populationcustodyaccept exercises the population custody native
// dispatch (G01.07e, issue #27): NativeCustodyOperations' Capture and Rescue
// pawn-target orders issued through the same rimgovernor/operations_execute
// wire contract Go's buildingruntime/capture.CaptureBoundary and
// buildingruntime/rescue.RescueBoundary drive via bridge.PawnOrderControl.
// A downed hostile is actually captured into prisoner custody and a downed,
// unadmitted friendly guest is actually rescued into an ordinary bed --
// real native roster/status change observed via ticks, not just a receipt.
// Uses the disposable test/population_setup fixture (PopulationFixture.cs)
// since a deterministic downed candidate and guest near a ready prison and
// spare beds cannot be relied on from native random pawn generation and
// colony state, mirroring moodreliefaccept's and surgeryaccept's own
// fixture-first pattern.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-population-custody-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-population-custody-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-population-custody-acceptance"
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
	report := na.NewReport("Native population custody dispatch: an actual Capture job carries a downed hostile "+
		"into prisoner custody and an actual Rescue job carries a downed unadmitted guest into an ordinary bed, "+
		"both issued through the typed operations contract, exact CAS/stale-identity refusal, preview non-mutation, "+
		"real roster/status change observed via native ticks, and replay idempotency.", !*rendered)
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
	if !na.Contains(names, "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}

	// --- Fixture: one disposable downed hostile candidate (capture target,
	// a ready prisoner bed) and one downed unadmitted friendly guest (rescue
	// target, spare sleeping spots). Run this BEFORE acquiring authority:
	// PopulationFixture spawns and configures pawns directly (outside any
	// authority.Owned() scope), and NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of who
	// holds it (RevokeExternal fires whenever the mutation did not happen
	// inside an Owned() scope, not just when a different owner holds it) --
	// so acquiring first just means the fixture immediately revokes it again.
	prepared, err := h.Call(ctx, "setup", "test/population_setup", map[string]any{"candidateKind": "Villager"})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("setup: population_setup refused: %#v", prepared)
	}
	candidateID := na.AsString(prepared["candidate"])
	visitorID := na.AsString(prepared["visitor"])
	prisonID := na.AsString(prepared["prison"])
	if candidateID == "" || visitorID == "" || prisonID == "" {
		return fmt.Errorf("setup: missing fixture ids: %#v", prepared)
	}
	report["fixture_candidate"] = candidateID
	report["fixture_visitor"] = visitorID

	bedChecks := na.AsSlice(prepared["bedChecks"])
	var capturerID string
	for _, entry := range bedChecks {
		row, _ := na.AsMap(entry)
		usable, _ := na.AsBool(row["usable"])
		reachable, _ := na.AsBool(row["reachable"])
		reservable, _ := na.AsBool(row["reservable"])
		if usable && reachable && reservable && na.AsString(row["nativeBed"]) == prisonID {
			capturerID = na.AsString(row["pawn"])
			break
		}
	}
	if capturerID == "" {
		return fmt.Errorf("setup: no fixture worker can reach/reserve the prisoner bed: %#v", bedChecks)
	}
	report["fixture_capturer"] = capturerID

	// acquire (re)grants Auto authority (SetMode(Auto)), reading the freshest
	// nativeGeneration first. Any native simulation activity that happens
	// outside an authority.Owned() scope -- another pawn's WorkGiver-issued
	// job, a colonist's own AI job assignment while ticks run at Fast speed,
	// a fixture spawning pawns -- revokes whatever authority is held via
	// NativeControlAuthority.RevokeExternal, regardless of who holds it. So
	// this must be called again after any such stretch (fixture setup, or
	// running ticks forward to observe an attempt complete) and before the
	// next real dispatch, not just once at the start.
	var grant, grantContext map[string]any
	acquire := func(label string) error {
		newGrant, err := na.GrantAuto(ctx, h.WireFunc(), label, identity)
		if err != nil {
			return err
		}
		grant = newGrant
		grantContext, _ = na.AsMap(grant["context"])
		return nil
	}
	if err := acquire("acquire"); err != nil {
		return err
	}

	// pawnRow reads any exact fixture pawn (worker, hostile candidate or
	// guest) through the same rimgovernor/observations_list_pawns call
	// bridge.ReadCombatPawns issues, so the snapshot token used below is
	// exactly what buildingruntime/capture and buildingruntime/rescue's own
	// boundary.PawnToken would decode from this call.
	pawnRow := func(label, pawnID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}, "includeDead": true},
			"details": map[string]any{},
			"page":    map[string]any{"limit": 1},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return nil, fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		return row, nil
	}
	token := func(row map[string]any) (string, error) {
		pawn, _ := na.AsMap(row["pawn"])
		snapshot, _ := na.AsMap(pawn["snapshot"])
		t := na.AsString(snapshot["token"])
		if t == "" {
			return "", fmt.Errorf("missing pawn snapshot token: %#v", row)
		}
		return t, nil
	}

	buildOperation := func(pawnID, pToken, targetID, targetToken, kind string) map[string]any {
		return map[string]any{"pawnTargetOrder": map[string]any{
			"pawn":               map[string]any{"entityId": pawnID, "expectedSnapshotToken": pToken},
			"target":             map[string]any{"entityId": targetID, "expectedSnapshotToken": targetToken},
			"kind":               kind,
			"requireSafeStorage": false,
		}}
	}
	// currentGeneration re-reads authority_read_status for the freshest
	// nativeGeneration. The native authority guard on a real Execute path
	// compares against the CURRENT generation, which advances over wall
	// clock/tick time independent of this tool's own writes (the same way
	// every production boundary refreshes it from the latest observed
	// context per dispatch rather than reusing a value read once at lease
	// acquisition).
	currentGeneration := func(label string) (any, error) {
		reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		statusContext, _ := na.AsMap(status["context"])
		return statusContext["nativeGeneration"], nil
	}
	buildRequest := func(actionID, attemptID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": attemptID},
			},
			"operation": operation,
		}
	}

	// =================================================================
	// Capture: the downed hostile candidate.
	// =================================================================

	capturerRow, err := pawnRow("capturer-before", capturerID)
	if err != nil {
		return err
	}
	capturerToken, err := token(capturerRow)
	if err != nil {
		return err
	}
	candidateRow, err := pawnRow("candidate-before", candidateID)
	if err != nil {
		return err
	}
	candidateToken, err := token(candidateRow)
	if err != nil {
		return err
	}
	if downed, _ := na.AsBool(candidateRow["downed"]); !downed {
		return fmt.Errorf("candidate-before: expected the fixture candidate to be downed: %#v", candidateRow)
	}
	if prisoner, _ := na.AsBool(candidateRow["prisoner"]); prisoner {
		return fmt.Errorf("candidate-before: expected the fixture candidate not yet a prisoner: %#v", candidateRow)
	}

	// Refusal 1: a stale performer CAS token must be refused.
	staleCapturerRequest := buildRequest("custody-capture-stale-pawn", "1", grantContext["nativeGeneration"],
		buildOperation(capturerID, "stale-pawn-token-00000000000000000000000000000000", candidateID, candidateToken, "PAWN_ORDER_KIND_CAPTURE"))
	if code, err := failureCode(ctx, h, "stale-capturer-token", staleCapturerRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-capturer-token: expected an owner-conflict or not-found refusal, got %q", code)
	}

	// Refusal 2: a stale patient CAS token must be refused.
	stalePatientRequest := buildRequest("custody-capture-stale-target", "1", grantContext["nativeGeneration"],
		buildOperation(capturerID, capturerToken, candidateID, "stale-target-token-0000000000000000000000000000000", "PAWN_ORDER_KIND_CAPTURE"))
	if code, err := failureCode(ctx, h, "stale-patient-token", stalePatientRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-patient-token: expected an owner-conflict or not-found refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live job/roster.
	captureOperation := buildOperation(capturerID, capturerToken, candidateID, candidateToken, "PAWN_ORDER_KIND_CAPTURE")
	previewReply, err := h.Wire(ctx, "preview-capture", "operations_preview", map[string]any{"identity": identity, "operation": captureOperation})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-capture: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-capture: expected capture to be accepted, got %#v", previewEvaluated)
	}
	afterPreviewCandidate, err := pawnRow("candidate-after-preview", candidateID)
	if err != nil {
		return err
	}
	if prisoner, _ := na.AsBool(afterPreviewCandidate["prisoner"]); prisoner {
		return fmt.Errorf("candidate-after-preview: expected an unchanged roster from a dry-run preview: %#v", afterPreviewCandidate)
	}

	// Execute: the real native Capture dispatch. Re-read the current
	// nativeGeneration immediately before dispatch: it advances over
	// wall-clock/tick time independent of this tool's own writes, so the
	// value captured once at lease acquisition is not reliably still current.
	captureGeneration, err := currentGeneration("generation-before-capture-execute")
	if err != nil {
		return err
	}
	captureRequest := buildRequest("custody-capture-execute", "1", captureGeneration, captureOperation)
	captureReceiptReply, err := h.Wire(ctx, "execute-capture", "operations_execute", captureRequest)
	if err != nil {
		return err
	}
	_, captureReceipt, err := na.Outcome(captureReceiptReply, "receipt")
	if err != nil {
		return err
	}
	captureApplied, ok := na.AsMap(captureReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-capture: expected an applied outcome, got %#v", captureReceipt)
	}
	captureObserved, _ := na.AsMap(captureApplied["observed"])
	captureJob, _ := na.AsMap(captureObserved["job"])
	if na.AsString(captureJob["pawnId"]) != capturerID || na.AsString(captureJob["jobDef"]) != "Capture" {
		return fmt.Errorf("execute-capture: unexpected applied capture evidence: %#v", captureJob)
	}
	if issued, _ := na.AsBool(captureJob["issued"]); !issued {
		return fmt.Errorf("execute-capture: expected the native capture job to be issued, got %#v", captureJob)
	}

	capturePrecondition, _ := na.AsMap(captureRequest["precondition"])
	captureAttempt := map[string]any{"identity": identity, "attempt": capturePrecondition["attempt"]}

	// Observe: run real game time forward until the candidate is actually
	// observed to become a colony prisoner; absence of the issued job never
	// proves capture by itself.
	if _, err := h.Call(ctx, "resume-capture", "rimworld/set_time_speed", map[string]any{"speed": "Fast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := pollCompleted(ctx, h, captureAttempt, 10*time.Minute); err != nil {
		return fmt.Errorf("observe-capture: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after-capture", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	afterCaptureRow, err := pawnRow("candidate-after-complete", candidateID)
	if err != nil {
		return err
	}
	if prisoner, _ := na.AsBool(afterCaptureRow["prisoner"]); !prisoner {
		return fmt.Errorf("candidate-after-complete: expected the native candidate to be an admitted prisoner: %#v", afterCaptureRow)
	}
	report["candidate_captured"] = true

	// Replay: the exact same attempt returns an identical receipt.
	captureReplayReply, err := h.Wire(ctx, "replay-capture", "operations_execute", captureRequest)
	if err != nil {
		return err
	}
	_, captureReplay, err := na.Outcome(captureReplayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(captureReplay, captureReceipt) {
		return fmt.Errorf("replay-capture: replay of the same attempt returned a different receipt")
	}
	captureLookupReply, err := h.Wire(ctx, "lookup-capture", "receipts_lookup", captureAttempt)
	if err != nil {
		return err
	}
	_, captureLookup, err := na.Outcome(captureLookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(captureLookup, captureReceipt) {
		return fmt.Errorf("lookup-capture: expected the same receipt as execute, got %#v", captureLookup)
	}

	// =================================================================
	// Rescue: the downed, unadmitted friendly guest.
	// =================================================================

	// Re-acquire: the Fast-speed run to observe the capture complete let the
	// colony's own AI issue jobs and run its usual simulation outside this
	// tool's authority.Owned() scope, which revokes whatever lease was held
	// (see the acquire closure's comment above). The prior lease is gone by
	// now; a fresh one is required before the real rescue dispatch below.
	if err := acquire("re-acquire-before-rescue"); err != nil {
		return err
	}

	rescuerRow, err := pawnRow("rescuer-before", capturerID)
	if err != nil {
		return err
	}
	rescuerToken, err := token(rescuerRow)
	if err != nil {
		return err
	}
	visitorRowBefore, err := pawnRow("visitor-before", visitorID)
	if err != nil {
		return err
	}
	visitorToken, err := token(visitorRowBefore)
	if err != nil {
		return err
	}
	if downed, _ := na.AsBool(visitorRowBefore["downed"]); !downed {
		return fmt.Errorf("visitor-before: expected the fixture guest to be downed: %#v", visitorRowBefore)
	}
	if inBed, _ := na.AsBool(visitorRowBefore["inBed"]); inBed {
		return fmt.Errorf("visitor-before: expected the fixture guest not yet admitted to a bed: %#v", visitorRowBefore)
	}

	rescueOperation := buildOperation(capturerID, rescuerToken, visitorID, visitorToken, "PAWN_ORDER_KIND_RESCUE")
	rescuePreviewReply, err := h.Wire(ctx, "preview-rescue", "operations_preview", map[string]any{"identity": identity, "operation": rescueOperation})
	if err != nil {
		return err
	}
	rescuePreviewEvaluated, ok := na.AsMap(rescuePreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-rescue: expected an evaluated reply, got %#v", rescuePreviewReply)
	}
	if accepted, _ := na.AsBool(rescuePreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-rescue: expected rescue to be accepted, got %#v", rescuePreviewEvaluated)
	}

	rescueGeneration, err := currentGeneration("generation-before-rescue-execute")
	if err != nil {
		return err
	}
	rescueRequest := buildRequest("custody-rescue-execute", "1", rescueGeneration, rescueOperation)
	rescueReceiptReply, err := h.Wire(ctx, "execute-rescue", "operations_execute", rescueRequest)
	if err != nil {
		return err
	}
	_, rescueReceipt, err := na.Outcome(rescueReceiptReply, "receipt")
	if err != nil {
		return err
	}
	rescueApplied, ok := na.AsMap(rescueReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-rescue: expected an applied outcome, got %#v", rescueReceipt)
	}
	rescueObserved, _ := na.AsMap(rescueApplied["observed"])
	rescueJob, _ := na.AsMap(rescueObserved["job"])
	if na.AsString(rescueJob["pawnId"]) != capturerID || na.AsString(rescueJob["jobDef"]) != "Rescue" {
		return fmt.Errorf("execute-rescue: unexpected applied rescue evidence: %#v", rescueJob)
	}
	if issued, _ := na.AsBool(rescueJob["issued"]); !issued {
		return fmt.Errorf("execute-rescue: expected the native rescue job to be issued, got %#v", rescueJob)
	}

	rescuePrecondition, _ := na.AsMap(rescueRequest["precondition"])
	rescueAttempt := map[string]any{"identity": identity, "attempt": rescuePrecondition["attempt"]}

	if _, err := h.Call(ctx, "resume-rescue", "rimworld/set_time_speed", map[string]any{"speed": "Fast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := pollCompleted(ctx, h, rescueAttempt, 10*time.Minute); err != nil {
		return fmt.Errorf("observe-rescue: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after-rescue", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	afterRescueRow, err := pawnRow("visitor-after-complete", visitorID)
	if err != nil {
		return err
	}
	if inBed, _ := na.AsBool(afterRescueRow["inBed"]); !inBed {
		return fmt.Errorf("visitor-after-complete: expected the native guest to be carried into a bed: %#v", afterRescueRow)
	}
	report["visitor_rescued"] = true

	rescueReplayReply, err := h.Wire(ctx, "replay-rescue", "operations_execute", rescueRequest)
	if err != nil {
		return err
	}
	_, rescueReplay, err := na.Outcome(rescueReplayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(rescueReplay, rescueReceipt) {
		return fmt.Errorf("replay-rescue: replay of the same attempt returned a different receipt")
	}
	rescueLookupReply, err := h.Wire(ctx, "lookup-rescue", "receipts_lookup", rescueAttempt)
	if err != nil {
		return err
	}
	_, rescueLookup, err := na.Outcome(rescueLookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(rescueLookup, rescueReceipt) {
		return fmt.Errorf("lookup-rescue: expected the same receipt as execute, got %#v", rescueLookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// pollCompleted polls receipts_observe_progress until the attempt is
// observed Completed, matching moodreliefaccept's own poll loop. It fails
// fast if the attempt is instead observed Unsuccessful.
func pollCompleted(ctx context.Context, h *na.Harness, attempt map[string]any, budget time.Duration) error {
	deadline := time.Now().Add(budget)
	for {
		if time.Now().After(deadline) {
			return fmt.Errorf("attempt did not complete within the polling deadline")
		}
		progressReply, err := h.Wire(ctx, "observe-poll", "receipts_observe_progress", attempt)
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(progressReply, "progress")
		if err != nil {
			return err
		}
		if unsuccessful, ok := na.AsMap(progress["unsuccessful"]); ok {
			return fmt.Errorf("attempt became unsuccessful before completion: %#v", unsuccessful)
		}
		if _, ok := na.AsMap(progress["completed"]); ok {
			return nil
		}
		time.Sleep(2 * time.Second)
	}
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring moodreliefaccept's helper of the same name.
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
