// The wall/removal case proves the typed RemoveWall and
// ReleaseWallRemovals operations (#83) on a loaded save: NativeWallRemovalOperations
// resolves the guarded demolition site from the wall identity alone, refuses
// a wall without completed stone backups, admits the original's demolition
// once same-stuff stone backups stand in its backup cells, and a real
// supervised native deconstruct job clears the wall (observed Completed with
// the wall gone from the building census, not just a receipt). A standing
// stone permanent wall then makes the backups cleanup sites: one backup is
// demolished the same way, and a further pending removal is retired by
// ReleaseWallRemovals (released_count 1, progress Unsuccessful, designation
// gone). Replay and lookup return the original receipt.
//
// A LightingFixture + UpkeepFixture build supplies test/lighting_prepare (an
// enclosed roofed room of colonist walls) and test/stone_walls_spawn (finished
// stone walls standing in for completed backup and permanent construction).
package wall

import (
	"context"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-wall-removal-acceptance"

// baselineSave is the committed save the lighting fixture builds its
// colonist walls on (profile/Saves/<name>.rws).
const baselineSave = "RimGovernor-tribal8-baseline"

type site struct {
	wall, stuff string
	x, z        float64
	nx, nz      float64
	cells       []string // "x,z" in native backup order
	token       string
}

func rowForNormal(rows []any, nx, nz float64) (map[string]any, error) {
	var found map[string]any
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		normal, _ := na.AsMap(row["normal"])
		if na.AsNumber(normal["x"]) != nx || na.AsNumber(normal["z"]) != nz {
			continue
		}
		if found != nil {
			return nil, fmt.Errorf("several rows share normal (%v,%v)", nx, nz)
		}
		found = row
	}
	if found == nil {
		return nil, fmt.Errorf("no row with normal (%v,%v) among %d", nx, nz, len(rows))
	}
	return found, nil
}

func contains(ids []string, id string) bool {
	for _, candidate := range ids {
		if candidate == id {
			return true
		}
	}
	return false
}

// colonistWalls pages the typed building census for every player Wall id.
func colonistWalls(ctx context.Context, h *na.Harness, scope map[string]any, label string) ([]string, error) {
	var ids []string
	cursor := ""
	for page := 0; page < 32; page++ {
		request := map[string]any{"scope": scope, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true, "page": map[string]any{"limit": 256}}
		if cursor != "" {
			request["page"] = map[string]any{"limit": 256, "cursor": cursor}
		}
		reply, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, page), "observations_list_buildings", request)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("%s page %d: %w", label, page, err)
		}
		for _, raw := range na.AsSlice(observed["buildings"]) {
			row, _ := na.AsMap(raw)
			building, _ := na.AsMap(row["building"])
			ids = append(ids, na.AsString(building["id"]))
		}
		completeness, _ := na.AsMap(observed["completeness"])
		pageInfo, _ := na.AsMap(completeness["page"])
		cursor = na.AsString(pageInfo["nextCursor"])
		if complete, _ := na.AsBool(pageInfo["complete"]); complete || cursor == "" {
			break
		}
	}
	sort.Strings(ids)
	return ids, nil
}

func init() {
	cases.Register(cases.Case{
		Name: "wall/removal",
		Scope: "Native RemoveWall/ReleaseWallRemovals dispatch: site resolution from the wall identity, refusal without " +
			"backups, guarded demolition of the original and of a backup by real supervised native deconstruct jobs, release of a pending " +
			"removal, replay and lookup idempotency.",
		Start:  cases.Fixture{Op: "test/lighting_prepare", On: cases.Save{Name: baselineSave}},
		Budget: 5 * time.Minute,
		Run:    runRemoval,
	})
}

