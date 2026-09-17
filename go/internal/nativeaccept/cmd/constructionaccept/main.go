// Command constructionaccept proves two NativeConstructionPlan/NativeConstructionRecord
// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeConstruction.cs) code paths
// that had no live-game acceptance before this: an Instant building (WorkToBuild == 0)
// that becomes a completed Building the instant PlaceBuilding is admitted, with no
// Blueprint/Frame stage and no pawn labor at all; and a genuinely in-flight Frame
// replaced by a second admitted PlaceBuilding sharing the same replaceTags
// (NativeConstructionPlan.Place()'s Frame-cancel loop), whose own attempt then reports
// UNSUCCESSFUL_REASON_CANCELLED rather than silently vanishing or fabricating absence.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-construction-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-construction-acceptance"
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
	report := na.NewReport("Real typed PlaceBuilding admission for an Instant (WorkToBuild==0) building "+
		"that completes on admission with no Frame/Blueprint stage or pawn labor, and a genuinely in-flight "+
		"Frame replaced by a second admitted PlaceBuilding sharing the same replaceTags, whose own attempt "+
		"then reports UNSUCCESSFUL_REASON_CANCELLED.", !*rendered)
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

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBeforeReply, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, initial, err := na.Outcome(identityBeforeReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(initial["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	initialContext, _ := na.AsMap(initial["context"])
	identity, _ := na.AsMap(initialContext["identity"])

	// Authority is deliberately NOT acquired yet: construction-prepare and the
	// candidate-cell/preview search below are read-only/fixture calls that need no
	// lease at all, and the real-time-bounded lease (leaseMs is capped at 30000ms,
	// not tick-bounded) must instead be acquired as late as possible, immediately
	// before the first operations_execute that actually depends on it.
	supervisor := &na.ScenarioClock{
		Wire: func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
			return h.Wire(ctx, label, method, request)
		},
		Identity: identity, Owner: na.AsString(na.Owner["controllerSessionId"]), Report: report,
	}

	prepared, err := h.Call(ctx, "construction-prepare", "test/guarded_construction_prepare", map[string]any{"siteCount": 2})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("guarded_construction_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) {
		return fmt.Errorf("guarded_construction_prepare identity does not match the fresh debug game")
	}
	sites := na.AsSlice(prepared["sites"])
	if len(sites) < 2 {
		return fmt.Errorf("expected at least 2 prepared wall sites, found %d", len(sites))
	}
	site0, _ := na.AsMap(sites[0])
	pawnCell, _ := na.AsMap(prepared["pawnCell"])

	// --- Scenario A: Instant construction (PartySpot, WorkToBuild==0). ---
	// PartySpot has no cost and no work; NativeConstructionPlan.Place() takes its
	// `!Instant` branch's opposite: ThingMaker.MakeThing + GenSpawn.Spawn directly,
	// so the very first admitted attempt must observe a completed Building with no
	// intervening Blueprint/Frame stage and no pawn/tick involvement at all.
	instantCell, err := findSite(ctx, h, identity, pawnCell, "PartySpot", "", site0)
	if err != nil {
		return fmt.Errorf("instant site: %w", err)
	}
	if err := renewOrAcquire(ctx, supervisor, "instant"); err != nil {
		return fmt.Errorf("authority before instant-place: %w", err)
	}
	instantRequest := placeRequest(identity, supervisor.Grant, 1, "PartySpot", "", instantCell)
	instantReply, err := h.Wire(ctx, "instant-place", "operations_execute", instantRequest)
	if err != nil {
		return err
	}
	_, instantReceipt, err := na.Outcome(instantReply, "receipt")
	if err != nil {
		return err
	}
	instantEffect, err := constructionEffect(instantReceipt, "applied")
	if err != nil {
		return fmt.Errorf("instant-place: %w", err)
	}
	if na.AsString(instantEffect["stage"]) != "CONSTRUCTION_STAGE_BUILDING" {
		return fmt.Errorf("instant-place: expected an immediate Building stage, got %#v", instantEffect)
	}
	if present, _ := na.AsBool(instantEffect["present"]); !present {
		return fmt.Errorf("instant-place: expected present=true")
	}
	if started, _ := na.AsBool(instantEffect["started"]); !started {
		return fmt.Errorf("instant-place: expected started=true")
	}
	if failed, _ := na.AsBool(instantEffect["failed"]); failed {
		return fmt.Errorf("instant-place: expected failed=false")
	}
	if na.AsString(instantEffect["originThingId"]) == "" || instantEffect["originThingId"] != instantEffect["currentThingId"] {
		return fmt.Errorf("instant-place: expected the origin and current thing id to be identical: %#v", instantEffect)
	}
	if len(na.AsSlice(instantEffect["cancelledFrameIds"])) != 0 || len(na.AsSlice(instantEffect["wipedThingIds"])) != 0 {
		return fmt.Errorf("instant-place: expected no cancelled/wiped ids on an empty site: %#v", instantEffect)
	}

	instantAttempt := attemptRef(identity, instantRequest)
	instantProgressReply, err := h.Wire(ctx, "instant-observe", "receipts_observe_progress", instantAttempt)
	if err != nil {
		return err
	}
	_, instantProgress, err := na.Outcome(instantProgressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(instantProgress["completeInspection"]); !complete {
		return fmt.Errorf("instant-observe: expected a complete inspection")
	}
	if _, ok := instantProgress["completed"]; !ok {
		return fmt.Errorf("instant-observe: expected an immediate completed outcome, got %#v", instantProgress)
	}

	instantReplayReply, err := h.Wire(ctx, "instant-replay", "operations_execute", instantRequest)
	if err != nil {
		return err
	}
	_, instantReplay, err := na.Outcome(instantReplayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(instantReplay, instantReceipt) {
		return fmt.Errorf("instant-replay: replay of the same attempt returned a different receipt")
	}

	// --- Scenario B: replacement construction (Frame-cancel via replaceTags). ---
	// Placing an ordinary WoodLog Wall blueprint takes the Instant==false branch:
	// GenConstruct.PlaceBlueprintForBuild returns a Blueprint, not yet a Frame. The
	// prepared pawn/wood/priorities/schedule let genuine ordinary colony AI carry
	// it from Blueprint to Frame once ticks run; a second admitted PlaceBuilding at
	// the same cell must then legitimately cancel that in-flight Frame (Wall's own
	// replaceTags), and the first attempt's own progress must report the exact
	// cancellation, not an ambiguous unknown or fabricated absence.
	if err := renewOrAcquire(ctx, supervisor, "wall"); err != nil {
		return fmt.Errorf("authority before wall-place: %w", err)
	}
	wallRequest := placeRequest(identity, supervisor.Grant, 2, na.AsString(site0["defName"]), na.AsString(site0["stuff"]), site0)
	wallReply, err := h.Wire(ctx, "wall-place", "operations_execute", wallRequest)
	if err != nil {
		return err
	}
	_, wallReceipt, err := na.Outcome(wallReply, "receipt")
	if err != nil {
		return err
	}
	wallEffect, err := constructionEffect(wallReceipt, "applied")
	if err != nil {
		return fmt.Errorf("wall-place: %w", err)
	}
	if na.AsString(wallEffect["stage"]) != "CONSTRUCTION_STAGE_BLUEPRINT" {
		return fmt.Errorf("wall-place: expected an initial Blueprint stage, got %#v", wallEffect)
	}
	wallAttempt := attemptRef(identity, wallRequest)

	if err := renewOrAcquire(ctx, supervisor, "frame-wait-start"); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names}

	// A lone prepared colonist with reachable wood finishes an ordinary Wall
	// (small WorkToBuild, single stack of WoodLog) well inside a few hundred
	// ticks once under way, so windows must be short enough to actually observe
	// the Frame stage in between Blueprint and a completed Building rather than
	// skipping over it entirely between two once-per-window checks.
	var frameThingID string
	const windowTicks = 50
	const maxWindows = 200
	for window := 0; window < maxWindows; window++ {
		if _, err := na.AdvanceGame(ctx, rt, windowTicks, na.WithTimeout(180*time.Second)); err != nil {
			return fmt.Errorf("frame-wait window %d: %w", window, err)
		}
		progressReply, err := h.Wire(ctx, fmt.Sprintf("frame-wait-%d", window), "receipts_observe_progress", wallAttempt)
		if err != nil {
			return err
		}
		_, progress, err := na.Outcome(progressReply, "progress")
		if err != nil {
			return err
		}
		if complete, _ := na.AsBool(progress["completeInspection"]); complete {
			if _, ok := progress["completed"]; ok {
				return fmt.Errorf("frame-wait window %d: the prepared wall finished building before a Frame could be observed", window)
			}
			pending, ok := na.AsMap(progress["pending"])
			if !ok {
				return fmt.Errorf("frame-wait window %d: expected a pending outcome, got %#v", window, progress)
			}
			evidence, _ := na.AsMap(pending["evidence"])
			effect, _ := na.AsMap(evidence["construction"])
			if na.AsString(effect["stage"]) == "CONSTRUCTION_STAGE_FRAME" {
				frameThingID = na.AsString(effect["currentThingId"])
				break
			}
		}
	}
	if frameThingID == "" {
		return fmt.Errorf("the prepared wall never reached a Frame stage within %d ticks", windowTicks*maxWindows)
	}

	if err := renewOrAcquire(ctx, supervisor, "replacement"); err != nil {
		return fmt.Errorf("authority before replacement-place: %w", err)
	}
	// The replacement must be a *different* def sharing Wall's own replaceTags
	// ("Wall"), not another Wall/WoodLog blueprint: GenConstruct.CanPlaceBlueprintAt
	// refuses an identical thing at an occupied cell before NativeConstructionPlan's
	// own replaceTags-driven Frame-cancel loop ever runs, so placing the exact same
	// def+stuff here would only ever observe "Identical thing already exists here.",
	// never the cancellation this scenario is proving. Vanilla Fence (stuffCategories
	// include Woody, so WoodLog still works) shares replaceTags=[Wall] with Wall and
	// is a genuinely different def, matching the real in-game case of replacing an
	// in-flight Wall with something else built in its place (e.g. a fence).
	replacementRequest := placeRequest(identity, supervisor.Grant, 3, "Fence", "WoodLog", site0)
	replacementPreviewReply, err := h.Wire(ctx, "replacement-preview", "operations_preview", map[string]any{
		"identity": identity, "operation": replacementRequest["operation"],
	})
	if err != nil {
		return err
	}
	evaluated, ok := na.AsMap(replacementPreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("replacement-preview: expected an evaluated reply, got %#v", replacementPreviewReply)
	}
	if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
		return fmt.Errorf("replacement-preview: expected the replacement to be accepted, got %#v", evaluated)
	}
	placementEvaluation, _ := na.AsMap(evaluated["placement"])
	rotations := na.AsSlice(placementEvaluation["rotations"])
	if len(rotations) != 1 {
		return fmt.Errorf("replacement-preview: expected exactly one evaluated rotation, got %#v", rotations)
	}
	rotationRow, _ := na.AsMap(rotations[0])
	if accepted, _ := na.AsBool(rotationRow["accepted"]); !accepted {
		return fmt.Errorf("replacement-preview: expected the evaluated rotation to be accepted, got %#v", rotationRow)
	}
	foundCancelSignal := false
	for _, raw := range na.AsSlice(rotationRow["blockingThings"]) {
		blocker, _ := na.AsMap(raw)
		isFrame, _ := na.AsBool(blocker["isFrame"])
		wouldCancel, _ := na.AsBool(blocker["frameWouldBeCancelled"])
		if isFrame && wouldCancel {
			foundCancelSignal = true
		}
	}
	if !foundCancelSignal {
		return fmt.Errorf("replacement-preview: expected the dry-run to predict the in-flight Frame's cancellation, got %#v", rotationRow)
	}

	replacementReply, err := h.Wire(ctx, "replacement-place", "operations_execute", replacementRequest)
	if err != nil {
		return err
	}
	_, replacementReceipt, err := na.Outcome(replacementReply, "receipt")
	if err != nil {
		return err
	}
	replacementEffect, err := constructionEffect(replacementReceipt, "applied")
	if err != nil {
		return fmt.Errorf("replacement-place: %w", err)
	}
	if na.AsString(replacementEffect["stage"]) != "CONSTRUCTION_STAGE_BLUEPRINT" {
		return fmt.Errorf("replacement-place: expected a fresh Blueprint stage, got %#v", replacementEffect)
	}
	cancelledIDs := na.AsSlice(replacementEffect["cancelledFrameIds"])
	foundFrame := false
	for _, raw := range cancelledIDs {
		if fmt.Sprint(raw) == frameThingID {
			foundFrame = true
		}
	}
	if !foundFrame {
		return fmt.Errorf("replacement-place: expected cancelledFrameIds to include the prior in-flight Frame %q, got %#v", frameThingID, cancelledIDs)
	}
	replacementAttempt := attemptRef(identity, replacementRequest)

	cancelledProgressReply, err := h.Wire(ctx, "wall-cancelled-observe", "receipts_observe_progress", wallAttempt)
	if err != nil {
		return err
	}
	_, cancelledProgress, err := na.Outcome(cancelledProgressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(cancelledProgress["completeInspection"]); !complete {
		return fmt.Errorf("wall-cancelled-observe: expected a complete inspection")
	}
	unsuccessful, ok := na.AsMap(cancelledProgress["unsuccessful"])
	if !ok {
		return fmt.Errorf("wall-cancelled-observe: expected an unsuccessful outcome, got %#v", cancelledProgress)
	}
	if na.AsString(unsuccessful["reason"]) != "UNSUCCESSFUL_REASON_CANCELLED" {
		return fmt.Errorf("wall-cancelled-observe: expected UNSUCCESSFUL_REASON_CANCELLED, got %#v", unsuccessful)
	}
	cancelledEvidence, _ := na.AsMap(unsuccessful["evidence"])
	cancelledEffect, _ := na.AsMap(cancelledEvidence["construction"])
	if na.AsString(cancelledEffect["stage"]) != "CONSTRUCTION_STAGE_CANCELLED" {
		return fmt.Errorf("wall-cancelled-observe: expected a Cancelled stage in the evidence, got %#v", cancelledEffect)
	}
	if present, _ := na.AsBool(cancelledEffect["present"]); present {
		return fmt.Errorf("wall-cancelled-observe: expected present=false")
	}

	replacementProgressReply, err := h.Wire(ctx, "replacement-observe", "receipts_observe_progress", replacementAttempt)
	if err != nil {
		return err
	}
	_, replacementProgress, err := na.Outcome(replacementProgressReply, "progress")
	if err != nil {
		return err
	}
	replacementPending, ok := na.AsMap(replacementProgress["pending"])
	if !ok {
		return fmt.Errorf("replacement-observe: expected a pending outcome, got %#v", replacementProgress)
	}
	replacementPendingEvidence, _ := na.AsMap(replacementPending["evidence"])
	replacementPendingEffect, _ := na.AsMap(replacementPendingEvidence["construction"])
	if present, _ := na.AsBool(replacementPendingEffect["present"]); !present {
		return fmt.Errorf("replacement-observe: expected present=true for the fresh replacing blueprint")
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, final, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(final["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed")
	}
	finalContext, _ := na.AsMap(final["context"])
	if !na.DeepEqual(finalContext["identity"], identity) {
		return fmt.Errorf("identity changed during the run")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	report["instant_cell"] = instantCell
	report["wall_site"] = site0
	report["frame_thing_id"] = frameThingID
	report["final_context"] = finalContext
	return nil
}

// renewOrAcquire refreshes supervisor's authority grant immediately before an
// operations_execute that needs it. It prefers Renew (cheap, keeps the same
// lease) but falls back to a fresh Acquire whenever the prior lease is no
// longer renewable — most notably after NativeControlAuthority's own
// wall-clock (not tick-bound) leaseMs deadline has already lapsed between
// native round-trips, which unconditionally revokes the lease and advances
// the generation (NativeControlAuthority.Invalidate/Advance), independent of
// game-tick time. A lapsed lease is exactly the condition under which a
// fresh Acquire is legal again (the server-side lease is already nil), so
// this fallback is always safe to attempt after any Renew failure.
func renewOrAcquire(ctx context.Context, supervisor *na.ScenarioClock, label string) error {
	if supervisor.Grant != nil {
		if err := supervisor.RenewAuthority(ctx); err == nil {
			return nil
		}
	}
	_, err := supervisor.Acquire(ctx, label+"-acquire")
	return err
}

// findSite scans a bounded grid of cells around origin and returns the first one
// operations_preview accepts for an admission-free (no stuff) or stuffed PlaceBuilding
// of defName, skipping any cell equal to one of exclude.
func findSite(ctx context.Context, h *na.Harness, identity, origin map[string]any, defName, stuff string, exclude ...map[string]any) (map[string]any, error) {
	originX, originZ := na.AsNumber(origin["x"]), na.AsNumber(origin["z"])
	var exactCells []any
	for dx := -8; dx <= 8; dx++ {
		for dz := -8; dz <= 8; dz++ {
			distance := dx*dx + dz*dz
			if distance < 1 || distance > 64 {
				continue
			}
			x, z := originX+float64(dx), originZ+float64(dz)
			if x < 0 || z < 0 {
				continue
			}
			exactCells = append(exactCells, map[string]any{"x": x, "z": z})
		}
	}
	// Only terrain, roof, visibility and traversal cell fields are implemented by
	// observations_get_cells; occupancy is instead left to operations_preview below,
	// which already evaluates real placement legality (including blocking things).
	fields := map[string]any{
		"terrain": true, "traversal": true, "visibility": true,
		"roof": false, "zone": false, "areas": false, "designations": false, "room": false, "growth": false,
	}
	reply, err := h.Wire(ctx, "candidate-cells-"+defName, "observations_get_cells", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "exactCells": map[string]any{"cells": exactCells}, "fields": fields,
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	excluded := func(cell map[string]any) bool {
		for _, ex := range exclude {
			if na.AsNumber(cell["x"]) == na.AsNumber(ex["x"]) && na.AsNumber(cell["z"]) == na.AsNumber(ex["z"]) {
				return true
			}
		}
		return false
	}
	number := 0
	for _, raw := range na.AsSlice(observed["cells"]) {
		row, _ := na.AsMap(raw)
		cell, _ := na.AsMap(row["cell"])
		walkable, _ := na.AsBool(row["walkable"])
		fogged, _ := na.AsBool(row["fogged"])
		if !walkable || fogged || excluded(cell) {
			continue
		}
		number++
		placement := map[string]any{"defName": defName, "x": cell["x"], "z": cell["z"], "rotation": "ROTATION_NORTH"}
		if stuff != "" {
			placement["stuff"] = stuff
		}
		previewReply, err := h.Wire(ctx, fmt.Sprintf("preview-%s-%d", defName, number), "operations_preview", map[string]any{
			"identity": identity, "operation": map[string]any{"placeBuilding": map[string]any{"placement": placement}},
		})
		if err != nil {
			return nil, err
		}
		evaluated, ok := na.AsMap(previewReply["evaluated"])
		if !ok {
			continue
		}
		if accepted, _ := na.AsBool(evaluated["accepted"]); accepted {
			return cell, nil
		}
	}
	return nil, fmt.Errorf("no legal %s site found near the prepared pawn", defName)
}

// placeRequest builds an operations_execute placeBuilding request under grant's lease.
func placeRequest(identity, grant map[string]any, number int, defName, stuff string, cell map[string]any) map[string]any {
	request := na.ExecuteRequest(identity, grant, nil, number)
	placement := map[string]any{"defName": defName, "x": cell["x"], "z": cell["z"], "rotation": "ROTATION_NORTH"}
	if stuff != "" {
		placement["stuff"] = stuff
	}
	request["operation"] = map[string]any{"placeBuilding": map[string]any{"placement": placement}}
	return request
}

// attemptRef builds a receipts_observe_progress request for request's own attempt.
func attemptRef(identity, request map[string]any) map[string]any {
	precondition, _ := na.AsMap(request["precondition"])
	return map[string]any{"identity": identity, "attempt": precondition["attempt"]}
}

// constructionEffect asserts receipt's named case (default "applied") carries a
// ConstructionEffect and returns it.
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
