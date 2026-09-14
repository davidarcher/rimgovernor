// Command zoneaccept exercises three typed native zone dispatch verticals
// (N01.04, issue #34) in one run: CreateZone (wired but previously with no
// acceptance harness at all), the new native rimgovernor/observations_list_zones
// tool (NativeZoneObservationTools.cs), and DeleteZone
// (NativeZoneDeletion.cs) -- the small, self-contained half of the two
// remaining zone-editing operations. EditZoneCells (porting
// ZoneCellsTool.cs's add/remove cell simulation) is not covered here.
//
// A real stockpile zone is created over a disposable fixture's roofed,
// walled, empty 2x2 interior through the typed operations contract, its
// per-zone CAS snapshot token is read back through the new ListZones tool
// (not just inferred from the create receipt), and that exact zone is then
// deleted through the typed operations contract -- with stale-token
// refusal, preview non-mutation, real effect evidence, a real post-delete
// ListZones readback showing the zone gone, and replay idempotency checked
// at each step, mirroring bedassignaccept's own real-evidence shape.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-zone-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-zone-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-zone-acceptance"
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
	report := na.NewReport("Native typed zone dispatch: a real stockpile zone is created via the typed "+
		"CreateZone operation over a disposable fixture site, its per-zone CAS snapshot token is read back "+
		"through the new rimgovernor/observations_list_zones tool, and that exact zone is deleted via the typed "+
		"DeleteZone operation, with stale-token refusal, preview non-mutation, real effect evidence, a real "+
		"post-delete ListZones readback and replay idempotency.", !*rendered)
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
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_colony_facts"} {
		if !na.Contains(names, required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}

	// Fixture: one small roofed, walled, empty, unzoned 2x2 interior. Run
	// this BEFORE acquiring authority, mirroring bedassignaccept/
	// recoveryareaaccept's own ordering (NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of owner).
	prepared, err := h.Call(ctx, "prepare", "test/zone_delete_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("prepare: zone_delete_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) {
		return fmt.Errorf("prepare: fixture identity does not match the fresh debug game")
	}
	cellRows := na.AsSlice(prepared["cells"])
	if len(cellRows) == 0 {
		return fmt.Errorf("prepare: missing fixture cells: %#v", prepared)
	}
	var cells []map[string]any
	for _, row := range cellRows {
		cell, _ := na.AsMap(row)
		cells = append(cells, map[string]any{"x": cell["x"], "z": cell["z"]})
	}
	report["fixture_cell_count"] = len(cells)

	var grant map[string]any
	acquire := func(label string) error {
		statusReply, err := h.Wire(ctx, label+"-status", "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return err
		}
		_, status, err := na.Outcome(statusReply, "status")
		if err != nil {
			return err
		}
		statusContext, _ := na.AsMap(status["context"])
		grantReply, err := h.Wire(ctx, label, "authority_control", map[string]any{"acquire": map[string]any{
			"identity": identity, "expectedGeneration": statusContext["nativeGeneration"],
			"owner": map[string]any{"controllerSessionId": sessionOwner, "playerDirection": "1"}, "leaseMs": 30000,
		}})
		if err != nil {
			return err
		}
		_, newGrant, err := na.Outcome(grantReply, "granted")
		if err != nil {
			return err
		}
		grant = newGrant
		return nil
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

	// mapSnapshotToken reads the whole-map zone census token through
	// rimgovernor/observations_read_colony_facts (planning=true), the same
	// boundary read bridge.ReadZoneTarget issues for CreateZone.
	mapSnapshotToken := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "planning": true, "page": map[string]any{"limit": 256},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		planning, _ := na.AsMap(observed["planning"])
		planningObserved, _ := na.AsMap(planning["observed"])
		snapshot, _ := na.AsMap(planningObserved["zoneMapSnapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("%s: missing zone map snapshot token", label)
		}
		return token, nil
	}

	// zoneState reads one zone's exact per-zone CAS snapshot token through
	// the new rimgovernor/observations_list_zones tool -- the same boundary
	// read bridge.ReadZoneEditTarget issues for EditZoneCells/DeleteZone.
	// present=false with no error means the zone genuinely is not listed.
	zoneState := func(label, zoneID string) (token string, present bool, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_zones", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{zoneID}, "page": map[string]any{"limit": 1},
		})
		if err != nil {
			return "", false, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", false, err
		}
		rows := na.AsSlice(observed["zones"])
		if len(rows) == 0 {
			return "", false, nil
		}
		if len(rows) != 1 {
			return "", false, fmt.Errorf("%s: expected at most one observed zone, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		if na.AsString(row["id"]) != zoneID {
			return "", false, fmt.Errorf("%s: unexpected zone row: %#v", label, row)
		}
		snapshot, _ := na.AsMap(row["snapshot"])
		token = na.AsString(snapshot["token"])
		if token == "" {
			return "", false, fmt.Errorf("%s: missing zone snapshot token: %#v", label, row)
		}
		return token, true, nil
	}

	buildRequest := func(actionID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation, "leaseId": grant["leaseId"],
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

	// --- CreateZone: real dispatch, first acceptance coverage for this operation. ---

	mapToken, err := mapSnapshotToken("map-before-create")
	if err != nil {
		return err
	}
	createOperation := map[string]any{"createZone": map[string]any{
		"expectedMapSnapshotToken": mapToken,
		"label":                    "RimGovernor food storage",
		"type":                     "ZONE_TYPE_STOCKPILE",
		"cells":                    map[string]any{"explicitCells": map[string]any{"cells": cells}},
		"stockpile":                map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT", "preset": "FILTER_PRESET_FOOD"},
	}}

	staleCreateGeneration, err := currentGeneration("generation-stale-create")
	if err != nil {
		return err
	}
	staleCreateRequest := buildRequest("zone-create-stale-token", staleCreateGeneration, map[string]any{"createZone": map[string]any{
		"expectedMapSnapshotToken": "zone-stale-00000000000000000000000000000000000000000000000000000000000000",
		"label":                    "RimGovernor food storage", "type": "ZONE_TYPE_STOCKPILE",
		"cells":     map[string]any{"explicitCells": map[string]any{"cells": cells}},
		"stockpile": map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT", "preset": "FILTER_PRESET_FOOD"},
	}})
	if code, err := failureCode("create-stale-token", staleCreateRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("create-stale-token: expected an invalid-request/not-found refusal, got %q", code)
	}

	createPreviewReply, err := h.Wire(ctx, "preview-create", "operations_preview", map[string]any{"identity": identity, "operation": createOperation})
	if err != nil {
		return err
	}
	createPreviewEvaluated, ok := na.AsMap(createPreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-create: expected an evaluated reply, got %#v", createPreviewReply)
	}
	if accepted, _ := na.AsBool(createPreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-create: expected the zone creation to be accepted, got %#v", createPreviewEvaluated)
	}

	createGeneration, err := currentGeneration("generation-before-create")
	if err != nil {
		return err
	}
	createRequest := buildRequest("zone-create", createGeneration, createOperation)
	createReply, err := h.Wire(ctx, "execute-create", "operations_execute", createRequest)
	if err != nil {
		return err
	}
	_, createReceipt, err := na.Outcome(createReply, "receipt")
	if err != nil {
		return err
	}
	createApplied, ok := na.AsMap(createReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-create: expected an applied outcome, got %#v", createReceipt)
	}
	createObserved, _ := na.AsMap(createApplied["observed"])
	createZoneEffect, ok := na.AsMap(createObserved["zone"])
	if !ok {
		return fmt.Errorf("execute-create: expected zone effect evidence, got %#v", createObserved)
	}
	zoneID := na.AsString(createZoneEffect["zoneId"])
	if zoneID == "" {
		return fmt.Errorf("execute-create: missing zoneId in effect evidence: %#v", createZoneEffect)
	}
	if present, _ := na.AsBool(createZoneEffect["present"]); !present {
		return fmt.Errorf("execute-create: expected present=true in the effect evidence, got %#v", createZoneEffect)
	}
	report["created_zone_id"] = zoneID

	// --- ListZones: real per-zone CAS snapshot token readback, first coverage for this observation tool. ---

	deleteToken, present, err := zoneState("zone-after-create", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-create: expected the created zone to be listed")
	}
	report["zone_token_after_create"] = deleteToken

	// --- DeleteZone: real dispatch. ---

	staleDeleteGeneration, err := currentGeneration("generation-stale-delete")
	if err != nil {
		return err
	}
	staleDeleteRequest := buildRequest("zone-delete-stale-token", staleDeleteGeneration, map[string]any{"deleteZone": map[string]any{
		"zone": map[string]any{"entityId": zoneID, "expectedSnapshotToken": "zone-stale-00000000000000000000000000000000000000000000000000000000000000"},
	}})
	if code, err := failureCode("delete-stale-token", staleDeleteRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("delete-stale-token: expected an invalid-request/not-found refusal, got %q", code)
	}

	deleteOperation := map[string]any{"deleteZone": map[string]any{"zone": map[string]any{"entityId": zoneID, "expectedSnapshotToken": deleteToken}}}
	deletePreviewReply, err := h.Wire(ctx, "preview-delete", "operations_preview", map[string]any{"identity": identity, "operation": deleteOperation})
	if err != nil {
		return err
	}
	deletePreviewEvaluated, ok := na.AsMap(deletePreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-delete: expected an evaluated reply, got %#v", deletePreviewReply)
	}
	if accepted, _ := na.AsBool(deletePreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-delete: expected the zone deletion to be accepted, got %#v", deletePreviewEvaluated)
	}
	if _, previewPresent, err := zoneState("zone-after-delete-preview", zoneID); err != nil {
		return err
	} else if !previewPresent {
		return fmt.Errorf("preview-delete: dry-run preview unexpectedly deleted the zone")
	}

	deleteGeneration, err := currentGeneration("generation-before-delete")
	if err != nil {
		return err
	}
	deleteRequest := buildRequest("zone-delete", deleteGeneration, deleteOperation)
	deleteReply, err := h.Wire(ctx, "execute-delete", "operations_execute", deleteRequest)
	if err != nil {
		return err
	}
	_, deleteReceipt, err := na.Outcome(deleteReply, "receipt")
	if err != nil {
		return err
	}
	deleteApplied, ok := na.AsMap(deleteReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-delete: expected an applied outcome, got %#v", deleteReceipt)
	}
	deleteObserved, _ := na.AsMap(deleteApplied["observed"])
	deleteZoneEffect, ok := na.AsMap(deleteObserved["zone"])
	if !ok {
		return fmt.Errorf("execute-delete: expected zone effect evidence, got %#v", deleteObserved)
	}
	if na.AsString(deleteZoneEffect["zoneId"]) != zoneID {
		return fmt.Errorf("execute-delete: unexpected zone effect identity: %#v", deleteZoneEffect)
	}
	if present, _ := na.AsBool(deleteZoneEffect["present"]); present {
		return fmt.Errorf("execute-delete: expected present=false in the effect evidence, got %#v", deleteZoneEffect)
	}

	if _, afterDeletePresent, err := zoneState("zone-after-delete", zoneID); err != nil {
		return err
	} else if afterDeletePresent {
		return fmt.Errorf("zone-after-delete: expected the deleted zone to no longer be listed")
	}
	report["zone_deleted"] = true

	// Replay: the exact same delete attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-delete", "operations_execute", deleteRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, deleteReceipt) {
		return fmt.Errorf("replay-delete: replay of the same attempt returned a different receipt")
	}
	deletePrecondition, _ := na.AsMap(deleteRequest["precondition"])
	deleteAttempt := map[string]any{"identity": identity, "attempt": deletePrecondition["attempt"]}
	lookupReply, err := h.Wire(ctx, "lookup-delete", "receipts_lookup", deleteAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, deleteReceipt) {
		return fmt.Errorf("lookup-delete: expected the same receipt as execute, got %#v", lookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
