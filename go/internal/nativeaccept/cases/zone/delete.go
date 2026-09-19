// The zone/delete case exercises the full set of typed native zone dispatch
// verticals (N01.04, issue #34) in one run: CreateZone, the native
// rimgovernor/observations_list_zones tool (NativeZoneObservationTools.cs),
// PatchStockpile (NativeStockpilePatch.cs), EditZoneCells
// (NativeZoneCellEdit.cs, both directions), and DeleteZone
// (NativeZoneDeletion.cs).
//
// A real stockpile zone is created over a disposable fixture's roofed,
// walled, empty 2x2 interior through the typed operations contract with the
// allow-list body the Go covered-storage planners send (Nothing preset plus
// an explicit thing_def allow list) and a hit-point/quality range; its
// filter and per-zone CAS snapshot token are read back through the
// ListZones tool (not just inferred from the create receipt). The priority
// and ranges are then changed through PatchStockpile, with an unresolvable
// selector refused. One corner cell is then removed
// through EditZoneCells (leaving a contiguous 3-cell L-shape, and returning
// that cell to genuinely free ground), that same cell is added back through
// EditZoneCells (restoring the original 2x2), and the exact zone is then
// deleted through the typed operations contract -- with stale-token
// refusal, preview non-mutation, real effect evidence, real ListZones
// readbacks and replay idempotency checked at each step, mirroring
// bedassignaccept's own real-evidence shape.
package zone

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-zone-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "zone/delete",
		Scope: "Native typed zone dispatch: a real allow-list stockpile zone with hit-point/quality ranges " +
			"is created via the typed CreateZone operation over a disposable fixture site, its filter and per-zone CAS " +
			"snapshot token are read back through the rimgovernor/observations_list_zones tool, its priority and ranges " +
			"are changed via the typed PatchStockpile operation, one corner cell is removed and then re-added " +
			"through the typed EditZoneCells operation, and the exact zone is deleted via the typed DeleteZone " +
			"operation, with stale-token refusal, preview non-mutation, real effect evidence, real ListZones " +
			"readbacks and replay idempotency. The re-add runs under a playing clock window and the journal's " +
			"observation_invalidated names the zone id and its cell rectangle (#359).",
		Start:  cases.Fixture{Op: "test/zone_delete_prepare"},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Fixture: one small roofed, walled, empty, unzoned 2x2 interior. Run
	// this BEFORE acquiring authority, mirroring bedassignaccept/
	// recoveryareaaccept's own ordering (NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of owner).
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/operations_execute", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_colony_facts"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
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

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
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
	// rimgovernor/observations_list_zones, the same
	// boundary read bridge.ReadZoneTarget issues for CreateZone.
	mapSnapshotToken := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_list_zones", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 16},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		snapshot, _ := na.AsMap(observed["mapSnapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" {
			return "", fmt.Errorf("%s: missing zone map snapshot token", label)
		}
		return token, nil
	}

	// zoneRow reads one zone's row (with its filter) through the
	// rimgovernor/observations_list_zones tool. A nil row with no error
	// means the zone genuinely is not listed.
	zoneRow := func(label, zoneID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_zones", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{zoneID}, "includeFilter": true, "page": map[string]any{"limit": 1},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(observed["zones"])
		if len(rows) == 0 {
			return nil, nil
		}
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: expected at most one observed zone, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		if na.AsString(row["id"]) != zoneID {
			return nil, fmt.Errorf("%s: unexpected zone row: %#v", label, row)
		}
		return row, nil
	}
	// zoneState reads one zone's exact per-zone CAS snapshot token -- the
	// same boundary read a PatchStockpile/EditZoneCells/DeleteZone caller
	// issues. present=false with no error means the zone is not listed.
	zoneState := func(label, zoneID string) (token string, present bool, err error) {
		row, err := zoneRow(label, zoneID)
		if err != nil || row == nil {
			return "", false, err
		}
		snapshot, _ := na.AsMap(row["snapshot"])
		token = na.AsString(snapshot["token"])
		if token == "" {
			return "", false, fmt.Errorf("%s: missing zone snapshot token: %#v", label, row)
		}
		return token, true, nil
	}
	// zoneFilter asserts the exact filter readback: the allowed def set,
	// the hit-point fractions and the quality names.
	zoneFilter := func(label, zoneID, priority string, allowed []string, hpMin, hpMax float64, qualityMin, qualityMax string) error {
		row, err := zoneRow(label, zoneID)
		if err != nil {
			return err
		}
		if row == nil {
			return fmt.Errorf("%s: expected the zone to be listed", label)
		}
		if na.AsString(row["priority"]) != priority {
			return fmt.Errorf("%s: expected priority %s, got %#v", label, priority, row["priority"])
		}
		filter, ok := na.AsMap(row["filter"])
		if !ok {
			return fmt.Errorf("%s: expected a filter readback, got %#v", label, row)
		}
		var names []string
		for _, name := range na.AsSlice(filter["allowedDefNames"]) {
			names = append(names, na.AsString(name))
		}
		if !na.DeepEqual(names, allowed) {
			return fmt.Errorf("%s: expected allowed defs %v, got %v", label, allowed, names)
		}
		if na.AsNumber(filter["hitPointsMin"]) != hpMin || na.AsNumber(filter["hitPointsMax"]) != hpMax {
			return fmt.Errorf("%s: expected hit-point range [%v, %v], got %#v", label, hpMin, hpMax, filter)
		}
		if na.AsString(filter["qualityMin"]) != qualityMin || na.AsString(filter["qualityMax"]) != qualityMax {
			return fmt.Errorf("%s: expected quality range [%s, %s], got %#v", label, qualityMin, qualityMax, filter)
		}
		return nil
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

	// --- CreateZone: real dispatch, first acceptance coverage for this operation. ---

	mapToken, err := mapSnapshotToken("map-before-create")
	if err != nil {
		return err
	}
	// The allow-list body bridge.stockpileSettings sends for
	// domain.NewAllowListStockpileZone, plus both filter ranges.
	stockpileBody := map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT", "preset": "FILTER_PRESET_NOTHING",
		"filter": map[string]any{"allow": []map[string]any{{"thingDef": "Steel"}, {"thingDef": "WoodLog"}},
			"hitPointsMin": 0.5, "hitPointsMax": 1, "qualityMin": "Normal", "qualityMax": "Legendary"}}
	createOperation := map[string]any{"createZone": map[string]any{
		"expectedMapSnapshotToken": mapToken,
		"label":                    "RimGovernor supplies storage",
		"type":                     "ZONE_TYPE_STOCKPILE",
		"cells":                    map[string]any{"explicitCells": map[string]any{"cells": cells}},
		"stockpile":                stockpileBody,
	}}

	staleCreateGeneration, err := currentGeneration("generation-stale-create")
	if err != nil {
		return err
	}
	staleCreateRequest := buildRequest("zone-create-stale-token", staleCreateGeneration, map[string]any{"createZone": map[string]any{
		"expectedMapSnapshotToken": "zone-stale-00000000000000000000000000000000000000000000000000000000000000",
		"label":                    "RimGovernor supplies storage", "type": "ZONE_TYPE_STOCKPILE",
		"cells":     map[string]any{"explicitCells": map[string]any{"cells": cells}},
		"stockpile": stockpileBody,
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
	createSnapshot, _ := na.AsMap(createZoneEffect["snapshot"])
	if na.AsString(createSnapshot["afterToken"]) == "" {
		return fmt.Errorf("execute-create: expected an after-token proving the live settings equal the admitted body, got %#v", createZoneEffect)
	}
	report["created_zone_id"] = zoneID

	// --- ListZones: real per-zone CAS snapshot token and filter readback. ---

	tokenAfterCreate, present, err := zoneState("zone-after-create", zoneID)
	if err != nil {
		return err
	}
	if !present {
		return fmt.Errorf("zone-after-create: expected the created zone to be listed")
	}
	report["zone_token_after_create"] = tokenAfterCreate
	if err := zoneFilter("filter-after-create", zoneID, "Important", []string{"Steel", "WoodLog"}, 0.5, 1, "Normal", "Legendary"); err != nil {
		return err
	}

	// --- PatchStockpile: real dispatch, first acceptance coverage for this operation. ---
	//
	// Priority and the hit-point range change; the absent quality range and
	// selectors preserve the live filter. A selector that does not resolve
	// refuses the whole body before anything is applied.

	unresolvedGeneration, err := currentGeneration("generation-patch-unresolved")
	if err != nil {
		return err
	}
	unresolvedRequest := buildRequest("stockpile-patch-unresolved", unresolvedGeneration, map[string]any{"patchStockpile": map[string]any{
		"zone":     map[string]any{"entityId": zoneID, "expectedSnapshotToken": tokenAfterCreate},
		"settings": map[string]any{"filter": map[string]any{"allow": []map[string]any{{"thingDef": "NoSuchThingDefForZoneAccept"}}}},
	}})
	if code, err := failureCode("patch-unresolved-selector", unresolvedRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("patch-unresolved-selector: expected an invalid-request refusal, got %q", code)
	}
	if token, _, err := zoneState("zone-after-patch-unresolved", zoneID); err != nil {
		return err
	} else if token != tokenAfterCreate {
		return fmt.Errorf("patch-unresolved-selector: a refused body unexpectedly changed the zone")
	}

	patchOperation := map[string]any{"patchStockpile": map[string]any{
		"zone":     map[string]any{"entityId": zoneID, "expectedSnapshotToken": tokenAfterCreate},
		"settings": map[string]any{"priority": "STORAGE_PRIORITY_NORMAL", "filter": map[string]any{"hitPointsMin": 0.25, "hitPointsMax": 0.75}},
	}}
	patchPreviewReply, err := h.Wire(ctx, "preview-patch", "operations_preview", map[string]any{"identity": identity, "operation": patchOperation})
	if err != nil {
		return err
	}
	patchPreviewEvaluated, ok := na.AsMap(patchPreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-patch: expected an evaluated reply, got %#v", patchPreviewReply)
	}
	if accepted, _ := na.AsBool(patchPreviewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-patch: expected the stockpile patch to be accepted, got %#v", patchPreviewEvaluated)
	}
	if token, _, err := zoneState("zone-after-patch-preview", zoneID); err != nil {
		return err
	} else if token != tokenAfterCreate {
		return fmt.Errorf("preview-patch: dry-run preview unexpectedly changed the zone")
	}

	patchGeneration, err := currentGeneration("generation-before-patch")
	if err != nil {
		return err
	}
	patchRequest := buildRequest("stockpile-patch", patchGeneration, patchOperation)
	patchReply, err := h.Wire(ctx, "execute-patch", "operations_execute", patchRequest)
	if err != nil {
		return err
	}
	_, patchReceipt, err := na.Outcome(patchReply, "receipt")
	if err != nil {
		return err
	}
	patchApplied, ok := na.AsMap(patchReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-patch: expected an applied outcome, got %#v", patchReceipt)
	}
	patchObserved, _ := na.AsMap(patchApplied["observed"])
	patchZoneEffect, ok := na.AsMap(patchObserved["zone"])
	if !ok {
		return fmt.Errorf("execute-patch: expected zone effect evidence, got %#v", patchObserved)
	}
	patchSnapshot, _ := na.AsMap(patchZoneEffect["snapshot"])
	if na.AsString(patchSnapshot["beforeToken"]) != tokenAfterCreate || na.AsString(patchSnapshot["afterToken"]) == "" || na.AsString(patchSnapshot["afterToken"]) == tokenAfterCreate {
		return fmt.Errorf("execute-patch: expected before=admitted token and a changed after-token, got %#v", patchZoneEffect)
	}
	if err := zoneFilter("filter-after-patch", zoneID, "Normal", []string{"Steel", "WoodLog"}, 0.25, 0.75, "Normal", "Legendary"); err != nil {
		return err
	}
	tokenAfterPatch, _, err := zoneState("zone-after-patch", zoneID)
	if err != nil {
		return err
	}
	if tokenAfterPatch != na.AsString(patchSnapshot["afterToken"]) {
		return fmt.Errorf("execute-patch: ListZones token %q differs from the receipt's after-token %#v", tokenAfterPatch, patchSnapshot)
	}
	// Replay: the exact same patch attempt returns an identical receipt, and
	// its progress read completes against the live settings.
	replayPatchReply, err := h.Wire(ctx, "replay-patch", "operations_execute", patchRequest)
	if err != nil {
		return err
	}
	_, replayPatch, err := na.Outcome(replayPatchReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayPatch, patchReceipt) {
		return fmt.Errorf("replay-patch: replay of the same attempt returned a different receipt")
	}
	patchPrecondition, _ := na.AsMap(patchRequest["precondition"])
	patchProgressReply, err := h.Wire(ctx, "progress-patch", "receipts_observe_progress", map[string]any{"identity": identity, "attempt": patchPrecondition["attempt"]})
	if err != nil {
		return err
	}
	_, patchProgress, err := na.Outcome(patchProgressReply, "progress")
	if err != nil {
		return err
	}
	if _, completed := na.AsMap(patchProgress["completed"]); !completed {
		return fmt.Errorf("progress-patch: expected a completed effect, got %#v", patchProgress)
	}
	report["zone_token_after_patch"] = tokenAfterPatch
	tokenAfterCreate = tokenAfterPatch

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

	// The add runs under a playing clock window so the native probe sees
	// the zone change and journals an observation_invalidated narrowed to
	// this zone and its rectangle (#359); the window is paused again before
	// the readbacks below.
	var editAddReply map[string]any
	if _, err := editUnderEpoch(ctx, h, identity, report, zoneID, cells, func() error {
		editAddGeneration, err := currentGeneration("generation-before-edit-add")
		if err != nil {
			return err
		}
		editAddReply, err = h.Wire(ctx, "execute-edit-add", "operations_execute", buildRequest("zone-edit-add", editAddGeneration, addOperation))
		return err
	}); err != nil {
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

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
