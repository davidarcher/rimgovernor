package layout

import (
	"compress/gzip"
	"encoding/json"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
	s := baselineSurvey(t)
	for _, pawns := range []int{8} {
		plan, ok := policy.DeriveLayoutPlan(s, pawns, policy.BuildTierCamp, nil).Value()
		if !ok {
			t.Fatal("no plan")
		}
		a := auditSoil(plan, s, nil)
		t.Logf("%+v outgrown(+1)=%v", a, plan.LayoutOutgrown(pawns+1))
		t.Logf("rich overlap: %s", a.richOverlap())
		if err := a.err(); err != nil {
			t.Fatal(err)
		}
		if !plan.LayoutOutgrown(pawns + 1) {
			t.Fatal("one more colonist does not outgrow the plan")
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
		return policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleStorage,
			Interior: policy.Rectangle{X: 4, Z: 9, Width: w, Height: 1}}}}
	}
	if a := auditSoil(room(2), s, nil); a.RichBuiltN != 4 || a.err() != nil {
		t.Fatalf("2%% overlap failed: %s %v", a.richOverlap(), a.err())
	}
	if a := auditSoil(room(3), s, nil); a.err() == nil {
		t.Fatalf("3%% overlap passed: %s", a.richOverlap())
	}
}

// A patch split off the margin line, a wall on fertile soil and crop
// blocks with a gap are each caught.
func TestAuditSoilCatchesSplitsAndGaps(t *testing.T) {
	const n = 60
	s := policy.MapSurvey{Bounds: policy.Bounds{Width: n, Height: n}}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			fertility := 1.0
			if x >= 20 && x < 30 && z >= 20 && z < 30 {
				fertility = 1.4 // the patch is rich: the wall takes in only rich soil
			}
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: fertility})
		}
	}
	field := policy.LayoutZone{Kind: policy.ZoneField}
	for z := int32(20); z < 30; z++ {
		field.Runs = append(field.Runs, policy.RowRun{Z: z, X: 20, Length: 10})
	}
	plan := policy.LayoutPlan{
		Rooms: []policy.LayoutRoom{{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 22, Z: 22, Width: 2, Height: 2}}},
		Zones: []policy.LayoutZone{field},
		// A closed ring through the patch's middle column.
		Reservations: []policy.LayoutReservation{
			{Kind: policy.ReservePerimeter, Area: policy.Rectangle{X: 15, Z: 15, Width: 11, Height: 1}},
			{Kind: policy.ReservePerimeter, Area: policy.Rectangle{X: 15, Z: 35, Width: 11, Height: 1}},
			{Kind: policy.ReservePerimeter, Area: policy.Rectangle{X: 15, Z: 15, Width: 1, Height: 21}},
			{Kind: policy.ReservePerimeter, Area: policy.Rectangle{X: 25, Z: 15, Width: 1, Height: 21}},
		},
	}
	a := auditSoil(plan, s, [][]domain.Cell{{{X: 20, Z: 20}}, {{X: 22, Z: 20}}})
	if len(a.PatchSplit) == 0 || len(a.WallOnFertile) == 0 || len(a.CropGaps) == 0 {
		t.Fatalf("missed a violation: %+v", a)
	}
}
