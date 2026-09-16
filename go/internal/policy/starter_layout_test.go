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

func TestStarterHutStylePrefersOvalTemplates(t *testing.T) {
	r := starterFixture()
	r.Shelter = ShelterHut
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) == 0 {
		t.Fatal(layouts, err)
	}
	want, _ := domain.EllipseFootprint(domain.Cell{X: 20, Z: 20}, 4, 4, domain.EllipseNorthSouth, domain.South)
	first := layouts[0]
	if !domain.SameRoomFootprint(first.Shell, want) {
		t.Fatalf("first hut %+v, want circle at the anchor", first.Shell.Bounds())
	}
	if first.Room != (Rectangle{15, 15, 11, 11}) {
		t.Fatalf("hut bounds %v", first.Room)
	}
	inside := map[domain.Cell]bool{}
	for _, c := range first.Shell.Interior() {
		inside[c] = true
	}
	for _, p := range rectCells(first.Storage) {
		if !inside[p] {
			t.Fatalf("storage cell %v outside the hut", p)
		}
	}
	for _, l := range layouts {
		if !l.Shell.RoofSupported() || l.Shell.Entrance() != domain.South {
			t.Fatal("hut must roof itself and face south", l.Shell.Bounds())
		}
		for _, patch := range l.Farms {
			for _, p := range rectCells(patch) {
				if inside[p] {
					t.Fatal("farm inside hut", p)
				}
			}
		}
	}
	// Rectangle style is untouched by the new field.
	r.Shelter = ShelterRectangle
	plain, err := StarterLayouts(r)
	if err != nil || plain[0].Room != (Rectangle{16, 16, 9, 9}) {
		t.Fatal(plain, err)
	}
	rect, _ := domain.RectangleFootprint(domain.RoomBounds{X: 16, Z: 16, Width: 9, Height: 9}, domain.South)
	if !domain.SameRoomFootprint(plain[0].Shell, rect) {
		t.Fatal("rectangle shell differs from its bounds")
	}
}

func TestStarterHutNarrowsThenGrowsFootprint(t *testing.T) {
	// Only a nine-wide strip of light-supporting ground: the circle of radius
	// four fails and the narrow north-south oval is taken.
	r := starterFixture()
	r.Shelter = ShelterHut
	for i := range r.Cells {
		if r.Cells[i].Cell.X < 15 || r.Cells[i].Cell.X > 23 {
			r.Cells[i].SupportsLight = domain.Known(false)
		}
	}
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) == 0 {
		t.Fatal(layouts, err)
	}
	narrow, _ := domain.EllipseFootprint(domain.Cell{X: 19, Z: 20}, 3, 5, domain.EllipseNorthSouth, domain.South)
	if !domain.SameRoomFootprint(layouts[0].Shell, narrow) {
		t.Fatalf("expected the narrow oval, got %v", layouts[0].Room)
	}
	// A five-wide corridor fits neither template: grow an irregular footprint.
	for i := range r.Cells {
		if r.Cells[i].Cell.X < 15 || r.Cells[i].Cell.X > 19 {
			r.Cells[i].SupportsLight = domain.Known(false)
		}
	}
	layouts, err = StarterLayouts(r)
	if err != nil || len(layouts) != 1 {
		t.Fatal(layouts, err)
	}
	grown := layouts[0]
	if grown.Room.Width != 5 || len(grown.Shell.Interior()) != starterInterior || !grown.Shell.RoofSupported() {
		t.Fatalf("grown shell %v interior %d", grown.Room, len(grown.Shell.Interior()))
	}
	for _, c := range grown.Shell.Cells() {
		if c.X < 15 || c.X > 19 {
			t.Fatalf("grown shell cell %v off the lit strip", c)
		}
	}
	if grown.Storage.Width != 3 || grown.Storage.X < 16 || grown.Storage.X+3 > 19 {
		t.Fatalf("storage %v not inside the grown room", grown.Storage)
	}
	for i, j := 0, len(r.Cells)-1; i < j; i, j = i+1, j-1 {
		r.Cells[i], r.Cells[j] = r.Cells[j], r.Cells[i]
	}
	again, err := StarterLayouts(r)
	if err != nil || !reflect.DeepEqual(layouts, again) {
		t.Fatal("grown footprint depends on input order")
	}
}
