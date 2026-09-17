// Command recoveryserviceaccept exercises the disaster-recovery service
// native dispatch vertical (G01.07e, issue #27): NativeRecoveryOperations'
// RecoverService operation (integrations/rimgovernor-native/src/Bridge/
// Protocol/NativeRecoveryOperations.cs), wired onto its own distinct
// CommandOneofCase.RecoverService in NativeOperationTools.cs the same way
// PlaceBuilding (proven live by animalcontainmentaccept) is -- not a
// kind-based sub-routing that could silently misroute. A genuinely damaged
// player Wall (HitPoints below MaxHitPoints, useHitPoints=true) is actually
// repaired by a real native WorkGiver_Repair job carried out by an
// already-selected undrafted colonist, observed via real game ticks and
// independently confirmed via rimgovernor/observations_list_buildings
// (HitPoints == MaxHitPoints), not just a receipt.
//
// Uses the disposable test/recovery_service_prepare fixture
// (RecoveryServiceFixture.cs) since a deterministic damaged player building
// and a capable, reachable colonist cannot be relied on from native random
// pawn generation and starting colony state, mirroring
// animalcontainmentaccept's and populationcustodyaccept's own
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

const sessionOwner = "native-recovery-service-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-recovery-service-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-recovery-service-acceptance"
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
	report := na.NewReport("Native RecoverService (disaster-recovery service) dispatch: a genuinely damaged "+
		"player Wall is actually repaired by a real native WorkGiver_Repair job issued through the typed "+
		"operations contract, exact CAS/stale-identity refusal, preview non-mutation, real HitPoints change "+
		"observed via native ticks (not just a receipt), and replay idempotency.", !*rendered)
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

	// Fixture: one genuinely damaged player Wall and one capable
	// Construction-enabled colonist. Run this BEFORE acquiring authority: the
	// fixture spawns/damages a building directly outside any
	// authority.Owned() scope, and NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of who
	// holds it, mirroring animalcontainmentaccept's own ordering.
	prepared, err := h.Call(ctx, "prepare", "test/recovery_service_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("prepare: recovery_service_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) {
		return fmt.Errorf("prepare: fixture identity does not match the fresh debug game")
	}
	pawnID := na.AsString(prepared["pawn"])
	wallID := na.AsString(prepared["wall"])
	if pawnID == "" || wallID == "" {
		return fmt.Errorf("prepare: missing fixture pawn/wall ids: %#v", prepared)
	}
	maxHitPoints := na.AsNumber(prepared["maxHitPoints"])
	damagedHitPoints := na.AsNumber(prepared["damagedHitPoints"])
	if maxHitPoints <= 0 || damagedHitPoints <= 0 || damagedHitPoints >= maxHitPoints {
		return fmt.Errorf("prepare: fixture wall is not genuinely damaged: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID
	report["fixture_wall"] = wallID
	report["fixture_max_hit_points"] = maxHitPoints
	report["fixture_damaged_hit_points"] = damagedHitPoints

	// acquire grants the bot Auto authority (SetMode(Auto), the only handshake
	// since #52). Only one dispatch happens in this tool (no Fast-speed
	// tick-advance window precedes it), so a single acquire before the whole
	// stale-token/preview/execute sequence is enough -- unlike
	// animalcontainmentaccept/populationcustodyaccept's multi-dispatch runs,
	// there is no need to re-grant before later dispatches here.
	acquire := func(label string) error {
		_, err := na.GrantAuto(ctx, h.WireFunc(), label, identity)
		return err
	}
	if err := acquire("acquire"); err != nil {
		return err
	}

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

	// pawnToken reads the exact native pawn-control snapshot token through
	// rimgovernor/observations_list_pawns, the same call
	// buildingruntime.RecoveryServiceBoundary.InspectRecoveryService's own
	// bridge.ReadPawns issues.
	pawnToken := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{},
			"page":    map[string]any{"limit": 1},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return "", fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return "", fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		snapshot, _ := na.AsMap(pawn["snapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("%s: missing pawn snapshot token: %#v", label, row)
		}
		return token, nil
	}

	// wallState reads HitPoints/maxHitPoints through
	// rimgovernor/observations_list_buildings with statuses=["built"],
	// category="artificial", playerOnly=true, purely to independently assert
	// the wall's real HitPoints before/after dispatch (not for its CAS
	// token: see discoverTargetToken below).
	wallState := func(label string) (hitPoints, maxHP float64, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{wallID},
			"statuses": []string{"built"}, "category": "artificial", "playerOnly": true,
			"page": map[string]any{"limit": 1},
		})
		if err != nil {
			return 0, 0, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return 0, 0, err
		}
		rows := na.AsSlice(observed["buildings"])
		if len(rows) != 1 {
			return 0, 0, fmt.Errorf("%s: expected exactly one building row, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["id"]) != wallID {
			return 0, 0, fmt.Errorf("%s: unexpected building row: %#v", label, row)
		}
		if _, present := row["hitPoints"]; !present {
			return 0, 0, fmt.Errorf("%s: missing hitPoints: %#v", label, row)
		}
		return na.AsNumber(row["hitPoints"]), na.AsNumber(row["maxHitPoints"]), nil
	}

	// buildOperation mirrors bridge.recoveryServiceOperation
	// (go/internal/bridge/disaster_recovery.go): target carries identity
	// only (its recovery-specific CAS token has no existing observation read
	// that could produce it ahead of time), and the token travels on the
	// decoupled expectedTargetSnapshotToken field instead, omitted entirely
	// when wToken is empty -- an unconstrained call, exactly like
	// discoverTargetToken below issues to establish the baseline.
	buildOperation := func(pID, pToken, wID, wToken string) map[string]any {
		recover := map[string]any{
			"target": map[string]any{"entityId": wID},
			"pawn":   map[string]any{"entityId": pID, "expectedSnapshotToken": pToken},
			"method": "SERVICE_METHOD_REPAIR",
		}
		if wToken != "" {
			recover["expectedTargetSnapshotToken"] = wToken
		}
		return map[string]any{"recoverService": recover}
	}

	// discoverTargetToken mirrors bridge.ReadRecoveryServiceTarget
	// (go/internal/bridge/disaster_recovery.go): the real production fix for
	// the bug this tool previously worked around (a local replica of
	// NativeRecoveryOperations.Token()'s algorithm). No existing observation
	// read exposes the wall's recovery-specific CAS token, so this issues
	// the exact same unconstrained rimgovernor/operations_preview round-trip
	// bridge.ReadRecoveryServiceTarget does (no expectedTargetSnapshotToken
	// supplied) and reads the native-computed value back from
	// evaluated.projected.job.targetSnapshotToken.
	discoverTargetToken := func(label, pID, pToken, wID string) (string, error) {
		reply, err := h.Wire(ctx, label, "operations_preview", map[string]any{
			"identity": identity, "operation": buildOperation(pID, pToken, wID, ""),
		})
		if err != nil {
			return "", err
		}
		_, evaluated, err := na.Outcome(reply, "evaluated")
		if err != nil {
			return "", err
		}
		if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
			return "", fmt.Errorf("%s: expected the discovery preview to be accepted, got %#v", label, evaluated)
		}
		projected, _ := na.AsMap(evaluated["projected"])
		job, _ := na.AsMap(projected["job"])
		if na.AsString(job["pawnId"]) != pID {
			return "", fmt.Errorf("%s: unexpected discovery preview pawn: %#v", label, job)
		}
		if targetA, _ := na.AsMap(job["targetA"]); na.AsString(targetA["thingId"]) != wID {
			return "", fmt.Errorf("%s: unexpected discovery preview target: %#v", label, job)
		}
		wToken := na.AsString(job["targetSnapshotToken"])
		if wToken == "" {
			return "", fmt.Errorf("%s: missing discovered target snapshot token: %#v", label, job)
		}
		return wToken, nil
	}
	buildRequest := func(actionID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": "1"},
			},
			"operation": operation,
		}
	}
	failureCode := func(label string, request map[string]any) (string, error) {
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

	beforeHitPoints, beforeMax, err := wallState("wall-before")
	if err != nil {
		return err
	}
	if beforeHitPoints >= beforeMax {
		return fmt.Errorf("wall-before: expected the fixture wall to be genuinely damaged, got hitPoints=%v maxHitPoints=%v", beforeHitPoints, beforeMax)
	}
	report["wall_before_hit_points"] = beforeHitPoints
	report["wall_max_hit_points"] = beforeMax
	token, err := pawnToken("pawn-before")
	if err != nil {
		return err
	}
	// Real production discovery: the same unconstrained preview round-trip
	// bridge.ReadRecoveryServiceTarget issues, proving the actual fix rather
	// than a client-side replica of the native hash.
	wallToken, err := discoverTargetToken("discover-wall-token", pawnID, token, wallID)
	if err != nil {
		return err
	}
	report["wall_token_discovered"] = true

	// Refusal 1: a stale performer CAS token must be refused.
	staleGeneration, err := currentGeneration("generation-stale-pawn")
	if err != nil {
		return err
	}
	stalePawnRequest := buildRequest("recovery-stale-pawn", staleGeneration,
		buildOperation(pawnID, "stale-pawn-token-00000000000000000000000000000000", wallID, wallToken))
	if code, err := failureCode("stale-pawn-token", stalePawnRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" && code != "FAILURE_CODE_NOT_FOUND" && code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-pawn-token: expected a stale-identity/owner-conflict/not-found refusal, got %q", code)
	}

	// Refusal 2: a stale target CAS token must be refused (the target's own
	// self-computed CAS, distinct from NativePawnControlState's pawn token).
	staleTargetRequest := buildRequest("recovery-stale-target", staleGeneration,
		buildOperation(pawnID, token, wallID, "stale-target-token-0000000000000000000000000000000"))
	if code, err := failureCode("stale-target-token", staleTargetRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("stale-target-token: expected an invalid-request/not-found refusal, got %q", code)
	}

	// Preview: accepted, but never mutates the live building/job state.
	repairOperation := buildOperation(pawnID, token, wallID, wallToken)
	previewReply, err := h.Wire(ctx, "preview-repair", "operations_preview", map[string]any{"identity": identity, "operation": repairOperation})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-repair: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-repair: expected repair to be accepted, got %#v", previewEvaluated)
	}
	previewProjected, _ := na.AsMap(previewEvaluated["projected"])
	previewJob, _ := na.AsMap(previewProjected["job"])
	if na.AsString(previewJob["jobDef"]) != "Repair" {
		return fmt.Errorf("preview-repair: expected a projected Repair job, got %#v", previewJob)
	}
	if issued, _ := na.AsBool(previewJob["issued"]); issued {
		return fmt.Errorf("preview-repair: expected a dry-run preview to not issue a job: %#v", previewJob)
	}
	afterPreviewHitPoints, _, err := wallState("wall-after-preview")
	if err != nil {
		return err
	}
	if afterPreviewHitPoints != beforeHitPoints {
		return fmt.Errorf("wall-after-preview: expected an unchanged HitPoints from a dry-run preview, got %v (was %v)", afterPreviewHitPoints, beforeHitPoints)
	}

	// Execute: the real native RecoverService/Repair dispatch. Re-read the
	// current nativeGeneration immediately before dispatch: it advances over
	// wall-clock/tick time independent of this tool's own writes.
	executeGeneration, err := currentGeneration("generation-before-execute")
	if err != nil {
		return err
	}
	executeRequest := buildRequest("recovery-execute", executeGeneration, repairOperation)
	executeReply, err := h.Wire(ctx, "execute-repair", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, executeReceipt, err := na.Outcome(executeReply, "receipt")
	if err != nil {
		return err
	}
	executeApplied, ok := na.AsMap(executeReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-repair: expected an applied outcome, got %#v", executeReceipt)
	}
	executeObserved, _ := na.AsMap(executeApplied["observed"])
	executeJob, _ := na.AsMap(executeObserved["job"])
	if na.AsString(executeJob["pawnId"]) != pawnID || na.AsString(executeJob["jobDef"]) != "Repair" {
		return fmt.Errorf("execute-repair: unexpected applied repair evidence: %#v", executeJob)
	}
	if issued, _ := na.AsBool(executeJob["issued"]); !issued {
		return fmt.Errorf("execute-repair: expected the native repair job to be issued, got %#v", executeJob)
	}
	if targetA, _ := na.AsMap(executeJob["targetA"]); na.AsString(targetA["thingId"]) != wallID {
		return fmt.Errorf("execute-repair: unexpected repair target: %#v", executeJob)
	}

	executePrecondition, _ := na.AsMap(executeRequest["precondition"])
	executeAttempt := map[string]any{"identity": identity, "attempt": executePrecondition["attempt"]}

	// Observe: run real game time forward until the fixture's damaged wall
	// is actually restored to full HitPoints by the real native repair job,
	// not merely inferred from the issued-job receipt.
	if _, err := h.Call(ctx, "resume-repair", "rimworld/set_time_speed", map[string]any{"speed": "Fast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := pollCompleted(ctx, h, executeAttempt, 15*time.Minute); err != nil {
		return fmt.Errorf("observe-repair: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after-repair", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	afterHitPoints, afterMax, err := wallState("wall-after-complete")
	if err != nil {
		return err
	}
	if afterHitPoints < afterMax {
		return fmt.Errorf("wall-after-complete: expected the native wall to be fully repaired, got hitPoints=%v maxHitPoints=%v", afterHitPoints, afterMax)
	}
	report["wall_after_hit_points"] = afterHitPoints
	report["wall_repaired"] = true

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-repair", "operations_execute", executeRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, executeReceipt) {
		return fmt.Errorf("replay-repair: replay of the same attempt returned a different receipt")
	}
	lookupReply, err := h.Wire(ctx, "lookup-repair", "receipts_lookup", executeAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, executeReceipt) {
		return fmt.Errorf("lookup-repair: expected the same receipt as execute, got %#v", lookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// pollCompleted polls receipts_observe_progress until the attempt is
// observed Completed, matching populationcustodyaccept's own poll loop. It
// fails fast if the attempt is instead observed Unsuccessful.
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
