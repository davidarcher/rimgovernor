// Command mapscopeaccept is the native-acceptance run for #35 M2: typed reads
// and operations are scoped to the map their identity names, not to whichever
// map the player is viewing. A fixture-generated second player map makes the
// distinction observable. Requires a build with -Fixture MapScopeFixture for
// test/map_scope_generate and test/map_scope_view.
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
	output := flag.String("output", "", "fresh output directory (default <root>/native-map-scope-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-map-scope-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Two loaded player maps: a status read for the second map's identity answers about that map while the first is viewed; switching the viewed map invalidates authority (IdentityChanged, generation+1) but reads for either identity still resolve their own map, the roster spans both maps with map_id, presentation reads bound to the viewed map refuse the other identity as stale, an identity naming an unloaded map is stale with the viewed context, and after switching back a draft operation bound to the first map is granted and lands there.", !*rendered)
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
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 90*time.Second)
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
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/observations_read_status",
		"rimgovernor/observations_list_pawns", "rimgovernor/presentation_colonists", "rimgovernor/presentation_selection",
		"rimgovernor/operations_execute", "test/map_scope_generate", "test/map_scope_view"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", tool)
		}
	}
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
	home, _ := na.AsMap(loadedContext["identity"])
	homeMap := int(na.AsNumber(home["mapId"]))

	// Setup: Auto granted on the viewed (home) map.
	generation, state, err := authority(ctx, h, "status-fresh", home)
	if err != nil {
		return err
	}
	if _, unavailable := state["unavailable"]; unavailable {
		generation = 1
	}
	granted, err := grant(ctx, h, "grant-home", home, generation)
	if err != nil {
		return err
	}

	// Case 1: a second player map exists; a read for its identity answers
	// about it while the home map stays viewed.
	generated, err := h.Call(ctx, "generate", "test/map_scope_generate", map[string]any{})
	if err != nil {
		return fmt.Errorf("generate: %w", err)
	}
	secondMap := int(na.AsNumber(generated["secondMapId"]))
	if ok, _ := na.AsBool(generated["success"]); !ok || secondMap == homeMap || int(na.AsNumber(generated["viewedMapId"])) != homeMap {
		return fmt.Errorf("generate: expected a distinct second map with home still viewed, got %v", generated)
	}
	second := withMap(home, secondMap)
	report["setup"] = map[string]any{"homeMapId": homeMap, "secondMapId": secondMap, "generationGranted": granted}
	if err := statusOn(ctx, h, "status-second-while-home-viewed", second, secondMap); err != nil {
		return err
	}
	if err := statusOn(ctx, h, "status-home-while-home-viewed", home, homeMap); err != nil {
		return err
	}
	// Map generation may flip the viewed map and back; that is the player's
	// own view change as far as authority is concerned. Measure from here.
	beforeSwitch, state, err := authority(ctx, h, "status-after-generate", home)
	if err != nil {
		return err
	}
	report["case_generate"] = map[string]any{"state": state, "generation": beforeSwitch}

	// Case 2: switching the viewed map invalidates authority as IdentityChanged
	// at generation+1; reads for either identity still resolve their own map.
	viewed, err := h.Call(ctx, "view-second", "test/map_scope_view", map[string]any{"mapId": secondMap})
	if err != nil {
		return fmt.Errorf("view-second: %w", err)
	}
	if int(na.AsNumber(viewed["viewedMapId"])) != secondMap {
		return fmt.Errorf("view-second: expected map %d viewed, got %v", secondMap, viewed)
	}
	afterSwitch, state, err := authority(ctx, h, "status-after-switch", home)
	if err != nil {
		return err
	}
	inactive, ok := na.AsMap(state["inactive"])
	if !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_IDENTITY_CHANGED" {
		return fmt.Errorf("status-after-switch: expected inactive IDENTITY_CHANGED, got %v", state)
	}
	if afterSwitch != beforeSwitch+1 {
		return fmt.Errorf("status-after-switch: expected generation %d, got %d", beforeSwitch+1, afterSwitch)
	}
	if err := statusOn(ctx, h, "status-home-while-second-viewed", home, homeMap); err != nil {
		return err
	}
	if err := statusOn(ctx, h, "status-second-while-second-viewed", second, secondMap); err != nil {
		return err
	}
	report["case_switch"] = map[string]any{"state": state, "generation": afterSwitch}

	// Case 3: the roster spans both maps and carries map_id; the home map's
	// colonists are all on the home map, and none is claimed on the second.
	rosterReply, err := h.Wire(ctx, "roster", "presentation_colonists", map[string]any{"identity": home})
	if err != nil {
		return err
	}
	_, roster, err := na.Outcome(rosterReply, "roster")
	if err != nil {
		return fmt.Errorf("roster: expected a roster outcome: %w", err)
	}
	colonists := na.AsSlice(roster["colonists"])
	if len(colonists) == 0 {
		return fmt.Errorf("roster: no colonists")
	}
	firstColonist := ""
	for _, row := range colonists {
		m, _ := na.AsMap(row)
		if int(na.AsNumber(m["mapId"])) != homeMap {
			return fmt.Errorf("roster: colonist %v is not on the home map", m)
		}
		if firstColonist == "" {
			firstColonist = na.AsString(m["pawnId"])
		}
	}
	narrowReply, err := h.Wire(ctx, "roster-second-only", "presentation_colonists", map[string]any{"identity": second, "currentMapOnly": true})
	if err != nil {
		return err
	}
	_, narrow, err := na.Outcome(narrowReply, "roster")
	if err != nil {
		return fmt.Errorf("roster-second-only: expected a roster outcome: %w", err)
	}
	if n := len(na.AsSlice(narrow["colonists"])); n != 0 {
		return fmt.Errorf("roster-second-only: expected no colonists on the generated map, got %d", n)
	}
	report["case_roster"] = map[string]any{"colonists": len(colonists), "secondOnly": 0}

	// Case 4: presentation state lives on the viewed map; the home identity
	// is stale there, with the viewed map's context, while the second is not.
	selectionReply, err := h.Wire(ctx, "selection-home-while-second-viewed", "presentation_selection", map[string]any{"identity": home})
	if err != nil {
		return err
	}
	if code, failed := na.FailureCode(selectionReply); !failed || code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("selection-home-while-second-viewed: expected STALE_IDENTITY, got %v", selectionReply)
	}
	staleFailure, _ := na.AsMap(selectionReply["failure"])
	observed, _ := na.AsMap(staleFailure["observedContext"])
	observedIdentity, _ := na.AsMap(observed["identity"])
	if int(na.AsNumber(observedIdentity["mapId"])) != secondMap {
		return fmt.Errorf("selection-home-while-second-viewed: expected observed map %d, got %v", secondMap, observed)
	}
	selectionSecond, err := h.Wire(ctx, "selection-second-while-second-viewed", "presentation_selection", map[string]any{"identity": second})
	if err != nil {
		return err
	}
	// Identity accepted: a selection snapshot when rendered, and headless the
	// graphical-only refusal, which comes after the identity check.
	if _, _, err := na.Outcome(selectionSecond, "selection"); err != nil {
		if code, failed := na.FailureCode(selectionSecond); !headless || !failed || code != "FAILURE_CODE_UNAVAILABLE" {
			return fmt.Errorf("selection-second-while-second-viewed: %w", err)
		}
	}

	// Case 5: an identity naming a map that is not loaded is stale, carrying
	// the viewed map's context so the caller can re-anchor.
	goneReply, err := h.Wire(ctx, "status-unloaded-map", "observations_read_status", map[string]any{
		"scope": map[string]any{"expectedIdentity": withMap(home, 987654)},
	})
	if err != nil {
		return err
	}
	if code, failed := na.FailureCode(goneReply); !failed || code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("status-unloaded-map: expected STALE_IDENTITY, got %v", goneReply)
	}
	goneFailure, _ := na.AsMap(goneReply["failure"])
	goneObserved, _ := na.AsMap(goneFailure["observedContext"])
	goneIdentity, _ := na.AsMap(goneObserved["identity"])
	if int(na.AsNumber(goneIdentity["mapId"])) != secondMap {
		return fmt.Errorf("status-unloaded-map: expected observed map %d, got %v", secondMap, goneObserved)
	}

	// Case 6: back on the home map, a fresh grant admits a draft bound to the
	// home identity and it lands on the home map's colonist.
	if _, err := h.Call(ctx, "view-home", "test/map_scope_view", map[string]any{"mapId": homeMap}); err != nil {
		return fmt.Errorf("view-home: %w", err)
	}
	backGeneration, _, err := authority(ctx, h, "status-back-home", home)
	if err != nil {
		return err
	}
	regranted, err := grant(ctx, h, "regrant-home", home, backGeneration)
	if err != nil {
		return err
	}
	pawnReply, err := h.Wire(ctx, "pawn-before", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": home}, "filter": map[string]any{"colonist": true, "downed": false, "ids": []any{firstColonist}},
	})
	if err != nil {
		return err
	}
	before, err := na.PawnRow(pawnReply, home, firstColonist)
	if err != nil {
		return fmt.Errorf("pawn-before: %w", err)
	}
	draftReply, err := h.Wire(ctx, "draft", "operations_execute", map[string]any{
		"precondition": map[string]any{
			"identity": home, "expectedGeneration": fmt.Sprint(regranted),
			"attempt": map[string]any{"controllerSessionId": na.Owner["controllerSessionId"], "actionId": "mapscope-draft-1", "attemptId": "1"},
		},
		"operation": map[string]any{"setDrafted": map[string]any{"pawn": na.Target(before), "drafted": true, "allowPersistentDraft": false}},
	})
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(draftReply, "receipt")
	if err != nil {
		return fmt.Errorf("draft: expected a receipt: %w", err)
	}
	afterReply, err := h.Wire(ctx, "pawn-after", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": home}, "filter": map[string]any{"colonist": true, "downed": false, "ids": []any{firstColonist}},
	})
	if err != nil {
		return err
	}
	applied, _ := na.AsMap(receipt["applied"])
	appliedObserved, _ := na.AsMap(applied["observed"])
	job, _ := na.AsMap(appliedObserved["job"])
	if verified, _ := na.AsBool(job["verified"]); !verified || na.AsString(job["pawnId"]) != firstColonist {
		return fmt.Errorf("draft: expected a verified draft of %s, got %v", firstColonist, receipt)
	}
	admitted, _ := na.AsMap(receipt["admittedContext"])
	admittedIdentity, _ := na.AsMap(admitted["identity"])
	if int(na.AsNumber(admittedIdentity["mapId"])) != homeMap {
		return fmt.Errorf("draft: admitted on map %v, expected %d", admittedIdentity["mapId"], homeMap)
	}
	_, afterObserved, err := na.Outcome(afterReply, "observed")
	if err != nil {
		return fmt.Errorf("pawn-after: %w", err)
	}
	afterRows := na.AsSlice(afterObserved["pawns"])
	if len(afterRows) != 1 {
		return fmt.Errorf("pawn-after: expected one row, got %d", len(afterRows))
	}
	after, _ := na.AsMap(afterRows[0])
	afterPawn, _ := na.AsMap(after["pawn"])
	afterSnapshot, _ := na.AsMap(afterPawn["snapshot"])
	if drafted, _ := na.AsBool(after["drafted"]); !drafted || int(na.AsNumber(afterPawn["mapId"])) != homeMap ||
		na.AsString(afterSnapshot["token"]) != na.AsString(job["resultingSnapshotToken"]) {
		return fmt.Errorf("draft: expected %s drafted on map %d with the receipt's snapshot token, got %v", firstColonist, homeMap, after)
	}
	report["case_draft_home"] = map[string]any{"generation": regranted, "pawnId": firstColonist}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

