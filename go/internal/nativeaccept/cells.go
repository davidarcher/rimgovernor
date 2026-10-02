package nativeaccept

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// DecodeCells is an observations_get_cells reply's observed snapshot (as
// Outcome returns it) and its grid decoded (#1346).
func DecodeCells(observed map[string]any) (*o.CellsSnapshot, *cellgrid.Grid, error) {
	data, err := json.Marshal(observed)
	if err != nil {
		return nil, nil, err
	}
	snapshot := &o.CellsSnapshot{}
	if err := protojson.Unmarshal(data, snapshot); err != nil {
		return nil, nil, fmt.Errorf("cells snapshot: %w", err)
	}
	grid, err := cellgrid.Apply(nil, true, snapshot.GetGrid())
	if err != nil {
		return nil, nil, err
	}
	return snapshot, grid, nil
}

// MapCells is the whole map's cell grid, read through
// observations_get_cells: an anchor read for the map's size, then the map.
func (h *Harness) MapCells(ctx context.Context, label string, identity map[string]any) (*cellgrid.Grid, error) {
	read := func(suffix string, maxX, maxZ int64) (*o.CellsSnapshot, *cellgrid.Grid, error) {
		reply, err := h.Wire(ctx, label+suffix, "observations_get_cells", map[string]any{"scope": map[string]any{"expectedIdentity": identity},
			"rectangle": map[string]any{"minimum": map[string]any{"x": 0, "z": 0}, "maximum": map[string]any{"x": maxX, "z": maxZ}}})
		if err != nil {
			return nil, nil, err
		}
		_, observed, err := Outcome(reply, "observed")
		if err != nil {
			return nil, nil, err
		}
		return DecodeCells(observed)
	}
	anchor, _, err := read("-size", 0, 0)
	if err != nil {
		return nil, err
	}
	_, grid, err := read("", int64(anchor.GetMapSize().GetWidth())-1, int64(anchor.GetMapSize().GetHeight())-1)
	return grid, err
}

// RoomCells are a grid's held cells by room key (a room row's gridRoom),
// row-major.
func RoomCells(grid *cellgrid.Grid) map[string][]domain.Cell {
	out := map[string][]domain.Cell{}
	for _, cell := range grid.Cells() {
		if key, known := cell.Room.Value(); known {
			out[key] = append(out[key], cell.Cell)
		}
	}
	return out
}

// ZoneCells are a grid's held cells by zone id, row-major.
func ZoneCells(grid *cellgrid.Grid) map[string][]domain.Cell {
	out := map[string][]domain.Cell{}
	for _, cell := range grid.Cells() {
		if id, known := cell.ZoneID.Value(); known {
			out[id] = append(out[id], cell.Cell)
		}
	}
	return out
}
