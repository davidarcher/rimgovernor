package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ShelterModule is the rectangular module family every room past Camp is
// built from (#609): one, two or four sub-cells of a colony grid module
// (ColonyGrid.SubCells), so neighbouring rooms share their divider walls, a
// whole module roofs itself without a pillar, and the door sits centred on
// a side that faces an aisle.
const ShelterModule ShelterStyle = "module"

// ModuleShell is one module template placed in one module of the grid.
type ModuleShell struct {
	// Name is the template's name for acceptance reports:
	// module-<interior>-<sub-cell>-<entrance>.
	Name string
	// Class is the template's place in the search order: 0 for a half
	// module (55 cells, nearest the starter interior), 1 for the whole
	// module, 2 for a quarter.
	Class int
	Shell domain.RoomFootprint
	// Absorbs is the aisle bay a double-module hall takes in (#673): the
	// one stretch of walkway the shell may build over, the hall's centred
	// doors carrying the through route instead. Zero for every other
	// template.
	Absorbs Rectangle
}

// moduleShellClass is a sub-cell's search class by its index in SubCells:
// the whole first, then the four halves, then the four quarters.
func moduleShellClass(subCell int) int {
	switch {
	case subCell == 0:
		return 1
	case subCell <= 4:
		return 0
	}
	return 2
}

// ModuleShells lists every module template placed in one module of the
// grid, in search order: the halves, the whole, then the quarters, each
// with a door centred on every side of it that faces an aisle (the whole
// module's four sides, a half's three, a quarter's two). Within a class the
// order is the sub-cell order of SubCells, then south, north, east, west.
// A rectangle that is not a module of the grid yields nil.
func ModuleShells(g ColonyGrid, module Rectangle) []ModuleShell {
	var out []ModuleShell
	for i, interior := range g.SubCells(module) {
		bounds := domain.RoomBounds{X: interior.X - 1, Z: interior.Z - 1, Width: interior.Width + 2, Height: interior.Height + 2}
		sides := []struct {
			entrance domain.Rotation
			aisle    bool
		}{
			{domain.South, bounds.Z == module.Z},
			{domain.North, bounds.Z+bounds.Height == module.Z+module.Height},
			{domain.East, bounds.X+bounds.Width == module.X+module.Width},
			{domain.West, bounds.X == module.X},
		}
		for _, side := range sides {
			if !side.aisle {
				continue
			}
			shell, err := domain.RectangleFootprint(bounds, side.entrance)
			if err != nil {
				continue
			}
			out = append(out, ModuleShell{Name: fmt.Sprintf("module-%dx%d-%d-%s", interior.Width, interior.Height, i, side.entrance), Class: moduleShellClass(i), Shell: shell})
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Class < out[j].Class })
	return out
}

// ModuleShellsAtDoor returns every module template whose door stands on
// door, the module-style counterpart of ShellShapesAtDoor: a shell planner
// recognises a module it began earlier from the door still standing.
func ModuleShellsAtDoor(g ColonyGrid, door domain.Cell) []domain.RoomFootprint {
	if !g.Valid() {
		return nil
	}
	var shells []domain.RoomFootprint
	for _, t := range ModuleShells(g, g.Module(door)) {
		if t.Shell.Door() == door {
			shells = append(shells, t.Shell)
		}
	}
	return shells
}

// moduleSites is the module-style starter search: every module the
// observed cells touch is tried in corner order, and the first template
// class with a buildable placement in it sites that module, the door
// nearest the plaza (the origin module's centre) preferred within the
// class so rooms open toward the colony. The class is the site's tier, so
// the tier's shape family (ShapeFamilyShells, class shapeFamilyClass) beats
// a half module, and a half module further out beats a quarter nearer the
// anchor.
func moduleSites(g ColonyGrid, family ShapeFamily, ordered []domain.Cell, buildable func(domain.RoomFootprint, Rectangle) bool, score func(domain.RoomFootprint) int64) []starterSite {
	plaza := g.cell(ColonyGridModule/2, ColonyGridModule/2)
	seen := map[Rectangle]bool{}
	var modules []Rectangle
	for _, c := range ordered {
		m := g.Module(c)
		if !seen[m] {
			seen[m] = true
			modules = append(modules, m)
		}
	}
	sort.Slice(modules, func(i, j int) bool {
		return cellLess(domain.Cell{X: modules[i].X, Z: modules[i].Z}, domain.Cell{X: modules[j].X, Z: modules[j].Z})
	})
	var sites []starterSite
	for _, m := range modules {
		templates := append(ShapeFamilyShells(g, m, family), ModuleShells(g, m)...)
		sort.SliceStable(templates, func(i, j int) bool {
			if templates[i].Class != templates[j].Class {
				return templates[i].Class < templates[j].Class
			}
			return squaredDistance(templates[i].Shell.Door(), plaza) < squaredDistance(templates[j].Shell.Door(), plaza)
		})
		for _, t := range templates {
			if !buildable(t.Shell, t.Absorbs) {
				continue
			}
			sites = append(sites, starterSite{t.Class, score(t.Shell), t.Shell})
			break
		}
	}
	return sites
}
