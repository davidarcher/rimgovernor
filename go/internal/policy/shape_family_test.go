package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shapeGrid is a grid whose origin module is the plaza, offset from the map
// origin so every module in the tests has room for its walls.
func shapeGrid() ColonyGrid {
	return ColonyGrid{Origin: domain.Cell{X: 100, Z: 120}, Pitch: GridPitch, Axes: ColonyGridAxes}
}

func TestModuleShapeFamilyOverTierAndRole(t *testing.T) {
	tiers := []BuildTier{BuildTierCamp, BuildTierMasonry, BuildTierPowered, BuildTierIndustrial, BuildTierSpacer}
	// Each role's family per tier, in tier order.
	cases := []struct {
		role     RoomRole
		families [5]ShapeFamily
	}{
		{RoomRoleDiningRoom, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyHall, ShapeFamilyHall, ShapeFamilyHall}},
		{RoomRoleWorkshop, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyHall, ShapeFamilyHall, ShapeFamilyHall}},
		{RoomRoleKitchen, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyHall, ShapeFamilyHall, ShapeFamilyHall}},
		{RoomRoleLaboratory, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyHall, ShapeFamilyHall, ShapeFamilyHall}},
		{RoomRoleBedroom, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyWings, ShapeFamilyWings}},
		{RoomRoleBarracks, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyWings, ShapeFamilyWings}},
		{RoomRoleHospital, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyWings, ShapeFamilyWings}},
		{RoomRoleNone, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyCourtyard}},
		{RoomRoleRecRoom, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyCourtyard}},
		{RoomRoleThroneRoom, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilyCourtyard}},
		// Storage and the fields keep the single module at every tier.
		{RoomRoleStoreroom, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle}},
		{RoomRoleBarn, [5]ShapeFamily{ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle, ShapeFamilySingle}},
	}
	for _, c := range cases {
		for i, tier := range tiers {
			if got := ModuleShapeFamily(tier, c.role); got != c.families[i] {
				t.Errorf("%s at %s: family %s, want %s", c.role, tier, got, c.families[i])
			}
		}
	}
}

// A Camp colony is offered no shaped proposal whatever its role, and no
// family's geometry comes back for the single family.
func TestCampAndMasonryTakeNoShapeFamily(t *testing.T) {
	g := shapeGrid()
	module := g.Module(domain.Cell{X: g.Origin.X + GridPitch, Z: g.Origin.Z})
	for _, tier := range []BuildTier{BuildTierCamp, BuildTierMasonry} {
		for _, role := range []RoomRole{RoomRoleNone, RoomRoleBedroom, RoomRoleWorkshop, RoomRoleDiningRoom, RoomRoleStoreroom} {
			if family := ModuleShapeFamily(tier, role); family != ShapeFamilySingle {
				t.Fatalf("%s at %s: %s", role, tier, family)
			}
			if shells := ShapeFamilyShells(g, module, ModuleShapeFamily(tier, role)); len(shells) != 0 {
				t.Fatalf("%s at %s: %d family shells", role, tier, len(shells))
			}
			if len(ModuleShapes(g, module, tier, role)) != len(ModuleShells(g, module)) {
				t.Fatalf("%s at %s does not fall back to the single-module templates", role, tier)
			}
		}
	}
}

func TestShapeFamilyShellsRefuseANonModule(t *testing.T) {
	g := shapeGrid()
	off := Rectangle{X: g.Origin.X + 3, Z: g.Origin.Z + 3, Width: ColonyGridModule, Height: ColonyGridModule}
	for _, family := range []ShapeFamily{ShapeFamilyHall, ShapeFamilyWings, ShapeFamilyCourtyard} {
		if shells := ShapeFamilyShells(g, off, family); len(shells) != 0 {
			t.Fatalf("%s placed %d shells off the grid", family, len(shells))
		}
	}
	if _, ok := PairedWingModule(g, off); ok {
		t.Fatal("a rectangle off the grid has a wing twin")
	}
}

