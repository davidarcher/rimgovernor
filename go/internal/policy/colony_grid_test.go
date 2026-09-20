package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gridAt(x, z int32) ColonyGrid {
	return ColonyGrid{Origin: domain.Cell{X: x, Z: z}, Pitch: GridPitch, Axes: ColonyGridAxes}
}

func wallCensus(cells ...domain.Cell) CurrentConstruction {
	census := CurrentConstruction{Colony: true}
	for i, c := range cells {
		b, err := domain.NewBuilding("Wall", c, domain.North, "WoodLog")
		if err != nil {
			panic(err)
		}
		census.Buildings = append(census.Buildings, CurrentBuilding{ID: string(rune('a' + i)), Building: b, Cells: []domain.Cell{c}})
	}
	return census
}

func ringCells(x, z, w, h int32) []domain.Cell {
	var out []domain.Cell
	for dx := int32(0); dx < w; dx++ {
		for dz := int32(0); dz < h; dz++ {
			if dx == 0 || dz == 0 || dx == w-1 || dz == h-1 {
				out = append(out, domain.Cell{X: x + dx, Z: z + dz})
			}
		}
	}
	return out
}

func TestDeriveColonyGridStarterShell(t *testing.T) {
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: 40, Z: 50, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	census := wallCensus(ringCells(10, 10, 20, 20)...)
	for _, tier := range []BuildTier{BuildTierCamp, BuildTierMasonry, BuildTierSpacer} {
		got, known := DeriveColonyGrid(domain.Known(shell), census, tier).Value()
		want := ColonyGrid{Origin: domain.Cell{X: 40, Z: 50}, Pitch: 16, Axes: ColonyGridAxes, Source: ColonyGridFromStarter}
		if !known || got != want {
			t.Fatalf("tier %v: got %+v known=%t, want %+v", tier, got, known, want)
		}
	}
	again, _ := DeriveColonyGrid(domain.Known(shell), census, BuildTierCamp).Value()
	if again != (ColonyGrid{Origin: domain.Cell{X: 40, Z: 50}, Pitch: 16, Axes: ColonyGridAxes, Source: ColonyGridFromStarter}) {
		t.Fatalf("derivation is not deterministic: %+v", again)
	}
}

func TestDeriveColonyGridLargestRoom(t *testing.T) {
	small := ringCells(5, 5, 4, 4)
	large := ringCells(30, 20, 7, 6)
	census := wallCensus(append(small, large...)...)
	got, known := DeriveColonyGrid(domain.Unknown[domain.RoomFootprint](), census, BuildTierMasonry).Value()
	if !known || got.Origin != (domain.Cell{X: 30, Z: 20}) || got.Source != ColonyGridFromRoom || got.Pitch != 16 {
		t.Fatalf("got %+v known=%t", got, known)
	}
	// A bench beside the ring is not a wall and does not move the origin.
	bench, _ := domain.NewBuilding("TableButcher", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	census.Buildings = append(census.Buildings, CurrentBuilding{ID: "bench", Building: bench, Cells: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}})
	if again, _ := DeriveColonyGrid(domain.Unknown[domain.RoomFootprint](), census, BuildTierMasonry).Value(); again != got {
		t.Fatalf("furniture moved the origin: %+v", again)
	}
}

func TestDeriveColonyGridUnknown(t *testing.T) {
	if _, known := DeriveColonyGrid(domain.Unknown[domain.RoomFootprint](), CurrentConstruction{Colony: true}, BuildTierCamp).Value(); known {
		t.Fatal("no shell and no walls must be unknown")
	}
	partial := wallCensus(ringCells(0, 0, 3, 3)...)
	partial.Colony = false
	if _, known := DeriveColonyGrid(domain.Unknown[domain.RoomFootprint](), partial, BuildTierCamp).Value(); known {
		t.Fatal("an exact refresh is not a colony census")
	}
}