func withMap(identity map[string]any, mapID int) map[string]any {
	copy := map[string]any{}
	for k, v := range identity {
		copy[k] = v
	}
	copy["mapId"] = mapID
	return copy
}

// statusOn asserts observations_read_status for identity answers with that
// identity's map in its context, whichever map is viewed.
func statusOn(ctx context.Context, h *na.Harness, label string, identity map[string]any, mapID int) error {
	reply, err := h.Wire(ctx, label, "observations_read_status", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "colonists": false, "threats": false,
	})
	if err != nil {
		return fmt.Errorf("%s: %w", label, err)
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return fmt.Errorf("%s: expected an observed outcome: %w", label, err)
	}
	context, _ := na.AsMap(observed["context"])
	contextIdentity, _ := na.AsMap(context["identity"])
	if int(na.AsNumber(contextIdentity["mapId"])) != mapID {
		return fmt.Errorf("%s: expected context map %d, got %v", label, mapID, context)
	}
	return nil
}

func authority(ctx context.Context, h *na.Harness, label string, identity map[string]any) (uint64, map[string]any, error) {
	reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return 0, nil, fmt.Errorf("%s: %w", label, err)
	}
	_, status, err := na.Outcome(reply, "status")
	if err != nil {
		return 0, nil, fmt.Errorf("%s: expected a status outcome: %w", label, err)
	}
	context, _ := na.AsMap(status["context"])
	generation := uint64(na.AsNumber(context["nativeGeneration"]))
	state := map[string]any{}
	for _, key := range []string{"unavailable", "inactive", "active"} {
		if value, ok := status[key]; ok {
			state[key] = value
		}
	}
	if len(state) != 1 {
		return 0, nil, fmt.Errorf("%s: expected exactly one authority state, got %v", label, status)
	}
	return generation, state, nil
}

func grant(ctx context.Context, h *na.Harness, label string, identity map[string]any, expected uint64) (uint64, error) {
	reply, err := h.Wire(ctx, label, "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": fmt.Sprint(expected), "mode": "MODE_AUTO",
	}})
	if err != nil {
		return 0, fmt.Errorf("%s: %w", label, err)
	}
	_, granted, err := na.Outcome(reply, "granted")
	if err != nil {
		return 0, fmt.Errorf("%s: expected a granted outcome: %w", label, err)
	}
	context, _ := na.AsMap(granted["context"])
	return uint64(na.AsNumber(context["nativeGeneration"])), nil
}
