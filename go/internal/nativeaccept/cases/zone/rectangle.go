package zone

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "zone/rectangle", Scope: "Native stockpile rectangle placement uses vanilla cell eligibility, keeps both components across a wall barrier and ground holding Steel, skips an existing player stockpile without changing it, and replays all created IDs without duplicates. A Go snapshot cannot verify the native designator, zone grid, hauling settings or replay cache.", Start: cases.Fixture{On: cases.LabStart(), Op: "test/stockpile_rectangle_prepare"}, Budget: 3 * time.Minute, Crew: cases.Crew{Size: 3}, Run: runRectangle})
}

func runRectangle(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	x, z := int32(na.AsNumber(prepared["x"])), int32(na.AsNumber(prepared["z"]))
	playerID := na.AsString(prepared["playerZoneId"])
	if playerID == "" {
		return fmt.Errorf("missing prepared player zone: %#v", prepared)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	intent := map[string]any{"label": "Rectangle storage", "kind": "ZONE_TYPE_STOCKPILE", "addCells": map[string]any{"rectangle": map[string]any{"origin": map[string]any{"x": x, "z": z}, "width": 7, "height": 5}}, "stockpile": map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT", "preset": "FILTER_PRESET_NOTHING", "filter": map[string]any{"allow": []any{map[string]any{"thingDef": "Steel"}}}}}
	apply := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": "rectangle-stockpile", "zone": intent}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, ok := na.AsMap(observed["zone"])
		if !ok {
			return nil, fmt.Errorf("%s: missing applied stockpile evidence: %#v", label, result)
		}
		return effect, nil
	}
	first, err := apply("rectangle-create")
	if err != nil {
		return err
	}
	rows := na.AsSlice(first["created"])
	if len(rows) != 2 {
		return fmt.Errorf("expected both components: %#v", first)
	}
	ids := []string{playerID}
	actual := map[string][]domain.Cell{}
	expected := map[domain.Cell]bool{}
	for dx := int32(0); dx < 7; dx++ {
		for dz := int32(0); dz < 5; dz++ {
			if dx != 3 && !(dx == 1 && dz == 2) && !(dx == 6 && dz == 4) {
				expected[domain.Cell{X: x + dx, Z: z + dz}] = true
			}
		}
	}
	count := 0
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["zoneId"])
		if id == "" || id == playerID || actual[id] != nil {
			return fmt.Errorf("invalid created identity: %#v", row)
		}
		ids = append(ids, id)
		for _, rawCell := range na.AsSlice(row["cells"]) {
			cell, _ := na.AsMap(rawCell)
			c := domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}
			if !expected[c] {
				return fmt.Errorf("unexpected/duplicate cell %v", c)
			}
			delete(expected, c)
			actual[id] = append(actual[id], c)
			count++
		}
	}
	if count != 28 || len(expected) != 0 {
		return fmt.Errorf("lost usable cells: count=%d missing=%v", count, expected)
	}
	grid, err := h.MapCells(ctx, "rectangle-grid", identity)
	if err != nil {
		return err
	}
	byZone := na.ZoneCells(grid)
	for id, cells := range actual {
		if len(byZone[id]) != len(cells) {
			return fmt.Errorf("zone %s: receipt/grid mismatch: %v %v", id, cells, byZone[id])
		}
		set := map[domain.Cell]bool{}
		for _, c := range cells {
			set[c] = true
		}
		for _, c := range byZone[id] {
			if !set[c] {
				return fmt.Errorf("zone %s unexpected grid cell %v", id, c)
			}
		}
	}
	reply, err := h.Wire(ctx, "rectangle-zone-settings", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "ids": ids, "includeFilter": true})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	zones := na.AsSlice(observed["zones"])
	if len(zones) != 3 {
		return fmt.Errorf("expected three zones: %#v", observed)
	}
	for _, raw := range zones {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["id"])
		filter, _ := na.AsMap(row["filter"])
		allowed := na.AsSlice(filter["allowedDefNames"])
		if id == playerID {
			if na.AsString(row["priority"]) != "Low" || len(allowed) != 0 {
				return fmt.Errorf("player zone changed: %#v", row)
			}
		} else if na.AsString(row["priority"]) != "Important" || len(allowed) != 1 || na.AsString(allowed[0]) != "Steel" {
			return fmt.Errorf("created settings mismatch: %#v", row)
		}
	}
	// Creation receipts remain recoverable after ordinary replay entries age out.
	var churn []any
	for i := 0; i < 260; i++ {
		churn = append(churn, map[string]any{"key": fmt.Sprintf("rectangle-settings-%d", i), "zone": map[string]any{"zone": map[string]any{"id": ids[1]}, "stockpile": map[string]any{"priority": "STORAGE_PRIORITY_IMPORTANT"}}})
	}
	if _, err := h.Wire(ctx, "rectangle-replay-churn", "operations_apply", map[string]any{"identity": identity, "actions": churn}); err != nil {
		return err
	}
	replay, err := apply("rectangle-replay")
	if err != nil {
		return err
	}
	if !na.DeepEqual(first, replay) {
		return fmt.Errorf("replay lost creation identities: %#v", replay)
	}
	grid, err = h.MapCells(ctx, "rectangle-replay-grid", identity)
	if err != nil {
		return err
	}
	for _, id := range ids {
		if !na.DeepEqual(byZone[id], na.ZoneCells(grid)[id]) {
			return fmt.Errorf("replay changed zone %s", id)
		}
	}
	s.Report()["created_components"] = len(rows)
	s.Report()["created_cells"] = count
	s.Report()["player_zone_unchanged"] = true
	s.Report()["replayed_without_duplicates"] = true
	return nil
}