func TestColonyGridSnapAndCornerError(t *testing.T) {
	g := gridAt(3, 5)
	cases := []struct {
		cell domain.Cell
		snap domain.Cell
		err  int
	}{
		{domain.Cell{X: 3, Z: 5}, domain.Cell{X: 3, Z: 5}, 0},
		{domain.Cell{X: 10, Z: 12}, domain.Cell{X: 3, Z: 5}, 14},
		{domain.Cell{X: 12, Z: 14}, domain.Cell{X: 19, Z: 21}, 14},
		{domain.Cell{X: 11, Z: 13}, domain.Cell{X: 3, Z: 5}, 16},
		{domain.Cell{X: 0, Z: 0}, domain.Cell{X: 3, Z: 5}, 8},
		{domain.Cell{X: 250, Z: 249}, domain.Cell{X: 243, Z: 245}, 11},
	}
	for _, c := range cases {
		if got := g.Snap(c.cell); got != c.snap {
			t.Errorf("Snap(%v) = %v, want %v", c.cell, got, c.snap)
		}
		r := Rectangle{X: c.cell.X, Z: c.cell.Z, Width: 13, Height: 13}
		if got := g.CornerError(r); got != c.err {
			t.Errorf("CornerError(%v) = %d, want %d", c.cell, got, c.err)
		}
		if g.OnGridLine(r) != (c.err == 0) {
			t.Errorf("OnGridLine(%v) disagrees with CornerError", c.cell)
		}
	}
	if (ColonyGrid{}).Snap(domain.Cell{X: 7, Z: 7}) != (domain.Cell{X: 7, Z: 7}) || (ColonyGrid{}).OnGridLine(Rectangle{}) {
		t.Fatal("an invalid grid snaps nothing")
	}
}

func TestColonyGridFlippedAxes(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 20, Z: 20}, Pitch: 16, Axes: [2]domain.Cell{{X: 0, Z: -1}, {X: 1, Z: 0}}}
	if !g.Valid() {
		t.Fatal("perpendicular unit axes are valid")
	}
	if got := g.Snap(domain.Cell{X: 22, Z: 6}); got != (domain.Cell{X: 20, Z: 4}) {
		t.Fatalf("Snap under flipped axes = %v", got)
	}
	if err := g.CornerError(Rectangle{X: 22, Z: 6}); err != 4 {
		t.Fatalf("CornerError under flipped axes = %d", err)
	}
	module := g.Module(domain.Cell{X: 20, Z: 20})
	if module != (Rectangle{X: 20, Z: 8, Width: 13, Height: 13}) {
		t.Fatalf("Module under flipped axes = %+v", module)
	}
	sub := g.SubCells(module)
	if len(sub) != 9 || sub[0] != (Rectangle{X: 21, Z: 9, Width: 11, Height: 11}) {
		t.Fatalf("SubCells under flipped axes = %+v", sub)
	}
	for _, bad := range [][2]domain.Cell{{{X: 1, Z: 0}, {X: 1, Z: 0}}, {{X: 1, Z: 1}, {X: 0, Z: 1}}, {{X: 2, Z: 0}, {X: 0, Z: 1}}} {
		if (ColonyGrid{Pitch: 16, Axes: bad}).Valid() {
			t.Errorf("axes %v must be invalid", bad)
		}
	}
}

