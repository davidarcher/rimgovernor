package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Planned-ground clearance: when a planned room needs
// ground, every building and constructed floor on that ground the plan does
// not hold is cleared, the colony's own included. Per room the order is
// furniture and other non-wall buildings, then each door standing on the
// room's wall ring where the plan has none (swapped for a wall in place, so
// the enclosure holds), then the roof and the walls and doors holding it,
// then the floors once the cells are clear.

// ClearanceFloor is one constructed floor cell on planned ground.
type ClearanceFloor struct {
	Cell       domain.Cell
	DefName    string
	Designated bool
}

// GroundPhase is the stage a planned room's ground clearance is in.
type GroundPhase string

const (
	GroundFurniture GroundPhase = "furniture"
	// GroundPack packs the room's packable furniture in one batch.
	GroundPack   GroundPhase = "pack"
	GroundDoors  GroundPhase = "doors"
	GroundWalls  GroundPhase = "walls"
	GroundFloors GroundPhase = "floors"
)

// PlannedGround is the ground of every open-ground planned room whose ring
// does not yet match the plan (GroundMatches), in plan order: its interior and
// its wall ring, standing rooms included; then the retired ground.
func PlannedGround(plan LayoutPlan, ground GroundCensus) []Rectangle {
	var out []Rectangle
	for _, r := range plan.groundRooms(ground) {
		out = append(out, roomGround(r.Interior))
	}
	return append(out, plan.RetiredGround...)
}

// roomGround is a room's interior and wall ring, clipped to the map's origin.
func roomGround(in Rectangle) Rectangle {
	g := Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}
	if g.X < 0 {
		g.Width, g.X = g.Width+g.X, 0
	}
	if g.Z < 0 {
		g.Height, g.Z = g.Height+g.Z, 0
	}
	return g
}

// RetiredGround is the ground of rooms the plan retired among the
// ground planned clearance is given, with the wall rectangles of the rooms the
// plan still holds: a building on a retired room's ring that also lies in a
// kept room's walls is that room's and stays. Nothing on retired ground is
// planned: its ring walls, doors and frames come down and its sleeping spots go;
// a research table is packed with the rest and installed in the laboratory.
type RetiredGround struct {
	Ground, Kept []Rectangle
}

// RetiredGroundOf is plan's retired ground with its kept rooms' walls.
func RetiredGroundOf(plan LayoutPlan) RetiredGround {
	out := RetiredGround{Ground: plan.RetiredGround}
	for _, r := range plan.roomsWithHerd() {
		out.Kept = append(out.Kept, roomWalls(r))
	}
	return out
}

func (rg RetiredGround) has(g Rectangle) bool { return slices.Contains(rg.Ground, g) }

func (rg RetiredGround) kept(c domain.Cell) bool {
	for _, k := range rg.Kept {
		if inside(c, k) {
			return true
		}
	}
	return false
}

func (rg RetiredGround) heldRow(row ClearanceTarget) bool {
	for x := row.Minimum.X; x <= row.Maximum.X; x++ {
		for z := row.Minimum.Z; z <= row.Maximum.Z; z++ {
			if rg.kept(domain.Cell{X: x, Z: z}) {
				return true
			}
		}
	}
	return false
}

// RetiredGroundDone is the retired ground with nothing left to clear: no
// building and no floor of ours that
// is not a kept room's. The plan drops these entries.
func RetiredGroundDone(plan LayoutPlan, rows []ClearanceTarget, floors []ClearanceFloor) []Rectangle {
	rg := RetiredGroundOf(plan)
	var done []Rectangle
	for _, g := range rg.Ground {
		if !retiredWork(rows, floors, g, rg) {
			done = append(done, g)
		}
	}
	return done
}

// WithoutRetiredGround is plan less the entries of done.
func (p LayoutPlan) WithoutRetiredGround(done []Rectangle) LayoutPlan {
	p.RetiredGround = slices.DeleteFunc(slices.Clone(p.RetiredGround), func(g Rectangle) bool { return slices.Contains(done, g) })
	return p
}