// The hall spans a module, the aisle bay and the neighbouring module along
// one axis: an 11x27 interior enclosed by one ring, roofed by its own side
// walls, with a door centred on each side.
func TestDoubleModuleHallSpansTwoModulesAndTheAisleBay(t *testing.T) {
	g := shapeGrid()
	module := g.Module(g.Origin)
	shells := ShapeFamilyShells(g, module, ShapeFamilyHall)
	// Four neighbours, four doors each.
	if len(shells) != 16 {
		t.Fatalf("%d hall templates: %v", len(shells), names(shells))
	}
	seen := map[Rectangle]bool{}
	for _, s := range shells {
		if s.Class != shapeFamilyClass {
			t.Fatalf("%s has class %d", s.Name, s.Class)
		}
		if !s.Shell.Set() || !s.Shell.RoofSupported() {
			t.Fatalf("%s does not enclose a roofed interior", s.Name)
		}
		b := s.Shell.Bounds()
		long, short := max(b.Width, b.Height), min(b.Width, b.Height)
		if short != ColonyGridModule || long != ColonyGridModule+GridPitch {
			t.Fatalf("%s bounds %+v are not two modules and the aisle bay", s.Name, b)
		}
		if len(s.Shell.Interior()) != int(ColonyGridInterior)*int(ColonyGridInterior+GridPitch) {
			t.Fatalf("%s interior is %d cells", s.Name, len(s.Shell.Interior()))
		}
		// Both modules' interiors lie inside the hall.
		hall := Rectangle{X: b.X, Z: b.Z, Width: b.Width, Height: b.Height}
		neighbour := g.Module(domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2})
		for _, m := range []Rectangle{module, neighbour} {
			if !covers(hall, inset(m, 1)) {
				t.Fatalf("%s %+v does not cover module interior %+v", s.Name, hall, inset(m, 1))
			}
		}
		doors := 0
		for _, p := range s.Shell.Placements("Wall", "Door", "WoodLog") {
			if p.Definition() == "Door" {
				doors++
			}
		}
		if doors != 1 {
			t.Fatalf("%s has %d doors", s.Name, doors)
		}
		seen[hall] = true
	}
	if len(seen) != 4 {
		t.Fatalf("%d distinct halls, want one per neighbouring module", len(seen))
	}
}

// Paired wings open onto the plaza and their twin is the module mirrored
// through it.
func TestPairedWingsFacePlazaAndMirrorThroughIt(t *testing.T) {
	g := shapeGrid()
	plaza := g.Module(g.Origin)
	if shells := ShapeFamilyShells(g, plaza, ShapeFamilyWings); len(shells) != 0 {
		t.Fatalf("the plaza module raised %d wings", len(shells))
	}
	cases := []struct {
		mu, mv   int32
		entrance domain.Rotation
	}{
		{1, 0, domain.West}, {-1, 0, domain.East}, {0, 1, domain.South}, {0, -1, domain.North},
		{2, 1, domain.West}, {-1, -2, domain.North},
	}
	for _, c := range cases {
		module := g.Module(domain.Cell{X: g.Origin.X + c.mu*GridPitch, Z: g.Origin.Z + c.mv*GridPitch})
		shells := ShapeFamilyShells(g, module, ShapeFamilyWings)
		if len(shells) != 1 {
			t.Fatalf("module %d,%d: %d wing templates", c.mu, c.mv, len(shells))
		}
		shell := shells[0].Shell
		if shell.Entrance() != c.entrance {
			t.Errorf("module %d,%d opens %s, want %s", c.mu, c.mv, shell.Entrance(), c.entrance)
		}
		if !shell.RoofSupported() || len(shell.Interior()) != int(ColonyGridInterior*ColonyGridInterior) {
			t.Errorf("module %d,%d wing is not a whole roofed module: %d cells", c.mu, c.mv, len(shell.Interior()))
		}
		twin, ok := PairedWingModule(g, module)
		if !ok {
			t.Fatalf("module %d,%d has no twin", c.mu, c.mv)
		}
		want := g.Module(domain.Cell{X: g.Origin.X - c.mu*GridPitch, Z: g.Origin.Z - c.mv*GridPitch})
		if twin != want {
			t.Errorf("module %d,%d twin %+v, want %+v", c.mu, c.mv, twin, want)
		}
		// The pair is symmetric: the twin's own twin is the module again,
		// and both open toward each other.
		if back, ok := PairedWingModule(g, twin); !ok || back != module {
			t.Errorf("module %d,%d twin is not mutual: %+v", c.mu, c.mv, back)
		}
	}
	if _, ok := PairedWingModule(g, plaza); ok {
		t.Fatal("the plaza module has a wing twin")
	}
}

