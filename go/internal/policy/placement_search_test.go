package policy

import (
	"encoding/json"
	"os"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Recorded from development.placement at ad402967 with the exact native facts
// in placementSearchFixture; every native preview refused to expose all 64 sites.
func TestPlacementSearchPythonReplay(t *testing.T) {
	data, err := os.ReadFile("testdata/placement-python.json")
	if err != nil {
		t.Fatal(err)
	}
	var expected map[PlacementEnvironment][]domain.Cell
	if err = json.Unmarshal(data, &expected); err != nil {
		t.Fatal(err)
	}
	for _, environment := range []PlacementEnvironment{PlacementAnywhere, PlacementIndoors, PlacementOutdoors} {
		r := placementSearchFixture()
		r.Environment = environment
		s, err := NewPlacementSearch(r)
		if err != nil || !reflect.DeepEqual(s.Candidates(), expected[environment]) {
			t.Fatal(environment, s.Candidates(), err)
		}
	}
}

func placementSearchFixture() PlacementSearchRequest {
	r := PlacementSearchRequest{Snapshot: current(), Tick: 20, Bounds: Bounds{20, 20}, Center: domain.Cell{X: 10, Z: 10}, Environment: PlacementAnywhere, Radius: 22, Limit: 64}
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Indoors: domain.Known(x < 10), Roofed: domain.Known(true)})
		}
	}
	return r
}

func TestPlacementSearchRanksAndCopiesBoundedNativeCells(t *testing.T) {
	r := placementSearchFixture()
	s, err := NewPlacementSearch(r)
	if err != nil {
		t.Fatal(err)
	}
	got := s.Candidates()
	want := []domain.Cell{{X: 10, Z: 10}, {X: 9, Z: 10}, {X: 10, Z: 9}, {X: 10, Z: 11}, {X: 11, Z: 10}}
	if len(got) != 64 || !reflect.DeepEqual(got[:5], want) {
		t.Fatal(got)
	}
	got[0].X = 99
	r.Cells[210].Occupied = domain.Known(true)
	if !reflect.DeepEqual(s.Candidates()[:5], want) {
		t.Fatal("search aliases caller data")
	}
	for i, j := 0, len(r.Cells)-1; i < j; i, j = i+1, j-1 {
		r.Cells[i], r.Cells[j] = r.Cells[j], r.Cells[i]
	}
	r.Cells[len(r.Cells)-1-210].Occupied = domain.Known(false)
	reversed, err := NewPlacementSearch(r)
	if err != nil || !reflect.DeepEqual(s.Candidates(), reversed.Candidates()) {
		t.Fatal("input order changed ranking", err)
	}
}

func TestPlacementSearchUnknownRoomAndProtectedCells(t *testing.T) {
	r := placementSearchFixture()
	r.Environment = PlacementIndoors
	r.Cells[9*20+10].Indoors = domain.Unknown[bool]()
	r.Protected = []domain.Cell{{X: 9, Z: 9}}
	r.Cells[9*20+11].Occupied = domain.Unknown[bool]()
	r.Cells[8*20+10].Zone = domain.Unknown[bool]()
	s, err := NewPlacementSearch(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range s.Candidates() {
		if c.X >= 10 || c == (domain.Cell{X: 9, Z: 10}) || c == (domain.Cell{X: 9, Z: 9}) || c == (domain.Cell{X: 9, Z: 11}) || c == (domain.Cell{X: 8, Z: 10}) {
			t.Fatal("unknown/protected geometry became indoors", c)
		}
	}
	r.Cells = append(r.Cells, r.Cells[0])
	if _, err = NewPlacementSearch(r); err == nil {
		t.Fatal("duplicate census accepted")
	}
}

func placementPreview(t *testing.T, id domain.ActionID, c domain.Cell, cells ...domain.Cell) Preview {
	t.Helper()
	b, err := domain.NewBuilding("Bed", c, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(id, b)
	if err != nil {
		t.Fatal(err)
	}
	return Preview{Action: a, Snapshot: current(), Tick: 20, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]Amount{{Resource: "WoodLog", Count: 45}})}
}

func TestPlacementSelectionUsesWholeFootprintAndFreshSafeEvidence(t *testing.T) {
	r := placementSearchFixture()
	r.Protected = []domain.Cell{{X: 10, Z: 11}}
	s, err := NewPlacementSearch(r)
	if err != nil {
		t.Fatal(err)
	}
	center := placementPreview(t, "center", r.Center, r.Center, domain.Cell{X: 10, Z: 11})
	next := placementPreview(t, "next", domain.Cell{X: 9, Z: 10}, domain.Cell{X: 9, Z: 10}, domain.Cell{X: 9, Z: 11})
	selected, ok, err := s.Select("Bed", "WoodLog", []Preview{next, center})
	if err != nil || !ok || selected.Action != next.Action {
		t.Fatal(selected, ok, err)
	}
	cells, _ := selected.Footprint.Value()
	cells[0].X = 99
	costs, _ := selected.Costs.Value()
	costs[0].Count = 999
	selected, ok, err = s.Select("Bed", "WoodLog", []Preview{center, next})
	cells, _ = selected.Footprint.Value()
	costs, _ = selected.Costs.Value()
	if err != nil || !ok || cells[0].X == 99 || costs[0].Count != 45 {
		t.Fatal("returned evidence aliases preview")
	}
	next.SafeToPlace = domain.Unknown[bool]()
	if _, ok, err = s.Select("Bed", "WoodLog", []Preview{center, next}); err != nil || ok {
		t.Fatal("unknown safety selected", err)
	}
	next.Tick--
	if _, _, err = s.Select("Bed", "WoodLog", []Preview{next}); err == nil {
		t.Fatal("stale preview accepted")
	}
}
