// Command wallupgradeaccept proves the typed wall-upgrade census behind
// rimgovernor/observations_list_wall_upgrade_sites (#78) on a loaded save:
// the cleanup census (no target) is complete, and for every colonist wall the
// per-target replacement rows agree with the legacy home/wall_upgrade_sites
// geometry (normal, side supports, backup cells) and stone material costs,
// each row carrying the target's CAS token. At least one wall must yield a
// site or the run is vacuous. A fixture build's test/lighting_prepare (or any
// -prepare tool) first builds an enclosed roofed room so the census has
// colonist walls to read; the stock saves hold none.
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
	output := flag.String("output", "", "fresh output directory (default <root>/native-wall-upgrade-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	save := flag.String("save", "RimGovernor-tribal8-baseline", "save at profile/Saves/<save>.rws to load")
	maxWalls := flag.Int("max-walls", 64, "colonist walls to cross-check per target (the first N by id)")
	prepare := flag.String("prepare", "test/lighting_prepare", "fixture tool that builds an enclosed roofed room of colonist walls (LightingFixture build); empty expects the save to already hold walls")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" || *save == "" {
		fmt.Fprintln(os.Stderr, "-root and -save are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-wall-upgrade-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Typed wall-upgrade site census against the legacy home/wall_upgrade_sites geometry; read-only, no designation or construction.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, *save, *prepare, *maxWalls, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID, save, prepare string, maxWalls int, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := cfg.UseSaveExpansions(save); err != nil {
		return err
	}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
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
	if !na.Contains(names, "rimgovernor/observations_list_wall_upgrade_sites") {
		return fmt.Errorf("missing rimgovernor/observations_list_wall_upgrade_sites in discovery")
	}
	if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
		"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if prepare != "" {
		if !na.Contains(names, prepare) {
			return fmt.Errorf("fixture tool %s not exported by this build", prepare)
		}
		prepared, err := h.Call(ctx, "prepare", prepare, map[string]any{})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return fmt.Errorf("%s refused: %#v", prepare, prepared)
		}
	}
	identityReply, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)
	scope := map[string]any{"expectedIdentity": identity}

	walls, err := colonistWalls(ctx, h, scope)
	if err != nil {
		return err
	}
	if len(walls) == 0 {
		return fmt.Errorf("save has no colonist wall; the census assertion would be vacuous")
	}
	report["colonist_walls"] = len(walls)

	cleanupReply, err := h.Wire(ctx, "cleanup-census", "observations_list_wall_upgrade_sites", map[string]any{"scope": scope, "page": map[string]any{"limit": 256}})
	if err != nil {
		return err
	}
	_, cleanup, err := na.Outcome(cleanupReply, "observed")
	if err != nil {
		return fmt.Errorf("cleanup census: %w", err)
	}
	if observedContext, _ := cleanup["context"].(map[string]any); !na.DeepEqual(observedContext, beforeContext) {
		return fmt.Errorf("cleanup census context drifted mid-run")
	}
	cleanupRows := na.AsSlice(cleanup["sites"])
	if err := na.CheckCompleteness(cleanup["completeness"], len(cleanupRows)); err != nil {
		return fmt.Errorf("cleanup completeness: %w", err)
	}
	for _, raw := range cleanupRows {
		row, _ := na.AsMap(raw)
		if _, ok := row["replacement"]; !ok || len(na.AsSlice(row["completedBackups"])) == 0 {
			return fmt.Errorf("cleanup row without a permanent replacement and remaining backups: %v", row["target"])
		}
	}
	report["cleanup_sites"] = len(cleanupRows)

	if len(walls) > maxWalls {
		walls = walls[:maxWalls]
	}
	sitesFound, wallsWithSites := 0, 0
	for _, wall := range walls {
		legacy, err := h.Call(ctx, "legacy-"+wall, "home/wall_upgrade_sites", map[string]any{"target": wall})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(legacy["success"]); !success {
			return fmt.Errorf("legacy home/wall_upgrade_sites refused %s", wall)
		}
		typedReply, err := h.Wire(ctx, "typed-"+wall, "observations_list_wall_upgrade_sites", map[string]any{"scope": scope, "targetId": wall, "page": map[string]any{"limit": 64}})
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(typedReply, "observed")
		if err != nil {
			return fmt.Errorf("%s: %w", wall, err)
		}
		rows := na.AsSlice(observed["sites"])
		if err := na.CheckCompleteness(observed["completeness"], len(rows)); err != nil {
			return fmt.Errorf("%s completeness: %w", wall, err)
		}
		if err := compareSites(wall, rows, legacy); err != nil {
			return err
		}
		if len(rows) > 0 {
			wallsWithSites++
			sitesFound += len(rows)
		}
	}
	if sitesFound == 0 {
		return fmt.Errorf("no colonist wall yielded a replacement site; the geometry assertion is vacuous")
	}
	report["walls_checked"] = len(walls)
	report["walls_with_sites"] = wallsWithSites
	report["replacement_sites"] = sitesFound

	for _, c := range []struct {
		label   string
		request map[string]any
		code    string
	}{
		{"bad-page", map[string]any{"scope": scope, "page": map[string]any{"limit": 257}}, "FAILURE_CODE_INVALID_REQUEST"},
		{"blank-target", map[string]any{"scope": scope, "targetId": " "}, "FAILURE_CODE_INVALID_REQUEST"},
		{"unknown-target", map[string]any{"scope": scope, "targetId": "Thing_NoSuchWall0"}, "FAILURE_CODE_NOT_FOUND"},
	} {
		reply, err := h.Wire(ctx, c.label, "observations_list_wall_upgrade_sites", c.request)
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != c.code {
			return fmt.Errorf("%s: expected %s, got %q", c.label, c.code, code)
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
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// colonistWalls pages the typed building census for every player Wall id.
func colonistWalls(ctx context.Context, h *na.Harness, scope map[string]any) ([]string, error) {
	var ids []string
	cursor := ""
	for page := 0; page < 32; page++ {
		request := map[string]any{"scope": scope, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true, "page": map[string]any{"limit": 256}}
		if cursor != "" {
			request["page"] = map[string]any{"limit": 256, "cursor": cursor}
		}
		reply, err := h.Wire(ctx, fmt.Sprintf("walls-%d", page), "observations_list_buildings", request)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("walls page %d: %w", page, err)
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

type geometry struct {
	nx, nz      float64
	left, right string
	backups     string
}

func compareSites(wall string, rows []any, legacy map[string]any) error {
	want := map[geometry]bool{}
	for _, raw := range na.AsSlice(legacy["sites"]) {
		site, _ := na.AsMap(raw)
		want[geometry{na.AsNumber(site["nx"]), na.AsNumber(site["nz"]), na.AsString(site["left"]), na.AsString(site["right"]), cells(site["backupCells"])}] = true
	}
	if len(want) != len(rows) {
		return fmt.Errorf("%s: legacy lists %d sites, typed census %d", wall, len(want), len(rows))
	}
	materials := map[string]map[string]float64{}
	for _, raw := range na.AsSlice(legacy["materials"]) {
		material, _ := na.AsMap(raw)
		costs := map[string]float64{}
		for name, units := range func() map[string]any { m, _ := na.AsMap(material["costs"]); return m }() {
			costs[name] = na.AsNumber(units)
		}
		materials[na.AsString(material["defName"])] = costs
	}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		target, _ := na.AsMap(row["target"])
		original, _ := na.AsMap(row["original"])
		originalBuilding, _ := na.AsMap(original["building"])
		snapshot, _ := na.AsMap(target["snapshot"])
		if na.AsString(target["id"]) != wall || na.AsString(originalBuilding["id"]) != wall || na.AsString(snapshot["token"]) == "" {
			return fmt.Errorf("%s: replacement row must target the original wall with a CAS token", wall)
		}
		if present, _ := na.AsBool(row["targetPresent"]); !present {
			return fmt.Errorf("%s: target reported absent", wall)
		}
		normal, _ := na.AsMap(row["normal"])
		leftSupport, _ := na.AsMap(row["leftSupport"])
		leftBuilding, _ := na.AsMap(leftSupport["building"])
		rightSupport, _ := na.AsMap(row["rightSupport"])
		rightBuilding, _ := na.AsMap(rightSupport["building"])
		key := geometry{na.AsNumber(normal["x"]), na.AsNumber(normal["z"]), na.AsString(leftBuilding["id"]), na.AsString(rightBuilding["id"]), cells(row["backupCells"])}
		if !want[key] {
			return fmt.Errorf("%s: typed site %+v missing from the legacy listing", wall, key)
		}
		delete(want, key)
		options := na.AsSlice(row["replacementMaterials"])
		if len(options) != len(materials) {
			return fmt.Errorf("%s: %d typed materials vs %d legacy", wall, len(options), len(materials))
		}
		for _, rawOption := range options {
			option, _ := na.AsMap(rawOption)
			costs, ok := materials[na.AsString(option["stuff"])]
			if !ok {
				return fmt.Errorf("%s: material %v missing from legacy", wall, option["stuff"])
			}
			for _, rawCost := range na.AsSlice(option["costs"]) {
				cost, _ := na.AsMap(rawCost)
				if costs[na.AsString(cost["defName"])] != na.AsNumber(cost["units"]) {
					return fmt.Errorf("%s: material %v cost %v disagrees with legacy", wall, option["stuff"], cost)
				}
			}
		}
	}
	return nil
}

func cells(v any) string {
	out := ""
	for _, raw := range na.AsSlice(v) {
		cell, _ := na.AsMap(raw)
		out += fmt.Sprintf("%v,%v;", na.AsNumber(cell["x"]), na.AsNumber(cell["z"]))
	}
	return out
}