// The courtyard leaves the module's central sub-cell open to the sky inside
// its own wall ring, with a two-wide roofed room around it.
func TestCourtyardEnclosesAnOpenCentre(t *testing.T) {
	g := shapeGrid()
	module := g.Module(domain.Cell{X: g.Origin.X + GridPitch, Z: g.Origin.Z})
	shells := ShapeFamilyShells(g, module, ShapeFamilyCourtyard)
	if len(shells) != 4 {
		t.Fatalf("%d courtyard templates: %v", len(shells), names(shells))
	}
	open := inset(module, (ColonyGridModule-ColonyGridSubCell)/2)
	if open.Width != ColonyGridSubCell || open.Height != ColonyGridSubCell {
		t.Fatalf("open centre %+v is not a sub-cell", open)
	}
	for _, s := range shells {
		shell := s.Shell
		if !shell.Set() || !shell.RoofSupported() {
			t.Fatalf("%s does not enclose a roofed interior", s.Name)
		}
		interior := map[domain.Cell]bool{}
		for _, c := range shell.Interior() {
			interior[c] = true
		}
		if len(interior) != int(ColonyGridInterior*ColonyGridInterior-courtyardHole*courtyardHole) {
			t.Fatalf("%s interior is %d cells", s.Name, len(interior))
		}
		walls := map[domain.Cell]bool{}
		for _, c := range shell.Walls() {
			walls[c] = true
		}
		// Every open cell is outside the room and walled off from it; every
		// cell of the module's interior is either room, inner wall or open.
		for _, c := range rectCellsOf(open) {
			if interior[c] || walls[c] {
				t.Fatalf("%s builds on the open centre cell %v", s.Name, c)
			}
		}
		for _, c := range rectCellsOf(inset(module, 1)) {
			if !interior[c] && !walls[c] && !containsCell(open, c) {
				t.Fatalf("%s leaves module cell %v neither room, wall nor courtyard", s.Name, c)
			}
		}
		if shell.Bounds() != (domain.RoomBounds{X: module.X, Z: module.Z, Width: module.Width, Height: module.Height}) {
			t.Fatalf("%s bounds %+v are not the module %+v", s.Name, shell.Bounds(), module)
		}
	}
}

// ModuleShapes offers the family's shells ahead of the single-module
// templates and keeps all of them.
func TestModuleShapesPrefersTheFamilyThenTheModuleTemplates(t *testing.T) {
	g := shapeGrid()
	module := g.Module(domain.Cell{X: g.Origin.X + GridPitch, Z: g.Origin.Z})
	single := len(ModuleShells(g, module))
	for _, c := range []struct {
		tier BuildTier
		role RoomRole
	}{
		{BuildTierPowered, RoomRoleWorkshop},
		{BuildTierIndustrial, RoomRoleBedroom},
		{BuildTierSpacer, RoomRoleRecRoom},
	} {
		family := ShapeFamilyShells(g, module, ModuleShapeFamily(c.tier, c.role))
		shapes := ModuleShapes(g, module, c.tier, c.role)
		if len(family) == 0 || len(shapes) != len(family)+single {
			t.Fatalf("%s at %s: %d shapes from %d family and %d module templates", c.role, c.tier, len(shapes), len(family), single)
		}
		for i, s := range shapes[:len(family)] {
			if s.Name != family[i].Name || s.Class != shapeFamilyClass {
				t.Fatalf("%s at %s: shape %d is %s (class %d)", c.role, c.tier, i, s.Name, s.Class)
			}
		}
		// A door of the family's shells is recognised again at that door.
		door := family[0].Shell.Door()
		found := false
		for _, shell := range ModuleShapesAtDoor(g, door, ModuleShapeFamily(c.tier, c.role)) {
			found = found || shell.Door() == door
		}
		if !found {
			t.Fatalf("%s at %s: no shape recognised at door %v", c.role, c.tier, door)
		}
	}
}

func names(shells []ModuleShell) []string {
	out := make([]string, 0, len(shells))
	for _, s := range shells {
		out = append(out, s.Name)
	}
	return out
}

// covers reports whether outer contains inner entirely.
func covers(outer, inner Rectangle) bool {
	return inner.X >= outer.X && inner.Z >= outer.Z &&
		inner.X+inner.Width <= outer.X+outer.Width && inner.Z+inner.Height <= outer.Z+outer.Height
}
