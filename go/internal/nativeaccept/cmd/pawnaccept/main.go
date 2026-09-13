// Command pawnaccept replaces scripts/native_pawn_acceptance.py: it owns the full
// disposable-worker lifecycle (prepare profile, launch GABS, start/connect the game,
// start a fresh debug game, read typed pawn rows, stop) and asserts the typed pawn
// read facts against the legacy home/list_pawns reads and each other.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-pawn-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 900*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-pawn-acceptance"
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
	report := nativeaccept.NewReport("Fresh native pawn read facts, exact filters, explicit detail presence, bounded refusals and paused identity/tick invariance. Draft-control snapshot/claim read validation only; no pawn operation or health/settings CAS acceptance.", !*rendered)
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

func run(ctx context.Context, root, output, gameID string, headless bool, report nativeaccept.Report) error {
	cfg := &nativeaccept.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	if files, err := nativeaccept.PackageFiles(fmt.Sprint(game["workingDir"])); err == nil {
		report["package_files"] = files
	} else {
		return err
	}
	gabsExecutable, err := nativeaccept.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := nativeaccept.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
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
	h := nativeaccept.NewHarness(client, output)

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !nativeaccept.Contains(names, "rimgovernor/observations_list_pawns") {
		return fmt.Errorf("missing rimgovernor/observations_list_pawns in discovery")
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
	_, loaded, err := nativeaccept.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := nativeaccept.AsBool(loaded["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	loadedContext, _ := loaded["context"].(map[string]any)
	identity, _ := loadedContext["identity"].(map[string]any)
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}

	read := func(label string, fields map[string]any) ([]any, map[string]any, error) {
		request := nativeaccept.Merge(scope, fields)
		reply, err := h.Wire(ctx, label, "observations_list_pawns", request)
		if err != nil {
			return nil, nil, err
		}
		_, observed, err := nativeaccept.Outcome(reply, "observed")
		if err != nil {
			return nil, nil, fmt.Errorf("%s: %w", label, err)
		}
		if observedContext, _ := observed["context"].(map[string]any); observedContext == nil || !nativeaccept.DeepEqual(observedContext, loadedContext) {
			return nil, nil, fmt.Errorf("%s: context drifted mid-run", label)
		}
		rows := nativeaccept.AsSlice(observed["pawns"])
		if err := nativeaccept.CheckCompleteness(observed["completeness"], len(rows)); err != nil {
			return nil, nil, fmt.Errorf("%s: %w", label, err)
		}
		for _, raw := range rows {
			row, _ := nativeaccept.AsMap(raw)
			if err := draftControl(row, observed["context"]); err != nil {
				return nil, nil, fmt.Errorf("%s: %w", label, err)
			}
		}
		return rows, observed, nil
	}

	// The unfiltered, all-details default request (details omitted entirely)
	// asks the native tool to populate every detail family for every pawn on
	// the map, per its documented default ("All detail families default
	// requested" in the rimgovernor/observations_list_pawns tool description,
	// integrations/rimgovernor-native/src/Bridge/Protocol/NativePawnObservationTools.cs:20).
	// Whether that reply exceeds the 1 MiB bounded-read envelope
	// (ProtoBoundary.MaximumEnvelopeBytes,
	// integrations/rimgovernor-native/src/Bridge/Protocol/ProtoBoundary.cs:16,
	// enforced by NativePawnObservationTools.Encode at line 165) depends on this
	// run's randomly generated population, so both an observed reply and a
	// LIMIT_EXCEEDED refusal are valid outcomes here; only reject an unavailable
	// reply for any other reason, which would not be the documented
	// refusal-over-silent-truncation behavior (contracts/native-operation-variants.md).
	defaultReply, err := h.Wire(ctx, "default-pawns", "observations_list_pawns", scope)
	if err != nil {
		return err
	}
	if _, observedPresent, _ := nativeaccept.Outcome(defaultReply, "observed"); observedPresent == nil {
		if reason, ok := nativeaccept.UnavailableReason(defaultReply); !ok || reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
			return fmt.Errorf("default-pawns: expected observed or UNAVAILABLE_REASON_LIMIT_EXCEEDED, got %q", reason)
		}
	}

	// Prove the happy path with a bounded, filtered read over the same
	// population: every detail family explicitly disabled keeps the reply
	// well under the envelope while still exercising the core pawn facts,
	// draft-claim and CAS-snapshot invariants that draftControl checks.
	baseline, _, err := read("default-pawns-bounded", map[string]any{
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(baseline) <= 3 {
		return fmt.Errorf("expected more than 3 fresh pawns, found %d", len(baseline))
	}
	legacy, err := h.Call(ctx, "legacy-pawns", "home/list_pawns", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := nativeaccept.AsBool(legacy["success"]); !success {
		return fmt.Errorf("legacy home/list_pawns refused")
	}
	if err := compareCore(baseline, nativeaccept.AsSlice(legacy["pawns"])); err != nil {
		return err
	}

	colonists, _, err := read("colonists", map[string]any{"filter": map[string]any{"colonist": true, "humanlike": true, "animal": false}})
	if err != nil {
		return err
	}
	if len(colonists) != 3 {
		return fmt.Errorf("expected exactly 3 colonists, found %d", len(colonists))
	}
	oldColonistDetails, err := h.Call(ctx, "legacy-colonist-details", "home/list_pawns", map[string]any{
		"colonistsOnly": true, "health": true, "needs": true, "equipment": true,
		"bio": true, "work": true, "schedule": true, "settings": true, "visibleHediffsOnly": false,
	})
	if err != nil {
		return err
	}
	if err := compareDetails(colonists, nativeaccept.AsSlice(oldColonistDetails["pawns"])); err != nil {
		return err
	}

	firstColonist, _ := nativeaccept.AsMap(colonists[0])
	firstPawn, _ := nativeaccept.AsMap(firstColonist["pawn"])
	target := nativeaccept.AsString(firstPawn["id"])
	exact, _, err := read("exact-id", map[string]any{"filter": map[string]any{"ids": []any{target}, "colonist": true, "animal": false}})
	if err != nil {
		return err
	}
	if len(exact) != 1 {
		return fmt.Errorf("exact id filter did not return exactly one pawn")
	}
	contradictory, _, err := read("contradictory-filter", map[string]any{"filter": map[string]any{"ids": []any{target}, "colonist": false}})
	if err != nil {
		return err
	}
	if len(contradictory) != 0 {
		return fmt.Errorf("contradictory filter unexpectedly matched a pawn")
	}
	unknown, _, err := read("unknown-id", map[string]any{"filter": map[string]any{"ids": []any{"missing-pawn-id"}}})
	if err != nil {
		return err
	}
	if len(unknown) != 0 {
		return fmt.Errorf("unknown id filter unexpectedly matched a pawn")
	}
	// As with default-pawns above, omitting details requests every detail family
	// for every matched pawn; a fresh map's animal population can carry enough
	// hediff/needs/social data to exceed the 1 MiB envelope on its own. Only
	// pawn.snapshot and draftClaim are checked below, neither of which depends on
	// Details, so keep this bounded the same way.
	animals, _, err := read("animals", map[string]any{
		"filter": map[string]any{"colonist": false, "animal": true},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(animals) == 0 {
		return fmt.Errorf("expected at least one animal in a fresh tribal colony")
	}
	for _, raw := range animals {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		if err := RequireSnapshotStrict(pawn["snapshot"]); err != nil {
			return fmt.Errorf("animal row missing exact target snapshot: %w", err)
		}
		claim, _ := nativeaccept.AsMap(row["draftClaim"])
		if _, ok := claim["unowned"]; !ok || len(claim) != 1 {
			return fmt.Errorf("fresh readable animal must have an unowned draft claim")
		}
	}
	disabled, _, err := read("details-disabled", map[string]any{
		"filter": map[string]any{"ids": []any{target}},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(disabled) != 1 {
		return fmt.Errorf("details-disabled did not return exactly one pawn")
	}
	disabledRow, _ := nativeaccept.AsMap(disabled[0])
	for _, section := range []string{"needs", "health", "equipment", "biography", "settings", "social", "animalState"} {
		if _, present := disabledRow[section]; present {
			return fmt.Errorf("disabled section %s unexpectedly present", section)
		}
	}
	// drafted:false,downed:false can match nearly the whole population; only the
	// drafted/downed booleans are checked below, so keep this bounded like
	// default-pawns above rather than risk the same 1 MiB overflow.
	knownFalse, _, err := read("known-false", map[string]any{
		"filter": map[string]any{"drafted": false, "downed": false},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	for _, raw := range knownFalse {
		row, _ := nativeaccept.AsMap(raw)
		drafted, _ := nativeaccept.AsBool(row["drafted"])
		downed, _ := nativeaccept.AsBool(row["downed"])
		if drafted || downed {
			return fmt.Errorf("known-false filter returned a drafted or downed pawn")
		}
	}
	// A page limit smaller than the matched pawn collection is ordinary pagination,
	// not a bounded-read refusal: ListPawns (NativePawnObservationTools.cs) truncates
	// and hands back a cursor (completeness.page.complete=false, populated
	// nextCursor) the same way observations_read_research does; its Require(page.Count,256)
	// call can never fire for a valid request (Validate already bounds page.limit to
	// 1..256, so page.Count never exceeds it), and Encode()'s 1 MiB check does not
	// apply to a single-row reply either. Assert the real truncation behavior
	// instead of the untested unavailable-refusal assumption this test previously
	// carried over from the Python original.
	overflow, err := h.Wire(ctx, "whole-query-limit", "observations_list_pawns", nativeaccept.Merge(scope, map[string]any{"page": map[string]any{"limit": 1}}))
	if err != nil {
		return err
	}
	_, overflowObserved, err := nativeaccept.Outcome(overflow, "observed")
	if err != nil {
		return fmt.Errorf("whole-query-limit: %w", err)
	}
	overflowCompleteness, _ := nativeaccept.AsMap(overflowObserved["completeness"])
	overflowPage, _ := nativeaccept.AsMap(overflowCompleteness["page"])
	if complete, _ := nativeaccept.AsBool(overflowPage["complete"]); complete {
		return fmt.Errorf("whole-query-limit: expected a truncated page for a limit smaller than the matched collection")
	}
	if nativeaccept.AsString(overflowPage["nextCursor"]) == "" {
		return fmt.Errorf("whole-query-limit: truncated page missing a nextCursor")
	}
	if len(nativeaccept.AsSlice(overflowObserved["pawns"])) != 1 {
		return fmt.Errorf("whole-query-limit: expected exactly one pawn row for page limit 1")
	}
	invalidCases := []struct {
		label  string
		change map[string]any
	}{
		{"zero-limit", map[string]any{"page": map[string]any{"limit": 0}}},
		{"duplicate-id", map[string]any{"filter": map[string]any{"ids": []any{target, target}}}},
		{"negative-distance", map[string]any{"filter": map[string]any{"withinColonistDistance": -1}}},
	}
	for _, c := range invalidCases {
		reply, err := h.Wire(ctx, c.label, "observations_list_pawns", nativeaccept.Merge(scope, c.change))
		if err != nil {
			return err
		}
		code, ok := nativeaccept.FailureCode(reply)
		if !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	// A short-but-undecodable cursor passes Validate (only length>4096 is rejected
	// as invalid there, NativePawnObservationTools.cs Validate) and instead fails
	// NativeObservationSnapshot.Cursor.TryDecode inside the handler, which reports
	// UNAVAILABLE_REASON_LIMIT_EXCEEDED ("Pawn cursor is stale or does not match
	// this query") rather than a Failure. Assert the outcome the tool actually
	// produces instead of an invalid-request failure.
	staleCursorReply, err := h.Wire(ctx, "cursor", "observations_list_pawns", nativeaccept.Merge(scope, map[string]any{"page": map[string]any{"cursor": "old"}}))
	if err != nil {
		return err
	}
	if _, observedPresent, _ := nativeaccept.Outcome(staleCursorReply, "observed"); observedPresent != nil {
		return fmt.Errorf("cursor: expected a stale/undecodable cursor to be refused, got observed")
	}
	if reason, ok := nativeaccept.UnavailableReason(staleCursorReply); !ok || reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
		return fmt.Errorf("cursor: expected UNAVAILABLE_REASON_LIMIT_EXCEEDED, got %q", reason)
	}
	staleScope := map[string]any{"scope": map[string]any{"expectedIdentity": nativeaccept.Merge(identity, map[string]any{"loadToken": "stale-load"})}}
	staleReply, err := h.Wire(ctx, "stale-identity", "observations_list_pawns", staleScope)
	if err != nil {
		return err
	}
	if code, ok := nativeaccept.FailureCode(staleReply); !ok || code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("expected FAILURE_CODE_STALE_IDENTITY for a stale load token")
	}
	identityAfter, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, finalLoaded, err := nativeaccept.Outcome(identityAfter, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := nativeaccept.AsBool(finalLoaded["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed during the read pass")
	}
	if finalContext, _ := finalLoaded["context"].(map[string]any); !nativeaccept.DeepEqual(finalContext, loadedContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	// Repeat the exact same bounded request that produced baseline (nil would both
	// risk the 1 MiB overflow again and compare an all-details reply against
	// baseline's all-details-disabled shape, which can never match).
	repeat, _, err := read("repeat-default", map[string]any{
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if !nativeaccept.DeepEqual(repeat, baseline) {
		return fmt.Errorf("repeating the default read returned a different snapshot")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := nativeaccept.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	report["context"] = loadedContext
	report["pawns"] = len(baseline)
	report["colonists"] = len(colonists)
	report["animals"] = len(animals)
	return nil
}

// draftControl validates a pawn row's draft claim/snapshot invariants. Health and
// settings sections now carry their own populated CAS snapshots (contracts/proto/
// observations.proto PawnHealth.snapshot=19, PawnSettings.snapshot=1), and social is
// now a populated PawnSocial block: earlier acceptance runs predated both and
// asserted their absence, which this port corrects rather than preserves.
func draftControl(row map[string]any, context any) error {
	pawn, _ := nativeaccept.AsMap(row["pawn"])
	if err := nativeaccept.RequireIdentifier(pawn["id"]); err != nil {
		return fmt.Errorf("pawn id: %w", err)
	}
	claim, _ := nativeaccept.AsMap(row["draftClaim"])
	if len(claim) != 1 {
		return fmt.Errorf("draftClaim must have exactly one case: %#v", claim)
	}
	if unavailable, ok := nativeaccept.AsMap(claim["unavailable"]); ok {
		if _, present := pawn["snapshot"]; present {
			return fmt.Errorf("unavailable draft claim row unexpectedly carries a pawn snapshot")
		}
		reason := nativeaccept.AsString(unavailable["reason"])
		if reason != "UNAVAILABLE_REASON_NOT_APPLICABLE" && reason != "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING" {
			return fmt.Errorf("unexpected draft-claim unavailable reason %q", reason)
		}
		contextMap, _ := nativeaccept.AsMap(context)
		identity, _ := nativeaccept.AsMap(contextMap["identity"])
		dead, _ := nativeaccept.AsBool(row["dead"])
		animal, _ := nativeaccept.AsBool(row["animal"])
		onCurrentMap := nativeaccept.AsNumber(pawn["mapId"]) == nativeaccept.AsNumber(identity["mapId"])
		if animal && !dead && onCurrentMap && reason != "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING" {
			return fmt.Errorf("a live current-map animal cannot be blanket %q", reason)
		}
		if !nativeaccept.RequireIssueReason(nativeaccept.AsSlice(row["issues"]), "pawn.snapshot", reason) {
			return fmt.Errorf("missing matching pawn.snapshot issue for unavailable draft claim")
		}
	} else {
		snapshot, _ := nativeaccept.AsMap(pawn["snapshot"])
		if err := RequireSnapshotStrict(snapshot); err != nil {
			return fmt.Errorf("draftable pawn missing a populated snapshot: %w", err)
		}
		if nativeaccept.AsString(snapshot["entityId"]) != nativeaccept.AsString(pawn["id"]) {
			return fmt.Errorf("snapshot entityId does not match the row's pawn id")
		}
		if !nativeaccept.DeepEqual(snapshot["context"], context) {
			return fmt.Errorf("snapshot context does not match the read context")
		}
		if owned, ok := nativeaccept.AsMap(claim["owned"]); ok {
			if err := nativeaccept.RequireIdentifier(owned["claimId"]); err != nil {
				return fmt.Errorf("owned draft claim id: %w", err)
			}
			owner, _ := nativeaccept.AsMap(owned["owner"])
			if err := nativeaccept.RequireIdentifier(owner["controllerSessionId"]); err != nil {
				return fmt.Errorf("owned draft claim owner: %w", err)
			}
			if !nativeaccept.DeepEqual(owned["pawnSnapshot"], snapshot) {
				return fmt.Errorf("owned draft claim's pawnSnapshot does not match the row's snapshot")
			}
			if drafted, _ := nativeaccept.AsBool(row["drafted"]); !drafted {
				return fmt.Errorf("owned draft claim requires drafted=true")
			}
		} else if _, ok := claim["unowned"]; !ok {
			return fmt.Errorf("draft claim is neither owned nor unowned: %#v", claim)
		}
	}
	// Both "health" and "settings" carry populated CAS snapshots: PawnSettings.Snapshot
	// is populated unconditionally by ListPawns (NativePawnObservationTools.cs:61-63,
	// NativeWorkSettings.Snapshot), and PawnHealth.Snapshot is now populated by
	// NativePawnDetails.Health via NativeObservationSnapshot.Snapshot (NativePawnDetails.cs).
	for _, section := range []string{"health", "settings"} {
		sectionValue, present := row[section]
		if !present {
			continue
		}
		sectionMap, _ := nativeaccept.AsMap(sectionValue)
		if err := RequireSnapshotStrict(sectionMap["snapshot"]); err != nil {
			return fmt.Errorf("%s section missing a populated CAS snapshot: %w", section, err)
		}
	}
	if _, present := row["social"]; present {
		if err := nativeaccept.RequireSocial(row); err != nil {
			return err
		}
	}
	return nil
}

// RequireSnapshotStrict is RequireSnapshot with a pawn-acceptance-specific message.
func RequireSnapshotStrict(v any) error { return nativeaccept.RequireSnapshot(v) }

func compareCore(typed []any, legacy []any) error {
	byThingID := map[string]map[string]any{}
	for _, raw := range legacy {
		row, _ := nativeaccept.AsMap(raw)
		byThingID[nativeaccept.AsString(row["thingId"])] = row
	}
	seen := map[string]bool{}
	for _, raw := range typed {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		seen[nativeaccept.AsString(pawn["id"])] = true
	}
	if len(seen) != len(byThingID) {
		return fmt.Errorf("typed pawn set does not exactly match legacy home/list_pawns: typed=%d legacy=%d", len(seen), len(byThingID))
	}
	for _, raw := range typed {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		old, ok := byThingID[nativeaccept.AsString(pawn["id"])]
		if !ok {
			return fmt.Errorf("typed pawn %s missing from legacy home/list_pawns", pawn["id"])
		}
		if nativeaccept.AsString(pawn["defName"]) != nativeaccept.AsString(old["defName"]) {
			return fmt.Errorf("defName mismatch for %s", pawn["id"])
		}
		pairs := map[string]string{"colonist": "isColonist", "freeColonist": "isFreeColonist", "prisoner": "isPrisoner"}
		for _, key := range []string{"animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted"} {
			pairs[key] = key
		}
		for newKey, oldKey := range pairs {
			newVal, newOK := nativeaccept.AsBool(row[newKey])
			oldVal, oldOK := nativeaccept.AsBool(old[oldKey])
			if !newOK {
				return fmt.Errorf("%s must be an explicit boolean for pawn %s, found %#v", newKey, pawn["id"], row[newKey])
			}
			if !oldOK || newVal != oldVal {
				return fmt.Errorf("%s mismatch for pawn %s: typed=%v legacy=%#v", newKey, pawn["id"], newVal, old[oldKey])
			}
		}
	}
	return nil
}

func compareDetails(typed []any, legacy []any) error {
	byThingID := map[string]map[string]any{}
	for _, raw := range legacy {
		row, _ := nativeaccept.AsMap(raw)
		byThingID[nativeaccept.AsString(row["thingId"])] = row
	}
	for _, raw := range typed {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		old, ok := byThingID[nativeaccept.AsString(pawn["id"])]
		if !ok {
			return fmt.Errorf("typed colonist %s missing from legacy detail read", pawn["id"])
		}
		health, _ := nativeaccept.AsMap(row["health"])
		oldHealth, _ := nativeaccept.AsMap(old["health"])
		if err := RequireSnapshotStrict(health["snapshot"]); err != nil {
			return fmt.Errorf("health section missing populated snapshot for %s: %w", pawn["id"], err)
		}
		settings, _ := nativeaccept.AsMap(row["settings"])
		if err := RequireSnapshotStrict(settings["snapshot"]); err != nil {
			return fmt.Errorf("settings section missing populated snapshot for %s: %w", pawn["id"], err)
		}
		if needsTend, _ := nativeaccept.AsBool(health["needsTend"]); needsTend != mustBool(oldHealth["needsTend"]) {
			return fmt.Errorf("needsTend mismatch for %s", pawn["id"])
		}
		equipment, _ := nativeaccept.AsMap(row["equipment"])
		oldEquipment, _ := nativeaccept.AsMap(old["equipment"])
		if armed, _ := nativeaccept.AsBool(equipment["armed"]); armed != mustBool(oldEquipment["armed"]) {
			return fmt.Errorf("armed mismatch for %s", pawn["id"])
		}
		if err := nativeaccept.RequireSocial(row); err != nil {
			return fmt.Errorf("colonist %s: %w", pawn["id"], err)
		}
	}
	return nil
}

func mustBool(v any) bool { b, _ := v.(bool); return b }
