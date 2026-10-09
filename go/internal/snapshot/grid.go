package snapshot

import (
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// Planning cells as a grid. A planning_cells section line holds the
// window as the wire's CellGrid (mirror.proto) rather than a row per cell:
//
//	{"Section": {"Name": "planning_cells", ..., "Key": true, "Grid": "<base64 proto>"}}
//
// over the rows' bounding rect, one array per policy.SiteCell field with
// the wire's sentinels (cell 0 not held, 1 held; bools 0 unknown, 1 false,
// 2 true; floats NaN unknown; strings 0 unknown, k for strings[k-1]). A
// keyframe carries every array; a delta, on the held rect, only the arrays
// that changed, each dense or sparse over the held array, whichever is
// smaller. Replay applies it with cellgrid.Apply and rebuilds the
// rows as Encode's JSON of its cells. The writer checks that rebuild
// against the rows it was given and falls back to the row form (Upserts,
// Removed, which older streams hold for planning_cells) when they differ,
// so a grid line always rebuilds its rows byte for byte.

// heldGrid is a planning window as the writer holds it: its arrays, and
// the grid as replay rebuilds it (the next delta's base).
type heldGrid struct {
	grid  *cellgrid.Grid
	built *cellgrid.Grid
	// cells caches built's cells for step lines (RecordStep).
	cells []policy.SiteCell
}

// gridRows applies a wire grid over held (nil for a keyframe) and is the
// rows it describes as the section holds them.
func gridRows(held *cellgrid.Grid, key bool, g *mp.CellGrid) (*cellgrid.Grid, map[string]json.RawMessage, map[string]json.RawMessage, error) {
	built, err := cellgrid.Apply(held, key, g)
	if err != nil {
		return nil, nil, nil, err
	}
	cells := built.Cells()
	rows := make(map[domain.Cell]policy.SiteCell, len(cells))
	for _, c := range cells {
		rows[c.Cell] = c
	}
	keys, out, err := encodeRows(rows)
	if err != nil {
		return nil, nil, nil, err
	}
	return built, keys, out, nil
}

// gridFrame is rows (encoded as keys and encoded) as a grid line over
// held (nil: a keyframe), with the grid the writer then holds; ok is false
// when the rows are not a grid or the grid would not rebuild them exactly.
func gridFrame(held *heldGrid, raw any, encoded map[string]json.RawMessage) ([]byte, *heldGrid, bool) {
	cells, ok := raw.(map[domain.Cell]policy.SiteCell)
	if !ok {
		return nil, nil, false
	}
	grid, err := cellgrid.FromCells(cells, 1<<16)
	if err != nil {
		return nil, nil, false
	}
	key := held == nil || held.grid.Rect != grid.Rect
	var base, built *cellgrid.Grid
	if !key {
		base, built = held.grid, held.built
	}
	return encodeGrid(&heldGrid{grid: grid}, key, base, built, encoded)
}

// encodeGrid writes next over base (nil: a keyframe) and checks it
// rebuilds encoded.
func encodeGrid(next *heldGrid, key bool, base, built *cellgrid.Grid, encoded map[string]json.RawMessage) ([]byte, *heldGrid, bool) {
	wire := next.grid.Wire(base)
	rebuilt, _, rows, err := gridRows(built, key, wire)
	if err != nil || len(rows) != len(encoded) {
		return nil, nil, false
	}
	for k, v := range encoded {
		if string(rows[k]) != string(v) {
			return nil, nil, false
		}
	}
	data, err := proto.MarshalOptions{Deterministic: true}.Marshal(wire)
	if err != nil {
		return nil, nil, false
	}
	next.built = rebuilt
	return data, next, true
}

// applyGrid lays a grid line over s (a replay).
func (s *recSection) applyGrid(f sectionFrame) (*recSection, error) {
	var held *cellgrid.Grid
	if !f.Key {
		if s == nil || s.grid == nil {
			return nil, fmt.Errorf("snapshot: section %s grid delta without a held grid", f.Name)
		}
		held = s.grid.built
	}
	var g mp.CellGrid
	if err := proto.Unmarshal(f.Grid, &g); err != nil {
		return nil, fmt.Errorf("snapshot: section %s grid: %w", f.Name, err)
	}
	built, keys, rows, err := gridRows(held, f.Key, &g)
	if err != nil {
		return nil, fmt.Errorf("snapshot: section %s grid: %w", f.Name, err)
	}
	return &recSection{scope: f.Scope, version: f.Version, asOf: f.AsOf, keys: keys, rows: rows, grid: &heldGrid{grid: built, built: built}}, nil
}