func runRemoval(ctx context.Context, s cases.Session) error {
	// The save carries its own expansion list (the runner enables them);
	// the lighting fixture builds the colonist walls on top of it.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	for _, name := range []string{"rimgovernor/operations_execute", "rimgovernor/operations_preview", "rimgovernor/observations_list_wall_upgrade_sites", "test/stone_walls_spawn"} {
		if !na.Contains(s.Names(), name) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture LightingFixture,UpkeepFixture", name)
		}
	}
	clock := na.NewSupervisedPlayClock(sessionOwner)
	if _, err := clock.Change(ctx, h, "initial-pause", "Paused", nil); err != nil {
		return err
	}
	scope := map[string]any{"expectedIdentity": identity}

	census := func(label, target string) ([]any, error) {
		request := map[string]any{"scope": scope, "page": map[string]any{"limit": 256}}
		if target != "" {
			request["targetId"] = target
		}
		reply, err := h.Wire(ctx, label, "observations_list_wall_upgrade_sites", request)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("%s: %w", label, err)
		}
		return na.AsSlice(observed["sites"]), nil
	}
	walls, err := colonistWalls(ctx, h, scope, "walls")
	if err != nil {
		return err
	}
	if len(walls) == 0 {
		return fmt.Errorf("fixture produced no colonist wall")
	}
	var chosen *site
	for _, wall := range walls {
		rows, err := census("candidates-"+wall, wall)
		if err != nil {
			return err
		}
		for _, raw := range rows {
			row, _ := na.AsMap(raw)
			normal, _ := na.AsMap(row["normal"])
			nx, nz := na.AsNumber(normal["x"]), na.AsNumber(normal["z"])
			owned, _ := na.AsBool(row["playerOwned"])
			cells := na.AsSlice(row["backupCells"])
			if nx != 0 && nz != 0 || na.AsString(row["blocker"]) != "" || owned || len(cells) != 3 || len(na.AsSlice(row["completedBackups"])) != 0 {
				continue
			}
			materials := na.AsSlice(row["replacementMaterials"])
			if len(materials) == 0 {
				continue
			}
			material, _ := na.AsMap(materials[0])
			original, _ := na.AsMap(row["original"])
			originalBuilding, _ := na.AsMap(original["building"])
			position, _ := na.AsMap(originalBuilding["position"])
			target, _ := na.AsMap(row["target"])
			snapshot, _ := na.AsMap(target["snapshot"])
			chosen = &site{wall: wall, stuff: na.AsString(material["stuff"]), x: na.AsNumber(position["x"]), z: na.AsNumber(position["z"]), nx: nx, nz: nz, token: na.AsString(snapshot["token"])}
			for _, rawCell := range cells {
				cell, _ := na.AsMap(rawCell)
				chosen.cells = append(chosen.cells, fmt.Sprintf("%d,%d", int(na.AsNumber(cell["x"])), int(na.AsNumber(cell["z"]))))
			}
			break
		}
		if chosen != nil {
			break
		}
	}
	if chosen == nil {
		return fmt.Errorf("no colonist wall offers a straight replacement site with three open backup cells")
	}
	report["site_wall"] = chosen.wall
	report["site_stuff"] = chosen.stuff
	report["site_backup_cells"] = strings.Join(chosen.cells, ";")

	// acquire sets Auto mode at the current generation (#52: no lease
	// handshake); it is repeated before every dispatch that follows a
	// supervised run, since player-visible activity may bump the generation.
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
		grantReply, err := h.Wire(ctx, label, "authority_control", map[string]any{"setMode": map[string]any{
			"identity": identity, "expectedGeneration": statusContext["nativeGeneration"], "mode": "MODE_AUTO",
		}})
		if err != nil {
			return err
		}
		_, _, err = na.Outcome(grantReply, "granted")
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
	removeOperation := func(wall string) map[string]any {
		return map[string]any{"removeWall": map[string]any{"wall": map[string]any{"entityId": wall}}}
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
	expectFailure := func(label string, request map[string]any, codes ...string) error {
		reply, err := h.Wire(ctx, label, "operations_execute", request)
		if err != nil {
			return err
		}
		_, failure, err := na.Outcome(reply, "failure")
		if err != nil {
			return fmt.Errorf("%s: %w", label, err)
		}
		code := na.AsString(failure["code"])
		for _, want := range codes {
			if code == want {
				return nil
			}
		}
		return fmt.Errorf("%s: expected %v, got %q (%s)", label, codes, code, na.AsString(failure["detail"]))
	}
	// execute dispatches one guarded removal and checks its applied evidence.
	execute := func(label, actionID, wall string) (map[string]any, map[string]any, error) {
		if err := acquire(label + "-acquire"); err != nil {
			return nil, nil, err
		}
		generation, err := currentGeneration(label + "-generation")
		if err != nil {
			return nil, nil, err
		}
		request := buildRequest(actionID, generation, removeOperation(wall))
		reply, err := h.Wire(ctx, label, "operations_execute", request)
		if err != nil {
			return nil, nil, err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return nil, nil, fmt.Errorf("%s: %w", label, err)
		}
		applied, ok := na.AsMap(receipt["applied"])
		if !ok {
			return nil, nil, fmt.Errorf("%s: expected an applied outcome, got %#v", label, receipt)
		}
		observed, _ := na.AsMap(applied["observed"])
		effect, _ := na.AsMap(observed["wall"])
		siteEvidence, _ := na.AsMap(effect["site"])
		if na.AsString(effect["targetId"]) != wall || na.AsString(effect["removalId"]) == "" || len(na.AsSlice(effect["workerIds"])) == 0 || na.AsString(siteEvidence["entityId"]) != wall || na.AsString(siteEvidence["beforeToken"]) == "" || na.AsString(siteEvidence["afterToken"]) == "" {
			return nil, nil, fmt.Errorf("%s: unexpected wall effect: %#v", label, effect)
		}
		if observedDemolition, _ := na.AsBool(effect["demolitionObserved"]); observedDemolition {
			return nil, nil, fmt.Errorf("%s: demolition cannot be observed at admission", label)
		}
		return request, receipt, nil
	}
	attemptOf := func(request map[string]any) map[string]any {
		precondition, _ := na.AsMap(request["precondition"])
		return map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	}
	progress := func(label string, attempt map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "receipts_observe_progress", attempt)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "progress")
		if err != nil {
			return nil, fmt.Errorf("%s: %w", label, err)
		}
		return observed, nil
	}
	// runUntilCompleted drives the supervised native clock, heartbeating it
	// and restarting bounded windows, until the attempt is observed Completed
	// within ticks of game time. The signature carries the game tick (the
	// clock's windows are what must keep moving; the attempt itself has no
	// intermediate progress to observe), so a game that stops ticking stalls
	// and one that keeps ticking without completing spends the tick budget.
	runUntilCompleted := func(label string, attempt map[string]any, ticks uint64) error {
		maxTicks := uint64(6000)
		if _, err := clock.Change(ctx, h, label+"-run", "Fast", &maxTicks); err != nil {
			return err
		}
		polls := 0
		var latest uint64
		tick := func(ctx context.Context) (uint64, error) {
			t, err := h.Tick(ctx)
			latest = t
			return t, err
		}
		err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Ticks: ticks, Tick: tick}, func(ctx context.Context) (string, bool, error) {
			polls++
			if _, err := clock.Poll(ctx, h, fmt.Sprintf("%s-poll-%d", label, polls)); err != nil {
				return "", false, err
			}
			observed, err := progress(fmt.Sprintf("%s-progress-%d", label, polls), attempt)
			if err != nil {
				return "", false, err
			}
			if unsuccessful, ok := na.AsMap(observed["unsuccessful"]); ok {
				return "", false, fmt.Errorf("%s: attempt became unsuccessful before completion: %#v", label, unsuccessful)
			}
			if _, ok := na.AsMap(observed["completed"]); ok {
				return "", true, nil
			}
			if active, _ := na.AsBool(clock.State["active"]); !active {
				clock.AllowResume()
				if _, err := clock.Change(ctx, h, fmt.Sprintf("%s-rerun-%d", label, polls), "Fast", &maxTicks); err != nil {
					return "", false, err
				}
			}
			return na.Signature(latest, clock.State["epoch"]), false, nil
		})
		if err != nil {
			return fmt.Errorf("%s: %w", label, err)
		}
		_, err = clock.Change(ctx, h, label+"-pause", "Paused", nil)
		return err
	}
	spawn := func(label, cells string) ([]string, error) {
		reply, err := h.Call(ctx, label, "test/stone_walls_spawn", map[string]any{"cells": cells, "stuff": chosen.stuff})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(reply["success"]); !success {
			return nil, fmt.Errorf("%s: %#v", label, reply)
		}
		var ids []string
		for _, raw := range na.AsSlice(reply["walls"]) {
			ids = append(ids, na.AsString(raw))
		}
		return ids, nil
	}

	// Refusals before any backup stands: the site is not demolition ready.
	if err := acquire("acquire"); err != nil {
		return err
	}
	generation, err := currentGeneration("generation-early")
	if err != nil {
		return err
	}
	if err := expectFailure("execute-without-backups", buildRequest("wall-early", generation, removeOperation(chosen.wall)), "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	if err := expectFailure("execute-unknown-wall", buildRequest("wall-unknown", generation, removeOperation("Thing_NoSuchWall0")), "FAILURE_CODE_NOT_FOUND"); err != nil {
		return err
	}
	previewReply, err := h.Wire(ctx, "preview-without-backups", "operations_preview", map[string]any{"identity": identity, "operation": removeOperation(chosen.wall)})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(previewReply, "failure"); err != nil {
		return fmt.Errorf("preview-without-backups: expected a refusal: %w", err)
	}

	// Completed backups: the target row now lists them and admits demolition.
	backups, err := spawn("spawn-backups", strings.Join(chosen.cells, ";"))
	if err != nil {
		return err
	}
	if len(backups) != 3 {
		return fmt.Errorf("spawn-backups: expected three backups, got %v", backups)
	}
	report["backups"] = backups
	// The guard admits the original's demolition only while stock covers the
	// permanent wall it will be rebuilt from; the fixture supplies that stock.
	stocked, err := h.Call(ctx, "stock-blocks", "test/wall_material_loss", map[string]any{"material": chosen.stuff, "restore": 60})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(stocked["success"]); !success {
		return fmt.Errorf("stock-blocks: %#v", stocked)
	}
	ready, err := census("ready-census", chosen.wall)
	if err != nil {
		return err
	}
	readyRow, err := rowForNormal(ready, chosen.nx, chosen.nz)
	if err != nil {
		return fmt.Errorf("ready-census: %w", err)
	}
	if n := len(na.AsSlice(readyRow["completedBackups"])); n != 3 || na.AsString(readyRow["blocker"]) != "" {
		return fmt.Errorf("ready-census: expected three completed backups and no blocker, got %#v", readyRow)
	}
	if designated, _ := na.AsBool(readyRow["designated"]); designated {
		return fmt.Errorf("ready-census: wall already designated before dispatch")
	}
	previewReply, err = h.Wire(ctx, "preview-ready", "operations_preview", map[string]any{"identity": identity, "operation": removeOperation(chosen.wall)})
	if err != nil {
		return err
	}
	_, evaluated, err := na.Outcome(previewReply, "evaluated")
	if err != nil {
		return fmt.Errorf("preview-ready: %w", err)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-ready: expected acceptance, got %#v", evaluated)
	}
	afterPreview, err := census("after-preview-census", chosen.wall)
	if err != nil {
		return err
	}
	afterPreviewRow, err := rowForNormal(afterPreview, chosen.nx, chosen.nz)
	if err != nil {
		return err
	}
	if designated, _ := na.AsBool(afterPreviewRow["designated"]); designated {
		return fmt.Errorf("after-preview-census: preview must not designate")
	}

	if _, err := h.Call(ctx, "player-demolition", "test/deconstruct_target", map[string]any{"target": chosen.wall, "action": "replace"}); err != nil {
		return err
	}
	foreign, err := census("foreign-designation", chosen.wall)
	if err != nil {
		return err
	}
	foreignRow, err := rowForNormal(foreign, chosen.nx, chosen.nz)
	if err != nil {
		return err
	}
	if owned, _ := na.AsBool(foreignRow["playerOwned"]); !owned {
		return fmt.Errorf("foreign demolition not observed: %#v", foreignRow)
	}
	unsafeReply, err := h.Wire(ctx, "generic-enclosure-refusal", "operations_preview", map[string]any{
		"identity": identity, "operation": map[string]any{"deconstruct": map[string]any{"target": map[string]any{"entityId": chosen.wall}}}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(unsafeReply, "failure"); err != nil {
		return fmt.Errorf("generic demolition bypassed enclosure guards: %w", err)
	}
	demolishRequest, demolishReceipt, err := execute("execute-adopt-demolition", "wall-demolish", chosen.wall)
	if err != nil {
		return err
	}
	demolishApplied, _ := na.AsMap(demolishReceipt["applied"])
	demolishObserved, _ := na.AsMap(demolishApplied["observed"])
	demolishEffect, _ := na.AsMap(demolishObserved["wall"])
	removalID := na.AsString(demolishEffect["removalId"])
	designatedRows, err := census("designated-census", chosen.wall)
	if err != nil {
		return err
	}
	designatedRow, err := rowForNormal(designatedRows, chosen.nx, chosen.nz)
	if err != nil {
		return err
	}
	if designated, _ := na.AsBool(designatedRow["designated"]); !designated || na.AsString(designatedRow["removalId"]) != removalID {
		return fmt.Errorf("designated-census: expected the ledger's designation %s, got %#v", removalID, designatedRow)
	}
	if owned, _ := na.AsBool(designatedRow["playerOwned"]); owned {
		return fmt.Errorf("designated-census: a ledger designation must not read as player owned")
	}
	replayReply, err := h.Wire(ctx, "replay-demolish", "operations_execute", demolishRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, demolishReceipt) {
		return fmt.Errorf("replay-demolish: replay of the same attempt returned a different receipt")
	}
	demolishAttempt := attemptOf(demolishRequest)
	lookupReply, err := h.Wire(ctx, "lookup-demolish", "receipts_lookup", demolishAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, demolishReceipt) {
		return fmt.Errorf("lookup-demolish: expected the execute receipt, got %#v", lookup)
	}
	pending, err := progress("progress-demolish-pending", demolishAttempt)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(pending["pending"]); !ok {
		return fmt.Errorf("progress-demolish-pending: expected a pending effect, got %#v", pending)
	}
	if err := runUntilCompleted("demolish", demolishAttempt, 2*na.TicksPerDay); err != nil {
		return err
	}
	completed, err := progress("progress-demolish-completed", demolishAttempt)
	if err != nil {
		return err
	}
	completedEffect, _ := na.AsMap(completed["completed"])
	completedEvidence, _ := na.AsMap(completedEffect["evidence"])
	completedWall, _ := na.AsMap(completedEvidence["wall"])
	if observedDemolition, _ := na.AsBool(completedWall["demolitionObserved"]); !observedDemolition || na.AsString(completedWall["removalId"]) != removalID {
		return fmt.Errorf("progress-demolish-completed: expected observed guarded demolition, got %#v", completedWall)
	}
	remaining, err := colonistWalls(ctx, h, scope, "walls-after-demolish")
	if err != nil {
		return err
	}
	if contains(remaining, chosen.wall) {
		return fmt.Errorf("walls-after-demolish: the original wall still stands")
	}
	report["original_demolished"] = true

	// A standing permanent wall turns the backups into cleanup sites.
	permanent, err := spawn("spawn-permanent", fmt.Sprintf("%d,%d", int(chosen.x), int(chosen.z)))
	if err != nil {
		return err
	}
	cleanupRows, err := census("cleanup-census", "")
	if err != nil {
		return err
	}
	cleanupRow, err := rowForNormal(cleanupRows, chosen.nx, chosen.nz)
	if err != nil {
		return fmt.Errorf("cleanup-census: %w", err)
	}
	cleanupTarget, _ := na.AsMap(cleanupRow["target"])
	replacement, _ := na.AsMap(cleanupRow["replacement"])
	replacementBuilding, _ := na.AsMap(replacement["building"])
	firstBackup := na.AsString(cleanupTarget["id"])
	if !contains(backups, firstBackup) || na.AsString(replacementBuilding["id"]) != permanent[0] || len(na.AsSlice(cleanupRow["completedBackups"])) != 3 {
		return fmt.Errorf("cleanup-census: expected a backup target and the permanent replacement, got %#v", cleanupRow)
	}
	cleanupRequest, _, err := execute("execute-cleanup", "wall-cleanup", firstBackup)
	if err != nil {
		return err
	}
	if err := runUntilCompleted("cleanup", attemptOf(cleanupRequest), 2*na.TicksPerDay); err != nil {
		return err
	}
	afterCleanup, err := census("after-cleanup-census", "")
	if err != nil {
		return err
	}
	afterCleanupRow, err := rowForNormal(afterCleanup, chosen.nx, chosen.nz)
	if err != nil {
		return fmt.Errorf("after-cleanup-census: %w", err)
	}
	afterCleanupTarget, _ := na.AsMap(afterCleanupRow["target"])
	secondBackup := na.AsString(afterCleanupTarget["id"])
	if len(na.AsSlice(afterCleanupRow["completedBackups"])) != 2 || secondBackup == firstBackup || !contains(backups, secondBackup) {
		return fmt.Errorf("after-cleanup-census: expected two remaining backups naming the next, got %#v", afterCleanupRow)
	}
	report["backup_demolished"] = firstBackup

	// Release retires a pending guarded removal and drops its designation.
	releasedRequest, _, err := execute("execute-second-cleanup", "wall-second-cleanup", secondBackup)
	if err != nil {
		return err
	}
	if err := acquire("release-acquire"); err != nil {
		return err
	}
	generation, err = currentGeneration("release-generation")
	if err != nil {
		return err
	}
	releaseReply, err := h.Wire(ctx, "execute-release", "operations_execute", buildRequest("wall-release", generation, map[string]any{"releaseWallRemovals": map[string]any{}}))
	if err != nil {
		return err
	}
	_, releaseReceipt, err := na.Outcome(releaseReply, "receipt")
	if err != nil {
		return fmt.Errorf("execute-release: %w", err)
	}
	releaseApplied, _ := na.AsMap(releaseReceipt["applied"])
	releaseObserved, _ := na.AsMap(releaseApplied["observed"])
	releaseEffect, _ := na.AsMap(releaseObserved["wall"])
	if na.AsNumber(releaseEffect["releasedCount"]) != 1 {
		return fmt.Errorf("execute-release: expected one released removal, got %#v", releaseEffect)
	}
	released, err := progress("progress-released", attemptOf(releasedRequest))
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(released["unsuccessful"]); !ok {
		return fmt.Errorf("progress-released: expected an unsuccessful effect after release, got %#v", released)
	}
	afterRelease, err := census("after-release-census", "")
	if err != nil {
		return err
	}
	afterReleaseRow, err := rowForNormal(afterRelease, chosen.nx, chosen.nz)
	if err != nil {
		return fmt.Errorf("after-release-census: %w", err)
	}
	if designated, _ := na.AsBool(afterReleaseRow["designated"]); designated {
		return fmt.Errorf("after-release-census: released designation still stands: %#v", afterReleaseRow)
	}
	report["released"] = secondBackup

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
