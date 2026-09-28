package rooms

import "testing"

// roomSample is a single-cell room with known-zero/false facts throughout,
// to prove those legitimate falsy values are never confused with an absent
// field.
func roomSample() map[string]any {
	return map[string]any{
		"role": "None", "properRoom": true, "outdoors": false, "psychologicallyOutdoors": false,
		"touchesMapEdge": false, "fogged": false, "openRoofCount": 0.0, "cellCount": 1.0,
		"id": "0", "doorway": false, "temperatureC": 0.0, "label": "Room", "contents": []any{},
		"beds": []any{}, "pawns": []any{}, "stockpileZoneIds": []any{}, "stats": []any{},
		"cells": []any{map[string]any{"x": 1.0, "z": 1.0}}, "center": map[string]any{"x": 1.0, "z": 1.0},
		"extents":  map[string]any{"minimum": map[string]any{"x": 1.0, "z": 1.0}, "maximum": map[string]any{"x": 1.0, "z": 1.0}},
		"snapshot": map[string]any{"context": map[string]any{}, "entityId": "Room_0", "token": "tok"},
		"issues":   []any{},
	}
}

func TestCheckRoomKnownZeroAndFalseFactsPass(t *testing.T) {
	if err := checkRoom(roomSample(), true); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestCheckRoomMissingFactsDoNotBecomeDefaults(t *testing.T) {
	for _, field := range []string{"outdoors", "fogged", "openRoofCount", "temperatureC"} {
		t.Run(field, func(t *testing.T) {
			row := roomSample()
			delete(row, field)
			if err := checkRoom(row, true); err == nil {
				t.Fatalf("expected an error for a missing %q fact", field)
			}
		})
	}
}

func TestCheckRoomIncompleteGeometryCannotPass(t *testing.T) {
	for _, fault := range []string{"duplicate", "missing", "center"} {
		t.Run(fault, func(t *testing.T) {
			row := roomSample()
			switch fault {
			case "duplicate":
				row["cells"] = append(row["cells"].([]any), map[string]any{"x": 1.0, "z": 1.0})
			case "missing":
				row["cells"] = []any{}
			case "center":
				row["center"] = map[string]any{"x": 99.0, "z": 99.0}
			}
			if err := checkRoom(row, true); err == nil {
				t.Fatalf("expected an error for fault %q", fault)
			}
		})
	}
}
