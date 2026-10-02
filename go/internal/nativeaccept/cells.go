package nativeaccept

import (
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
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
