package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestModuleShellsEncloseWithOneAisleDoorOnTheGrid(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 20, Z: 21}, Pitch: GridPitch, Axes: ColonyGridAxes}
	module := g.Module(domain.Cell{X: 25, Z: 25})
	shells := ModuleShells(g, module)
	// The whole module opens on four sides, each half on three, each
	// quarter on two: 4 + 4*3 + 4*2.
	if len(shells) != 24 {
		t.Fatalf("%d module templates", len(shells))
	}
	interiors := map[Rectangle]bool{}
	for _, sub := range g.SubCells(module) {
		interiors[sub] = true
	}
	classes := map[int]int{}
	for i, s := range shells {
		classes[s.Class]++
		if i > 0 && shells[i-1].Class > s.Class {
			t.Fatalf("template %d (%s) out of class order", i, s.Name)
		}
		shell := s.Shell
		if !shell.Set() || !shell.RoofSupported() {
			t.Fatalf("%s does not enclose a roofed interior", s.Name)
		}
		b := shell.Bounds()
		interior := Rectangle{X: b.X + 1, Z: b.Z + 1, Width: b.Width - 2, Height: b.Height - 2}
		if !interiors[interior] {
			t.Fatalf("%s interior %+v is not a sub-cell of the module", s.Name, interior)
		}
		if !g.OnGridLine(Rectangle{X: module.X, Z: module.Z, Width: 1, Height: 1}) {
			t.Fatalf("module %+v is off the grid", module)
		}
		door := shell.Door()
		doors := 0
		for _, p := range shell.Placements("Wall", "Door", "WoodLog") {
			if p.Definition() == "Door" {
				doors++
			}
		}
		if doors != 1 {
			t.Fatalf("%s has %d doors", s.Name, doors)
		}
		// The door stands on the module's exterior ring, centred on its
		// side, and its threshold is an aisle cell.
		onRing := door.X == module.X || door.X == module.X+module.Width-1 || door.Z == module.Z || door.Z == module.Z+module.Height-1
		if !onRing {
			t.Fatalf("%s door %v is not on the module's aisle-facing ring", s.Name, door)
		}
		threshold := shell.Threshold()
		if u, v := g.local(threshold); floorMod(u, g.Pitch) < ColonyGridModule && floorMod(v, g.Pitch) < ColonyGridModule {
			t.Fatalf("%s threshold %v is not in an aisle", s.Name, threshold)
		}
		switch shell.Entrance() {
		case domain.South, domain.North:
			if door.X != b.X+b.Width/2 {
				t.Fatalf("%s door %v is off centre", s.Name, door)
			}
		default:
			if door.Z != b.Z+b.Height/2 {
				t.Fatalf("%s door %v is off centre", s.Name, door)
			}
		}
	}
	if classes[0] != 12 || classes[1] != 4 || classes[2] != 8 {
		t.Fatalf("classes = %v", classes)
	}
	// Neighbouring sub-cells share their divider wall.
	walls := func(r Rectangle) map[domain.Cell]bool {
		out := map[domain.Cell]bool{}
		for _, s := range shells {
			b := s.Shell.Bounds()
			if (Rectangle{X: b.X + 1, Z: b.Z + 1, Width: b.Width - 2, Height: b.Height - 2}) == r {
				for _, w := range s.Shell.Walls() {
					out[w] = true
				}
				return out
			}
		}
		t.Fatalf("no template for %+v", r)
		return nil
	}
	left, right := walls(Rectangle{X: 21, Z: 22, Width: 5, Height: 11}), walls(Rectangle{X: 27, Z: 22, Width: 5, Height: 11})
	shared := 0
	for w := range left {
		if right[w] {
			shared++
		}
	}
	if shared != 13 {
		t.Fatalf("the halves share %d wall cells, want the 13-cell divider", shared)
	}
	if ModuleShells(g, Rectangle{X: 21, Z: 22, Width: 13, Height: 13}) != nil {
		t.Fatal("a rectangle off the grid is not a module")
	}
	at := ModuleShellsAtDoor(g, domain.Cell{X: 26, Z: 21})
	// The south door at the module's centre column: the whole module and
	// the 11x5 bottom half.
	if len(at) != 2 {
		t.Fatalf("%d shells at the south door", len(at))
	}
	for _, s := range at {
		if s.Door() != (domain.Cell{X: 26, Z: 21}) {
			t.Fatalf("shell at door %v", s.Door())
		}
	}
}

func TestStarterLayoutsModuleStyleFillsTheNearestFreeModule(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 20, Z: 20}, Pitch: GridPitch, Axes: ColonyGridAxes}
	bounds := Bounds{Width: 80, Height: 80}
	var cells []SiteCell
	for x := int32(0); x < bounds.Width; x++ {
		for z := int32(0); z < bounds.Height; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true)})
		}
	}
	// The origin module is taken; the anchor is the centre of the module
	// north of it.
	protected := append(g.Aisles(bounds), rectCells(g.Module(g.Origin))...)
	anchor := domain.Cell{X: 26, Z: 42}
	layouts, err := StarterLayouts(StarterRequest{Bounds: bounds, Anchor: anchor, Cells: cells, Protected: protected, Shelter: ShelterModule, Grid: domain.Known(g)})
	if err != nil {
		t.Fatal(err)
	}
	if len(layouts) == 0 {
		t.Fatal("no module layout")
	}
	best := layouts[0]
	module := g.Module(domain.Cell{X: best.Room.X, Z: best.Room.Z})
	if module != g.Module(anchor) {
		t.Fatalf("best layout %+v is not in the anchor's module %+v", best.Room, module)
	}
	if best.Room.Width != ColonyGridSubCell+2 && best.Room.Height != ColonyGridSubCell+2 {
		t.Fatalf("best layout %+v is not a half module", best.Room)
	}
	if !best.Shell.RoofSupported() {
		t.Fatal("module shell is not roofed")
	}
	// The door faces the plaza: a south door on the bottom half.
	if best.Shell.Entrance() != domain.South {
		t.Fatalf("door faces %s, want the aisle toward the origin", best.Shell.Entrance())
	}
	for _, c := range best.Shell.Cells() {
		if u, v := g.local(c); floorMod(u, g.Pitch) >= ColonyGridModule || floorMod(v, g.Pitch) >= ColonyGridModule {
			t.Fatalf("shell cell %v lies in an aisle", c)
		}
	}
	// Without a grid the module style searches as the rectangle.
	plain, err := StarterLayouts(StarterRequest{Bounds: bounds, Anchor: anchor, Cells: cells, Protected: protected, Shelter: ShelterModule})
	if err != nil {
		t.Fatal(err)
	}
	if len(plain) == 0 || plain[0].Room.Width != 9 || plain[0].Room.Height != 9 {
		t.Fatalf("no grid: %+v", plain)
	}
}