// retiredWork reports a building or floor left on retired ground g.
func retiredWork(rows []ClearanceTarget, floors []ClearanceFloor, g Rectangle, rg RetiredGround) bool {
	for _, row := range rows {
		if groundTarget(row, g, rg) {
			return true
		}
	}
	for _, f := range floors {
		if inside(f.Cell, g) && !rg.kept(f.Cell) && !groundCovered(f.Cell, rows) {
			return true
		}
	}
	return false
}

// groundTarget is a building clearance of retired ground g takes down:
// everything but a kept room's walls.
func groundTarget(row ClearanceTarget, g Rectangle, rg RetiredGround) bool {
	return row.Player && overlaps(row, g) && !rg.heldRow(row)
}

// SplitGroundRows separates the player rows only planned ground reports from
// the non-player census home clearance and salvage read.
func SplitGroundRows(rows []ClearanceTarget) (others, player []ClearanceTarget) {
	for _, row := range rows {
		if row.Player {
			player = append(player, row)
		} else {
			others = append(others, row)
		}
	}
	return others, player
}

// GroundStep is one clearance method: the phase labelling it, its targets
// (buildings or floors), the roof cells to remove (walls) and the ground the
// walls are deconstructed on. Ground is the retired ground it clears, zero for
// the batch of planned rooms.
type GroundStep struct {
	Ground  Rectangle
	Phase   GroundPhase
	Targets []ClearanceTarget
	Floors  []ClearanceFloor
	Roof    []domain.Cell
	Cleared []Rectangle
}

// GroundRects is cleared ground in the deconstruction's shape.
func GroundRects(ground []Rectangle) []domain.GroundRect {
	out := make([]domain.GroundRect, 0, len(ground))
	for _, g := range ground {
		out = append(out, domain.GroundRect{Origin: domain.Cell{X: g.X, Z: g.Z}, Width: g.Width, Height: g.Height})
	}
	return out
}

// standInBed is a shelter sleeping spot: the colony's only bed until the room
// stands, so planned-ground clearance leaves it. Clearing it first left the
// pawns sleeping outside (shelter unmet, the comfort planners held) while the
// room was still being dug.
func standInBed(row ClearanceTarget) bool { return row.DefName == "SleepingSpot" }

// retiredGroundStep picks the first retired ground, in plan order, with work
// left and its earliest phase: furniture, packs, then walls with their roof,
// then floors. Rows are the census's player rows on the ground
// (SplitGroundRows). ok is false when the
// ground is clear. Planned rooms go through Reconcile (PlannedGroundStep).
func retiredGroundStep(rows []ClearanceTarget, floors []ClearanceFloor, ground []Rectangle, rooms RoomObservation, rg RetiredGround) (GroundStep, bool) {
	ordered := append([]ClearanceTarget(nil), rows...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].EntityID < ordered[j].EntityID })
	cells := append([]ClearanceFloor(nil), floors...)
	sort.Slice(cells, func(i, j int) bool {
		a, b := cells[i].Cell, cells[j].Cell
		return a.Z < b.Z || a.Z == b.Z && a.X < b.X
	})
	claimed := map[string]bool{}
	claimedFloor := map[domain.Cell]bool{}
	for _, g := range ground {
		var furniture, packs, walls []ClearanceTarget
		for _, row := range ordered {
			if claimed[row.EntityID] || !groundTarget(row, g, rg) {
				continue
			}
			claimed[row.EntityID] = true
			if row.EnclosesRoom {
				walls = append(walls, row)
			} else if row.Packable && !row.Designated {
				packs = append(packs, row)
			} else {
				furniture = append(furniture, row)
			}
		}
		// Pieces in use go after every other piece in the room.
		sort.SliceStable(packs, func(i, j int) bool { return !packs[i].InUse && packs[j].InUse })
		switch {
		case len(furniture) > 0:
			return GroundStep{Ground: g, Phase: GroundFurniture, Targets: furniture}, true
		case len(packs) > 0:
			return GroundStep{Ground: g, Phase: GroundPack, Targets: packs}, true
		case len(walls) > 0:
			return GroundStep{Ground: g, Phase: GroundWalls, Targets: walls, Roof: enclosedRoof(walls, ground, rooms), Cleared: ground}, true
		}
		var mine []ClearanceFloor
		for _, f := range cells {
			if inside(f.Cell, g) && !claimedFloor[f.Cell] && !groundCovered(f.Cell, ordered) && !(rg.has(g) && rg.kept(f.Cell)) {
				claimedFloor[f.Cell] = true
				mine = append(mine, f)
			}
		}
		if len(mine) > 0 {
			return GroundStep{Ground: g, Phase: GroundFloors, Floors: mine}, true
		}
	}
	return GroundStep{}, false
}

