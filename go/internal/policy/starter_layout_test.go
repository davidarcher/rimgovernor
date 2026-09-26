package policy

import (
	"encoding/json"
	"os"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Recorded from colony_policy.starter_layouts at db2223f0 with starterFixture's
// exact 40x40 native facts. This checks every retained site's room and storage
// geometry and order; farms come from the shared PlanFarmSites score instead
// of the recorded distance-first packing.
func TestStarterRecordedReplay(t *testing.T) {
	data, err := os.ReadFile("testdata/starter-recorded.json")
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
		if layout.Room != want.Room || layout.Storage != want.Storage {
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

func TestShellShapesAtDoorReproduceStarterShells(t *testing.T) {
	center := domain.Cell{X: 40, Z: 40}
	huts := HutTemplateShells(center)
	if len(huts) != len(hutTemplates) {
		t.Fatalf("templates %d", len(huts))
	}
	for i, hut := range huts {
		shapes := ShellShapesAtDoor(hut.Door(), ShelterHut)
		found := false
		for _, s := range shapes {
			found = found || domain.SameRoomFootprint(s, hut)
		}
		if !found {
			t.Fatalf("template %d not reproduced from its door %v", i, hut.Door())
		}
		for _, s := range shapes {
			if s.Door() != hut.Door() {
				t.Fatalf("shape door %v want %v", s.Door(), hut.Door())
			}
		}
	}
	rect, err := domain.RectangleFootprint(domain.RoomBounds{X: 16, Z: 16, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	shapes := ShellShapesAtDoor(rect.Door(), ShelterRectangle)
	if len(shapes) != 1+len(concaveTemplates) || !domain.SameRoomFootprint(shapes[0], rect) {
		t.Fatalf("rectangle style shapes %d", len(shapes))
	}
	for _, template := range concaveTemplates {
		shell, err := template.Shape(center)
		if err != nil {
			t.Fatal(template.Name, err)
		}
		found := false
		for _, s := range ShellShapesAtDoor(shell.Door(), ShelterRectangle) {
			found = found || domain.SameRoomFootprint(s, shell)
		}
		if !found {
			t.Fatalf("%s not reproduced from its door %v", template.Name, shell.Door())
		}
	}
	if shapes := ShellShapesAtDoor(domain.Cell{X: 1, Z: 0}, ShelterHut); len(shapes) != 0 {
		t.Fatalf("map-edge door produced %d shapes", len(shapes))
	}
}

func TestStarterHutGrowsAlongCorridorTerrainRows(t *testing.T) {
	// The corridor terrain fixture (scripts/fixtures/CorridorTerrainFixture.cs):
	// granite rows every sixth cell, each pierced by a walkway every twelfth
	// cell, adjacent rows offset by six. Five-cell strips fit no hut template
	// and no 9x9 rectangle, so the only shell is a grown one confined to a
	// strip; the walkways must not let a seven-row template through.
	r := starterFixture()
	r.Shelter = ShelterHut
	rock := func(c domain.Cell) bool {
		if (c.Z-20)%6 != 0 {
			return false
		}
		phase := int32(0)
		if ((c.Z-20)/6)%2 != 0 {
			phase = 6
		}
		return ((c.X-20)%12+12)%12 != phase
	}
	for i := range r.Cells {
		if rock(r.Cells[i].Cell) {
			r.Cells[i].Walkable = domain.Known(false)
			r.Cells[i].Occupied = domain.Known(true)
		}
	}
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) != 1 {
		t.Fatal(layouts, err)
	}
	grown := layouts[0]
	if len(grown.Shell.Interior()) != starterInterior || !grown.Shell.RoofSupported() {
		t.Fatalf("grown shell %v interior %d", grown.Room, len(grown.Shell.Interior()))
	}
	if b := grown.Shell.Bounds(); b.Height != 5 {
		t.Fatalf("grown shell %v is not confined to a five-cell strip", b)
	}
	for _, c := range grown.Shell.Cells() {
		if rock(c) {
			t.Fatalf("grown shell cell %v on a rock row", c)
		}
	}
}

func TestStarterTemplatesOpenOntoFreeGround(t *testing.T) {
	// A nine-cell strip of lit ground running east-west: the circle (eleven
	// tall) fails, the east-west oval (nine tall) fits only with its south
	// door against the blocked row, so the low east-west oval (seven tall)
	// is the hut.
	r := starterFixture()
	r.Shelter = ShelterHut
	block := func(low, high int32) {
		for i := range r.Cells {
			if r.Cells[i].Cell.Z < low || r.Cells[i].Cell.Z > high {
				r.Cells[i].SupportsLight = domain.Known(false)
				r.Cells[i].Walkable = domain.Known(false)
			}
		}
	}
	block(15, 23)
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) == 0 {
		t.Fatal(layouts, err)
	}
	low, _ := domain.EllipseFootprint(domain.Cell{X: 20, Z: 20}, 2, 6, domain.EllipseEastWest, domain.South)
	if !domain.SameRoomFootprint(layouts[0].Shell, low) {
		t.Fatalf("expected the low east-west oval, got %v door %v", layouts[0].Room, layouts[0].Shell.Door())
	}
	for _, l := range layouts {
		if !free(r, l.Shell.Threshold()) {
			t.Fatalf("shell %v opens onto blocked ground at %v", l.Room, l.Shell.Threshold())
		}
	}
	// One row wider and the medium east-west oval has a threshold.
	r = starterFixture()
	r.Shelter = ShelterHut
	block(15, 24)
	layouts, err = StarterLayouts(r)
	if err != nil || len(layouts) == 0 {
		t.Fatal(layouts, err)
	}
	medium, _ := domain.EllipseFootprint(domain.Cell{X: 20, Z: 20}, 3, 5, domain.EllipseEastWest, domain.South)
	if !domain.SameRoomFootprint(layouts[0].Shell, medium) {
		t.Fatalf("expected the medium east-west oval, got %v", layouts[0].Room)
	}
}

func free(r StarterRequest, c domain.Cell) bool {
	for _, cell := range r.Cells {
		if cell.Cell == c {
			return positive(cell.Walkable)
		}
	}
	return false
}

func TestStarterConcaveTemplatesWrapAnObstacle(t *testing.T) {
	// Lit ground only inside an L-shaped clearing around the anchor: no hut
	// and no 9x9 rectangle fits, so the L template with the matching notch
	// is sited before any footprint is grown, for either style.
	for _, style := range []ShelterStyle{ShelterHut, ShelterRectangle} {
		r := starterFixture()
		r.Shelter = style
		want, _ := concaveTemplates[1].Shape(domain.Cell{X: 20, Z: 20})
		clearing := map[domain.Cell]bool{}
		for _, c := range want.Cells() {
			clearing[c] = true
		}
		clearing[want.Threshold()] = true
		for i := range r.Cells {
			if !clearing[r.Cells[i].Cell] {
				r.Cells[i].SupportsLight = domain.Known(false)
			}
		}
		layouts, err := StarterLayouts(r)
		if err != nil || len(layouts) != 1 {
			t.Fatal(style, layouts, err)
		}
		if !domain.SameRoomFootprint(layouts[0].Shell, want) {
			t.Fatalf("%s: expected %s, got %v with %d cells", style, concaveTemplates[1].Name, layouts[0].Room, len(layouts[0].Shell.Interior()))
		}
		inside := map[domain.Cell]bool{}
		for _, c := range want.Interior() {
			inside[c] = true
		}
		for _, p := range rectCells(layouts[0].Storage) {
			if !inside[p] {
				t.Fatalf("storage cell %v outside the L", p)
			}
		}
	}
	// Two 4x4 clearings a cell apart take the connector room.
	r := starterFixture()
	r.Shelter = ShelterHut
	want, _ := concaveTemplates[4].Shape(domain.Cell{X: 20, Z: 20})
	clearing := map[domain.Cell]bool{}
	for _, c := range want.Cells() {
		clearing[c] = true
	}
	clearing[want.Threshold()] = true
	for i := range r.Cells {
		if !clearing[r.Cells[i].Cell] {
			r.Cells[i].SupportsLight = domain.Known(false)
		}
	}
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) != 1 || !domain.SameRoomFootprint(layouts[0].Shell, want) {
		t.Fatalf("connector: %v %v", layouts, err)
	}
}

// rockWest turns every column at or west of edge into natural rock, under
// roof when roof is set.
func rockWest(r StarterRequest, edge int32, roof string) StarterRequest {
	return rockOutside(r, edge, 1<<30, roof)
}

// rockOutside turns every column west of or at west, and east of or at
// east, into natural rock.
func rockOutside(r StarterRequest, west, east int32, roof string) StarterRequest {
	for i, c := range r.Cells {
		if c.Cell.X > west && c.Cell.X < east {
			continue
		}
		c.Walkable, c.Occupied, c.NaturalRock = domain.Known(false), domain.Known(true), domain.Known(true)
		if roof != "" {
			c.Roofed, c.Roof = domain.Known(true), domain.Known(roof)
		}
		r.Cells[i] = c
	}
	return r
}

// #700: rock beside the anchor walls the shell rather than pushing it onto
// the nearest clean ground.
func TestStarterShellReusesRock(t *testing.T) {
	layouts, err := StarterLayouts(rockWest(starterFixture(), 16, ""))
	if err != nil {
		t.Fatal(err)
	}
	best := layouts[0]
	if len(best.Reused) != 9 || len(best.Mined) != 0 {
		t.Fatalf("best site does not lean on the rock: %+v", best)
	}
	for _, p := range best.Reused {
		if p.X != 16 {
			t.Fatalf("reused open ground %v", p)
		}
	}
	if best.Shell.Door().X <= 16 {
		t.Fatalf("door on rock: %v", best.Shell.Door())
	}
}

// A strip of open ground too narrow for the shell is widened into the rock
// beside it: one side reused as wall, the other mined out of the room.
func TestStarterShellMinesRockWhereOpenGroundIsTooNarrow(t *testing.T) {
	layouts, err := StarterLayouts(rockOutside(starterFixture(), 16, 23, ""))
	if err != nil {
		t.Fatal(err)
	}
	best := layouts[0]
	if len(best.Reused) == 0 || len(best.Mined) == 0 {
		t.Fatalf("best site does not mix reuse and mining: %+v", best)
	}
	reused := map[domain.Cell]bool{}
	for _, p := range best.Reused {
		reused[p] = true
	}
	for _, p := range best.Mined {
		if p.X > 16 && p.X < 23 || reused[p] {
			t.Fatalf("mined %v", p)
		}
	}
	if d := best.Shell.Door(); d.X <= 16 || d.X >= 23 {
		t.Fatalf("door on rock: %v", d)
	}
}

// Rock under a thick roof is overhead mountain: the ring may lean on it,
// the interior never digs into it.
func TestStarterShellNeverMinesOverheadMountain(t *testing.T) {
	layouts, err := StarterLayouts(rockWest(starterFixture(), 16, "RoofRockThick"))
	if err != nil {
		t.Fatal(err)
	}
	for _, l := range layouts {
		if len(l.Mined) != 0 {
			t.Fatalf("mined overhead mountain: %+v", l)
		}
	}
	if len(layouts[0].Reused) == 0 {
		t.Fatalf("ring does not lean on the mountain: %+v", layouts[0])
	}
}

// Bunks never stand on the rock the shell still has to mine.
func TestShelterBunksAvoidMinedRock(t *testing.T) {
	layouts, err := StarterLayouts(rockOutside(starterFixture(), 16, 23, ""))
	if err != nil {
		t.Fatal(err)
	}
	layout := layouts[0]
	mined := map[domain.Cell]bool{}
	for _, c := range layout.Mined {
		mined[c] = true
	}
	bunks := PlanShelterBunks(layout, 3, 3, nil)
	for _, anchor := range append(bunks.Beds, bunks.Spots...) {
		for _, p := range BunkFootprint(anchor) {
			if mined[p] {
				t.Fatalf("bunk at %v stands on rock %v", anchor, p)
			}
		}
	}
	if len(bunks.Beds)+len(bunks.Spots) == 0 {
		t.Fatal("no bunk fits")
	}
}
