// The cells/changed-since case proves the native per-cell change grid
// behind GetCellsRequest.changed_since_tick (issue #357): a full
// rimgovernor/observations_get_cells read of a clear 8x8 site stamps
// as_of_tick; the fixture then builds a wall, roofs a cell, sows a growing
// zone, drops a stack and lays a floor, and a read since the first read's
// tick lists those cells (and only a few neighbours the wall's room change
// touches) with unchanged covering the rest, so listed + unchanged equals
// the area; merging the delta over the first read reproduces a fresh full
// read row for row (drift 0). A second round after one game tick shows a
// read since the later tick omits the first round's cells while a read
// since the original tick lists both rounds.
package cells

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func init() {
	cases.Register(cases.Case{
		Name: "cells/changed-since",
		Scope: "Native per-cell change grid: after a full observations_get_cells read of a clear 8x8 site, a wall, roof, " +
			"growing zone, loose stack and floor laid directly by the fixture come back from a read with changed_since_tick " +
			"set to the first read's tick, unchanged counts the rest to the exact area, the delta merged over the first read " +
			"equals a fresh full read (drift 0), and after one game tick a read since the newer tick omits the older round.",
		Start:  cases.Fixture{Op: "test/cells_prepare"},
		Budget: 3 * time.Minute,
		Run:    run,
	})
}

