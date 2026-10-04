package snapshot

import (
	"bufio"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func siteCells(rows map[domain.Cell]policy.SiteCell) []policy.SiteCell {
	var out []policy.SiteCell
	for z := int32(0); z < 4; z++ {
		for x := int32(0); x < 4; x++ {
			if row, ok := rows[domain.Cell{X: x, Z: z}]; ok {
				out = append(out, row)
			}
		}
	}
	return out
}

// The mirror's tables ride the stream as section lines, and a review or
// step read whose cells the mirror holds leaves them to it, yet every line
// materialises exactly what was recorded (#795 step 4).
func TestRecordStreamsMirrorSections(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	base, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	m := facts.NewStore()
	m.SetRecorder(MirrorRecorder(dir))
	scope := facts.Scope{Load: "l", Map: 1, Generation: 1}
	cells := map[domain.Cell]policy.SiteCell{}
	for z := int32(0); z < 4; z++ {
		for x := int32(0); x < 4; x++ {
			cells[domain.Cell{X: x, Z: z}] = policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(x != 2)}
		}
	}
	name := string(facts.PlanningCells)
	var want []Routine
	var wantSteps []Step
	for i := 0; i < KeyEvery+2; i++ {
		switch i % 3 {
		case 1: // a changed row and a dropped one
			next := map[domain.Cell]policy.SiteCell{}
			for k, v := range cells {
				next[k] = v
			}
			next[domain.Cell{X: 1, Z: 1}] = policy.SiteCell{Cell: domain.Cell{X: 1, Z: 1}, Roofed: domain.Known(i%2 == 0)}
			delete(next, domain.Cell{X: 3, Z: int32(i % 4)})
			cells = next
		case 2: // a new scope: a keyframe
			scope.Generation++
		}
		facts.PutTable(m, scope, name, cells, facts.At(int64(base.Tick)+int64(i)))
		facts.PutTable(m, scope, "buildings", map[string]int{"b1": i, "b2": 2}, facts.At(int64(base.Tick)+int64(i)))
		v, _ := Load(cleanFilthy)
		v.Tick = base.Tick + domain.Tick(i)
		// A loaded review's projection carries the review's Facts.
		v.Projection = &observation.ColonyProjection{Facts: v.Facts, Workers: domain.Known(i)}
		v.Projection.Cells = siteCells(cells)
		if i == 5 {
			v.Projection.Cells = v.Projection.Cells[1:] // not the mirror's: stays inline
		}
		if err = Record(dir, v); err != nil {
			t.Fatal(err)
		}
		want = append(want, v)
		reading := *v.Projection
		reading.Facts = v.Facts
		reading.Identity.Tick = v.Tick
		reading.Rooms = domain.Known(policy.RoomObservation{})
		if err = RecordStep(dir, "building", policy.MaintainCleanFacilities, v.Snapshot, reading); err != nil {
			t.Fatal(err)
		}
		var s Step
		data, _ := Encode(Step{Recorded: "", Snapshot: v.Snapshot, Tick: v.Tick, Goal: policy.MaintainCleanFacilities, Planner: "building", Projection: reading})
		if err = Decode(data, &s); err != nil {
			t.Fatal(err)
		}
		wantSteps = append(wantSteps, s)
	}
	paths, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
	if len(paths) != 1 {
		t.Fatal("want one stream per serve, got", paths)
	}
	elided := 0
	f, _ := os.Open(paths[0])
	lines := bufio.NewScanner(f)
	lines.Buffer(nil, 1<<26)
	for lines.Scan() {
		var line streamLine
		if err = json.Unmarshal(lines.Bytes(), &line); err != nil {
			t.Fatal(err)
		}
		if line.Section != nil && line.Section.Name == name && (line.Section.Grid == nil || len(line.Section.Upserts) > 0) {
			t.Error("planning cells not recorded as a grid at", line.Tick)
		}
		if line.Mirror[name] != 0 {
			elided++
		}
	}
	f.Close()
	if elided < 2*len(want)-4 {
		t.Error("cells elided on only", elided, "lines")
	}
	i := 0
	err = Replay(paths[0], nil, func(at Review, got Routine) (bool, error) {
		if !reflect.DeepEqual(got, want[i]) {
			t.Errorf("review %d (%s) does not round-trip", i, at)
		}
		i++
		return true, nil
	})
	if err != nil || i != len(want) {
		t.Fatal(i, err)
	}
	last := want[len(want)-1]
	held, err := MirrorAt(paths[0], Review{Tick: last.Tick})
	if err != nil || len(held[name].Rows) != len(last.Projection.Cells) || string(held["buildings"].Rows[`"b1"`]) != "21" {
		t.Fatal("mirror at the last review:", held["buildings"], err)
	}
	steps, err := Steps(paths[0])
	if err != nil || len(steps) != len(want) {
		t.Fatal(steps, err)
	}
	for i, s := range steps {
		got, err := LoadStreamStep(paths[0], s.String())
		if err != nil {
			t.Fatal(err)
		}
		got.Recorded = ""
		if !reflect.DeepEqual(got, wantSteps[i]) {
			t.Errorf("step %s does not round-trip", s)
		}
	}
	// The KeyEvery-th review is a sync point: every section is keyed again
	// before it, and the last review and step read replay from there alone.
	from, err := syncBefore(paths[0], reviewBefore(last.Tick, 0))
	if err != nil || from == 0 {
		t.Fatal("no sync point before the last review", from, err)
	}
	found := false
	err = replayFrom(paths[0], from, func(r Review) bool { return r.Tick == last.Tick }, func(_ Review, got Routine) (bool, error) {
		found = reflect.DeepEqual(got, last)
		return false, nil
	})
	if err != nil || !found {
		t.Fatal("the last review does not replay from its sync point", err)
	}
	var step Step
	err = walkFrom(paths[0], from, visitStep(steps[len(steps)-1].String(), &step, &found))
	if step.Recorded = ""; err != nil || !reflect.DeepEqual(step, wantSteps[len(wantSteps)-1]) {
		t.Fatal("the last step read does not replay from its sync point", err)
	}
}
