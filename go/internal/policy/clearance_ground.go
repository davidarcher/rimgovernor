package policy

import (
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Planned-ground clearance (#1245, epic #1249): when a planned room needs
// ground, every building and constructed floor on that ground the plan does
// not hold is cleared, the colony's own included. Per room the order is
// furniture and other non-wall buildings, then each door standing on the
// room's wall ring where the plan has none (swapped for a wall in place, so
// the enclosure holds), then the roof and the walls and doors holding it,
// then the floors once the cells are clear.

// ClearanceFloor is one constructed floor cell on planned ground (#1365).
type ClearanceFloor struct {
	Cell       domain.Cell
	DefName    string
	Designated bool
}

// GroundPhase is the stage a planned room's ground clearance is in.
type GroundPhase string

const (
	GroundFurniture GroundPhase = "furniture"
	GroundDoors     GroundPhase = "doors"
	GroundWalls     GroundPhase = "walls"
	GroundFloors    GroundPhase = "floors"
)

// PlannedGround is the ground of every open-ground planned room with no room
// standing in it yet, in plan order: its interior and its wall ring. A room
// already standing holds its own buildings and is left alone.
func PlannedGround(plan LayoutPlan, rooms RoomObservation) []Rectangle {
	var out []Rectangle
	for _, r := range plan.AllRooms() {
		if r.Dug {
			continue
		}
		if _, ok := PlannedRoomStanding(r, rooms); ok {
			continue
		}
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

// RetiredGround is the ground of rooms the plan retired (#2075) among the
// ground planned clearance is given, with the wall rectangles of the rooms the
// plan still holds: a building on a retired room's ring that also lies in a
// kept room's walls is that room's and stays. Nothing on retired ground is
// planned: its ring walls, doors and frames come down, its sleeping spots go,
// and a research table holds the whole ground until it is relocated (#2047).
type RetiredGround struct {
	Ground, Kept []Rectangle
}

// RetiredGroundOf is plan's retired ground with its kept rooms' walls.
func RetiredGroundOf(plan LayoutPlan) RetiredGround {
	out := RetiredGround{Ground: plan.RetiredGround}
	for _, r := range plan.AllRooms() {
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
// building (a standing research table included) and no floor of ours that
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
		if groundTarget(row, g, nil, rg) {
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

// researchTableStands reports a research table on retired ground g.
func researchTableStands(rows []ClearanceTarget, g Rectangle, rg RetiredGround) bool {
	for _, row := range rows {
		if row.Player && overlaps(row, g) && strings.Contains(row.DefName, "ResearchBench") && !rg.heldRow(row) {
			return true
		}
	}
	return false
}

// groundTarget is a building clearance of ground g takes down. On planned
// ground that is what the plan does not hold; on retired ground, everything
// but a kept room's walls.
func groundTarget(row ClearanceTarget, g Rectangle, doors map[domain.Cell]bool, rg RetiredGround) bool {
	if !row.Player || !overlaps(row, g) {
		return false
	}
	if rg.has(g) {
		return !rg.heldRow(row)
	}
	return !standInBed(row) && (!groundPlanned(row, g) || ringDoorSwap(row, g, doors))
}

// PlannedDoors is every door cell the plan holds: each room's doors and its
// link door. A door standing on planned ground's wall ring anywhere else is
// swapped for a wall.
func PlannedDoors(plan LayoutPlan) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range plan.AllRooms() {
		out[r.Door] = true
		for _, d := range r.Doors {
			out[d.Cell] = true
		}
		if r.Link != nil {
			out[*r.Link] = true
		}
	}
	return out
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

// GroundStep is the next planned-ground clearance: the room's ground, the
// phase, its targets (buildings or floors) and, for walls, the cells of the
// enclosed rooms inside the cleared ground whose roof comes off first.
type GroundStep struct {
	Ground  Rectangle
	Phase   GroundPhase
	Targets []ClearanceTarget
	Floors  []ClearanceFloor
	Roof    []domain.Cell
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

// PlannedGroundStep picks the first room, in plan order, with ground work
// left and its earliest phase. Rows are the census's player rows on the
// ground (SplitGroundRows); a building the plan holds (groundPlanned) is no
// target. Walls wait for the room's furniture, floors for every building on
// it. A door on the ring where the plan has none (doors) is a swap target
// after the furniture. ok is false when the ground is clear.
func PlannedGroundStep(rows []ClearanceTarget, floors []ClearanceFloor, ground []Rectangle, doors map[domain.Cell]bool, rooms RoomObservation, rg RetiredGround) (GroundStep, bool) {
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
		if rg.has(g) && researchTableStands(ordered, g, rg) {
			continue
		}
		var furniture, swaps, walls []ClearanceTarget
		for _, row := range ordered {
			if claimed[row.EntityID] || !groundTarget(row, g, doors, rg) {
				continue
			}
			claimed[row.EntityID] = true
			if !rg.has(g) && ringDoorSwap(row, g, doors) {
				swaps = append(swaps, row)
			} else if row.EnclosesRoom {
				walls = append(walls, row)
			} else {
				furniture = append(furniture, row)
			}
		}
		switch {
		case len(furniture) > 0:
			return GroundStep{Ground: g, Phase: GroundFurniture, Targets: furniture}, true
		case len(swaps) > 0:
			return GroundStep{Ground: g, Phase: GroundDoors, Targets: swaps}, true
		case len(walls) > 0:
			return GroundStep{Ground: g, Phase: GroundWalls, Targets: walls, Roof: enclosedRoof(walls, ground, rooms)}, true
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

// PlannedGroundWork is the clearance deficit planned ground owes: every
// target building and every floor cell no player building covers, stable.
func PlannedGroundWork(rows []ClearanceTarget, floors []ClearanceFloor, ground []Rectangle, doors map[domain.Cell]bool, rg RetiredGround) []string {
	var out []string
	for _, row := range rows {
		for _, g := range ground {
			if groundTarget(row, g, doors, rg) && !(rg.has(g) && researchTableStands(rows, g, rg)) {
				out = append(out, row.EntityID)
				break
			}
		}
	}
	for _, f := range floors {
		for _, g := range ground {
			if inside(f.Cell, g) && !groundCovered(f.Cell, rows) && !(rg.has(g) && (rg.kept(f.Cell) || researchTableStands(rows, g, rg))) {
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

// groundPlanned is a building the planned room holds: a wall, door or frame
// lying wholly on its wall ring, where the room raises its own (a wall or
// door the ring keeps, a frame its builder owns), or any frame on the ground.
// The census classes a player Wall or door as ancient_wall_door.
func groundPlanned(row ClearanceTarget, g Rectangle) bool {
	if strings.HasPrefix(row.DefName, "Frame_") {
		return true
	}
	if row.Class != "ancient_wall_door" {
		return false
	}
	for x := row.Minimum.X; x <= row.Maximum.X; x++ {
		for z := row.Minimum.Z; z <= row.Maximum.Z; z++ {
			if !onRing(domain.Cell{X: x, Z: z}, g) {
				return false
			}
		}
	}
	return true
}

// ringDoorSwap is a player door the planned room's wall ring keeps standing
// where the plan has no door: it is swapped for a wall (#1245).
func ringDoorSwap(row ClearanceTarget, g Rectangle, doors map[domain.Cell]bool) bool {
	return row.Class == "ancient_wall_door" && strings.Contains(row.DefName, "Door") && row.Minimum == row.Maximum &&
		onRing(row.Minimum, g) && !doors[row.Minimum]
}

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
// deconstruct guard waits on (#1366). A room reaching outside is the
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
