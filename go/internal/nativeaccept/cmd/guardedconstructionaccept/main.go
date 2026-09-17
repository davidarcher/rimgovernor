// Command guardedconstructionaccept proves disposable ordinary WoodLog Wall construction under guarded
// authority, external-authority-revocation semantics (manual/external-order/
// player-control/lease-expiry), attempt-conflict and replay idempotency, and Go
// durable restart reconciliation (cmd/buildingsmoke) against a private game running
// the GuardedConstructionFixture build (build_native_mod.ps1 -Fixture
// GuardedConstructionFixture).
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-guarded-construction-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-guarded-construction-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	buildingSmoke := flag.String("buildingsmoke", "", "absolute path to a built cmd/buildingsmoke executable")
	expectedOutcome := flag.String("expected-outcome", "completed", "completed or cancelled: the Go observe phase's expected fixture outcome")
	timeout := flag.Duration("timeout", 40*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *buildingSmoke == "" {
		fmt.Fprintln(os.Stderr, "-buildingsmoke is required")
		os.Exit(2)
	}
	if *expectedOutcome != "completed" && *expectedOutcome != "cancelled" {
		fmt.Fprintln(os.Stderr, "-expected-outcome must be completed or cancelled")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-guarded-construction-acceptance"
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
	report := na.NewReport("Disposable ordinary WoodLog Wall construction, guarded authority and attempt "+
		"semantics, Go durable restart reconciliation.", !*rendered)
	report["expected_outcome"] = *expectedOutcome
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, *buildingSmoke, *expectedOutcome, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID, buildingSmoke, expectedOutcome string, headless bool, report na.Report) error {
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
	tick := loadedContext["tick"]
	foundListBuildings := false
	for _, raw := range na.AsSlice(loaded["capabilities"]) {
		capability, _ := na.AsMap(raw)
		if na.AsString(capability["fullMethodName"]) == "rimgovernor.observations.v1.Observations/ListBuildings" {
			if na.AsString(capability["support"]) != "CAPABILITY_SUPPORT_SUPPORTED" {
				return fmt.Errorf("ListBuildings is advertised but not supported: %#v", capability)
			}
			foundListBuildings = true
		}
	}
	if !foundListBuildings {
		return fmt.Errorf("ListBuildings capability not advertised")
	}

	prepared, err := h.Call(ctx, "prepare", "test/guarded_construction_prepare", map[string]any{"siteCount": 3})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("guarded_construction_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) ||
		na.AsNumber(prepared["mapId"]) != na.AsNumber(identity["mapId"]) {
		return fmt.Errorf("guarded_construction_prepare identity does not match the fresh debug game")
	}
	report["prepared"] = prepared
	sites := na.AsSlice(prepared["sites"])
	if len(sites) != 3 {
		return fmt.Errorf("expected exactly 3 prepared wall sites, found %d", len(sites))
	}
	site0, _ := na.AsMap(sites[0])
	site1, _ := na.AsMap(sites[1])
	site2, _ := na.AsMap(sites[2])

	typedBuildingStages := func(stage string) {
		stages, _ := report["typed_building_stages"].([]string)
		report["typed_building_stages"] = append(stages, stage)
	}
	typedBuilding := func(label string, site map[string]any, expectedStatus, expectedID string) error {
		anchor := map[string]any{"x": site["x"], "z": site["z"]}
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "defNames": []string{"Wall"}, "category": "all",
			"region": map[string]any{"minimum": anchor, "maximum": anchor}, "page": map[string]any{"limit": 16},
		})
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		observedContext, _ := na.AsMap(observed["context"])
		if !na.DeepEqual(observedContext["identity"], identity) {
			return fmt.Errorf("%s: observed identity mismatch: %#v", label, observedContext)
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
			return fmt.Errorf("%s: expected a complete page with no unreadable rows: %#v", label, completeness)
		}
		rows := na.AsSlice(observed["buildings"])
		expectedRows := 0
		if expectedStatus != "" {
			expectedRows = 1
		}
		if len(rows) != expectedRows || int(na.AsNumber(completeness["matched"])) != expectedRows ||
			int(na.AsNumber(completeness["returned"])) != expectedRows {
			return fmt.Errorf("%s: expected %d building row(s), got %#v", label, expectedRows, observed)
		}
		networksCompleteness, _ := na.AsMap(observed["networksCompleteness"])
		networksPage, _ := na.AsMap(networksCompleteness["page"])
		if complete, _ := na.AsBool(networksPage["complete"]); complete {
			return fmt.Errorf("%s: expected networksCompleteness.page.complete=false", label)
		}
		if expectedStatus == "" {
			return nil
		}
		row, _ := na.AsMap(rows[0])
		if na.AsString(row["status"]) != expectedStatus || na.AsString(row["stuff"]) != "WoodLog" || na.AsString(row["rotation"]) != "North" {
			return fmt.Errorf("%s: unexpected row: %#v", label, row)
		}
		building, _ := na.AsMap(row["building"])
		if !na.DeepEqual(building["position"], anchor) || na.AsNumber(building["mapId"]) != na.AsNumber(identity["mapId"]) {
			return fmt.Errorf("%s: building position/map mismatch: %#v", label, building)
		}
		if na.AsString(building["id"]) == "" {
			return fmt.Errorf("%s: expected a building id", label)
		}
		occupied := na.AsSlice(row["occupiedCells"])
		if len(occupied) != 1 || !na.DeepEqual(occupied[0], anchor) {
			return fmt.Errorf("%s: unexpected occupiedCells: %#v", label, occupied)
		}
		if expectedID != "" && na.AsString(building["id"]) != expectedID {
			return fmt.Errorf("%s: expected building id %q, got %q", label, expectedID, na.AsString(building["id"]))
		}
		if _, hasSnapshot := building["snapshot"]; hasSnapshot {
			return fmt.Errorf("%s: expected no snapshot on the building itself", label)
		}
		snapshot, ok := na.AsMap(row["snapshot"])
		if !ok {
			return fmt.Errorf("%s: expected a row-level snapshot handle, got %#v", label, row)
		}
		snapshotContext, _ := na.AsMap(snapshot["context"])
		if !na.DeepEqual(snapshotContext["identity"], identity) {
			return fmt.Errorf("%s: snapshot identity mismatch: %#v", label, snapshot)
		}
		if na.AsString(snapshot["entityId"]) != na.AsString(building["id"]) || na.AsString(snapshot["token"]) == "" {
			return fmt.Errorf("%s: unexpected snapshot handle: %#v", label, snapshot)
		}
		if burning, _ := na.AsBool(row["burning"]); burning {
			return fmt.Errorf("%s: expected burning=false", label)
		}
		if expectedStatus == "built" {
			if na.AsString(building["defName"]) != "Wall" {
				return fmt.Errorf("%s: expected defName Wall, got %#v", label, building)
			}
			if _, hasConstruction := row["construction"]; hasConstruction {
				return fmt.Errorf("%s: expected no construction field once built", label)
			}
			if usesHP, _ := na.AsBool(row["usesHitPoints"]); !usesHP || na.AsNumber(row["hitPoints"]) <= 0 {
				return fmt.Errorf("%s: expected positive hit points once built: %#v", label, row)
			}
		} else {
			if na.AsString(row["buildDefName"]) != "Wall" {
				return fmt.Errorf("%s: expected buildDefName Wall, got %#v", label, row)
			}
			construction, _ := na.AsMap(row["construction"])
			workLeft, totalWork := na.AsNumber(construction["workLeft"]), na.AsNumber(construction["totalWork"])
			if workLeft < 0 || workLeft > totalWork {
				return fmt.Errorf("%s: invalid construction work: %#v", label, construction)
			}
			if percent := na.AsNumber(construction["percentComplete"]); percent < 0 || percent > 1 {
				return fmt.Errorf("%s: invalid percentComplete: %v", label, percent)
			}
			costs := na.AsSlice(construction["resources"])
			if len(costs) == 0 {
				return fmt.Errorf("%s: expected construction resource costs", label)
			}
			resourcesComplete := true
			for _, raw := range costs {
				cost, _ := na.AsMap(raw)
				if na.AsString(cost["defName"]) != "WoodLog" {
					return fmt.Errorf("%s: expected WoodLog cost, got %#v", label, cost)
				}
				need, have, stillNeeded := na.AsNumber(cost["need"]), na.AsNumber(cost["have"]), na.AsNumber(cost["stillNeeded"])
				if need <= 0 || have < 0 || stillNeeded < 0 {
					return fmt.Errorf("%s: invalid cost row: %#v", label, cost)
				}
				if stillNeeded != 0 {
					resourcesComplete = false
				}
			}
			if complete, _ := na.AsBool(construction["resourcesComplete"]); complete != resourcesComplete {
				return fmt.Errorf("%s: resourcesComplete mismatch: %#v", label, construction)
			}
		}
		typedBuildingStages(expectedStatus)
		return nil
	}

	observedStatusReply, err := h.Wire(ctx, "typed-status", "observations_read_status", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "colonists": false, "threats": false,
	})
	if err != nil {
		return err
	}
	_, observedStatus, err := na.Outcome(observedStatusReply, "observed")
	if err != nil {
		return err
	}
	observedStatusContext, _ := na.AsMap(observedStatus["context"])
	if !na.DeepEqual(observedStatusContext["identity"], identity) || na.AsNumber(observedStatusContext["tick"]) != na.AsNumber(tick) {
		return fmt.Errorf("typed-status: identity/tick mismatch: %#v", observedStatusContext)
	}
	cellFields := map[string]any{"terrain": false, "roof": false, "visibility": false, "traversal": false,
		"zone": false, "areas": false, "things": false, "designations": false, "room": false, "growth": false}
	cellsReply, err := h.Wire(ctx, "typed-cells", "observations_get_cells", map[string]any{
		"scope":      map[string]any{"expectedIdentity": identity},
		"exactCells": map[string]any{"cells": []any{map[string]any{"x": site0["x"], "z": site0["z"]}}},
		"fields":     cellFields, "page": map[string]any{"limit": 1},
	})
	if err != nil {
		return err
	}
	_, cells, err := na.Outcome(cellsReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(cells["appliedFields"], cellFields) || len(na.AsSlice(cells["cells"])) != 1 {
		return fmt.Errorf("typed-cells: unexpected reply: %#v", cells)
	}
	mapSize, _ := na.AsMap(cells["mapSize"])
	if na.AsNumber(mapSize["width"]) <= na.AsNumber(site0["x"]) || na.AsNumber(mapSize["height"]) <= na.AsNumber(site0["z"]) {
		return fmt.Errorf("typed-cells: map size does not contain the prepared site: %#v", mapSize)
	}

	buildingArgs := map[string]any{"x": site1["x"], "z": site1["z"], "radius": 1, "category": "all", "aggregate": false}
	before, err := h.Call(ctx, "before-preview", "home/list_buildings", buildingArgs)
	if err != nil {
		return err
	}
	previewReply, err := h.Wire(ctx, "operation-preview", "operations_preview", map[string]any{
		"identity": identity, "operation": map[string]any{"placeBuilding": map[string]any{"placement": site1}},
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("operation-preview: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("operation-preview: expected the wall placement to be accepted, got %#v", evaluated)
	}
	after, err := h.Call(ctx, "after-preview", "home/list_buildings", buildingArgs)
	if err != nil {
		return err
	}
	if !equalExcept(before, after, "operation") {
		return fmt.Errorf("operation-preview: home/list_buildings changed across a dry-run preview: before=%#v after=%#v", before, after)
	}

	status := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, value, err := na.Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		valueContext, _ := na.AsMap(value["context"])
		if !na.DeepEqual(valueContext["identity"], identity) {
			return nil, fmt.Errorf("%s: authority status identity mismatch: %#v", label, value)
		}
		return value, nil
	}
	wire := func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
		return h.Wire(ctx, label, method, request)
	}
	supervisor := &na.ScenarioClock{Wire: wire, Identity: identity, Owner: sessionOwner, Report: report}

	grant, err := supervisor.Acquire(ctx, "acquire")
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "ordinary-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	pausedAuthority, err := status("pause-preserves-authority")
	if err != nil {
		return err
	}
	if _, ok := pausedAuthority["active"]; !ok {
		return fmt.Errorf("pause-preserves-authority: expected an active authority grant: %#v", pausedAuthority)
	}
	grantContext, _ := na.AsMap(grant["context"])
	pausedContext, _ := na.AsMap(pausedAuthority["context"])
	if na.AsNumber(pausedContext["nativeGeneration"]) != na.AsNumber(grantContext["nativeGeneration"]) {
		return fmt.Errorf("pause-preserves-authority: native generation changed across pause: %#v", pausedAuthority)
	}
	if err := supervisor.RenewAuthority(ctx); err != nil {
		return err
	}
	renewed := supervisor.Grant
	if na.AsString(renewed["leaseId"]) != na.AsString(grant["leaseId"]) {
		return fmt.Errorf("renew: expected the same lease id, got %#v", renewed)
	}

	request := placeRequest(identity, renewed, 1, site1)
	receiptReply, err := h.Wire(ctx, "place-cancel-site", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	construction, err := constructionEffect(receipt, "applied")
	if err != nil {
		return fmt.Errorf("place-cancel-site: %w", err)
	}
	if na.AsString(construction["stage"]) != "CONSTRUCTION_STAGE_BLUEPRINT" {
		return fmt.Errorf("place-cancel-site: expected a Blueprint stage, got %#v", construction)
	}
	if err := typedBuilding("typed-blueprint", site1, "blueprint", na.AsString(construction["currentThingId"])); err != nil {
		return err
	}
	attempt := attemptRef(identity, request)
	pendingReply, err := h.Wire(ctx, "initial-progress", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, pending, err := na.Outcome(pendingReply, "progress")
	if err != nil {
		return err
	}
	if _, ok := pending["pending"]; !ok {
		return fmt.Errorf("initial-progress: expected a pending outcome, got %#v", pending)
	}
	if complete, _ := na.AsBool(pending["completeInspection"]); !complete {
		return fmt.Errorf("initial-progress: expected completeInspection=true")
	}
	replayReply, err := h.Wire(ctx, "replay", "operations_execute", request)
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
	conflict := placeRequest(identity, renewed, 1, site2)
	if err := refuse(ctx, h, "attempt-conflict", conflict, "FAILURE_CODE_ATTEMPT_CONFLICT"); err != nil {
		return err
	}
	revokeReply, err := h.Wire(ctx, "manual", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": grantContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(revokeReply, "revoked"); err != nil {
		return err
	}
	replayAfterManualReply, err := h.Wire(ctx, "replay-after-manual", "operations_execute", request)
	if err != nil {
		return err
	}
	_, replayAfterManual, err := na.Outcome(replayAfterManualReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayAfterManual, receipt) {
		return fmt.Errorf("replay-after-manual: replay after a manual revoke returned a different receipt")
	}
	lookupAfterManualReply, err := h.Wire(ctx, "lookup-after-manual", "receipts_lookup", attempt)
	if err != nil {
		return err
	}
	_, lookupAfterManual, err := na.Outcome(lookupAfterManualReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookupAfterManual, receipt) {
		return fmt.Errorf("lookup-after-manual: lookup after a manual revoke returned a different receipt")
	}
	blockedRequest := placeRequest(identity, renewed, 2, site2)
	if err := refuse(ctx, h, "manual-blocks-new", blockedRequest, "FAILURE_CODE_STALE_GENERATION"); err != nil {
		return err
	}
	blockedAttempt := attemptRef(identity, blockedRequest)
	unadmittedReply, err := h.Wire(ctx, "refusal-not-admitted", "receipts_lookup", blockedAttempt)
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(unadmittedReply, "unknown"); err != nil {
		return fmt.Errorf("refusal-not-admitted: %w", err)
	}

	_, err = supervisor.Acquire(ctx, "cancel-authority")
	if err != nil {
		return err
	}
	cancelControl := flattenIdentity(identity, map[string]any{"operation": "cancel", "blueprintId": construction["currentThingId"]})
	cancelResult, err := h.Call(ctx, "external-cancel", "test/guarded_construction_control", cancelControl)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(cancelResult["success"]); !success {
		return fmt.Errorf("external-cancel refused: %#v", cancelResult)
	}
	cancelRevoked, err := status("cancel-revoked")
	if err != nil {
		return err
	}
	if inactive, ok := na.AsMap(cancelRevoked["inactive"]); !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_EXTERNAL_ORDER" {
		return fmt.Errorf("cancel-revoked: expected REVOCATION_REASON_EXTERNAL_ORDER, got %#v", cancelRevoked)
	}
	cancelProgressReply, err := h.Wire(ctx, "cancel-progress", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, cancelProgress, err := na.Outcome(cancelProgressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(cancelProgress["completeInspection"]); !complete {
		return fmt.Errorf("cancel-progress: expected completeInspection=true")
	}
	if _, ok := cancelProgress["unsuccessful"]; !ok {
		return fmt.Errorf("cancel-progress: expected an unsuccessful outcome, got %#v", cancelProgress)
	}
	if err := typedBuilding("typed-cancelled-empty", site1, "", ""); err != nil {
		return err
	}

	grant, err = supervisor.Acquire(ctx, "draft-authority")
	if err != nil {
		return err
	}
	_ = grant
	pawnID := na.AsString(prepared["pawnId"])
	draftControl := flattenIdentity(identity, map[string]any{"operation": "draft", "pawnId": pawnID})
	draftResult, err := h.Call(ctx, "external-draft", "test/guarded_construction_control", draftControl)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(draftResult["drafted"]); !drafted {
		return fmt.Errorf("external-draft: expected drafted=true, got %#v", draftResult)
	}
	draftRevoked, err := status("draft-revoked")
	if err != nil {
		return err
	}
	if inactive, ok := na.AsMap(draftRevoked["inactive"]); !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_PLAYER_CONTROL" {
		return fmt.Errorf("draft-revoked: expected REVOCATION_REASON_PLAYER_CONTROL, got %#v", draftRevoked)
	}
	if err := refuse(ctx, h, "draft-blocks-new", placeRequest(identity, supervisor.Grant, 3, site2), "FAILURE_CODE_STALE_GENERATION"); err != nil {
		return err
	}
	undraftResult, err := h.Call(ctx, "undraft", "test/guarded_construction_control", draftControl)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(undraftResult["drafted"]); drafted {
		return fmt.Errorf("undraft: expected drafted=false, got %#v", undraftResult)
	}

	if _, err := supervisor.Acquire(ctx, "ordered-job-authority"); err != nil {
		return err
	}
	pawnCell, _ := na.AsMap(prepared["pawnCell"])
	moveControl := flattenIdentity(identity, map[string]any{"operation": "move", "pawnId": pawnID, "x": pawnCell["x"], "z": pawnCell["z"]})
	moveResult, err := h.Call(ctx, "external-ordered-job", "test/guarded_construction_control", moveControl)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(moveResult["success"]); !success {
		return fmt.Errorf("external-ordered-job refused: %#v", moveResult)
	}
	orderedJobRevoked, err := status("ordered-job-revoked")
	if err != nil {
		return err
	}
	if inactive, ok := na.AsMap(orderedJobRevoked["inactive"]); !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_EXTERNAL_ORDER" {
		return fmt.Errorf("ordered-job-revoked: expected REVOCATION_REASON_EXTERNAL_ORDER, got %#v", orderedJobRevoked)
	}

	expiredGrant, err := acquireWithLease(ctx, h, identity, sessionOwner, "expiry-authority", 1000)
	if err != nil {
		return err
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(1200 * time.Millisecond):
	}
	expired, err := status("expired")
	if err != nil {
		return err
	}
	if inactive, ok := na.AsMap(expired["inactive"]); !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_LEASE_EXPIRED" {
		return fmt.Errorf("expired: expected REVOCATION_REASON_LEASE_EXPIRED, got %#v", expired)
	}
	if err := refuse(ctx, h, "expired-blocks-new", placeRequest(identity, expiredGrant, 4, site2), "FAILURE_CODE_STALE_GENERATION"); err != nil {
		return err
	}

	currentReply, err := h.Wire(ctx, "before-go", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, current, err := na.Outcome(currentReply, "loaded")
	if err != nil {
		return err
	}
	currentContext, _ := na.AsMap(current["context"])
	if paused, _ := na.AsBool(current["paused"]); !paused || na.AsNumber(currentContext["tick"]) != na.AsNumber(tick) {
		return fmt.Errorf("before-go: expected the game still paused with an unchanged tick, got %#v", current)
	}

	fixturePath := filepath.Join(output, "go-placement.json")
	fixtureData, err := json.Marshal(site0)
	if err != nil {
		return err
	}
	if err := os.WriteFile(fixturePath, fixtureData, 0644); err != nil {
		return err
	}
	profileName := "profile"
	if headless {
		profileName = "headless-profile"
	}
	profile := filepath.Join(root, profileName)
	statePath := filepath.Join(output, "building.sqlite")

	placed, err := goPhase(ctx, buildingSmoke, gabsExecutable, cfg.Configuration, profile, statePath, output, gameID,
		"place", "completed", fixturePath, report)
	if err != nil {
		return err
	}
	if _, err := client.ConnectGameWithTakeover(ctx); err != nil {
		return fmt.Errorf("reconnect after Go place: %w", err)
	}
	executeCalls := 0
	var goReceiptCase map[string]any
	for _, raw := range na.AsSlice(placed["calls"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["Name"]) != "operations_execute" {
			continue
		}
		executeCalls++
		receiptWrap, _ := na.AsMap(row["Receipt"])
		structured, _ := na.AsMap(receiptWrap["Structured"])
		goReceiptCase, err = unwrapPayload(structured)
		if err != nil {
			return fmt.Errorf("go-place operations_execute receipt: %w", err)
		}
	}
	if executeCalls != 1 {
		return fmt.Errorf("expected exactly one Go operations_execute call, found %d", executeCalls)
	}
	_, goReceipt, err := na.Outcome(goReceiptCase, "receipt")
	if err != nil {
		return err
	}
	goEffect, err := constructionEffect(goReceipt, "applied")
	if err != nil {
		return fmt.Errorf("go-place: %w", err)
	}
	if err := typedBuilding("typed-go-blueprint", site0, "blueprint", na.AsString(goEffect["currentThingId"])); err != nil {
		return err
	}
	lookup := map[string]any{"identity": identity, "attempt": goReceipt["attempt"]}

	if expectedOutcome == "cancelled" {
		cancelGoControl := flattenIdentity(identity, map[string]any{"operation": "cancel", "blueprintId": goEffect["currentThingId"]})
		cancelledResult, err := h.Call(ctx, "cancel-go-blueprint", "test/guarded_construction_control", cancelGoControl)
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(cancelledResult["success"]); !success {
			return fmt.Errorf("cancel-go-blueprint refused: %#v", cancelledResult)
		}
		cancelledProgressReply, err := h.Wire(ctx, "go-cancelled-progress", "receipts_observe_progress", lookup)
		if err != nil {
			return err
		}
		_, cancelledProgress, err := na.Outcome(cancelledProgressReply, "progress")
		if err != nil {
			return err
		}
		if complete, _ := na.AsBool(cancelledProgress["completeInspection"]); !complete {
			return fmt.Errorf("go-cancelled-progress: expected completeInspection=true")
		}
		unsuccessful, ok := na.AsMap(cancelledProgress["unsuccessful"])
		if !ok || na.AsString(unsuccessful["reason"]) != "UNSUCCESSFUL_REASON_CANCELLED" {
			return fmt.Errorf("go-cancelled-progress: expected UNSUCCESSFUL_REASON_CANCELLED, got %#v", cancelledProgress)
		}
		if err := typedBuilding("typed-go-cancelled-empty", site0, "", ""); err != nil {
			return err
		}
	}

	if err := renewOrAcquire(ctx, supervisor, "construction-wait"); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report}
	complete := expectedOutcome == "cancelled"
	windows := 0
	if expectedOutcome == "completed" {
		windows = 13
	}
	for window := 0; window < windows; window++ {
		progressReply, err := h.Wire(ctx, fmt.Sprintf("construction-progress-%d", window), "receipts_observe_progress", lookup)
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(progressReply, "progress")
		if err != nil {
			return err
		}
		if completedCase, ok := na.AsMap(progress["completed"]); ok {
			if complete, _ := na.AsBool(progress["completeInspection"]); !complete {
				return fmt.Errorf("construction-progress-%d: expected completeInspection=true", window)
			}
			evidence, _ := na.AsMap(completedCase["evidence"])
			effect, _ := na.AsMap(evidence["construction"])
			if na.AsString(effect["stage"]) != "CONSTRUCTION_STAGE_BUILDING" {
				return fmt.Errorf("construction-progress-%d: expected a Building stage, got %#v", window, effect)
			}
			if present, _ := na.AsBool(effect["present"]); !present {
				return fmt.Errorf("construction-progress-%d: expected present=true", window)
			}
			if na.AsString(effect["defName"]) != "Wall" || na.AsString(effect["stuff"]) != "WoodLog" {
				return fmt.Errorf("construction-progress-%d: unexpected effect: %#v", window, effect)
			}
			cell, _ := na.AsMap(effect["cell"])
			if na.AsNumber(cell["x"]) != na.AsNumber(site0["x"]) || na.AsNumber(cell["z"]) != na.AsNumber(site0["z"]) {
				return fmt.Errorf("construction-progress-%d: unexpected cell: %#v", window, cell)
			}
			if err := typedBuilding(fmt.Sprintf("typed-go-built-%d", window), site0, "built", na.AsString(effect["currentThingId"])); err != nil {
				return err
			}
			complete = true
			break
		}
		pendingCase, ok := na.AsMap(progress["pending"])
		if !ok {
			return fmt.Errorf("construction-progress-%d: construction did not remain pending: %#v", window, progress)
		}
		pendingEvidence, _ := na.AsMap(pendingCase["evidence"])
		pendingEffect, _ := na.AsMap(pendingEvidence["construction"])
		stageNames := map[string]string{"CONSTRUCTION_STAGE_BLUEPRINT": "blueprint", "CONSTRUCTION_STAGE_FRAME": "frame"}
		state, ok := stageNames[na.AsString(pendingEffect["stage"])]
		if !ok {
			return fmt.Errorf("construction-progress-%d: unexpected pending stage: %#v", window, pendingEffect)
		}
		if err := typedBuilding(fmt.Sprintf("typed-go-pending-%d", window), site0, state, na.AsString(pendingEffect["currentThingId"])); err != nil {
			return err
		}
		if window < windows-1 {
			if _, err := na.AdvanceGame(ctx, rt, 600, na.WithTimeout(180*time.Second)); err != nil {
				return err
			}
		}
	}
	if !complete {
		return fmt.Errorf("construction not completed within %d simulation windows", windows)
	}

	// The construction-wait loop above kept supervisor's own authority grant
	// continuously renewed (via clock_start/renew) through completion, so the
	// native writer authority is still actively held by this harness session,
	// not Go observe's own buildingsmoke session. Go observe's ObserveTarget
	// only admits a target whose authority is either inactive or already owned
	// by its own session (durable-restart reconciliation never force-takes
	// authority the way GABS game ownership does), so it must be released here
	// or every observe attempt fails with ErrControl ("writer authority
	// unavailable").
	preObserveStatus, err := status("pre-observe-authority-status")
	if err != nil {
		return err
	}
	preObserveContext, _ := na.AsMap(preObserveStatus["context"])
	releaseReply, err := h.Wire(ctx, "release-before-observe", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": preObserveContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(releaseReply, "revoked"); err != nil {
		return err
	}

	if _, err := goPhase(ctx, buildingSmoke, gabsExecutable, cfg.Configuration, profile, statePath, output, gameID,
		"observe", expectedOutcome, "", report); err != nil {
		return err
	}
	if _, err := client.ConnectGameWithTakeover(ctx); err != nil {
		return fmt.Errorf("reconnect after Go observe: %w", err)
	}
	finalReply, err := h.Wire(ctx, "final-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, final, err := na.Outcome(finalReply, "loaded")
	if err != nil {
		return err
	}
	finalContext, _ := na.AsMap(final["context"])
	if !na.DeepEqual(finalContext["identity"], identity) {
		return fmt.Errorf("final-identity: identity changed during the run")
	}
	if paused, _ := na.AsBool(final["paused"]); !paused {
		return fmt.Errorf("final-identity: expected the game still paused")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// renewOrAcquire refreshes supervisor's authority grant immediately before an
// operations_execute or clock control call that needs it, mirroring
// cmd/constructionaccept's helper of the same name. It prefers Renew (cheap,
// keeps the same lease) but falls back to a fresh Acquire whenever the prior
// lease is no longer renewable, most notably after wall-clock time elapsed
// during the Go place subprocess (games_connect, operations_execute) already
// lapsed supervisor's lease and advanced the native generation independently
// of any tick simulation.
func renewOrAcquire(ctx context.Context, supervisor *na.ScenarioClock, label string) error {
	if supervisor.Grant != nil {
		if err := supervisor.RenewAuthority(ctx); err == nil {
			return nil
		}
	}
	_, err := supervisor.Acquire(ctx, label+"-acquire")
	return err
}

// unwrapPayload extracts and decodes the ProtoJSON string under structured's
// "payload" field, matching Harness.Wire's own envelope handling. buildingsmoke's
// recorded bridge.Result.Structured carries the same {"operation":..., "payload":
// "<json>"} envelope as any other native tool reply, so its receipt needs the same
// unwrapping before na.Outcome can read the oneof case underneath.
func unwrapPayload(structured map[string]any) (map[string]any, error) {
	payloadString, ok := structured["payload"].(string)
	if !ok {
		return nil, fmt.Errorf("reply did not preserve the ProtoJSON string envelope: %#v", structured)
	}
	var message map[string]any
	if err := json.Unmarshal([]byte(payloadString), &message); err != nil {
		return nil, fmt.Errorf("invalid ProtoJSON reply: %w", err)
	}
	return message, nil
}

// placeRequest builds an operations_execute placeBuilding request under grant's
// lease for site (already a full placement: defName/stuff/rotation/x/z, as returned
// by test/guarded_construction_prepare).
func placeRequest(identity, grant map[string]any, number int, site map[string]any) map[string]any {
	grantContext, _ := na.AsMap(grant["context"])
	return map[string]any{
		"precondition": map[string]any{
			"identity":           identity,
			"expectedGeneration": grantContext["nativeGeneration"],
			"leaseId":            grant["leaseId"],
			"attempt": map[string]any{
				"controllerSessionId": sessionOwner,
				"actionId":            fmt.Sprintf("fixture-%d", number),
				"attemptId":           "1",
			},
		},
		"operation": map[string]any{"placeBuilding": map[string]any{"placement": site}},
	}
}

// attemptRef builds a receipts_observe_progress/receipts_lookup request for
// request's own attempt.
func attemptRef(identity, request map[string]any) map[string]any {
	precondition, _ := na.AsMap(request["precondition"])
	return map[string]any{"identity": identity, "attempt": precondition["attempt"]}
}

// refuse asserts operations_execute(request) is refused with exactly code.
func refuse(ctx context.Context, h *na.Harness, label string, request map[string]any, code string) error {
	reply, err := h.Wire(ctx, label, "operations_execute", request)
	if err != nil {
		return err
	}
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(failure["code"]) != code {
		return fmt.Errorf("%s: expected %s, got %#v", label, code, failure)
	}
	return nil
}

// constructionEffect asserts receipt's named case carries a ConstructionEffect and
// returns it, mirroring constructionaccept's own helper of the same name.
func constructionEffect(receipt map[string]any, caseName string) (map[string]any, error) {
	caseValue, ok := na.AsMap(receipt[caseName])
	if !ok {
		return nil, fmt.Errorf("expected a %q outcome, got %#v", caseName, receipt)
	}
	observed, _ := na.AsMap(caseValue["observed"])
	effect, ok := na.AsMap(observed["construction"])
	if !ok {
		return nil, fmt.Errorf("expected a construction effect, got %#v", observed)
	}
	return effect, nil
}

// flattenIdentity merges identity's colonyId/loadToken/mapId at the top level of
// extra, matching test/guarded_construction_control's flat (non-nested-identity)
// parameter shape.
func flattenIdentity(identity map[string]any, extra map[string]any) map[string]any {
	out := map[string]any{"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"]}
	for k, v := range extra {
		out[k] = v
	}
	return out
}

// acquireWithLease acquires a fresh authority grant with an explicit leaseMs
// for its one non-default-duration case (the lease-expiry scenario).
func acquireWithLease(ctx context.Context, h *na.Harness, identity map[string]any, owner, label string, leaseMs int) (map[string]any, error) {
	statusReply, err := h.Wire(ctx, label+"-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return nil, err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return nil, err
	}
	statusContext, _ := na.AsMap(status["context"])
	grantReply, err := h.Wire(ctx, label, "authority_control", map[string]any{"acquire": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"],
		"owner": map[string]any{"controllerSessionId": owner, "playerDirection": "1"}, "leaseMs": leaseMs,
	}})
	if err != nil {
		return nil, err
	}
	_, grant, err := na.Outcome(grantReply, "granted")
	return grant, err
}

// equalExcept reports whether a and b are deeply equal after dropping key from
// both.
func equalExcept(a, b map[string]any, key string) bool {
	left, right := map[string]any{}, map[string]any{}
	for k, v := range a {
		if k != key {
			left[k] = v
		}
	}
	for k, v := range b {
		if k != key {
			right[k] = v
		}
	}
	return na.DeepEqual(left, right)
}

// goPhase spawns a fresh cmd/buildingsmoke process in mode ("place" or "observe"). state is
// reused across both phases (place creates it, observe reopens it).
func goPhase(ctx context.Context, binary, gabsExecutable, configuration, profile, state, output, gameID,
	mode, expectedOutcome, requestPath string, report na.Report) (map[string]any, error) {
	destination := filepath.Join(output, "go-"+mode)
	binaryData, err := os.ReadFile(binary)
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256(binaryData)
	record := map[string]any{"mode": mode, "binary_sha256": hex.EncodeToString(sum[:])}
	goRuns, _ := report["go"].([]any)
	report["go"] = append(goRuns, record)

	args := []string{"-mode", mode, "-gabs", gabsExecutable, "-config", configuration, "-profile", profile,
		"-state", state, "-output", destination, "-game", gameID, "-force-takeover"}
	if mode == "place" {
		args = append(args, "-execute", "-request", requestPath)
	} else {
		args = append(args, "-expected-outcome", expectedOutcome)
	}
	stdout, err := os.Create(filepath.Join(output, "go-"+mode+"-stdout.txt"))
	if err != nil {
		return nil, err
	}
	defer stdout.Close()
	stderr, err := os.Create(filepath.Join(output, "go-"+mode+"-stderr.txt"))
	if err != nil {
		return nil, err
	}
	defer stderr.Close()
	runCtx, cancel := context.WithTimeout(ctx, 150*time.Second)
	defer cancel()
	cmd := exec.CommandContext(runCtx, binary, args...)
	cmd.Stdout, cmd.Stderr = stdout, stderr
	runErr := cmd.Run()
	if cmd.ProcessState != nil {
		record["exit_code"] = cmd.ProcessState.ExitCode()
	}
	if runErr != nil {
		return nil, fmt.Errorf("go %s failed; see %s/go-%s-{stdout,stderr}.txt: %w", mode, output, mode, runErr)
	}
	data, err := os.ReadFile(filepath.Join(destination, "report.json"))
	if err != nil {
		return nil, err
	}
	var result map[string]any
	if err := json.Unmarshal(data, &result); err != nil {
		return nil, fmt.Errorf("go %s report.json: %w", mode, err)
	}
	if passed, _ := na.AsBool(result["passed"]); !passed {
		return nil, fmt.Errorf("go %s did not pass: %#v", mode, result)
	}
	nativeCalled, _ := na.AsBool(result["nativeCalled"])
	if nativeCalled != (mode == "place") {
		return nil, fmt.Errorf("go %s: expected nativeCalled=%v, got %#v", mode, mode == "place", result)
	}
	if mode == "observe" {
		if na.AsString(result["expectedOutcome"]) != expectedOutcome {
			return nil, fmt.Errorf("go observe: expected expectedOutcome=%q, got %#v", expectedOutcome, result)
		}
		progress, _ := na.AsMap(result["progress"])
		if unresolved, _ := na.AsBool(progress["Unresolved"]); unresolved {
			return nil, fmt.Errorf("go observe: expected a resolved progress view, got %#v", progress)
		}
		for _, raw := range na.AsSlice(result["calls"]) {
			row, _ := na.AsMap(raw)
			if na.AsString(row["Name"]) == "operations_execute" {
				return nil, fmt.Errorf("go observe: unexpectedly called operations_execute")
			}
		}
	}
	record["report"] = result
	return result, nil
}
