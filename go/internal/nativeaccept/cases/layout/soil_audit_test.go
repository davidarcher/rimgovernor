package layout

import (
	"compress/gzip"
	"encoding/json"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// The baseline survey (#1280) is the world layout/rich-soil runs on.
func baselineSurvey(t *testing.T) policy.MapSurvey {
	t.Helper()
	f, err := os.Open("../../../policy/testdata/baseline-survey.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	z, err := gzip.NewReader(f)
	if err != nil {
		t.Fatal(err)
	}
	var s policy.MapSurvey
	if err := json.NewDecoder(z).Decode(&s); err != nil {
		t.Fatal(err)
	}
	return s
}

// The fresh plan the case's colony derives passes the plan assertions
// offline, and one more colonist outgrows it (#1291).
func TestRichSoilBaselinePlan(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := baselineSurvey(t)
	for _, pawns := range []int{8} {
		plan, ok := policy.DeriveLayoutPlan(s, pawns, policy.BuildTierCamp, nil, 30, 0).Value()
		if !ok {
			t.Fatal("no plan")
		}
		a := auditSoil(plan, s, nil)
		t.Logf("%+v", a)
		t.Logf("rich overlap: %s", a.richOverlap())
		if err := a.err(); err != nil {
			t.Fatal(err)
		}
		if plan.LayoutOutgrown(pawns) {
			t.Fatal("the plan does not house the colony")
		}
		if a.RichCells == 0 || a.Patches == 0 || a.WallCells == 0 {
			t.Fatalf("baseline lacks rich soil, patches or a ring: %+v", a)
		}
	}
}

// Rooms and hallways may cover up to 2% of the rich cells, no more.
func TestRichOverlapBudget(t *testing.T) {
	// 200 rich cells in rows z >= 10; a one-row room at z=9 puts only its
	// top wall row (Width+2 cells) on them.
	const n = 20
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true,
				Fertility: map[bool]float64{true: 1.2, false: 1}[z >= 10]})
		}
	}
	room := func(w int32) policy.LayoutPlan {
		return policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage,
			Interior: policy.Rectangle{X: 4, Z: 9, Width: w, Height: 1}}}}
	}
	if a := auditSoil(room(2), s, nil); a.RichBuiltN != 4 || a.err() != nil {
		t.Fatalf("2%% overlap failed: %s %v", a.richOverlap(), a.err())
	}
	if a := auditSoil(room(3), s, nil); a.err() == nil {
		t.Fatalf("3%% overlap passed: %s", a.richOverlap())
	}
}

// Two field zones touching on one rich patch and crop blocks with a gap
// are each caught.
func TestAuditSoilCatchesSplitsAndGaps(t *testing.T) {
	const n = 60
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			fertility := 1.0
			if x >= 20 && x < 30 && z >= 20 && z < 30 {
				fertility = 1.4
			}
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: fertility})
		}
	}
	north, south := policy.LayoutZone{Kind: policy.ZoneField}, policy.LayoutZone{Kind: policy.ZoneField}
	for z := int32(20); z < 30; z++ {
		zone := &north
		if z >= 25 {
			zone = &south
		}
		zone.Runs = append(zone.Runs, policy.RowRun{Z: z, X: 20, Length: 10})
	}
	plan := policy.LayoutPlan{
		Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 22, Z: 22, Width: 2, Height: 2}}},
		Zones: []policy.LayoutZone{north, south},
	}
	a := auditSoil(plan, s, [][]domain.Cell{{{X: 20, Z: 20}}, {{X: 22, Z: 20}}})
	if len(a.PatchSplit) == 0 || len(a.CropGaps) == 0 {
		t.Fatalf("missed a violation: %+v", a)
	}
}

// plusSurvey is bare ground with a rich plus (not a rectangle) in the
// middle.
func plusSurvey(n int32) policy.MapSurvey {
	mid := n / 2
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			f := 0.5
			if (x >= mid-10 && x < mid+10 && z >= mid-4 && z < mid+4) || (x >= mid-4 && x < mid+4 && z >= mid-10 && z < mid+10) {
				f = 1.4
			}
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: f})
		}
	}
	return s
}

// The courtyard plan on a plus-shaped rich patch passes the audit: the
// patch is one field zone, nothing is built on it (#1960).
func TestAuditSoilCourtyardPlan(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := plusSurvey(140)
	plan, ok := policy.DeriveLayoutPlan(s, 8, policy.BuildTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	a := auditSoil(plan, s, nil)
	if err := a.err(); err != nil {
		t.Fatal(err, a)
	}
	if a.RichBuiltN != 0 || a.RichCells != 256 || a.Patches != 1 {
		t.Fatalf("courtyard audit: %+v", a)
	}
}

// A room standing on the thin neck of a non-rectangular patch is under the
// overlap budget but splits the patch in two: the audit catches it.
func TestAuditSoilCatchesPatchSplitByARoom(t *testing.T) {
	const n = 60
	// Two 20x20 rich blocks (x 10..29 and 33..52) joined by a 4x3 neck
	// (x 30..32, z 19..21 plus the blocks' edge columns).
	rich := func(x, z int32) bool {
		return z >= 10 && z < 30 && (x >= 10 && x < 30 || x >= 33 && x < 53) || z >= 19 && z < 22 && x >= 30 && x < 33
	}
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			f := 1.0
			if rich(x, z) {
				f = 1.4
			}
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: f})
		}
	}
	farm := func(room *policy.PlannedRoom) policy.LayoutPlan {
		zone := policy.LayoutZone{Kind: policy.ZoneField}
		for z := int32(10); z < 30; z++ {
			for x := int32(10); x < 53; x++ {
				if !rich(x, z) {
					continue
				}
				if room != nil && x >= room.Interior.X-1 && x <= room.Interior.X+1 && z >= room.Interior.Z-1 && z <= room.Interior.Z+1 {
					continue
				}
				zone.Runs = append(zone.Runs, policy.RowRun{Z: z, X: x, Length: 1})
			}
		}
		plan := policy.LayoutPlan{Zones: []policy.LayoutZone{zone}}
		if room != nil {
			plan.Rooms = []policy.PlannedRoom{*room}
		}
		return plan
	}
	if a := auditSoil(farm(nil), s, nil); a.err() != nil {
		t.Fatalf("the whole patch failed: %v", a.err())
	}
	room := policy.PlannedRoom{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 31, Z: 20, Width: 1, Height: 1}}
	a := auditSoil(farm(&room), s, nil)
	if a.RichOverlap > richOverlapBudget {
		t.Fatalf("the room is meant to stay inside the budget: %s", a.richOverlap())
	}
	if len(a.PatchSplit) == 0 || a.err() == nil {
		t.Fatalf("a patch split by a room passed: %+v", a)
	}
}
