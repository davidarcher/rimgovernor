package snapshot

import (
	"bufio"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// regrid records tables (a planning_cells history) into a fresh stream,
// replays it, and checks every recorded version rebuilds its rows byte for
// byte; it returns the stream's path.
func regrid(t *testing.T, tables []map[domain.Cell]policy.SiteCell, scopes []facts.Scope) string {
	t.Helper()
	dir := t.TempDir()
	name := string(facts.PlanningCells)
	var want []map[string]json.RawMessage
	for i, rows := range tables {
		_, encoded, err := encodeRows(rows)
		if err != nil {
			t.Fatal(err)
		}
		want = append(want, encoded)
		recordSection(dir, facts.Published{Section: name, Version: uint64(i + 1), AsOf: facts.At(int64(i + 1)), Scope: scopes[i], Rows: rows})
		// A review every table, so KeyEvery sync points rekey the grid.
		streamsMu.Lock()
		rec := streams[dir]
		if rec.lines%KeyEvery == 0 && rec.keyed {
			rec.keySections()
		}
		rec.lines++
		rec.keyed = true
		streamsMu.Unlock()
	}
	streamsMu.Lock()
	path := streams[dir].path
	delete(streams, dir)
	streamsMu.Unlock()
	grids, versions := 0, 0
	err := walk(path, func(line streamLine, st *replayState) (bool, error) {
		if line.Section == nil {
			return true, nil
		}
		if line.Section.Grid != nil {
			grids++
		}
		s := st.sections[name]
		got, wantRows := s.rows, want[s.version-1]
		if len(got) != len(wantRows) {
			t.Fatalf("version %d: %d rows, want %d", s.version, len(got), len(wantRows))
		}
		for k, v := range wantRows {
			if string(got[k]) != string(v) {
				t.Fatalf("version %d row %s: %s, want %s", s.version, k, got[k], v)
			}
		}
		versions++
		return true, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if versions < len(tables) || grids == 0 {
		t.Fatalf("%d versions replayed of %d, %d grid lines", versions, len(tables), grids)
	}
	return path
}

// Every committed planning window records as a grid and rebuilds exactly,
// through changed facts, dropped rows, a scope change and a sync point.
func TestGridRoundTripsCommittedCells(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	paths, _ := filepath.Glob("testdata/*.json*")
	windows := 0
	for _, path := range paths {
		r, err := Load(path)
		if err != nil || r.Projection == nil || len(r.Projection.Cells) == 0 {
			continue
		}
		windows++
		t.Run(filepath.Base(path), func(t *testing.T) {
			cells := map[domain.Cell]policy.SiteCell{}
			for _, c := range r.Projection.Cells {
				cells[c.Cell] = c
			}
			scope := facts.Scope{Load: "l", Map: 1, Generation: 1}
			var tables []map[domain.Cell]policy.SiteCell
			var scopes []facts.Scope
			for i := 0; i < KeyEvery+5; i++ {
				next := map[domain.Cell]policy.SiteCell{}
				for k, v := range cells {
					next[k] = v
				}
				n := 0
				for k, v := range next {
					if n++; n%(7+i) != 0 {
						continue
					}
					switch i % 4 {
					case 0:
						v.Walkable = domain.Known(i%2 == 0)
						v.Glow = domain.Known(float64(i) / 3)
					case 1:
						v.ZoneID = domain.Known("Zone_" + string(rune('a'+i)))
						v.RuinHold = "hold"
					case 2:
						delete(next, k)
						continue
					case 3:
						v.Roof = domain.Unknown[string]()
						v.Fertility = domain.Unknown[float64]()
					}
					next[k] = v
				}
				if i == 9 {
					scope.Generation++
				}
				cells = next
				tables, scopes = append(tables, next), append(scopes, scope)
			}
			regrid(t, tables, scopes)
		})
	}
	if windows == 0 {
		t.Fatal("no committed planning window")
	}
}

// A recorded stream (RIMGOVERNOR_GRID_STREAMS, a path list) re-records as
// grids and rebuilds every planning_cells version exactly; the sizes of
// both are logged.
func TestGridRoundTripsLiveStream(t *testing.T) {
	list := os.Getenv("RIMGOVERNOR_GRID_STREAMS")
	if list == "" {
		t.Skip("RIMGOVERNOR_GRID_STREAMS names no stream")
	}
	name := string(facts.PlanningCells)
	for _, path := range filepath.SplitList(list) {
		var tables []map[domain.Cell]policy.SiteCell
		var scopes []facts.Scope
		err := walk(path, func(line streamLine, st *replayState) (bool, error) {
			if line.Section == nil || line.Section.Name != name {
				return true, nil
			}
			rows := map[domain.Cell]policy.SiteCell{}
			for _, raw := range st.sections[name].rows {
				var c policy.SiteCell
				if err := Decode(raw, &c); err != nil {
					return false, err
				}
				rows[c.Cell] = c
			}
			tables, scopes = append(tables, rows), append(scopes, line.Section.Scope)
			return true, nil
		})
		if err != nil {
			t.Fatal(err)
		}
		t.Logf("%s: %d planning_cells tables", path, len(tables))
		logSizes(t, "recorded", path)
		logRowForm(t, tables, scopes)
		logSizes(t, "regridded", regrid(t, tables, scopes))
	}
}

func logSizes(t *testing.T, label, path string) {
	in, done, err := openStreamFile(path)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	lines := bufio.NewReader(in)
	var total, key, delta int
	for {
		raw, err := lines.ReadBytes('\n')
		total += len(raw)
		if strings.Contains(string(raw[:min(len(raw), 200)]), `"Name":"planning_cells"`) {
			if strings.Contains(string(raw), `"Key":true`) {
				key += len(raw)
			} else {
				delta += len(raw)
			}
		}
		if err != nil {
			break
		}
	}
	t.Logf("%s: total %d, planning_cells keyframes %d, deltas %d", label, total, key, delta)
}

// logRowForm logs the planning_cells bytes tables take as row-form lines,
// the format before grids.
func logRowForm(t *testing.T, tables []map[domain.Cell]policy.SiteCell, scopes []facts.Scope) {
	var key, delta int
	var held map[string]json.RawMessage
	for i, table := range tables {
		keys, rows, err := encodeRows(table)
		if err != nil {
			t.Fatal(err)
		}
		f := sectionFrame{Name: string(facts.PlanningCells), Scope: scopes[i], Key: held == nil || scopes[i] != scopes[i-1]}
		for _, k := range sortedKeys(rows) {
			if old, ok := held[k]; f.Key || !ok || string(old) != string(rows[k]) {
				f.Upserts = append(f.Upserts, [2]json.RawMessage{keys[k], rows[k]})
			}
		}
		for _, k := range sortedKeys(held) {
			if _, ok := rows[k]; !ok && !f.Key {
				f.Removed = append(f.Removed, json.RawMessage(k))
			}
		}
		data, _ := json.Marshal(streamLine{Section: &f})
		if f.Key {
			key += len(data) + 1
		} else {
			delta += len(data) + 1
		}
		held = rows
	}
	t.Logf("row form: planning_cells keyframes %d, deltas %d", key, delta)
}
