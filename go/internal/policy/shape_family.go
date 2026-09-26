package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shape families (#610, #637) are the room shapes a build tier unlocks
// beyond C6's single-module templates (ModuleShells): a double-module hall
// for dining and production at Powered, paired wings mirrored about the
// plaza module for housing at Industrial, and an enclosed courtyard for the
// plaza's own common rooms at Spacer. Like every other tier-style rule the
// selection is pure f(tier, role) and the geometry pure f(grid, module), so
// a Camp or Masonry colony is offered nothing but the C6 templates.
//
// A family never replaces the single-module templates: ModuleShapes offers
// the family's shells first (search class shapeFamilyClass, ahead of the
// halves) and falls back to ModuleShells, so a module that cannot hold the
// family's shape is still filled.

// ShapeFamily names the shape family a tier and room role select.
type ShapeFamily string

const (
	// ShapeFamilySingle is C6's family: one module, one half or one quarter.
	ShapeFamilySingle ShapeFamily = "single-module"
	// ShapeFamilyHall is the double-module hall: two modules along one grid
	// axis with the aisle bay between them taken in, an 11x27 interior. The
	// hall is 11 cells across, so its own side walls support the roof and it
	// needs no pillar row.
	ShapeFamilyHall ShapeFamily = "double-module-hall"
	// ShapeFamilyWings is the paired wings: a whole module opening onto the
	// plaza, its twin the module mirrored through the plaza
	// (PairedWingModule), so housing grows as symmetric pairs.
	ShapeFamilyWings ShapeFamily = "paired-wings"
	// ShapeFamilyCourtyard is the enclosed courtyard: a whole module whose
	// central 5x5 sub-cell is left open to the sky inside its own wall ring,
	// leaving a two-wide roofed room around it.
	ShapeFamilyCourtyard ShapeFamily = "courtyard"
)

// shapeFamilyClass is the search class a family's shells take: ahead of the
// half modules (class 0), so a tier's shape is preferred wherever it fits.
const shapeFamilyClass = -1

// courtyardHole is the width of the module interior left out of a courtyard
// shell: the open 5x5 sub-cell plus the one-cell wall ring around it.
const courtyardHole int32 = ColonyGridSubCell + 2

// ModuleShapeFamily is the shape family a room role takes at a build tier.
// The roles partition: dining and the production rooms take the hall from
// Powered, the housing rooms take paired wings from Industrial, and the
// plaza's own rooms take the courtyard at Spacer. Below a family's tier, and
// for every role no family claims, the answer is the single module.
func ModuleShapeFamily(tier BuildTier, role RoomRole) ShapeFamily {
	switch {
	case hallRole(role):
		if tier >= BuildTierPowered {
			return ShapeFamilyHall
		}
	case RoomDistrict(role) == DistrictHousing:
		if tier >= BuildTierIndustrial {
			return ShapeFamilyWings
		}
	case RoomDistrict(role) == DistrictPlaza:
		if tier >= BuildTierSpacer {
			return ShapeFamilyCourtyard
		}
	}
	return ShapeFamilySingle
}

// hallRole reports the roles the hall serves: the dining room and every
// production room.
func hallRole(role RoomRole) bool {
	return role == RoomRoleDiningRoom || RoomDistrict(role) == DistrictProduction
}

// ModuleShapes lists every shell template for one module under a tier and
// role: the shape family's shells first, then C6's single-module templates.
func ModuleShapes(g ColonyGrid, module Rectangle, tier BuildTier, role RoomRole) []ModuleShell {
	return append(ShapeFamilyShells(g, module, ModuleShapeFamily(tier, role)), ModuleShells(g, module)...)
}

// ModuleShapesAtDoor returns every shell shape of the family, and every
// single-module template, whose door stands on door: the planner recognises
// a shell it began earlier from the door still standing.
func ModuleShapesAtDoor(g ColonyGrid, door domain.Cell, family ShapeFamily) []domain.RoomFootprint {
	if !g.Valid() {
		return nil
	}
	var shells []domain.RoomFootprint
	module := g.Module(door)
	for _, t := range append(ShapeFamilyShells(g, module, family), ModuleShells(g, module)...) {
		if t.Shell.Door() == door {
			shells = append(shells, t.Shell)
		}
	}
	return shells
}

// ShapeFamilyShells lists the family's shells for one module, one per side
// the shape opens on (and, for the hall, per neighbouring module). The
// single family, an invalid grid and a rectangle that is not a module of the
// grid yield none.
func ShapeFamilyShells(g ColonyGrid, module Rectangle, family ShapeFamily) []ModuleShell {
	if !g.Valid() || g.Module(domain.Cell{X: module.X, Z: module.Z}) != module {
		return nil
	}
	switch family {
	case ShapeFamilyHall:
		return hallShells(g, module)
	case ShapeFamilyWings:
		return wingShells(g, module)
	case ShapeFamilyCourtyard:
		return courtyardShells(g, module)
	}
	return nil
}

// PairedWingModule is the module a wing's twin stands in: the module
// mirrored through the plaza (the origin module), so the pair is symmetric
// about it. The plaza module itself has no twin.
func PairedWingModule(g ColonyGrid, module Rectangle) (Rectangle, bool) {
	if !g.Valid() || g.Module(domain.Cell{X: module.X, Z: module.Z}) != module {
		return Rectangle{}, false
	}
	mu, mv := g.module(domain.Cell{X: module.X + module.Width/2, Z: module.Z + module.Height/2})
	if mu == 0 && mv == 0 {
		return Rectangle{}, false
	}
	return g.rectangle(-mu*g.Pitch, -mv*g.Pitch, ColonyGridModule, ColonyGridModule), true
}

// hallShells are the double-module halls a module begins: one per
// neighbouring module along either grid axis, spanning both modules and the
// aisle bay between them, with a door centred on each of the hall's four
// sides.
func hallShells(g ColonyGrid, module Rectangle) []ModuleShell {
	var out []ModuleShell
	for _, axis := range g.axes() {
		for _, sign := range []int32{1, -1} {
			step := domain.Cell{X: axis.X * sign * g.Pitch, Z: axis.Z * sign * g.Pitch}
			neighbour := Rectangle{X: module.X + step.X, Z: module.Z + step.Z, Width: module.Width, Height: module.Height}
			hall := union(module, neighbour)
			name := fmt.Sprintf("hall-%dx%d-%d,%d", hall.Width-2, hall.Height-2, step.X/g.Pitch, step.Z/g.Pitch)
			out = append(out, ringShells(name, hall, rectCellsOf(inset(hall, 1)))...)
		}
	}
	return out
}

// wingShells is the whole-module shell of a wing: one template, its door
// centred on the side facing the plaza, so the module and its twin
// (PairedWingModule) open toward each other across the plaza. The plaza
// module raises no wing.
func wingShells(g ColonyGrid, module Rectangle) []ModuleShell {
	mu, mv := g.module(domain.Cell{X: module.X + module.Width/2, Z: module.Z + module.Height/2})
	if mu == 0 && mv == 0 {
		return nil
	}
	axes := g.axes()
	// The side facing the plaza is the one along the axis the module lies
	// furthest out on, pointing back toward the origin module.
	axis, steps := axes[0], mu
	if abs32(mv) > abs32(mu) {
		axis, steps = axes[1], mv
	}
	inward := domain.Cell{X: -axis.X * sign32(steps), Z: -axis.Z * sign32(steps)}
	entrance, ok := rotationToward(inward)
	if !ok {
		return nil
	}
	shell, err := domain.RectangleFootprint(domain.RoomBounds{X: module.X, Z: module.Z, Width: module.Width, Height: module.Height}, entrance)
	if err != nil {
		return nil
	}
	name := fmt.Sprintf("wings-%dx%d-%s", module.Width-2, module.Height-2, entrance)
	return []ModuleShell{{Name: name, Class: shapeFamilyClass, Shell: shell}}
}

// courtyardShells is the enclosed courtyard: the module's 11x11 interior
// less its central hole, which NewRoomFootprint walls in as a second ring,
// leaving the middle 5x5 sub-cell open to the sky. One template per side.
func courtyardShells(g ColonyGrid, module Rectangle) []ModuleShell {
	interior, hole := inset(module, 1), inset(module, (ColonyGridModule-courtyardHole)/2)
	var cells []domain.Cell
	for _, c := range rectCellsOf(interior) {
		if !containsCell(hole, c) {
			cells = append(cells, c)
		}
	}
	name := fmt.Sprintf("courtyard-%dx%d", interior.Width, interior.Height)
	return ringShells(name, module, cells)
}

// ringShells builds one template per side of a shell's exterior rectangle,
// each with its door centred on that side, from the shell's interior cells.
// Sides whose door the footprint rules refuse are skipped.
func ringShells(name string, exterior Rectangle, interior []domain.Cell) []ModuleShell {
	midX, midZ := exterior.X+exterior.Width/2, exterior.Z+exterior.Height/2
	sides := []struct {
		entrance domain.Rotation
		door     domain.Cell
	}{
		{domain.South, domain.Cell{X: midX, Z: exterior.Z}},
		{domain.North, domain.Cell{X: midX, Z: exterior.Z + exterior.Height - 1}},
		{domain.East, domain.Cell{X: exterior.X + exterior.Width - 1, Z: midZ}},
		{domain.West, domain.Cell{X: exterior.X, Z: midZ}},
	}
	var out []ModuleShell
	for _, side := range sides {
		shell, err := domain.NewRoomFootprint(interior, side.door, side.entrance)
		if err != nil {
			continue
		}
		out = append(out, ModuleShell{Name: fmt.Sprintf("%s-%s", name, side.entrance), Class: shapeFamilyClass, Shell: shell})
	}
	return out
}

// union is the smallest rectangle covering both.
func union(a, b Rectangle) Rectangle {
	minX, minZ := min(a.X, b.X), min(a.Z, b.Z)
	maxX, maxZ := max(a.X+a.Width, b.X+b.Width), max(a.Z+a.Height, b.Z+b.Height)
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ}
}

// inset shrinks a rectangle by n cells on every side.
func inset(r Rectangle, n int32) Rectangle {
	return Rectangle{X: r.X + n, Z: r.Z + n, Width: r.Width - 2*n, Height: r.Height - 2*n}
}

func containsCell(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height
}

// rectCellsOf lists a rectangle's cells, z outer and x inner.
func rectCellsOf(r Rectangle) []domain.Cell {
	out := make([]domain.Cell, 0, max(int32(0), r.Width*r.Height))
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

func sign32(v int32) int32 {
	switch {
	case v > 0:
		return 1
	case v < 0:
		return -1
	}
	return 0
}

// rotationToward is the entrance side a unit map direction faces.
func rotationToward(d domain.Cell) (domain.Rotation, bool) {
	switch {
	case d.X == 0 && d.Z > 0:
		return domain.North, true
	case d.X == 0 && d.Z < 0:
		return domain.South, true
	case d.Z == 0 && d.X > 0:
		return domain.East, true
	case d.Z == 0 && d.X < 0:
		return domain.West, true
	}
	return "", false
}
