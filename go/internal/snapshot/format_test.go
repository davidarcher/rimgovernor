package snapshot

import (
	"encoding/json"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A section line's Format names its form: 1 rows, 2 a CellGrid, absent the
// legacy row form; anything else fails the load rather than being guessed.
func TestSectionLineFormat(t *testing.T) {
	cells := map[domain.Cell]policy.SiteCell{}
	for z := int32(0); z < 3; z++ {
		for x := int32(0); x < 3; x++ {
			cells[domain.Cell{X: x, Z: z}] = policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(x != 1)}
		}
	}
	keys, rows, err := encodeRows(cells)
	if err != nil {
		t.Fatal(err)
	}
	name := string(facts.PlanningCells)
	legacy := sectionFrame{Name: name, Version: 1, Key: true}
	for _, k := range sortedKeys(rows) {
		legacy.Upserts = append(legacy.Upserts, [2]json.RawMessage{keys[k], rows[k]})
	}
	data, _, ok := gridFrame(nil, cells, rows)
	if !ok {
		t.Fatal("cells do not grid")
	}
	gridLine := sectionFrame{Name: name, Version: 1, Key: true, Grid: data}

	stamped := legacy
	stamped.stamp()
	grid := gridLine
	grid.stamp()
	if stamped.Format != FormatRows || grid.Format != FormatGrid {
		t.Fatalf("stamp: rows %d grid %d", stamped.Format, grid.Format)
	}
	for label, f := range map[string]sectionFrame{"rows": stamped, "grid": grid} {
		// Through JSON, as a stream holds it.
		raw, err := json.Marshal(f)
		if err != nil {
			t.Fatal(err)
		}
		var back sectionFrame
		if err := json.Unmarshal(raw, &back); err != nil {
			t.Fatal(err)
		}
		got, err := (*recSection)(nil).apply(back)
		if err != nil {
			t.Fatalf("%s: %v", label, err)
		}
		if !reflect.DeepEqual(got.rows, rows) {
			t.Fatalf("%s: rows differ", label)
		}
	}

	with := func(f sectionFrame, format int) sectionFrame { f.Format = format; return f }
	for _, c := range []struct {
		f    sectionFrame
		want string
	}{
		{legacy, "unknown Format 0"},
		{with(stamped, 7), "unknown Format 7"},
		{with(gridLine, FormatRows), "with a Grid"},
		{with(stamped, FormatGrid), "with no Grid"},
	} {
		if _, err := (*recSection)(nil).apply(c.f); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Fatalf("Format %d: err %v, want %q", c.f.Format, err, c.want)
		}
	}
}
