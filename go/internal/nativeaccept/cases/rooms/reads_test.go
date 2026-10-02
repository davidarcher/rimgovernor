package rooms

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// sampleGrid is the map grid's rooms: room "11" on the sample's cell.
func sampleGrid() map[string][]domain.Cell {
	return map[string][]domain.Cell{"11": {{X: 1, Z: 1}}}
}

// roomSample is a single-cell room with known-zero/false facts throughout,
// to prove those legitimate falsy values are never confused with an absent
// field.
func roomSample() map[string]any {
	return map[string]any{
		"role": "None", "properRoom": true, "outdoors": false, "psychologicallyOutdoors": false,
		"touchesMapEdge": false, "fogged": false, "openRoofCount": 0.0, "cellCount": 1.0,
		"id": "0", "doorway": false, "temperatureC": 0.0, "label": "Room", "contents": []any{},
		"beds": []any{}, "pawns": []any{}, "stockpileZones": []any{}, "stats": []any{},
		"gridRoom": "11", "center": map[string]any{"x": 1.0, "z": 1.0},
		"extents":  map[string]any{"minimum": map[string]any{"x": 1.0, "z": 1.0}, "maximum": map[string]any{"x": 1.0, "z": 1.0}},
		"snapshot": map[string]any{"context": map[string]any{}, "entityId": "Room_0", "token": "tok"},
		"issues":   []any{},
	}
}

func TestCheckRoomKnownZeroAndFalseFactsPass(t *testing.T) {
	if err := checkRoom(roomSample(), sampleGrid()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestCheckRoomMissingFactsDoNotBecomeDefaults(t *testing.T) {
	for _, field := range []string{"outdoors", "fogged", "openRoofCount", "temperatureC"} {
		t.Run(field, func(t *testing.T) {
			row, grid := roomSample(), sampleGrid()
			delete(row, field)
			if err := checkRoom(row, grid); err == nil {
				t.Fatalf("expected an error for a missing %q fact", field)
			}
		})
	}
}

func TestCheckRoomIncompleteGeometryCannotPass(t *testing.T) {
	for _, fault := range []string{"extra", "missing", "center"} {
		t.Run(fault, func(t *testing.T) {
			row, grid := roomSample(), sampleGrid()
			switch fault {
			case "extra":
				grid["11"] = append(grid["11"], domain.Cell{X: 2, Z: 1})
			case "missing":
				row["gridRoom"] = "12"
			case "center":
				row["center"] = map[string]any{"x": 99.0, "z": 99.0}
			}
			if err := checkRoom(row, grid); err == nil {
				t.Fatalf("expected an error for fault %q", fault)
			}
		})
	}
}