func TestColonyGridAisles(t *testing.T) {
	g := gridAt(0, 0)
	cells := g.Aisles(Bounds{Width: 20, Height: 20})
	set := map[domain.Cell]bool{}
	for i, c := range cells {
		set[c] = true
		if i > 0 && !extentCellLess(cells[i-1], c) {
			t.Fatalf("aisles unsorted at %d: %v", i, cells[i-1:i+1])
		}
	}
	for _, c := range []domain.Cell{{X: 13, Z: 0}, {X: 15, Z: 7}, {X: 4, Z: 14}, {X: 14, Z: 14}, {X: 13, Z: 19}} {
		if !set[c] {
			t.Errorf("%v should be an aisle cell", c)
		}
	}
	for _, c := range []domain.Cell{{X: 0, Z: 0}, {X: 12, Z: 12}, {X: 16, Z: 16}, {X: 18, Z: 3}} {
		if set[c] {
			t.Errorf("%v should be a module cell", c)
		}
	}
	// 20x20 with lines at 0 and 16: aisle columns 13-15, rows 13-15.
	if want := 20*3 + 20*3 - 9; len(cells) != want {
		t.Fatalf("%d aisle cells, want %d", len(cells), want)
	}
	// The origin near the map edge counts aisles on its negative side too.
	edge := gridAt(2, 2).Aisles(Bounds{Width: 3, Height: 3})
	if !reflect.DeepEqual(edge, []domain.Cell{{X: 0, Z: 0}, {X: 0, Z: 1}, {X: 0, Z: 2}, {X: 1, Z: 0}, {X: 1, Z: 1}, {X: 1, Z: 2}, {X: 2, Z: 0}, {X: 2, Z: 1}}) {
		t.Fatalf("edge aisles = %v", edge)
	}
	if gridAt(0, 0).Aisles(Bounds{}) != nil || (ColonyGrid{}).Aisles(Bounds{Width: 4, Height: 4}) != nil {
		t.Fatal("empty bounds or an invalid grid have no aisles")
	}
	within := g.AislesWithin(Bounds{Width: 20, Height: 20}, Rectangle{X: 12, Z: -5, Width: 3, Height: 10})
	if !reflect.DeepEqual(within, []domain.Cell{{X: 13, Z: 0}, {X: 13, Z: 1}, {X: 13, Z: 2}, {X: 13, Z: 3}, {X: 13, Z: 4}, {X: 14, Z: 0}, {X: 14, Z: 1}, {X: 14, Z: 2}, {X: 14, Z: 3}, {X: 14, Z: 4}}) {
		t.Fatalf("AislesWithin = %v", within)
	}
}

func TestColonyGridModuleAndSubCells(t *testing.T) {
	g := gridAt(3, 5)
	module := g.Module(domain.Cell{X: 30, Z: 30})
	if module != (Rectangle{X: 19, Z: 21, Width: 13, Height: 13}) {
		t.Fatalf("Module = %+v", module)
	}
	if aisle := g.Module(domain.Cell{X: 33, Z: 35}); aisle != module {
		t.Fatalf("aisle cell belongs to the module it follows, got %+v", aisle)
	}
	sub := g.SubCells(module)
	want := []Rectangle{
		{X: 20, Z: 22, Width: 11, Height: 11},
		{X: 20, Z: 22, Width: 5, Height: 11}, {X: 26, Z: 22, Width: 5, Height: 11},
		{X: 20, Z: 22, Width: 11, Height: 5}, {X: 20, Z: 28, Width: 11, Height: 5},
		{X: 20, Z: 22, Width: 5, Height: 5}, {X: 20, Z: 28, Width: 5, Height: 5},
		{X: 26, Z: 22, Width: 5, Height: 5}, {X: 26, Z: 28, Width: 5, Height: 5},
	}
	if !reflect.DeepEqual(sub, want) {
		t.Fatalf("SubCells = %+v", sub)
	}
	if g.SubCells(Rectangle{X: 20, Z: 21, Width: 13, Height: 13}) != nil || g.SubCells(Rectangle{X: 19, Z: 21, Width: 12, Height: 13}) != nil {
		t.Fatal("a rectangle off the grid is not a module")
	}
}

func TestColonyGridUnsetAxesAreTheMapAxes(t *testing.T) {
	bare := ColonyGrid{Origin: domain.Cell{X: 3, Z: 5}, Pitch: GridPitch}
	full := ColonyGrid{Origin: domain.Cell{X: 3, Z: 5}, Pitch: GridPitch, Axes: ColonyGridAxes}
	if !bare.Valid() {
		t.Fatal("a grid without axes is the map-aligned grid")
	}
	bounds := Bounds{Width: 40, Height: 40}
	if a, b := bare.Aisles(bounds), full.Aisles(bounds); len(a) != len(b) || len(a) == 0 {
		t.Fatalf("aisles differ: %d vs %d", len(a), len(b))
	}
	if bare.Snap(domain.Cell{X: 20, Z: 22}) != full.Snap(domain.Cell{X: 20, Z: 22}) {
		t.Fatal("snap differs without axes")
	}
}
