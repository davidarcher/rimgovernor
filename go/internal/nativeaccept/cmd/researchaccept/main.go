// Command researchaccept proves the full
// disposable-worker lifecycle plus typed research-project reads compared against
// the legacy home/research getter and a private fingerprint fixture that proves the
// typed read never mutates saved research state.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-research-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-research-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Private read-only research fingerprint fixture; typed read invariance before separate native getter audit. No selection, save or pawn work.", !*rendered)
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
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/observations_read_research") {
		return fmt.Errorf("missing rimgovernor/observations_read_research in discovery")
	}
	hasFingerprint := na.Contains(names, "test/research_observation_fingerprint")
	if !hasFingerprint {
		return fmt.Errorf("missing test/research_observation_fingerprint fixture in discovery")
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(before["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)

	fingerprintBefore, err := h.Call(ctx, "fingerprint-before", "test/research_observation_fingerprint", nil)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(fingerprintBefore["success"]); !success {
		return fmt.Errorf("research fingerprint fixture refused")
	}

	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}
	full := na.Merge(scope, map[string]any{"includeLocked": true, "includeFinished": true, "includeCapability": true})
	fullReply, err := h.Wire(ctx, "full", "observations_read_research", full)
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(fullReply, "observed")
	if err != nil {
		return err
	}
	if _, present := observed["snapshot"]; !present {
		return fmt.Errorf("expected a populated research snapshot on the full read")
	}
	if err := na.RequireSnapshot(observed["snapshot"]); err != nil {
		return fmt.Errorf("research read missing a populated CAS snapshot: %w", err)
	}
	projects := na.AsSlice(observed["projects"])
	if len(projects) <= 1 {
		return fmt.Errorf("expected more than 1 research project")
	}
	repeatReply, err := h.Wire(ctx, "repeat", "observations_read_research", full)
	if err != nil {
		return err
	}
	_, repeatObserved, err := na.Outcome(repeatReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(repeatObserved, observed) {
		return fmt.Errorf("repeating the full read returned a different snapshot")
	}

	defaultsReply, err := h.Wire(ctx, "defaults", "observations_read_research", scope)
	if err != nil {
		return err
	}
	_, defaults, err := na.Outcome(defaultsReply, "observed")
	if err != nil {
		return err
	}
	if len(na.AsSlice(defaults["benches"])) != 0 || len(na.AsSlice(defaults["researchers"])) != 0 {
		return fmt.Errorf("default (unrequested) benches/researchers were unexpectedly populated")
	}

	firstProject, _ := na.AsMap(projects[0])
	firstProjectDef, _ := na.AsMap(firstProject["project"])
	needle := na.AsString(firstProjectDef["defName"])
	filteredReply, err := h.Wire(ctx, "filtered", "observations_read_research", na.Merge(full, map[string]any{"nameContains": needle}))
	if err != nil {
		return err
	}
	_, filtered, err := na.Outcome(filteredReply, "observed")
	if err != nil {
		return err
	}
	if err := na.CheckCompleteness(filtered["completeness"], len(na.AsSlice(filtered["projects"]))); err != nil {
		return fmt.Errorf("filtered completeness: %w", err)
	}

	// Bounded search for a project row with a populated unlocks collection;
	// unlocks are only returned per-row on request.
	var unlockRows []any
	candidates := projects
	for index, raw := range candidates {
		if index >= 16 {
			break
		}
		row, _ := na.AsMap(raw)
		finished, _ := na.AsBool(row["finished"])
		if finished {
			continue
		}
		rowDef, _ := na.AsMap(row["project"])
		reply, err := h.Wire(ctx, fmt.Sprintf("unlocks-%d", index), "observations_read_research",
			na.Merge(full, map[string]any{"nameContains": na.AsString(rowDef["defName"]), "includeUnlocks": true}))
		if err != nil {
			return err
		}
		if reason, ok := na.UnavailableReason(reply); ok {
			if reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
				return fmt.Errorf("unexpected unavailable reason for unlock probe: %s", reason)
			}
			continue
		}
		_, rows, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		candidateRows := na.AsSlice(rows["projects"])
		hasUnlocks := false
		for _, r := range candidateRows {
			rm, _ := na.AsMap(r)
			if len(na.AsSlice(rm["unlocks"])) > 0 {
				hasUnlocks = true
			}
		}
		unlockRows = candidateRows
		if hasUnlocks {
			break
		}
	}
	hasPopulatedUnlocks := false
	for _, raw := range unlockRows {
		row, _ := na.AsMap(raw)
		if len(na.AsSlice(row["unlocks"])) > 0 {
			hasPopulatedUnlocks = true
		}
	}
	if !hasPopulatedUnlocks {
		return fmt.Errorf("no populated bounded unlock collection was verified")
	}

	invalidCases := []struct {
		label  string
		change map[string]any
	}{
		{"limit", map[string]any{"page": map[string]any{"limit": 257}}},
	}
	for _, c := range invalidCases {
		reply, err := h.Wire(ctx, c.label, "observations_read_research", na.Merge(scope, c.change))
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	// A short-but-undecodable cursor passes Validate (NativeResearchObservationTools.cs
	// Validate only rejects cursor length>4096 as invalid) and instead fails
	// NativeObservationSnapshot.Cursor.TryDecode inside the handler, which reports
	// UNAVAILABLE_REASON_LIMIT_EXCEEDED rather than a Failure. Assert the outcome
	// the tool actually produces instead of an invalid-request failure.
	staleCursorReply, err := h.Wire(ctx, "cursor", "observations_read_research", na.Merge(scope, map[string]any{"page": map[string]any{"cursor": "stale"}}))
	if err != nil {
		return err
	}
	if _, observedPresent, _ := na.Outcome(staleCursorReply, "observed"); observedPresent != nil {
		return fmt.Errorf("cursor: expected a stale/undecodable cursor to be refused, got observed")
	}
	if reason, ok := na.UnavailableReason(staleCursorReply); !ok || reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
		return fmt.Errorf("cursor: expected UNAVAILABLE_REASON_LIMIT_EXCEEDED, got %q", reason)
	}
	// A page limit smaller than the matched project collection is ordinary
	// pagination, not a bounded-read refusal: ListResearch (NativeResearchObservationTools.cs)
	// truncates and hands back a cursor (completeness.page.complete=false,
	// populated nextCursor) the same way it does for observations_list_pawns/rooms
	// truncation elsewhere; only a single row's own child collections (Bound(),
	// line 236) or the whole-reply size (1 MiB, line 232) trigger LIMIT_EXCEEDED.
	// An earlier version expected an unavailable refusal that this tool
	// has never actually produced for a small page limit; fixed forward to assert
	// the real truncation behavior instead of preserving the untested assumption.
	overflowReply, err := h.Wire(ctx, "overflow", "observations_read_research", na.Merge(full, map[string]any{"page": map[string]any{"limit": 1}}))
	if err != nil {
		return err
	}
	_, overflowObserved, err := na.Outcome(overflowReply, "observed")
	if err != nil {
		return fmt.Errorf("overflow: %w", err)
	}
	overflowCompleteness, _ := na.AsMap(overflowObserved["completeness"])
	overflowPage, _ := na.AsMap(overflowCompleteness["page"])
	if complete, _ := na.AsBool(overflowPage["complete"]); complete {
		return fmt.Errorf("overflow: expected a truncated page for a limit smaller than the matched collection")
	}
	if na.AsString(overflowPage["nextCursor"]) == "" {
		return fmt.Errorf("overflow: truncated page missing a nextCursor")
	}
	if len(na.AsSlice(overflowObserved["projects"])) != 1 {
		return fmt.Errorf("overflow: expected exactly one project row for page limit 1")
	}

	fingerprintAfter, err := h.Call(ctx, "fingerprint-after", "test/research_observation_fingerprint", nil)
	if err != nil {
		return err
	}
	fpBefore, err := fingerprintState(fingerprintBefore)
	if err != nil {
		return fmt.Errorf("fingerprint-before: %w", err)
	}
	fpAfter, err := fingerprintState(fingerprintAfter)
	if err != nil {
		return fmt.Errorf("fingerprint-after: %w", err)
	}
	if !na.DeepEqual(fpBefore, fpAfter) {
		return fmt.Errorf("typed research read mutated saved research state")
	}
	report["saved_state_unchanged"] = true

	// Separate native getter audit; called only after the invariant proof above.
	legacy, err := h.Call(ctx, "separate-native-getter-audit", "home/research", map[string]any{
		"locked": true, "finished": true, "unlocks": true,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(legacy["success"]); !success {
		return fmt.Errorf("legacy home/research getter refused")
	}
	if applied, _ := na.AsBool(legacy["applied"]); applied {
		return fmt.Errorf("legacy home/research getter unexpectedly applied a mutation")
	}
	if err := compareProjects(observed, legacy); err != nil {
		return err
	}
	if err := compareCapability(observed, legacy); err != nil {
		return err
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, after, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if pausedAfter, _ := na.AsBool(after["paused"]); !pausedAfter {
		return fmt.Errorf("game unexpectedly resumed during the read pass")
	}
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	report["projects"] = len(projects)
	report["researchers"] = len(na.AsSlice(observed["researchers"]))
	report["benches"] = len(na.AsSlice(observed["benches"]))
	return nil
}

func fingerprintState(v map[string]any) (map[string]any, error) {
	if success, ok := na.AsBool(v["success"]); !ok || !success {
		return nil, fmt.Errorf("fixture did not report success=true: %#v", v["success"])
	}
	out := map[string]any{}
	for _, field := range []string{"current", "progress", "knowledge", "slots", "techprints", "tick", "paused"} {
		val, present := v[field]
		if !present {
			return nil, fmt.Errorf("fixture missing declared field %q", field)
		}
		out[field] = val
	}
	return out, nil
}

func compareProjects(observed map[string]any, legacy map[string]any) error {
	rows := na.AsSlice(observed["projects"])
	if err := na.CheckCompleteness(observed["completeness"], len(rows)); err != nil {
		return fmt.Errorf("projects completeness: %w", err)
	}
	native := map[string]map[string]any{}
	for _, key := range []string{"available", "locked"} {
		for _, raw := range na.AsSlice(legacy[key]) {
			row, _ := na.AsMap(raw)
			native[na.AsString(row["defName"])] = row
		}
	}
	finished := map[string]bool{}
	for _, raw := range na.AsSlice(legacy["finished"]) {
		finished[na.AsString(raw)] = true
	}
	seen := map[string]bool{}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		project, _ := na.AsMap(row["project"])
		name := na.AsString(project["defName"])
		seen[name] = true
		isFinished, _ := na.AsBool(row["finished"])
		if isFinished != finished[name] {
			return fmt.Errorf("project %s finished mismatch", name)
		}
		if isFinished {
			canStart, _ := na.AsBool(row["canStart"])
			available, _ := na.AsBool(row["available"])
			if canStart || available {
				return fmt.Errorf("finished project %s must report canStart=false and available=false", name)
			}
			continue
		}
		source, ok := native[name]
		if !ok {
			return fmt.Errorf("project %s missing from legacy home/research getter", name)
		}
		canStart, _ := na.AsBool(row["canStart"])
		if sourceCanStart, _ := na.AsBool(source["canStartNow"]); canStart != sourceCanStart {
			return fmt.Errorf("project %s canStart mismatch", name)
		}
	}
	// The typed project set must exactly account for every legacy available/locked
	// and finished defName -- not merely a subset -- so a finished project the typed
	// read silently dropped (or a phantom typed entry) cannot pass unnoticed.
	for name := range native {
		if !seen[name] {
			return fmt.Errorf("legacy project %s missing from typed read", name)
		}
	}
	for name := range finished {
		if !seen[name] {
			return fmt.Errorf("legacy finished project %s missing from typed read", name)
		}
	}
	return nil
}

func compareCapability(observed map[string]any, legacy map[string]any) error {
	capability, ok := na.AsMap(legacy["researchBenches"])
	if !ok {
		return fmt.Errorf("legacy researchBenches capability missing")
	}
	if readable, _ := na.AsBool(capability["readable"]); !readable {
		return fmt.Errorf("legacy researchBenches capability not readable")
	}
	benches := na.AsSlice(observed["benches"])
	nativeBenches := na.AsSlice(capability["benches"])
	count := na.AsNumber(capability["count"])
	if len(benches) != len(nativeBenches) || float64(len(benches)) != count {
		return fmt.Errorf("bench count mismatch: typed=%d legacy=%d capability.count=%v", len(benches), len(nativeBenches), capability["count"])
	}
	benchNames := make([]string, 0, len(benches))
	for _, raw := range benches {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		def, _ := na.AsMap(building["building"])
		benchNames = append(benchNames, na.AsString(def["defName"]))
	}
	nativeBenchNames := make([]string, 0, len(nativeBenches))
	for _, raw := range nativeBenches {
		row, _ := na.AsMap(raw)
		nativeBenchNames = append(nativeBenchNames, na.AsString(row["defName"]))
	}
	sort.Strings(benchNames)
	sort.Strings(nativeBenchNames)
	if !na.DeepEqual(benchNames, nativeBenchNames) {
		return fmt.Errorf("bench defName sets differ: typed=%v legacy=%v", benchNames, nativeBenchNames)
	}
	researchers := na.AsSlice(observed["researchers"])
	nativeResearchers := na.AsSlice(capability["researchers"])
	if len(researchers) != len(nativeResearchers) || len(researchers) == 0 {
		return fmt.Errorf("researcher count mismatch or empty: typed=%d legacy=%d", len(researchers), len(nativeResearchers))
	}
	byPawnID := map[string]map[string]any{}
	for _, raw := range researchers {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		byPawnID[na.AsString(pawn["id"])] = row
	}
	for _, raw := range nativeResearchers {
		native, _ := na.AsMap(raw)
		// Native GetUniqueLoadID() is Thing_ + the ThingID exposed by home/research.
		id := "Thing_" + na.AsString(native["thingId"])
		row, ok := byPawnID[id]
		if !ok {
			return fmt.Errorf("researcher %s missing from typed read", id)
		}
		for _, field := range []string{"intellectual", "priority", "disabled", "everWork", "active"} {
			if _, present := row[field]; !present {
				return fmt.Errorf("researcher %s missing typed field %q", id, field)
			}
			if !na.DeepEqual(row[field], native[field]) {
				return fmt.Errorf("researcher %s field %q mismatch: typed=%#v legacy=%#v", id, field, row[field], native[field])
			}
		}
	}
	return nil
}
