package movement

import (
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// movementCells is a get_cells snapshot over x 0..6, z 0: every cell
// walkable except x 1, 4 and 5; edit adjusts the x=2 cell, or drops it
// (fogged) when it returns false.
func movementCells(t *testing.T, edit func(*policy.SiteCell) bool) map[string]any {
	t.Helper()
	cells := map[domain.Cell]policy.SiteCell{}
	for x := int32(0); x <= 6; x++ {
		cell := policy.SiteCell{Cell: domain.Cell{X: x}, Walkable: domain.Known(x != 1 && x != 4 && x != 5)}
		if x == 2 && edit != nil && !edit(&cell) {
			continue
		}
		cells[cell.Cell] = cell
	}
	grid, err := cellgrid.FromCells(cells, cellgrid.MaxCells)
	if err != nil {
		t.Fatal(err)
	}
	data, err := protojson.Marshal(&o.CellsSnapshot{Grid: grid.Wire(nil)})
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	if err := json.Unmarshal(data, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func TestCandidatesBoundedAndWalkableRequired(t *testing.T) {
	origin := map[string]any{"x": 0.0, "z": 0.0}
	got, err := candidates(movementCells(t, nil), origin)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := []map[string]any{{"x": 2.0, "z": 0.0}, {"x": 3.0, "z": 0.0}}
	if !na.DeepEqual(got, want) {
		t.Fatalf("unexpected candidates: %#v", got)
	}
	for name, edit := range map[string]func(*policy.SiteCell) bool{
		"unwalkable": func(c *policy.SiteCell) bool { c.Walkable = domain.Known(false); return true },
		"unknown":    func(c *policy.SiteCell) bool { c.Walkable = domain.Unknown[bool](); return true },
		"fogged":     func(*policy.SiteCell) bool { return false },
	} {
		got, err := candidates(movementCells(t, edit), origin)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if want := []map[string]any{{"x": 3.0, "z": 0.0}}; !na.DeepEqual(got, want) {
			t.Fatalf("%s: expected only the x=3 cell, got %#v", name, got)
		}
	}
}
