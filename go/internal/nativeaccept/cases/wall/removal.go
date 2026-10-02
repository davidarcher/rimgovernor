// The wall/removal case proves the wall_upgrade-guarded Designate on Actions/Apply (#989, #1351)
// on a loaded save: NativeWallRemovalOperations resolves the guarded
// demolition site from the wall at the named cell, refuses a wall without
// completed stone backups, applies the original's demolition once same-stuff
// stone backups stand in its backup cells, and a real supervised native
// deconstruct job clears the wall (the wall gone from the building census,
// which is what Go's dependency gate reads). A standing stone permanent wall
// then makes the backups cleanup sites: one backup is demolished the same
// way. A resent key returns its first result, and a cleared cell applies
// again.
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

// colonistWalls reads the typed building census for every player Wall id.
func colonistWalls(ctx context.Context, h *na.Harness, scope map[string]any, label string) ([]string, error) {
	request := map[string]any{"scope": scope, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true}
	reply, err := h.Wire(ctx, label, "observations_list_buildings", request)
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	var ids []string
	for _, raw := range na.AsSlice(observed["buildings"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		ids = append(ids, na.AsString(building["id"]))
	}
	sort.Strings(ids)
	return ids, nil
}

func init() {
	cases.Register(cases.Case{
		Name: "wall/removal",
		Scope: "Native wall_upgrade-guarded Designate: site resolution from the wall at a cell, refusal without " +
			"backups, guarded demolition of the original and of a backup by real supervised native deconstruct jobs, resend idempotency.",
		Start:  cases.Fixture{Op: "test/lighting_prepare", On: cases.LabStart()},
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
	for _, name := range []string{"rimgovernor/operations_apply", "rimgovernor/observations_list_wall_upgrade_sites", "test/stone_walls_spawn"} {
		if !na.Contains(s.Names(), name) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture LightingFixture,UpkeepFixture", name)
		}
	}
	clock := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: sessionOwner, Report: report}
	if _, err := h.Call(ctx, "initial-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	scope := map[string]any{"expectedIdentity": identity}

	census := func(label, target string) ([]any, error) {
		request := map[string]any{"scope": scope}
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
			cells := na.AsSlice(row["backupCells"])
			if nx != 0 && nz != 0 || na.AsString(row["blocker"]) != "" || len(cells) != 3 || len(na.AsSlice(row["completedBackups"])) != 0 {
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
	// The clock keeps the grant, so its windows start at the same generation.
	acquire := func(label string) error {
		_, err := clock.Acquire(ctx, label)
		return err
	}
	// apply sends one wall_upgrade-guarded Designate for the wall at cell under key and
	// returns its ActionResult.
	apply := func(label, key string, cell map[string]any, expected string) (map[string]any, error) {
		intent := map[string]any{"designation": "THING_DESIGNATION_DECONSTRUCT", "cell": cell, "guard": "DESIGNATION_GUARD_WALL_UPGRADE"}
		if expected != "" {
			intent["target"] = map[string]any{"id": expected}
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "designate": intent}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	expectRefused := func(label, key string, cell map[string]any, expected string, codes ...string) error {
		result, err := apply(label, key, cell, expected)
		if err != nil {
			return err
		}
		refusal, ok := na.AsMap(result["refused"])
		if !ok {
			return fmt.Errorf("%s: expected a refusal, got %#v", label, result)
		}
		code := na.AsString(refusal["code"])
		for _, want := range codes {
			if code == want {
				return nil
			}
		}
		return fmt.Errorf("%s: expected %v, got %q (%s)", label, codes, code, na.AsString(refusal["reason"]))
	}
	// execute applies one guarded removal and checks its applied evidence.
	execute := func(label, key, wall string, cell map[string]any) (map[string]any, error) {
		if err := acquire(label + "-acquire"); err != nil {
			return nil, err
		}
		result, err := apply(label, key, cell, wall)
		if err != nil {
			return nil, err
		}
		applied, ok := na.AsMap(result["applied"])
		if !ok {
			return nil, fmt.Errorf("%s: expected an applied outcome, got %#v", label, result)
		}
		inner, _ := na.AsMap(applied["applied"])
		observed, _ := na.AsMap(inner["observed"])
		effect, _ := na.AsMap(observed["wall"])
		siteEvidence, _ := na.AsMap(effect["site"])
		if na.AsString(effect["targetId"]) != wall || na.AsString(effect["removalId"]) == "" || len(na.AsSlice(effect["workerIds"])) == 0 || na.AsString(siteEvidence["entityId"]) != wall || na.AsString(siteEvidence["beforeToken"]) == "" || na.AsString(siteEvidence["afterToken"]) == "" {
			return nil, fmt.Errorf("%s: unexpected wall effect: %#v", label, effect)
		}
		if observedDemolition, _ := na.AsBool(effect["demolitionObserved"]); observedDemolition {
			return nil, fmt.Errorf("%s: demolition cannot be observed at admission", label)
		}
		return result, nil
	}
	// runUntilGone drives the supervised native clock, heartbeating it and
	// restarting bounded windows, until the building census no longer lists
	// wall within ticks of game time. The signature carries the game tick, so
	// a game that stops ticking stalls and one that keeps ticking without
	// the demolition spends the tick budget.
	runUntilGone := func(label, wall string, ticks uint64) error {
		maxTicks := uint64(6000)
		if _, err := clock.Change(ctx, "Fast", maxTicks); err != nil {
			return err
		}
		var state map[string]any
		polls := 0
		var latest uint64
		tick := func(ctx context.Context) (uint64, error) {
			t, err := h.Tick(ctx)
			latest = t
			return t, err
		}
		err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Ticks: ticks, Tick: tick}, func(ctx context.Context) (string, bool, error) {
			polls++
			var err error
			if state, err = clock.Call(ctx, "status", nil); err != nil {
				return "", false, err
			}
			walls, err := colonistWalls(ctx, h, scope, fmt.Sprintf("%s-walls-%d", label, polls))
			if err != nil {
				return "", false, err
			}
			if !contains(walls, wall) {
				return "", true, nil
			}
			if active, _ := na.AsBool(state["active"]); !active {
				clock.Hold = ""
				if _, err := clock.Change(ctx, "Fast", maxTicks); err != nil {
					return "", false, err
				}
			}
			return na.Signature(latest, state["epoch"]), false, nil
		})
		if err != nil {
			return fmt.Errorf("%s: %w", label, err)
		}
		if active, _ := na.AsBool(state["active"]); active {
			_, err = clock.Call(ctx, "pause", map[string]any{"owner": clock.Owner, "epoch": state["epoch"]})
		}
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
	wallCell := map[string]any{"x": int(chosen.x), "z": int(chosen.z)}
	if err := acquire("acquire"); err != nil {
		return err
	}
	if err := expectRefused("apply-without-backups", "wall-early", wallCell, chosen.wall, "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	if err := expectRefused("apply-other-wall", "wall-other", wallCell, "Thing_NoSuchWall0", "FAILURE_CODE_STALE_IDENTITY"); err != nil {
		return err
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

	if _, err := h.Call(ctx, "standing-demolition", "test/deconstruct_target", map[string]any{"target": chosen.wall, "action": "replace"}); err != nil {
		return err
	}
	standing, err := census("standing-designation", chosen.wall)
	if err != nil {
		return err
	}
	standingRow, err := rowForNormal(standing, chosen.nx, chosen.nz)
	if err != nil {
		return err
	}
	if designated, _ := na.AsBool(standingRow["designated"]); !designated || na.AsString(standingRow["removalId"]) != "" {
		return fmt.Errorf("standing demolition designation not observed: %#v", standingRow)
	}
	unsafeReply, err := h.Wire(ctx, "generic-enclosure-refusal", "operations_apply", map[string]any{
		"identity": identity, "actions": []any{map[string]any{"key": "generic-enclosure-refusal", "deconstruct": map[string]any{"targetId": chosen.wall}}}})
	if err != nil {
		return err
	}
	if results := na.AsSlice(unsafeReply["results"]); len(results) != 1 {
		return fmt.Errorf("generic demolition: expected one result: %#v", unsafeReply)
	} else if result, _ := na.AsMap(results[0]); result["refused"] == nil {
		return fmt.Errorf("generic demolition bypassed enclosure guards: %#v", result)
	}
	demolishResult, err := execute("apply-adopt-demolition", "wall-demolish", chosen.wall, wallCell)
	if err != nil {
		return err
	}
	demolishApplied, _ := na.AsMap(demolishResult["applied"])
	demolishInner, _ := na.AsMap(demolishApplied["applied"])
	demolishObserved, _ := na.AsMap(demolishInner["observed"])
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
	replay, err := apply("replay-demolish", "wall-demolish", wallCell, chosen.wall)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, demolishResult) {
		return fmt.Errorf("replay-demolish: a resent key returned a different result")
	}
	again, err := apply("reapply-demolish", "wall-demolish-again", wallCell, chosen.wall)
	if err != nil {
		return err
	}
	againApplied, _ := na.AsMap(again["applied"])
	againInner, _ := na.AsMap(againApplied["applied"])
	againObserved, _ := na.AsMap(againInner["observed"])
	againEffect, _ := na.AsMap(againObserved["wall"])
	if na.AsString(againEffect["removalId"]) != removalID {
		return fmt.Errorf("reapply-demolish: a new key must apply again on the pending removal, got %#v", again)
	}
	if err := runUntilGone("demolish", chosen.wall, 2*na.TicksPerDay); err != nil {
		return err
	}
	cleared, err := apply("reapply-cleared", "wall-demolish-cleared", wallCell, chosen.wall)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(cleared["applied"]); !ok {
		return fmt.Errorf("reapply-cleared: a cleared cell must apply again, got %#v", cleared)
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
	backupPosition, _ := na.AsMap(cleanupTarget["position"])
	backupCell := map[string]any{"x": int(na.AsNumber(backupPosition["x"])), "z": int(na.AsNumber(backupPosition["z"]))}
	if _, err := execute("apply-cleanup", "wall-cleanup", firstBackup, backupCell); err != nil {
		return err
	}
	if err := runUntilGone("cleanup", firstBackup, 2*na.TicksPerDay); err != nil {
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

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
