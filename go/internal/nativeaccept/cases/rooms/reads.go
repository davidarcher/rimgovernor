// The rooms/reads case proves typed room reads (geometry, native stats,
// contents, cells) on the lab: CAS snapshots, exact-id, cell and boundary
// lookups agree with each other and each room's cells with its own extents.
package rooms

import (
	"context"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "rooms/reads",
		Scope:  "Naturally generated rooms; read-only geometry, native stats and contents, no fixture spawning or construction orders.",
		Start:  cases.Fixture{Op: "test/bed_assign_prepare", On: cases.LabStart()},
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, names := s.Harness(), s.Names()
	if !na.Contains(names, "rimgovernor/observations_list_rooms") {
		return fmt.Errorf("missing rimgovernor/observations_list_rooms in discovery")
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

	defaultReply, err := h.Wire(ctx, "typed-default", "observations_list_rooms", scope)
	if err != nil {
		return err
	}
	_, defaultObserved, err := na.Outcome(defaultReply, "observed")
	if err != nil {
		return err
	}
	rooms := na.AsSlice(defaultObserved["rooms"])
	if len(rooms) == 0 {
		return fmt.Errorf("typed room read returned no rooms")
	}
	var candidates []map[string]any
	for _, raw := range rooms {
		row, _ := na.AsMap(raw)
		if err := checkRoom(row, nil); err != nil {
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
		return fmt.Errorf("no indoor room found for populated acceptance")
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

	exactRequest := na.Merge(scope, map[string]any{"roomIds": []any{targetID}})
	grid, err := h.MapCells(ctx, "map-cells", identity)
	if err != nil {
		return err
	}
	gridRooms := na.RoomCells(grid)
	exactReply, err := h.Wire(ctx, "typed-exact-cells", "observations_list_rooms", exactRequest)
	if err != nil {
		return err
	}
	_, selected, err := na.Outcome(exactReply, "observed")
	if err != nil {
		return err
	}
	selectedRows := na.AsSlice(selected["rooms"])
	if len(selectedRows) != 1 {
		return fmt.Errorf("exact room selection did not return exactly one room")
	}
	selectedRow, _ := na.AsMap(selectedRows[0])
	if err := checkRoom(selectedRow, gridRooms); err != nil {
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
	if len(na.AsSlice(absentObserved["rooms"])) != 0 {
		return fmt.Errorf("unknown room id unexpectedly matched a room")
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
	if err := checkRoom(boundaryRow, gridRooms); err != nil {
		return err
	}
	if sumUnits(boundaryRow["contents"]) <= sumUnits(target["contents"]) {
		return fmt.Errorf("no populated boundary buildings tested (boundary contents did not exceed indoor-only contents)")
	}

	invalidCases := []struct {
		label  string
		change map[string]any
	}{
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
	report["rooms"] = len(rooms)
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

// checkRoom validates one typed room row's CAS snapshot and, given the
// map grid's rooms, its cell geometry (the grid cells carrying its
// gridRoom key) against its own cellCount, center and extents.
func checkRoom(row map[string]any, gridRooms map[string][]domain.Cell) error {
	if err := na.RequireSnapshot(row["snapshot"]); err != nil {
		return fmt.Errorf("room %v missing a populated CAS snapshot: %w", row["id"], err)
	}
	for _, field := range []string{"role", "properRoom", "doorway", "outdoors", "psychologicallyOutdoors", "touchesMapEdge", "fogged", "openRoofCount", "cellCount", "temperatureC"} {
		if _, ok := row[field]; !ok {
			return fmt.Errorf("room %v lacks %s", row["id"], field)
		}
	}
	if gridRooms == nil {
		return nil
	}
	var actualCoords [][2]float64
	for _, cell := range gridRooms[na.AsString(row["gridRoom"])] {
		actualCoords = append(actualCoords, [2]float64{float64(cell.X), float64(cell.Z)})
	}
	if len(actualCoords) == 0 || len(actualCoords) != int(na.AsNumber(row["cellCount"])) {
		return fmt.Errorf("room %v grid cells (%d) do not match cellCount", row["id"], len(actualCoords))
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
	return nil
}

func containsCoord(coords [][2]float64, want [2]float64) bool {
	for _, c := range coords {
		if c == want {
			return true
		}
	}
	return false
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
