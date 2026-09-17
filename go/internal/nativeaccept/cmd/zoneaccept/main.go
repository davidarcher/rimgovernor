// Command zoneaccept exercises the full set of typed native zone dispatch
// verticals (N01.04, issue #34) in one run: CreateZone, the native
// rimgovernor/observations_list_zones tool (NativeZoneObservationTools.cs),
// EditZoneCells (NativeZoneCellEdit.cs, both directions), and DeleteZone
// (NativeZoneDeletion.cs).
//
// A real stockpile zone is created over a disposable fixture's roofed,
// walled, empty 2x2 interior through the typed operations contract, its
// per-zone CAS snapshot token is read back through the ListZones tool (not
// just inferred from the create receipt). One corner cell is then removed
// through EditZoneCells (leaving a contiguous 3-cell L-shape, and returning
// that cell to genuinely free ground), that same cell is added back through
// EditZoneCells (restoring the original 2x2), and the exact zone is then
// deleted through the typed operations contract -- with stale-token
// refusal, preview non-mutation, real effect evidence, real ListZones
// readbacks and replay idempotency checked at each step, mirroring
// bedassignaccept's own real-evidence shape.
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
		"through the rimgovernor/observations_list_zones tool, one corner cell is removed and then re-added "+
		"through the typed EditZoneCells operation, and the exact zone is deleted via the typed DeleteZone "+
		"operation, with stale-token refusal, preview non-mutation, real effect evidence, real ListZones "+
		"readbacks and replay idempotency.", !*rendered)
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

	tokenAfterCreate, present, err := zoneState("zone-after-create", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-create: expected the created zone to be listed")
	}
	report["zone_token_after_create"] = tokenAfterCreate

	// --- EditZoneCells: real dispatch, first acceptance coverage for this operation. ---
	//
	// cells[0] is one corner of the fixture's 2x2 interior. Removing it
	// leaves a contiguous 3-cell L-shape and returns that cell to genuinely
	// free ground (EditZoneCells's ADD never steals a cell from another
	// zone -- it only ever accepts free ground, matching CreateZone's own
	// eligibility test -- so re-adding the very cell this harness just
	// freed is the one add case the fixture can exercise without a second
	// site).

	editCell := cells[0]

	staleEditGeneration, err := currentGeneration("generation-stale-edit-remove")
	if err != nil {
		return err
	}
	staleEditRemoveRequest := buildRequest("zone-edit-remove-stale-token", staleEditGeneration, map[string]any{"editZoneCells": map[string]any{
		"zone": map[string]any{"entityId": zoneID, "expectedSnapshotToken": "zone-stale-00000000000000000000000000000000000000000000000000000000000000"},
		"edit": "CELL_EDIT_REMOVE", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{editCell}}},
	}})
	if code, err := failureCode("edit-remove-stale-token", staleEditRemoveRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("edit-remove-stale-token: expected an invalid-request/not-found refusal, got %q", code)
	}

	removeOperation := map[string]any{"editZoneCells": map[string]any{
		"zone": map[string]any{"entityId": zoneID, "expectedSnapshotToken": tokenAfterCreate},
		"edit": "CELL_EDIT_REMOVE", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{editCell}}},
	}}
	removePreviewReply, err := h.Wire(ctx, "preview-edit-remove", "operations_preview", map[string]any{"identity": identity, "operation": removeOperation})
	if err != nil {
		return err
	}
	removePreviewEvaluated, ok := na.AsMap(removePreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-edit-remove: expected an evaluated reply, got %#v", removePreviewReply)
	}
	if accepted, _ := na.AsBool(removePreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-edit-remove: expected the cell removal to be accepted, got %#v", removePreviewEvaluated)
	}
	if token, previewPresent, err := zoneState("zone-after-edit-remove-preview", zoneID); err != nil {
		return err
	} else if token != tokenAfterCreate || !previewPresent {
		return fmt.Errorf("preview-edit-remove: dry-run preview unexpectedly changed the zone")
	}

	editRemoveGeneration, err := currentGeneration("generation-before-edit-remove")
	if err != nil {
		return err
	}
	editRemoveRequest := buildRequest("zone-edit-remove", editRemoveGeneration, removeOperation)
	editRemoveReply, err := h.Wire(ctx, "execute-edit-remove", "operations_execute", editRemoveRequest)
	if err != nil {
		return err
	}
	_, editRemoveReceipt, err := na.Outcome(editRemoveReply, "receipt")
	if err != nil {
		return err
	}
	editRemoveApplied, ok := na.AsMap(editRemoveReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-edit-remove: expected an applied outcome, got %#v", editRemoveReceipt)
	}
	editRemoveObserved, _ := na.AsMap(editRemoveApplied["observed"])
	editRemoveZoneEffect, ok := na.AsMap(editRemoveObserved["zone"])
	if !ok {
		return fmt.Errorf("execute-edit-remove: expected zone effect evidence, got %#v", editRemoveObserved)
	}
	if present, _ := na.AsBool(editRemoveZoneEffect["present"]); !present {
		return fmt.Errorf("execute-edit-remove: expected present=true (3 cells remain), got %#v", editRemoveZoneEffect)
	}
	if listed := na.AsNumber(editRemoveZoneEffect["listedCellCount"]); int(listed) != len(cells)-1 {
		return fmt.Errorf("execute-edit-remove: expected %d listed cells after removing one, got %#v", len(cells)-1, editRemoveZoneEffect)
	}

	tokenAfterRemove, present, err := zoneState("zone-after-edit-remove", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-edit-remove: expected the zone to still be listed with 3 cells")
	}
	if tokenAfterRemove == tokenAfterCreate {
		return fmt.Errorf("zone-after-edit-remove: expected the per-zone CAS token to change after a real cell removal")
	}

	// Replay: the exact same remove attempt returns an identical receipt.
	replayEditRemoveReply, err := h.Wire(ctx, "replay-edit-remove", "operations_execute", editRemoveRequest)
	if err != nil {
		return err
	}
	_, replayEditRemove, err := na.Outcome(replayEditRemoveReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayEditRemove, editRemoveReceipt) {
		return fmt.Errorf("replay-edit-remove: replay of the same attempt returned a different receipt")
	}
	report["zone_cells_after_edit_remove"] = len(cells) - 1

	addOperation := map[string]any{"editZoneCells": map[string]any{
		"zone": map[string]any{"entityId": zoneID, "expectedSnapshotToken": tokenAfterRemove},
		"edit": "CELL_EDIT_ADD", "cells": map[string]any{"explicitCells": map[string]any{"cells": []map[string]any{editCell}}},
	}}
	addPreviewReply, err := h.Wire(ctx, "preview-edit-add", "operations_preview", map[string]any{"identity": identity, "operation": addOperation})
	if err != nil {
		return err
	}
	addPreviewEvaluated, ok := na.AsMap(addPreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-edit-add: expected an evaluated reply, got %#v", addPreviewReply)
	}
	if accepted, _ := na.AsBool(addPreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-edit-add: expected the cell addition to be accepted, got %#v", addPreviewEvaluated)
	}

	editAddGeneration, err := currentGeneration("generation-before-edit-add")
	if err != nil {
		return err
	}
	editAddRequest := buildRequest("zone-edit-add", editAddGeneration, addOperation)
	editAddReply, err := h.Wire(ctx, "execute-edit-add", "operations_execute", editAddRequest)
	if err != nil {
		return err
	}
	_, editAddReceipt, err := na.Outcome(editAddReply, "receipt")
	if err != nil {
		return err
	}
	editAddApplied, ok := na.AsMap(editAddReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-edit-add: expected an applied outcome, got %#v", editAddReceipt)
	}
	editAddObserved, _ := na.AsMap(editAddApplied["observed"])
	editAddZoneEffect, ok := na.AsMap(editAddObserved["zone"])
	if !ok {
		return fmt.Errorf("execute-edit-add: expected zone effect evidence, got %#v", editAddObserved)
	}
	if listed := na.AsNumber(editAddZoneEffect["listedCellCount"]); int(listed) != len(cells) {
		return fmt.Errorf("execute-edit-add: expected %d listed cells after re-adding the corner, got %#v", len(cells), editAddZoneEffect)
	}

	deleteToken, present, err := zoneState("zone-after-edit-add", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-edit-add: expected the zone to be listed with all %d cells restored", len(cells))
	}
	report["zone_cells_after_edit_add"] = len(cells)

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
