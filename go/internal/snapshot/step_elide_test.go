package snapshot

import (
	"bufio"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A step read whose planning cells equal the mirror's held cells is
// recorded without encoding them: its line names the section, its patch
// carries no cells, and it still materialises exactly what was read. A
// window that differs stays inline (#1590).
func TestRecordStepElidesHeldCells(t *testing.T) {
	base, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	m := facts.NewStore()
	m.SetRecorder(MirrorRecorder(dir))
	scope := facts.Scope{Load: "l", Map: 1, Generation: 1}
	rows := map[domain.Cell]policy.SiteCell{}
	for z := int32(0); z < 4; z++ {
		for x := int32(0); x < 4; x++ {
			rows[domain.Cell{X: x, Z: z}] = policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(x != 2)}
		}
	}
	name := string(facts.PlanningCells)
	facts.PutTable(m, scope, name, rows, facts.At(int64(base.Tick)))
	held := siteCells(rows)
	differs := append([]policy.SiteCell(nil), held[1:]...)

	var want []Step
	for i, cells := range [][]policy.SiteCell{held, differs, held} {
		reading := observation.ColonyProjection{Facts: base.Facts, Workers: domain.Known(i)}
		reading.Cells = cells
		reading.Identity.Tick = base.Tick
		if err = RecordStep(dir, "building", policy.MaintainCleanFacilities, base.Snapshot, reading); err != nil {
			t.Fatal(err)
		}
		var s Step
		data, _ := Encode(Step{Snapshot: base.Snapshot, Tick: base.Tick, Concern: policy.MaintainCleanFacilities, Planner: "building", Projection: reading})
		if err = Decode(data, &s); err != nil {
			t.Fatal(err)
		}
		s.Recorded = ""
		want = append(want, s)
	}

	paths, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
	if len(paths) != 1 {
		t.Fatal("want one stream, got", paths)
	}
	f, _ := os.Open(paths[0])
	defer f.Close()
	lines := bufio.NewScanner(f)
	lines.Buffer(nil, 1<<26)
	var named []bool
	for lines.Scan() {
		var line streamLine
		if err = json.Unmarshal(lines.Bytes(), &line); err != nil {
			t.Fatal(err)
		}
		if line.Step == nil {
			continue
		}
		named = append(named, line.Mirror[name] != 0)
		if line.Mirror[name] != 0 && strings.Contains(string(line.Step.Patch), `"Walkable"`) {
			t.Error("an elided step patch still carries the cells")
		}
	}
	if !reflect.DeepEqual(named, []bool{true, false, true}) {
		t.Fatalf("steps naming the cells' section: %v, want [true false true]", named)
	}
	for i := range want {
		got, err := LoadStreamStep(paths[0], StepRead{Planner: "building", Concern: policy.MaintainCleanFacilities, Tick: base.Tick, Seq: i + 1}.String())
		if err != nil {
			t.Fatal(err)
		}
		got.Recorded = ""
		if !reflect.DeepEqual(got, want[i]) {
			t.Errorf("step %d does not round-trip", i+1)
		}
	}
}
