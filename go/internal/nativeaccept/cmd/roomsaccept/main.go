// Command roomsaccept proves the full disposable-
// worker lifecycle plus typed room reads (geometry, native stats, contents, cells)
// compared against the legacy home/list_rooms getter on a naturally generated map.
package main

import (
	"context"
	"flag"
	"fmt"
	"math"
	"os"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-rooms-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-rooms-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Naturally generated rooms; read-only geometry, native stats and contents, no fixture spawning or construction orders.", !*rendered)
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
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
	h := na.NewHarness(client, output)

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/observations_list_rooms") {
		return fmt.Errorf("missing rimgovernor/observations_list_rooms in discovery")
	}
	for _, name := range names {
		if len(name) >= 5 && name[:5] == "test/" {
			return fmt.Errorf("unexpected fixture export %s in production rooms discovery", name)
		}
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietIfAvailable); err != nil {
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
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}

	legacy, err := h.Call(ctx, "native-default", "home/list_rooms", map[string]any{"cells": true})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(legacy["success"]); !success {
		return fmt.Errorf("legacy home/list_rooms refused")
	}
	nativeByID := map[string]map[string]any{}
	for _, raw := range na.AsSlice(legacy["rooms"]) {
		row, _ := na.AsMap(raw)
		nativeByID[fmt.Sprint(row["id"])] = row
	}

	defaultReply, err := h.Wire(ctx, "typed-default", "observations_list_rooms", scope)
	if err != nil {
		return err
	}
	_, defaultObserved, err := na.Outcome(defaultReply, "observed")
	if err != nil {
		return err
	}
	if err := na.CheckCompleteness(defaultObserved["completeness"], len(nativeByID)); err != nil {
		return fmt.Errorf("default rooms completeness: %w", err)
	}
	rooms := na.AsSlice(defaultObserved["rooms"])
	if len(rooms) != len(nativeByID) {
		return fmt.Errorf("typed room count %d does not match legacy count %d", len(rooms), len(nativeByID))
	}
	var candidates []map[string]any
	for _, raw := range rooms {
		row, _ := na.AsMap(raw)
		native, ok := nativeByID[na.AsString(row["id"])]
		if !ok {
			return fmt.Errorf("typed room %s missing from legacy home/list_rooms", row["id"])
		}
		if err := compareRoom(row, native, false); err != nil {
			return err
		}
		properRoom, _ := na.AsBool(row["properRoom"])
		outdoors, _ := na.AsBool(row["outdoors"])
		cellCount := int(na.AsNumber(row["cellCount"]))
		if properRoom && !outdoors && cellCount <= 4096 {
			candidates = append(candidates, row)
		}
	}
	if len(candidates) == 0 {
		return fmt.Errorf("no naturally generated indoor room found for populated acceptance")
	}
	sort.Slice(candidates, func(i, j int) bool {
		ci, cj := int(na.AsNumber(candidates[i]["cellCount"])), int(na.AsNumber(candidates[j]["cellCount"]))
		if ci != cj {
			return ci > cj
		}
		return na.AsString(candidates[i]["id"]) < na.AsString(candidates[j]["id"])
	})
	target := candidates[0]
	targetID := na.AsString(target["id"])

	exactRequest := na.Merge(scope, map[string]any{"roomIds": []any{targetID}, "includeCells": true})
	exactReply, err := h.Wire(ctx, "typed-exact-cells", "observations_list_rooms", exactRequest)
	if err != nil {
		return err
	}
	_, selected, err := na.Outcome(exactReply, "observed")
	if err != nil {
		return err
	}
	if err := na.CheckCompleteness(selected["completeness"], 1); err != nil {
		return fmt.Errorf("exact selection completeness: %w", err)
	}
	selectedRows := na.AsSlice(selected["rooms"])
	if len(selectedRows) != 1 {
		return fmt.Errorf("exact room selection did not return exactly one room")
	}
	selectedRow, _ := na.AsMap(selectedRows[0])
	if err := compareRoom(selectedRow, nativeByID[targetID], true); err != nil {
		return err
	}
	repeatReply, err := h.Wire(ctx, "repeat", "observations_list_rooms", exactRequest)
	if err != nil {
		return err
	}
	_, repeatObserved, err := na.Outcome(repeatReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(repeatObserved["rooms"], selected["rooms"]) {
		return fmt.Errorf("repeating the exact room read returned a different snapshot")
	}
	center, _ := na.AsMap(selectedRow["center"])
	region := map[string]any{"minimum": center, "maximum": center}
	byCellReply, err := h.Wire(ctx, "cell-intersection", "observations_list_rooms", na.Merge(exactRequest, map[string]any{"region": region}))
	if err != nil {
		return err
	}
	_, byCellObserved, err := na.Outcome(byCellReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(byCellObserved["rooms"], selected["rooms"]) {
		return fmt.Errorf("cell-intersection lookup returned a different room than the exact-id lookup")
	}
	absentReply, err := h.Wire(ctx, "unknown-id", "observations_list_rooms", na.Merge(scope, map[string]any{"roomIds": []any{"absent-room"}}))
	if err != nil {
		return err
	}
	_, absentObserved, err := na.Outcome(absentReply, "observed")
	if err != nil {
		return err
	}
	if err := na.CheckCompleteness(absentObserved["completeness"], 0); err != nil {
		return fmt.Errorf("unknown-id completeness: %w", err)
	}
	if len(na.AsSlice(absentObserved["rooms"])) != 0 {
		return fmt.Errorf("unknown room id unexpectedly matched a room")
	}

	nativeBoundaryReply, err := h.Call(ctx, "native-boundary", "home/list_rooms", map[string]any{"includeBoundary": true, "cells": true})
	if err != nil {
		return err
	}
	nativeBoundaryByID := map[string]map[string]any{}
	for _, raw := range na.AsSlice(nativeBoundaryReply["rooms"]) {
		row, _ := na.AsMap(raw)
		// legacy home/list_rooms encodes id as a raw JSON number (Room.ID is an int
		// counter per the tool's own "ids" note), not a string like the typed proto
		// read's id field. na.AsString only unwraps JSON strings and silently returns
		// "" for a number, which collapsed every room here onto one empty-string key
		// and made every real lookup miss. fmt.Sprint normalizes either JSON shape,
		// matching how nativeByID is keyed above.
		nativeBoundaryByID[fmt.Sprint(row["id"])] = row
	}
	boundaryReply, err := h.Wire(ctx, "typed-boundary", "observations_list_rooms", na.Merge(exactRequest, map[string]any{"includeBoundary": true}))
	if err != nil {
		return err
	}
	_, boundaryObserved, err := na.Outcome(boundaryReply, "observed")
	if err != nil {
		return err
	}
	boundaryRows := na.AsSlice(boundaryObserved["rooms"])
	if len(boundaryRows) != 1 {
		return fmt.Errorf("typed boundary read did not return exactly one room")
	}
	boundaryRow, _ := na.AsMap(boundaryRows[0])
	if err := compareRoom(boundaryRow, nativeBoundaryByID[targetID], true); err != nil {
		return err
	}
	if sumUnits(boundaryRow["contents"]) <= sumUnits(target["contents"]) {
		return fmt.Errorf("no populated boundary buildings tested (boundary contents did not exceed indoor-only contents)")
	}

	invalidCases := []struct {
		label  string
		change map[string]any
	}{
		{"invalid-page", map[string]any{"page": map[string]any{"limit": 257}}},
		{"duplicate-id", map[string]any{"roomIds": []any{targetID, targetID}}},
		{"unknown-write", map[string]any{"set": true}},
	}
	for _, c := range invalidCases {
		reply, err := h.Wire(ctx, c.label, "observations_list_rooms", na.Merge(scope, c.change))
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	// A short-but-undecodable cursor passes Validate (NativeRoomObservationTools.cs
	// Validate only rejects cursor length>4096 as invalid) and instead fails
	// NativeObservationSnapshot.Cursor.TryDecode inside the handler, which reports
	// UNAVAILABLE_REASON_LIMIT_EXCEEDED ("Room cursor is stale or does not match
	// this query") rather than a Failure. Assert the outcome the tool actually
	// produces instead of an invalid-request failure.
	staleCursorReply, err := h.Wire(ctx, "cursor", "observations_list_rooms", na.Merge(scope, map[string]any{"page": map[string]any{"cursor": "stale"}}))
	if err != nil {
		return err
	}
	if _, observedPresent, _ := na.Outcome(staleCursorReply, "observed"); observedPresent != nil {
		return fmt.Errorf("cursor: expected a stale/undecodable cursor to be refused, got observed")
	}
	if reason, ok := na.UnavailableReason(staleCursorReply); !ok || reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
		return fmt.Errorf("cursor: expected UNAVAILABLE_REASON_LIMIT_EXCEEDED, got %q", reason)
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
	report["rooms"] = len(nativeByID)
	report["indoor_room"] = targetID
	report["indoor_cells"] = int(na.AsNumber(target["cellCount"]))
	return nil
}

func sumUnits(v any) float64 {
	total := 0.0
	for _, raw := range na.AsSlice(v) {
		item, _ := na.AsMap(raw)
		total += na.AsNumber(item["units"])
	}
	return total
}

// compareRoom validates one typed room row's CAS snapshot and its native-comparable
// fields against home/list_rooms's legacy row. Cell-level geometry is only compared
// when includeCells/includeBoundary was requested (cells param true).
func compareRoom(row, native map[string]any, cells bool) error {
	if native == nil {
		return fmt.Errorf("legacy room row missing for comparison")
	}
	if err := na.RequireSnapshot(row["snapshot"]); err != nil {
		return fmt.Errorf("room %v missing a populated CAS snapshot: %w", row["id"], err)
	}
	pairs := map[string]string{
		"role": "role", "properRoom": "properRoom", "doorway": "isDoorway", "outdoors": "outdoors",
		"psychologicallyOutdoors": "psychologicallyOutdoors", "touchesMapEdge": "touchesMapEdge",
		"fogged": "fogged", "openRoofCount": "openRoofCount", "cellCount": "cellCount",
	}
	for typedKey, nativeKey := range pairs {
		if !equalLoose(row[typedKey], native[nativeKey]) {
			return fmt.Errorf("room %v field %s mismatch: typed=%v native=%v", row["id"], typedKey, row[typedKey], native[nativeKey])
		}
	}
	temperature := na.AsNumber(row["temperatureC"])
	nativeTemperature := na.AsNumber(native["temperature"])
	if math.Abs(temperature-nativeTemperature) > 0.050001 {
		return fmt.Errorf("room %v temperature mismatch: typed=%v native=%v", row["id"], temperature, nativeTemperature)
	}
	if na.AsNumber(native["contentsNotListed"]) != 0 {
		return fmt.Errorf("room %v native contentsNotListed must be zero for a comparable read", row["id"])
	}
	if len(na.AsSlice(row["beds"])) != int(na.AsNumber(native["bedCount"])) {
		return fmt.Errorf("room %v bed count mismatch", row["id"])
	}
	if len(na.AsSlice(row["pawns"])) != int(na.AsNumber(native["pawnCount"])) {
		return fmt.Errorf("room %v pawn count mismatch", row["id"])
	}
	if cells {
		if complete, _ := na.AsBool(native["cellsComplete"]); !complete {
			return fmt.Errorf("room %v native cellsComplete must be true for a comparable read", row["id"])
		}
		if na.AsNumber(native["cellsNotListed"]) != 0 {
			return fmt.Errorf("room %v native cellsNotListed must be zero for a comparable read", row["id"])
		}
		actual := na.AsSlice(row["cells"])
		actualCoords := roomCoordinates(actual)
		if len(actual) != int(na.AsNumber(row["cellCount"])) {
			return fmt.Errorf("room %v returned cell count does not match cellCount", row["id"])
		}
		if !equalCoordSets(actualCoords, roomCoordinates(na.AsSlice(native["cells"]))) {
			return fmt.Errorf("room %v typed cells do not match native cells", row["id"])
		}
		if len(actualCoords) != len(dedupeCoords(actualCoords)) {
			return fmt.Errorf("room %v returned cells contain a duplicate coordinate", row["id"])
		}
		center, _ := na.AsMap(row["center"])
		centerCoord := [2]float64{na.AsNumber(center["x"]), na.AsNumber(center["z"])}
		if !containsCoord(actualCoords, centerCoord) {
			return fmt.Errorf("room %v center is not among the room's own cells", row["id"])
		}
		minX, minZ, maxX, maxZ := roomExtent(actualCoords)
		extents, _ := na.AsMap(row["extents"])
		minimum, _ := na.AsMap(extents["minimum"])
		maximum, _ := na.AsMap(extents["maximum"])
		if na.AsNumber(minimum["x"]) != minX || na.AsNumber(minimum["z"]) != minZ || na.AsNumber(maximum["x"]) != maxX || na.AsNumber(maximum["z"]) != maxZ {
			return fmt.Errorf("room %v extents do not match the room's own cells", row["id"])
		}
		if err := na.CheckCompleteness(row["cellsCompleteness"], len(actual)); err != nil {
			return fmt.Errorf("room %v cells completeness: %w", row["id"], err)
		}
	} else {
		if len(na.AsSlice(row["cells"])) != 0 {
			return fmt.Errorf("room %v cells populated without includeCells", row["id"])
		}
		completeness, _ := na.AsMap(row["cellsCompleteness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); complete {
			return fmt.Errorf("room %v cellsCompleteness unexpectedly reports complete without includeCells", row["id"])
		}
		if !na.RequireIssueReason(na.AsSlice(row["issues"]), "cells", "UNAVAILABLE_REASON_NOT_REQUESTED") {
			return fmt.Errorf("room %v missing a NOT_REQUESTED issue for unrequested cells", row["id"])
		}
	}
	return nil
}

func roomCoordinates(cells []any) [][2]float64 {
	out := make([][2]float64, 0, len(cells))
	for _, raw := range cells {
		cell, _ := na.AsMap(raw)
		out = append(out, [2]float64{na.AsNumber(cell["x"]), na.AsNumber(cell["z"])})
	}
	return out
}

func dedupeCoords(coords [][2]float64) [][2]float64 {
	seen := map[[2]float64]bool{}
	out := make([][2]float64, 0, len(coords))
	for _, c := range coords {
		if seen[c] {
			continue
		}
		seen[c] = true
		out = append(out, c)
	}
	return out
}

func containsCoord(coords [][2]float64, want [2]float64) bool {
	for _, c := range coords {
		if c == want {
			return true
		}
	}
	return false
}

func equalCoordSets(a, b [][2]float64) bool {
	if len(a) != len(b) {
		return false
	}
	sortCoords := func(s [][2]float64) [][2]float64 {
		out := append([][2]float64{}, s...)
		sort.Slice(out, func(i, j int) bool {
			if out[i][0] != out[j][0] {
				return out[i][0] < out[j][0]
			}
			return out[i][1] < out[j][1]
		})
		return out
	}
	sa, sb := sortCoords(a), sortCoords(b)
	for i := range sa {
		if sa[i] != sb[i] {
			return false
		}
	}
	return true
}

func roomExtent(coords [][2]float64) (minX, minZ, maxX, maxZ float64) {
	if len(coords) == 0 {
		return 0, 0, 0, 0
	}
	minX, minZ = coords[0][0], coords[0][1]
	maxX, maxZ = coords[0][0], coords[0][1]
	for _, c := range coords[1:] {
		if c[0] < minX {
			minX = c[0]
		}
		if c[1] < minZ {
			minZ = c[1]
		}
		if c[0] > maxX {
			maxX = c[0]
		}
		if c[1] > maxZ {
			maxZ = c[1]
		}
	}
	return minX, minZ, maxX, maxZ
}

func equalLoose(a, b any) bool {
	if af, aok := numeric(a); aok {
		if bf, bok := numeric(b); bok {
			return af == bf
		}
	}
	return na.DeepEqual(a, b)
}

func numeric(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, true
	case int:
		return float64(n), true
	default:
		return 0, false
	}
}
