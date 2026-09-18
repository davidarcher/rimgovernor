package rooms

import "testing"

// roomSample is the sample fixture: a single-cell room
// with known-zero/false facts throughout, to prove those legitimate falsy values are
// never confused with an absent field.
func roomSample() (map[string]any, map[string]any) {
	complete := map[string]any{"page": map[string]any{"complete": true}, "matched": "0", "returned": "0", "unreadable": "0"}
	row := map[string]any{
		"role": "None", "properRoom": true, "outdoors": false, "psychologicallyOutdoors": false,
		"touchesMapEdge": false, "fogged": false, "openRoofCount": 0.0, "cellCount": 1.0,
		"id": "0", "doorway": false, "temperatureC": 0.0, "label": "Room", "contents": []any{},
		"contentsCompleteness": copyRoomAny(complete),
		"beds":                 []any{}, "pawns": []any{}, "stockpileZoneIds": []any{}, "stats": []any{},
		"cells": []any{map[string]any{"x": 1.0, "z": 1.0}}, "center": map[string]any{"x": 1.0, "z": 1.0},
		"extents":           map[string]any{"minimum": map[string]any{"x": 1.0, "z": 1.0}, "maximum": map[string]any{"x": 1.0, "z": 1.0}},
		"cellsCompleteness": map[string]any{"page": map[string]any{"complete": true}, "matched": "1", "returned": "1", "unreadable": "0"},
		"snapshot":          map[string]any{"context": map[string]any{}, "entityId": "Room_0", "token": "tok"},
		"issues":            []any{},
	}
	native := map[string]any{
		"role": "None", "properRoom": true, "outdoors": false, "psychologicallyOutdoors": false,
		"touchesMapEdge": false, "fogged": false, "openRoofCount": 0.0, "cellCount": 1.0,
		"id": 0.0, "isDoorway": false, "temperature": 0.0, "gameLabel": "Room", "contentsNotListed": 0.0,
		"contents": []any{}, "bedCount": 0.0, "beds": []any{}, "pawnCount": 0.0, "pawns": []any{},
		"stockpiles": []any{}, "stats": map[string]any{}, "cellsComplete": true, "cellsNotListed": 0.0,
		"cells": []any{map[string]any{"x": 1.0, "z": 1.0}},
	}
	return row, native
}

func copyRoomAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		out[k] = v
	}
	return out
}

func TestCompareRoomKnownZeroAndFalseFactsPass(t *testing.T) {
	row, native := roomSample()
	if err := compareRoom(row, native, true); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestCompareRoomTemperatureAccountsOnlyForDeclaredRounding(t *testing.T) {
	row, native := roomSample()
	row["temperatureC"], native["temperature"] = 4.6758575439453125, 4.7
	if err := compareRoom(row, native, true); err != nil {
		t.Fatalf("unexpected error within rounding tolerance: %v", err)
	}
	row["temperatureC"] = 4.64
	if err := compareRoom(row, native, true); err == nil {
		t.Fatal("expected an error once temperature drifts beyond declared rounding tolerance")
	}
}

func TestCompareRoomMissingFactsDoNotBecomeDefaults(t *testing.T) {
	for _, field := range []string{"outdoors", "fogged", "openRoofCount", "temperatureC"} {
		t.Run(field, func(t *testing.T) {
			row, native := roomSample()
			delete(row, field)
			if err := compareRoom(row, native, true); err == nil {
				t.Fatalf("expected an error for a missing %q fact", field)
			}
		})
	}
}

func TestCompareRoomIncompleteGeometryAndCensusCannotPass(t *testing.T) {
	for _, fault := range []string{"duplicate", "missing", "center", "partial", "contents"} {
		t.Run(fault, func(t *testing.T) {
			row, native := roomSample()
			switch fault {
			case "duplicate":
				cells := row["cells"].([]any)
				first := cells[0].(map[string]any)
				row["cells"] = append(cells, copyRoomAny(first))
			case "missing":
				row["cells"] = []any{}
			case "center":
				row["center"] = map[string]any{"x": 99.0, "z": 99.0}
			case "partial":
				completeness := row["cellsCompleteness"].(map[string]any)
				page := completeness["page"].(map[string]any)
				page["complete"] = false
			default:
				native["contentsNotListed"] = 1.0
			}
			if err := compareRoom(row, native, true); err == nil {
				t.Fatalf("expected an error for fault %q", fault)
			}
		})
	}
}