// retiredGroundWork is the clearance deficit retired ground owes: every
// target building and every floor cell no player building covers, stable.
func retiredGroundWork(rows []ClearanceTarget, floors []ClearanceFloor, ground []Rectangle, rg RetiredGround) []string {
	var out []string
	for _, row := range rows {
		for _, g := range ground {
			if groundTarget(row, g, rg) {
				out = append(out, row.EntityID)
				break
			}
		}
	}
	for _, f := range floors {
		for _, g := range ground {
			if inside(f.Cell, g) && !groundCovered(f.Cell, rows) && !(rg.has(g) && rg.kept(f.Cell)) {
				out = append(out, GroundFloorID(f.Cell))
				break
			}
		}
	}
	sort.Strings(out)
	return out
}

// GroundFloorID names one floor cell as a clearance deficit target.
func GroundFloorID(c domain.Cell) string { return fmt.Sprintf("floor@%d,%d", c.X, c.Z) }

// groundCovered reports a cell under a building the step left standing (a
// planned ring wall): its floor stays.
func groundCovered(c domain.Cell, rows []ClearanceTarget) bool {
	for _, row := range rows {
		if row.Player && c.X >= row.Minimum.X && c.X <= row.Maximum.X && c.Z >= row.Minimum.Z && c.Z <= row.Maximum.Z {
			return true
		}
	}
	return false
}

// enclosedRoof is the cells of every enclosed census room a wall target
// bounds that lies wholly inside the cleared ground: the roof the native
// deconstruct guard waits on. A room reaching outside is the
// guard's refusal, not a roof to remove.
func enclosedRoof(walls []ClearanceTarget, ground []Rectangle, rooms RoomObservation) []domain.Cell {
	seen := map[domain.Cell]bool{}
	var out []domain.Cell
	for _, room := range rooms.Rooms {
		if enclosed, known := room.Enclosed.Value(); known && !enclosed || len(room.Cells) == 0 {
			continue
		}
		within, bounded := true, false
		for _, c := range room.Cells {
			in := false
			for _, g := range ground {
				in = in || inside(c, g)
			}
			within = within && in
			for _, w := range walls {
				bounded = bounded || c.X >= w.Minimum.X-1 && c.X <= w.Maximum.X+1 && c.Z >= w.Minimum.Z-1 && c.Z <= w.Maximum.Z+1
			}
		}
		if !within || !bounded {
			continue
		}
		for _, c := range room.Cells {
			if !seen[c] {
				seen[c] = true
				out = append(out, c)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].X < out[j].X || out[i].X == out[j].X && out[i].Z < out[j].Z })
	return out
}

func inside(c domain.Cell, g Rectangle) bool {
	return c.X >= g.X && c.X < g.X+g.Width && c.Z >= g.Z && c.Z < g.Z+g.Height
}

func onRing(c domain.Cell, g Rectangle) bool {
	return inside(c, g) && (c.X == g.X || c.X == g.X+g.Width-1 || c.Z == g.Z || c.Z == g.Z+g.Height-1)
}

func overlaps(row ClearanceTarget, g Rectangle) bool {
	return row.Minimum.X < g.X+g.Width && row.Maximum.X >= g.X && row.Minimum.Z < g.Z+g.Height && row.Maximum.Z >= g.Z
}
