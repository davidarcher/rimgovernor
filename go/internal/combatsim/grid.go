package combatsim

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ErrUnsupported marks a scenario the simulator cannot represent faithfully:
// required state is missing or outside what the core models. The simulator
// never guesses a value for it.
var ErrUnsupported = errors.New("combatsim: unsupported scenario")

func unsupported(format string, a ...any) error {
	return fmt.Errorf("%w: %s", ErrUnsupported, fmt.Sprintf(format, a...))
}

// Fill is a thing def's FillCategory.
type Fill uint8

const (
	FillNone Fill = iota
	FillPartial
	FillFull
)

// Passability is a thing def's Traversability.
type Passability uint8

const (
	// Standable things can be stood on (the zero value: loose items).
	Standable Passability = iota
	// PassThroughOnly things can be walked through but not stopped on (doors,
	// sandbags).
	PassThroughOnly
	// Impassable things cannot be entered (walls, fences).
	Impassable
)

// ThingDef carries the definition values the core reads from the native
// catalog; the package holds no def values of its own. A cell holds at most
// one thing: the one that dominates cover and sight (see Scenario.Things).
type ThingDef struct {
	Name        string
	Fillage     Fill
	FillPercent float64
	Passability Passability
	// PathCost is ThingDef.pathCost.
	PathCost int
	// Door marks a Building_Door: it blocks sight while closed and a pawn
	// approaching it opens it.
	Door bool
}

// TerrainDef carries terrain values the core reads from the native catalog.
type TerrainDef struct {
	Name     string
	PathCost int
	// Passable is false for Traversability.Impassable terrain (deep water).
	Passable bool
}

// impassableCost is the PathGrid cost of a cell no pawn can enter.
const impassableCost = 10000

// grid is the map plus the door state that moves.
type grid struct {
	w, h     int
	terrain  []TerrainDef
	things   []ThingDef // index 0 is "no thing"
	terrainI []uint8
	thingI   []uint8
	roofed   []bool
	doorOpen []bool
}

func (g *grid) inBounds(c domain.Cell) bool {
	return c.X >= 0 && c.Z >= 0 && int(c.X) < g.w && int(c.Z) < g.h
}

func (g *grid) idx(c domain.Cell) int { return int(c.Z)*g.w + int(c.X) }

func (g *grid) cell(i int) domain.Cell {
	return domain.Cell{X: int32(i % g.w), Z: int32(i / g.w)}
}

func (g *grid) thing(c domain.Cell) *ThingDef { return &g.things[g.thingI[g.idx(c)]] }

// canBeSeenOver ports GenGrid.CanBeSeenOver: out of bounds blocks sight; a
// Full edifice blocks it unless it is an open door.
func (g *grid) canBeSeenOver(c domain.Cell) bool {
	if !g.inBounds(c) {
		return false
	}
	t := g.thing(c)
	if t.Fillage != FillFull {
		return true
	}
	return t.Door && g.doorOpen[g.idx(c)]
}

// hasCover ports the cover grid: a cell's cover thing is its thing when its
// fill percent exceeds 0.001 (CoverGrid.RecalculateCell).
func (g *grid) hasCover(c domain.Cell) bool {
	return g.inBounds(c) && g.thing(c).FillPercent > 0.001
}

// baseBlockChance ports CoverUtility.BaseBlockChance(Thing): an open door
// gives none, a Full thing 0.75, otherwise its fill percent.
func (g *grid) baseBlockChance(c domain.Cell) float64 {
	t := g.thing(c)
	if t.Door && g.doorOpen[g.idx(c)] {
		return 0
	}
	if t.Fillage == FillFull {
		return 0.75
	}
	return t.FillPercent
}

// cellCost is PathGrid.CalculatedCostAt without snow, sand and fire: the
// larger of the terrain's and the thing's path cost, or impassableCost.
func (g *grid) cellCost(c domain.Cell) int { return g.cellCostAt(g.idx(c)) }

// cellCostAt is cellCost for the cell at index i.
func (g *grid) cellCostAt(i int) int {
	te := &g.terrain[g.terrainI[i]]
	th := &g.things[g.thingI[i]]
	if !te.Passable || th.Passability == Impassable {
		return impassableCost
	}
	return max(te.PathCost, th.PathCost)
}

func (g *grid) walkable(c domain.Cell) bool {
	return g.inBounds(c) && g.cellCost(c) < impassableCost
}

// standable ports GenGrid.Standable: walkable and the thing, if any, standable.
func (g *grid) standable(c domain.Cell) bool {
	return g.walkable(c) && g.thing(c).Passability == Standable
}
