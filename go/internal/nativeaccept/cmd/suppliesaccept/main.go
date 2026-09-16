// Command suppliesaccept proves the full
// disposable-worker lifecycle plus typed supply-stock reads compared against the
// legacy home/list_things census, for both held and spawned-only ownership modes.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"strconv"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-supplies-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-supplies-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Fresh production typed supply census against existing native stock reads; no stock spawning, fixture mutation or gameplay orders.", !*rendered)
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
	if !na.Contains(names, "rimgovernor/observations_list_supplies") {
		return fmt.Errorf("missing rimgovernor/observations_list_supplies in discovery")
	}
	for _, name := range names {
		if len(name) >= 5 && (name[:5] == "test/" || contains(name, "fixture")) {
			return fmt.Errorf("unexpected fixture export %s in production supplies discovery", name)
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

	for _, includeHeld := range []bool{true, false} {
		label := "spawned"
		if includeHeld {
			label = "held"
		}
		legacy, err := h.Call(ctx, label+"-native-census", "home/list_things", map[string]any{
			"category": "haulable", "ownership": "all", "includeHeld": includeHeld,
			"maxPositionsPerDef": 0, "maxCorpsesPerRow": 0,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(legacy["success"]); !success {
			return fmt.Errorf("legacy home/list_things refused")
		}
		known := map[string]map[string]any{}
		for _, raw := range na.AsSlice(legacy["things"]) {
			row, _ := na.AsMap(raw)
			known[na.AsString(row["defName"])] = row
		}
		for _, required := range []string{"WoodLog", "Steel"} {
			row, ok := known[required]
			if !ok || na.AsNumber(row["total"]) <= 0 {
				return fmt.Errorf("fresh native starting material %s unavailable", required)
			}
		}
		var heldDefs []string
		for name, row := range known {
			if na.AsNumber(row["carried"])+na.AsNumber(row["inContainer"]) > 0 {
				heldDefs = append(heldDefs, name)
			}
		}
		heldDefs = dedupSorted(heldDefs)
		selected := dedupSorted(append([]string{"WoodLog", "Steel"}, limit(heldDefs, 14)...))
		report[label+"_held_definitions"] = heldDefs

		defNames := make([]any, len(selected))
		for i, s := range selected {
			defNames[i] = s
		}
		request := map[string]any{
			"scope":  map[string]any{"expectedIdentity": identity},
			"filter": map[string]any{"defNames": defNames, "ownership": "all", "includeHeld": includeHeld},
			"page":   map[string]any{"limit": 16},
		}
		reply, err := h.Wire(ctx, label+"-typed-census", "observations_list_supplies", request)
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		if observedContext, _ := observed["context"].(map[string]any); !na.DeepEqual(observedContext, beforeContext) {
			return fmt.Errorf("%s: context drifted mid-run", label)
		}
		rows := na.AsSlice(observed["stocks"])
		if err := na.CheckCompleteness(observed["completeness"], len(selected)); err != nil {
			return fmt.Errorf("%s completeness: %w", label, err)
		}
		if len(rows) != len(selected) {
			return fmt.Errorf("%s: expected %d stock rows, found %d", label, len(selected), len(rows))
		}
		for _, raw := range rows {
			row, _ := na.AsMap(raw)
			definition, _ := na.AsMap(row["definition"])
			source, ok := known[na.AsString(definition["defName"])]
			if !ok {
				return fmt.Errorf("%s: typed row %v missing from legacy census", label, definition["defName"])
			}
			if err := checkStock(row, source, identity, includeHeld); err != nil {
				return fmt.Errorf("%s: %w", label, err)
			}
		}
		report[label+"_definitions_checked"] = selected
	}

	defaultRequest := map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": map[string]any{"defNames": []any{"WoodLog", "Steel"}},
	}
	defaultReply, err := h.Wire(ctx, "default-ownership-held", "observations_list_supplies", defaultRequest)
	if err != nil {
		return err
	}
	_, defaultObserved, err := na.Outcome(defaultReply, "observed")
	if err != nil {
		return err
	}
	defaultRows := na.AsSlice(defaultObserved["stocks"])
	if err := na.CheckCompleteness(defaultObserved["completeness"], 2); err != nil {
		return fmt.Errorf("default completeness: %w", err)
	}
	for _, raw := range defaultRows {
		row, _ := na.AsMap(raw)
		if na.AsNumber(row["ours"]) <= 0 {
			return fmt.Errorf("default read: expected a positive ours quantity")
		}
		if _, ok := row["carried"]; !ok {
			return fmt.Errorf("default read: carried should be populated (includeHeld defaults true)")
		}
		if _, ok := row["inContainer"]; !ok {
			return fmt.Errorf("default read: inContainer should be populated (includeHeld defaults true)")
		}
	}

	invalidCases := []struct {
		label   string
		request map[string]any
	}{
		{"bad-category", map[string]any{"filter": map[string]any{"category": "unknown"}}},
		{"bad-page", map[string]any{"page": map[string]any{"limit": 257}}},
	}
	for _, c := range invalidCases {
		request := na.Merge(c.request, map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
		reply, err := h.Wire(ctx, c.label, "observations_list_supplies", request)
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
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
	return nil
}

// checkStock compares one typed ResourceStock row against home/list_things's legacy
// row. Contained EntityRef items now carry their own populated CAS snapshot
// (contracts/proto/observations.proto EntityRef.snapshot=6): earlier acceptance runs
// predated that and asserted its absence ("snapshot" not in item), which this port
// corrects rather than preserves.
func checkStock(row, legacy map[string]any, identity map[string]any, includeHeld bool) error {
	definition, _ := na.AsMap(row["definition"])
	if na.AsString(definition["defName"]) != na.AsString(legacy["defName"]) {
		return fmt.Errorf("definition defName mismatch: typed=%v legacy=%v", definition["defName"], legacy["defName"])
	}
	mapping := map[string]string{
		"units": "total", "stacks": "stacks", "ours": "ours", "oursUnforbidden": "oursUnforbidden",
		"forbidden": "forbidden", "otherFaction": "otherFaction", "fogged": "fogged", "reserved": "reserved",
		"inStockpile": "inStockpile", "inHomeArea": "inHomeArea",
	}
	if includeHeld {
		mapping["carried"] = "carried"
		mapping["inContainer"] = "inContainer"
		mapping["traderStock"] = "traderStock"
	}
	for typedKey, legacyKey := range mapping {
		legacyVal := na.AsNumber(legacy[legacyKey])
		if legacyVal < 0 {
			return fmt.Errorf("legacy %s must be a non-negative integer", legacyKey)
		}
		typedVal, err := count(row[typedKey])
		if err != nil {
			return fmt.Errorf("native stock %v.%s: %w", row["definition"], typedKey, err)
		}
		if typedVal != int64(legacyVal) {
			return fmt.Errorf("native stock mismatch %v.%s: typed=%v legacy=%v", row["definition"], typedKey, row[typedKey], legacyVal)
		}
	}
	held := int64(0)
	if includeHeld {
		held = int64(na.AsNumber(legacy["carried"])) + int64(na.AsNumber(legacy["inContainer"]))
	}
	spawned, err := count(row["spawned"])
	if err != nil {
		return fmt.Errorf("spawned for %v: %w", row["definition"], err)
	}
	units, err := count(row["units"])
	if err != nil {
		return fmt.Errorf("units for %v: %w", row["definition"], err)
	}
	if spawned+held != units {
		return fmt.Errorf("spawned+held does not equal units for %v", row["definition"])
	}
	playerFaction, err := count(row["playerFaction"])
	if err != nil {
		return fmt.Errorf("playerFaction for %v: %w", row["definition"], err)
	}
	if playerFaction > units {
		return fmt.Errorf("playerFaction exceeds units for %v", row["definition"])
	}
	stacks, err := count(row["stacks"])
	if err != nil {
		return fmt.Errorf("stacks for %v: %w", row["definition"], err)
	}
	items := na.AsSlice(row["items"])
	if int64(len(items)) != stacks {
		return fmt.Errorf("item count does not match stacks for %v", row["definition"])
	}
	// Not every loose supply item supports a CAS snapshot: NativeSupplyAllow.Snapshot
	// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeSupplyAllow.cs:48)
	// returns null for a spawned item that fails Eligible (e.g. missing
	// CompForbiddable, as techprints and some other special items do), and
	// ListSupplies records that with an "items.snapshot"/UNSUPPORTED row issue
	// instead of failing the read (NativeSuppliesObservationTools.cs:72-73,262).
	// Held items always get a stateless CAS token from HeldSnapshot regardless, so
	// only a snapshot-less item backed by that documented gap is acceptable.
	rowAllowsSnapshotless := na.RequireIssueReason(na.AsSlice(row["issues"]), "items.snapshot", "UNAVAILABLE_REASON_UNSUPPORTED")
	seenIDs := map[string]bool{}
	for _, raw := range items {
		item, _ := na.AsMap(raw)
		id := na.AsString(item["id"])
		if id == "" {
			return fmt.Errorf("stock item missing id for %v", row["definition"])
		}
		if seenIDs[id] {
			return fmt.Errorf("duplicate stock item id %s for %v", id, row["definition"])
		}
		seenIDs[id] = true
		if int(na.AsNumber(item["mapId"])) != int(na.AsNumber(identity["mapId"])) {
			return fmt.Errorf("stock item %s has the wrong mapId", id)
		}
		if err := na.RequireSnapshot(item["snapshot"]); err != nil {
			if rowAllowsSnapshotless && item["snapshot"] == nil {
				continue // documented Eligible()-gated gap, not a Go-port bug
			}
			return fmt.Errorf("stock item %s missing a populated CAS snapshot: %w", id, err)
		}
	}
	if err := na.CheckCompleteness(row["itemsCompleteness"], len(items)); err != nil {
		return fmt.Errorf("items completeness for %v: %w", row["definition"], err)
	}
	holders := na.AsSlice(row["holders"])
	if includeHeld {
		if err := na.CheckCompleteness(row["holdersCompleteness"], len(holders)); err != nil {
			return fmt.Errorf("holders completeness for %v: %w", row["definition"], err)
		}
		total := int64(0)
		for _, raw := range holders {
			holder, _ := na.AsMap(raw)
			units, err := count(holder["units"])
			if err != nil {
				return fmt.Errorf("holder units for %v: %w", row["definition"], err)
			}
			if units <= 0 {
				return fmt.Errorf("holder with non-positive units for %v", row["definition"])
			}
			holderRef, _ := na.AsMap(holder["holder"])
			if na.AsString(holderRef["id"]) == "" {
				return fmt.Errorf("holder missing id for %v", row["definition"])
			}
			total += units
		}
		if total != held {
			return fmt.Errorf("holder unit total %v does not match held %v for %v", total, held, row["definition"])
		}
	} else {
		if len(holders) != 0 {
			return fmt.Errorf("holders populated without includeHeld for %v", row["definition"])
		}
		completeness, _ := na.AsMap(row["holdersCompleteness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); complete {
			return fmt.Errorf("holdersCompleteness unexpectedly complete without includeHeld for %v", row["definition"])
		}
		for _, field := range []string{"carried", "inContainer", "traderStock"} {
			if _, present := row[field]; present {
				return fmt.Errorf("%s unexpectedly present without includeHeld for %v", field, row["definition"])
			}
		}
		for _, field := range []string{"carried", "in_container", "trader_stock"} {
			if !na.RequireIssueReason(na.AsSlice(row["issues"]), field, "UNAVAILABLE_REASON_NOT_REQUESTED") {
				return fmt.Errorf("missing NOT_REQUESTED issue for %s on %v", field, row["definition"])
			}
		}
	}
	return na.CheckCompleteness(row["corpsesCompleteness"], len(na.AsSlice(row["corpses"])))
}

// count asserts value is a canonical ProtoJSON int64 quantity string -- ASCII decimal
// digits only, no leading zero (except the literal "0"), no sign, and within int64
// range. A missing/absent
// quantity (nil, a bare number, or a malformed string) must never be read as zero.
func count(v any) (int64, error) {
	s, ok := v.(string)
	if !ok {
		return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %#v", v)
	}
	if s == "" {
		return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %q", s)
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %q", s)
		}
	}
	if s != "0" && s[0] == '0' {
		return 0, fmt.Errorf("noncanonical leading zero: %q", s)
	}
	n, err := strconv.ParseUint(s, 10, 64)
	if err != nil {
		return 0, fmt.Errorf("quantity is not a valid uint64: %q", s)
	}
	if n > 9223372036854775807 {
		return 0, fmt.Errorf("quantity exceeds int64 max: %q", s)
	}
	return int64(n), nil
}

func dedupSorted(values []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, v := range values {
		if !seen[v] {
			seen[v] = true
			out = append(out, v)
		}
	}
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && out[j-1] > out[j]; j-- {
			out[j-1], out[j] = out[j], out[j-1]
		}
	}
	return out
}

func limit(values []string, n int) []string {
	dedupSortedInPlace := dedupSorted(values)
	if len(dedupSortedInPlace) > n {
		return dedupSortedInPlace[:n]
	}
	return dedupSortedInPlace
}

func contains(haystack, needle string) bool {
	for i := 0; i+len(needle) <= len(haystack); i++ {
		if haystack[i:i+len(needle)] == needle {
			return true
		}
	}
	return false
}
