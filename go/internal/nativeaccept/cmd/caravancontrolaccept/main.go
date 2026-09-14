// Command caravancontrolaccept exercises the TravelCaravan vertical (G01.08)
// end to end against a live game: an already-formed real player caravan,
// holding it in place through its native path follower (Stop), routing it to
// an already-scouted neighboring tile (Move), sending it home
// (ReturnHome), exact CAS/stale-identity refusal, replay idempotency and
// durable lookup -- through the same rimgovernor/operations_execute wire
// contract Go's bridge.TravelCaravanWriter drives. Uses a private disposable
// fixture (test/caravan_control_prepare) since a deterministic reachable
// destination and caravan crew cannot be produced from native random world
// generation alone.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-caravan-control-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-caravan-control-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-caravan-control-acceptance"
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
	report := na.NewReport("Native TravelCaravan vertical: hold (Stop), route (Move) and return-home dispositions "+
		"of an already-formed real player caravan through its actual native path follower, exact CAS/"+
		"stale-identity refusal, replay idempotency and durable lookup.", !*rendered)
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

	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
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

	prepared, err := h.Call(ctx, "prepare", "test/caravan_control_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("caravan_control_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) ||
		na.AsNumber(prepared["mapId"]) != na.AsNumber(identity["mapId"]) {
		return fmt.Errorf("caravan_control_prepare identity does not match the fresh debug game")
	}
	report["prepared"] = prepared
	caravanID := na.AsString(prepared["caravanId"])
	homeTile := na.AsNumber(prepared["homeTile"])
	destinationTile := int32(na.AsNumber(prepared["destinationTile"]))
	if caravanID == "" {
		return fmt.Errorf("caravan_control_prepare: unexpected fixture identifiers: %#v", prepared)
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
	// caravan, mirroring bridge.TravelCaravanToken's own extraction from the
	// same census (NativeWorldProgressionObservation.cs's Caravans()), so the
	// token used below is exactly what the Go boundary itself would compute
	// from this call.
	target := func(label string) (caravanRow map[string]any, err error) {
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

	caravanRow, err := target("target-before")
	if err != nil {
		return err
	}
	if na.AsNumber(caravanRow["tile"]) != homeTile {
		return fmt.Errorf("target-before: expected the caravan still at the home tile, got %#v", caravanRow)
	}
	if moving, _ := na.AsBool(caravanRow["moving"]); moving {
		return fmt.Errorf("target-before: expected a stationary caravan, got %#v", caravanRow)
	}
	stationaryToken := caravanToken(caravanID, int64(homeTile), false)

	buildOperation := func(kind string, cToken string, tile *int32) map[string]any {
		travel := map[string]any{"caravan": map[string]any{"entityId": caravanID, "expectedSnapshotToken": cToken}, "kind": kind}
		if tile != nil {
			travel["destinationTile"] = *tile
		}
		return map[string]any{"travelCaravan": travel}
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

	// Refusal 1: a stale token (recomputed with the wrong moving flag) must
	// be refused as stale identity rather than silently accepted.
	staleToken := caravanToken(caravanID, int64(homeTile), true)
	staleRequest := buildRequest("caravan-hold-stale", "1", buildOperation("TRAVEL_KIND_STOP", staleToken, nil))
	if code, err := failureCode(ctx, h, "stale-token-hold", staleRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("stale-token-hold: expected FAILURE_CODE_STALE_IDENTITY, got %q", code)
	}

	// Preview: Move is accepted but never mutates the live path follower.
	previewReply, err := h.Wire(ctx, "preview", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation("TRAVEL_KIND_MOVE", stationaryToken, &destinationTile),
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview: expected the route to be accepted, got %#v", evaluated)
	}
	afterPreviewRow, err := target("target-after-preview")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(afterPreviewRow["moving"]); moving {
		return fmt.Errorf("target-after-preview: expected the dry-run preview to leave the caravan stationary: %#v", afterPreviewRow)
	}

	// Execute: Move actually starts the native path follower.
	moveRequest := buildRequest("caravan-route", "1", buildOperation("TRAVEL_KIND_MOVE", stationaryToken, &destinationTile))
	moveReceiptReply, err := h.Wire(ctx, "execute-move", "operations_execute", moveRequest)
	if err != nil {
		return err
	}
	_, moveReceipt, err := na.Outcome(moveReceiptReply, "receipt")
	if err != nil {
		return err
	}
	moveApplied, ok := na.AsMap(moveReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-move: expected an applied outcome, got %#v", moveReceipt)
	}
	moveObserved, _ := na.AsMap(moveApplied["observed"])
	moveCaravan, _ := na.AsMap(moveObserved["caravan"])
	if pathStarted, _ := na.AsBool(moveCaravan["pathStarted"]); !pathStarted {
		return fmt.Errorf("execute-move: expected pathStarted=true, got %#v", moveCaravan)
	}
	if na.AsNumber(moveCaravan["destinationTile"]) != float64(destinationTile) {
		return fmt.Errorf("execute-move: unexpected destination tile: %#v", moveCaravan)
	}

	movingRow, err := target("target-after-move")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(movingRow["moving"]); !moving {
		return fmt.Errorf("target-after-move: expected the caravan to actually be moving, got %#v", movingRow)
	}
	movingTile := int64(na.AsNumber(movingRow["tile"]))
	movingToken := caravanToken(caravanID, movingTile, true)

	// Replay: the exact same attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-move", "operations_execute", moveRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, moveReceipt) {
		return fmt.Errorf("replay-move: replay of the same attempt returned a different receipt")
	}

	// Durable lookup: receipts_lookup independently returns the same receipt.
	movePrecondition, _ := na.AsMap(moveRequest["precondition"])
	moveAttempt := map[string]any{"identity": identity, "attempt": movePrecondition["attempt"]}
	lookupReply, err := h.Wire(ctx, "lookup-move", "receipts_lookup", moveAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, moveReceipt) {
		return fmt.Errorf("lookup-move: expected the same receipt as execute, got %#v", lookup)
	}

	// Hold: stop the now-moving caravan through the native path follower.
	holdRequest := buildRequest("caravan-hold", "1", buildOperation("TRAVEL_KIND_STOP", movingToken, nil))
	holdReceiptReply, err := h.Wire(ctx, "execute-hold", "operations_execute", holdRequest)
	if err != nil {
		return err
	}
	_, holdReceipt, err := na.Outcome(holdReceiptReply, "receipt")
	if err != nil {
		return err
	}
	holdApplied, ok := na.AsMap(holdReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-hold: expected an applied outcome, got %#v", holdReceipt)
	}
	holdObserved, _ := na.AsMap(holdApplied["observed"])
	holdCaravan, _ := na.AsMap(holdObserved["caravan"])
	if stopped, _ := na.AsBool(holdCaravan["stopped"]); !stopped {
		return fmt.Errorf("execute-hold: expected stopped=true, got %#v", holdCaravan)
	}

	stoppedRow, err := target("target-after-hold")
	if err != nil {
		return err
	}
	if moving, _ := na.AsBool(stoppedRow["moving"]); moving {
		return fmt.Errorf("target-after-hold: expected the caravan to be stationary, got %#v", stoppedRow)
	}
	stoppedTile := int64(na.AsNumber(stoppedRow["tile"]))
	stoppedToken := caravanToken(caravanID, stoppedTile, false)

	// Return home: route the held caravan back to the home tile.
	returnRequest := buildRequest("caravan-return-home", "1", buildOperation("TRAVEL_KIND_RETURN_HOME", stoppedToken, nil))
	returnReceiptReply, err := h.Wire(ctx, "execute-return-home", "operations_execute", returnRequest)
	if err != nil {
		return err
	}
	_, returnReceipt, err := na.Outcome(returnReceiptReply, "receipt")
	if err != nil {
		return err
	}
	returnApplied, ok := na.AsMap(returnReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-return-home: expected an applied outcome, got %#v", returnReceipt)
	}
	returnObserved, _ := na.AsMap(returnApplied["observed"])
	returnCaravan, _ := na.AsMap(returnObserved["caravan"])
	if pathStarted, _ := na.AsBool(returnCaravan["pathStarted"]); !pathStarted {
		return fmt.Errorf("execute-return-home: expected pathStarted=true, got %#v", returnCaravan)
	}
	if na.AsNumber(returnCaravan["destinationTile"]) != homeTile {
		return fmt.Errorf("execute-return-home: expected destinationTile=%v, got %#v", homeTile, returnCaravan)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// failureCode wires request through operations_execute and returns the
// failure code, mirroring questfulfillaccept's/movementaccept's helper of the
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

// caravanToken reproduces NativeCaravanTravel.CaravanToken/Go's unexported
// travelCaravanToken exactly (same joined-string SHA256 hex, lowercase
// booleans), from a world-progression caravan row's id/tile/moving alone, so
// this acceptance binary can self-compute the same CAS token the native
// contract and the Go boundary both use without needing to export that
// helper from internal/bridge.
func caravanToken(id string, tile int64, moving bool) string {
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s", id, tile, movingText)))
	return "caravan-travel-" + hex.EncodeToString(sum[:])
}