type cell struct{ x, z int }

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/observations_get_cells", "test/cells_mutate"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", required)
		}
	}
	origin, _ := na.AsMap(prepared["origin"])
	ox, oz, size := int(na.AsNumber(origin["x"])), int(na.AsNumber(origin["z"])), int(na.AsNumber(prepared["size"]))
	if size <= 0 {
		return fmt.Errorf("prepare: missing fixture site: %#v", prepared)
	}
	area := size * size
	report["site"] = map[string]any{"x": ox, "z": oz, "size": size}

	// Exclude growth: daylight changes on the real tick between rounds, so
	// glow legitimately dirties otherwise untouched cells.
	// read asks for the whole site, since 0 meaning a full read, and
	// returns the rows by cell, the unchanged count and as_of_tick.
	read := func(label string, since int64) (map[cell]string, int, int64, error) {
		request := map[string]any{
			"scope":     map[string]any{"expectedIdentity": identity},
			"rectangle": map[string]any{"minimum": map[string]any{"x": ox, "z": oz}, "maximum": map[string]any{"x": ox + size - 1, "z": oz + size - 1}},
			"fields":    map[string]any{"terrain": true, "roof": true, "visibility": true, "traversal": true, "zone": true, "room": true, "growth": false},
			"page":      map[string]any{"limit": 256},
		}
		if since > 0 {
			request["changedSinceTick"] = fmt.Sprint(since)
		}
		reply, err := h.Wire(ctx, label, "observations_get_cells", request)
		if err != nil {
			return nil, 0, 0, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, 0, 0, err
		}
		// Compare compact planning coverage with the same full or delta read.
		// Terrain is outside the compact planning projection.
		request["compact"] = true
		request["fields"] = map[string]any{"terrain": false, "roof": true, "visibility": true, "traversal": true, "zone": true, "room": true, "growth": false}
		packed, err := h.Wire(ctx, label+"-compact", "observations_get_cells", request)
		if err != nil {
			return nil, 0, 0, err
		}
		_, compactObserved, err := na.Outcome(packed, "observed")
		if err != nil {
			return nil, 0, 0, err
		}
		decode := func(value map[string]any) (*o.CellsSnapshot, error) {
			data, err := json.Marshal(value)
			if err != nil {
				return nil, err
			}
			snapshot := &o.CellsSnapshot{}
			if err := protojson.Unmarshal(data, snapshot); err != nil {
				return nil, err
			}
			if err := bridge.ExpandCompactCells(snapshot); err != nil {
				return nil, err
			}
			snapshot.AppliedFields.Terrain = proto.Bool(false)
			for _, cell := range snapshot.Cells {
				cell.Terrain = nil
			}
			return snapshot, nil
		}
		plain, err := decode(observed)
		if err != nil {
			return nil, 0, 0, err
		}
		compact, err := decode(compactObserved)
		if err != nil {
			return nil, 0, 0, err
		}
		if !proto.Equal(plain, compact) {
			return nil, 0, 0, fmt.Errorf("%s: compact full/delta facts differ", label)
		}
		obsContext, _ := na.AsMap(observed["context"])
		asOf, tick := na.AsNumber(observed["asOfTick"]), na.AsNumber(obsContext["tick"])
		if observed["asOfTick"] == nil || asOf != tick {
			return nil, 0, 0, fmt.Errorf("%s: as_of_tick %v is not the context tick %v", label, observed["asOfTick"], tick)
		}
		unchanged := int(na.AsNumber(observed["unchanged"]))
		if observed["unchanged"] == nil {
			unchanged = 0
		}
		completeness, _ := na.AsMap(observed["completeness"])
		filtered := int(na.AsNumber(completeness["filtered"]))
		if completeness["filtered"] == nil {
			filtered = 0
		}
		rows := map[cell]string{}
		for _, raw := range na.AsSlice(observed["cells"]) {
			row, _ := na.AsMap(raw)
			at, _ := na.AsMap(row["cell"])
			// room_id is not a change the grid tracks (rooms are renumbered
			// on every region rebuild), so rows compare without it.
			delete(row, "roomId")
			encoded, err := json.Marshal(row)
			if err != nil {
				return nil, 0, 0, err
			}
			rows[cell{int(na.AsNumber(at["x"])), int(na.AsNumber(at["z"]))}] = string(encoded)
		}
		if len(rows)+unchanged+filtered != area {
			return nil, 0, 0, fmt.Errorf("%s: listed %d + unchanged %d + filtered %d != area %d", label, len(rows), unchanged, filtered, area)
		}
		return rows, unchanged, int64(tick), nil
	}
	mutate := func(label, phase string) ([]cell, int64, error) {
		result, err := h.Call(ctx, label, "test/cells_mutate", map[string]any{"originX": ox, "originZ": oz, "phase": phase})
		if err != nil {
			return nil, 0, err
		}
		if success, _ := na.AsBool(result["success"]); !success {
			return nil, 0, fmt.Errorf("%s: cells_mutate refused: %#v", label, result)
		}
		var touched []cell
		for _, raw := range na.AsSlice(result["cells"]) {
			at, _ := na.AsMap(raw)
			touched = append(touched, cell{int(na.AsNumber(at["x"])), int(na.AsNumber(at["z"]))})
		}
		return touched, int64(na.AsNumber(result["tick"])), nil
	}
	contains := func(rows map[cell]string, touched []cell) []cell {
		var missing []cell
		for _, c := range touched {
			if _, ok := rows[c]; !ok {
				missing = append(missing, c)
			}
		}
		return missing
	}
	overlap := func(rows map[cell]string, touched []cell) []cell {
		var listed []cell
		for _, c := range touched {
			if _, ok := rows[c]; ok {
				listed = append(listed, c)
			}
		}
		return listed
	}
	defer func() {
		if _, _, err := mutate("cleanup", "cleanup"); err != nil {
			report["cleanup_error"] = err.Error()
		}
	}()

	full0, unchanged0, tick0, err := read("full-before", 0)
	if err != nil {
		return err
	}
	if unchanged0 != 0 || len(full0) != area {
		return fmt.Errorf("full read: %d rows, unchanged %d", len(full0), unchanged0)
	}
	report["tick_before"] = tick0

	first, tickFirst, err := mutate("mutate-first", "first")
	if err != nil {
		return err
	}
	if tickFirst != tick0 {
		return fmt.Errorf("the first round ran at tick %d, not the paused tick %d", tickFirst, tick0)
	}
	delta1, unchanged1, _, err := read("delta-since-before", tick0)
	if err != nil {
		return err
	}
	report["first_round"] = map[string]any{"touched": len(first), "listed": len(delta1), "unchanged": unchanged1}
	if missing := contains(delta1, first); len(missing) != 0 {
		return fmt.Errorf("delta since %d omits mutated cells %v (listed %d, unchanged %d)", tick0, missing, len(delta1), unchanged1)
	}
	// The wall changes the region and room of its neighbours as well, so a
	// few cells beyond the touched ones may be listed; most must not be.
	if unchanged1 < area/2 {
		return fmt.Errorf("delta since %d lists %d cells for %d mutated (unchanged %d)", tick0, len(delta1), len(first), unchanged1)
	}
	full1, _, _, err := read("full-after", 0)
	if err != nil {
		return err
	}
	merged := map[cell]string{}
	for c, row := range full0 {
		merged[c] = row
	}
	for c, row := range delta1 {
		merged[c] = row
	}
	drift := 0
	for c, row := range full1 {
		if merged[c] != row {
			drift++
		}
	}
	report["drift"] = drift
	if drift != 0 {
		return fmt.Errorf("merging the delta over the first read drifts from a full read on %d of %d cells", drift, area)
	}

	second, tickSecond, err := mutate("mutate-second", "second")
	if err != nil {
		return err
	}
	if tickSecond != tick0+1 {
		return fmt.Errorf("the second round ran at tick %d, not %d", tickSecond, tick0+1)
	}
	delta2, unchanged2, _, err := read("delta-since-second", tickSecond)
	if err != nil {
		return err
	}
	report["second_round"] = map[string]any{"touched": len(second), "listed": len(delta2), "unchanged": unchanged2}
	if missing := contains(delta2, second); len(missing) != 0 {
		return fmt.Errorf("delta since %d omits mutated cells %v", tickSecond, missing)
	}
	if stale := overlap(delta2, first); len(stale) != 0 {
		return fmt.Errorf("delta since %d still lists the first round's cells %v", tickSecond, stale)
	}
	both, _, _, err := read("delta-since-before-again", tick0)
	if err != nil {
		return err
	}
	if missing := contains(both, append(append([]cell{}, first...), second...)); len(missing) != 0 {
		return fmt.Errorf("delta since %d omits cells %v of both rounds", tick0, missing)
	}
	return nil
}
