package policy

import (
	"encoding/json"
	"os"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Recorded from colony_policy.starter_layouts at db2223f0 with starterFixture's
// exact 40x40 native facts. This checks every retained site's geometry and order.
func TestStarterPythonReplay(t *testing.T) {
	data, err := os.ReadFile("testdata/starter-python.json")
	if err != nil {
		t.Fatal(err)
	}
	var expected []struct {
		Room, Storage Rectangle
		Farms         []Rectangle
	}
	if err = json.Unmarshal(data, &expected); err != nil {
		t.Fatal(err)
	}
	actual, err := StarterLayouts(starterFixture())
	if err != nil {
		t.Fatal(err)
	}
	if len(actual) != len(expected) {
		t.Fatal("candidate count differs")
	}
	for i, layout := range actual {
		want := expected[i]
		if layout.Room != want.Room || layout.Storage != want.Storage || !reflect.DeepEqual(layout.Farms, want.Farms) {
			t.Fatalf("candidate %d differs: %+v / %+v", i, layout, want)
		}
	}
}

func starterFixture() StarterRequest {
	r := StarterRequest{Bounds: Bounds{40, 40}, Anchor: domain.Cell{X: 20, Z: 20}, NutritionPerDay: domain.Known(5.0), CropGrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0), FertilityMin: domain.Known(.7)}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false), SupportsLight: domain.Known(true), Fertility: domain.Known(1.0)})
		}
	}
	return r
}
func TestStarterBoundedDeterministicGeometry(t *testing.T) {
	r := starterFixture()
	layouts, e := StarterLayouts(r)
	if e != nil {
		t.Fatal(e)
	}
	if len(layouts) != 12 || layouts[0].Room != (Rectangle{16, 16, 9, 9}) || layouts[0].TargetCells != domain.Known(38) {
		t.Fatal(layouts)
	}
	for _, l := range layouts {
		protected := map[domain.Cell]bool{}
		for _, p := range rectCells(Rectangle{l.Room.X - 1, l.Room.Z - 1, 11, 11}) {
			protected[p] = true
		}
		for _, p := range rectCells(Rectangle{l.Room.X, l.Room.Z - 5, 9, 4}) {
			protected[p] = true
		}
		for _, patch := range l.Farms {
			for _, p := range rectCells(patch) {
				if protected[p] {
					t.Fatal("overlap", p)
				}
				protected[p] = true
			}
		}
		if len(l.Farms) > 32 || l.SelectedCells < 38 {
			t.Fatal(l)
		}
	}
	for i, j := 0, len(r.Cells)-1; i < j; i, j = i+1, j-1 {
		r.Cells[i], r.Cells[j] = r.Cells[j], r.Cells[i]
	}
	again, e := StarterLayouts(r)
	if e != nil {
		t.Fatal(e)
	}
	if !reflect.DeepEqual(layouts, again) {
		t.Fatal("input order changed ranking")
	}
}
func TestStarterFragmentedFieldsAndProtectedCells(t *testing.T) {
	r := starterFixture()
	r.Protected = []domain.Cell{{X: 20, Z: 20}}
	for i := range r.Cells {
		c := &r.Cells[i]
		if c.Cell.X%2 == 0 || c.Cell.Z%2 == 0 {
			c.Fertility = domain.Known(0.0)
		}
	}
	l, e := StarterLayouts(r)
	if e != nil || len(l) == 0 {
		t.Fatal(e)
	}
	for _, s := range l {
		if len(s.Farms) != 32 || s.SelectedCells != 32 {
			t.Fatal("fragmented fields should retain useful partial capacity", s)
		}
		for _, p := range rectCells(s.Room) {
			if p == r.Protected[0] {
				t.Fatal("player exclusion lost")
			}
		}
	}
}
func TestStarterUnknownGeometryAndCrop(t *testing.T) {
	r := starterFixture()
	r.Cells = r.Cells[:8]
	l, e := StarterLayouts(r)
	if e != nil || len(l) != 0 {
		t.Fatal("missing cells admitted a room", l, e)
	}
	r = starterFixture()
	r.FertilityMin = domain.Unknown[float64]()
	l, e = StarterLayouts(r)
	if e != nil || len(l) == 0 || len(l[0].Farms) != 0 {
		t.Fatal(l, e)
	}
	r = starterFixture()
	r.Cells = append(r.Cells, r.Cells[0])
	if _, e = StarterLayouts(r); e == nil {
		t.Fatal("duplicate cell accepted")
	}
}
