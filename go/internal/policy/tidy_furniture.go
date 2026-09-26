package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The TidyLayout furniture kind (#809) compares each room's installed
// pieces with its derived interior plan (#800) and re-sites the off-plan
// ones through the game's Reinstall (#808), keeping quality and HP. A room
// is re-sited as one ordered batch: no piece moves onto a cell another
// still stands on, and a swap parks one piece on a free cell first.

// tidyFurnitureCap bounds one room's batch: a retrofit re-sites a whole
// room in one plan, but never more than this many moves; the rest of the
// room falls to a later batch.
const tidyFurnitureCap = 8

// TidyPiece is one installed building inside a room: its thing id, its
// definition, its North size, rotation and world footprint.
type TidyPiece struct {
	Thing string
	Def   string
	Size  domain.Cell
	Rot   domain.Rotation
	Rect  Rectangle
}

// TidyRoom is a room the furniture kind measures: its census id, its plan
// input and every building standing on its floor.
type TidyRoom struct {
	ID     string
	Room   InteriorRoom
	Pieces []TidyPiece
}

// TidyMove is one reinstall of a batch: the piece, the footprint and
// rotation it moves to, the plan slot it fills (empty for a Temp leg that
// parks the piece on a free cell to break a swap), and the batch indices
// of the moves that must complete first.
type TidyMove struct {
	Thing string
	Def   string
	Slot  string
	Size  domain.Cell
	From  Rectangle
	To    Rectangle
	Rot   domain.Rotation
	Temp  bool
	After []int `json:",omitempty"`
}

// Anchor is the destination cell a native reinstall names.
func (m TidyMove) Anchor() domain.Cell { return AnchorForRect(m.To, m.Size, m.Rot) }

// TidyFurnitureRooms reads the rectangular census rooms that have a derived
// interior plan as furniture items: the doors are the observed doorways and
// the pieces every census building whose footprint lies on the floor.
func TidyFurnitureRooms(rooms RoomObservation, census CurrentConstruction, cells []SiteCell) []TidyRoom {
	var doorways []domain.Cell
	for _, c := range cells {
		if positive(c.Doorway) {
			doorways = append(doorways, c.Cell)
		}
	}
	var out []TidyRoom
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if !known || room.ID == "" {
			continue
		}
		input, ok := InteriorRoomFromCensus(room, role, doorways)
		if !ok {
			continue
		}
		if _, ok := PlanInterior(input, InteriorPieceDef{}); !ok {
			continue
		}
		t := TidyRoom{ID: room.ID, Room: input}
		for _, b := range census.Buildings {
			if b.ID == "" || len(b.Cells) == 0 {
				continue
			}
			rect := cellsRectangle(b.Cells)
			if !rectInside(input.Interior, rect) {
				continue
			}
			rot := b.Building.Rotation()
			size := domain.Cell{X: rect.Width, Z: rect.Height}
			if rot == domain.East || rot == domain.West {
				size = domain.Cell{X: rect.Height, Z: rect.Width}
			}
			t.Pieces = append(t.Pieces, TidyPiece{Thing: b.ID, Def: b.Building.Definition(), Size: size, Rot: rot, Rect: rect})
		}
		sort.Slice(t.Pieces, func(i, j int) bool { return t.Pieces[i].Thing < t.Pieces[j].Thing })
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

func rectInside(outer, inner Rectangle) bool {
	return inner.X >= outer.X && inner.Z >= outer.Z && inner.X+inner.Width <= outer.X+outer.Width && inner.Z+inner.Height <= outer.Z+outer.Height
}

// tidyFurnitureCandidates counts the rooms with at least one wanted move.
func tidyFurnitureCandidates(rooms []TidyRoom, tidied map[string]bool) int {
	n := 0
	for _, room := range rooms {
		if len(tidyFurnitureMatch(room, tidied)) > 0 {
			n++
		}
	}
	return n
}

// tidyFurnitureProposal is the first room (by id) with a feasible batch.
func tidyFurnitureProposal(rooms []TidyRoom, tidied map[string]bool) *TidyProposal {
	for _, room := range rooms {
		wanted := tidyFurnitureMatch(room, tidied)
		if len(wanted) == 0 {
			continue
		}
		moves := tidyFurnitureOrder(room, wanted)
		if len(moves) == 0 {
			continue
		}
		r := room.Room.Interior
		final := tidyFurnitureFinal(moves)
		explanation := fmt.Sprintf("furniture %s (%s, %dx%d at %d,%d): %d of %d off-plan pieces re-sited in one batch of %d moves", room.ID, room.Room.Role, r.Width, r.Height, r.X, r.Z, final, len(wanted), len(moves))
		return &TidyProposal{Item: TidyItem{Kind: TidyFurniture, ID: room.ID, Footprint: r, Cells: len(moves)}, Gain: final, Moves: moves, Explanation: explanation}
	}
	return nil
}

func tidyFurnitureFinal(moves []TidyMove) int {
	n := 0
	for _, m := range moves {
		if !m.Temp {
			n++
		}
	}
	return n
}

// tidyFurnitureMatch pairs the room's pieces with its plan's slots: a piece
// already on a slot (footprint and rotation) holds it; each open slot, in
// slot order, takes the nearest untidied off-plan piece of its definition
// and size. Pieces the plan has no slot for stay where they are.
func tidyFurnitureMatch(room TidyRoom, tidied map[string]bool) []TidyMove {
	plan, ok := PlanInterior(room.Room, InteriorPieceDef{})
	if !ok {
		return nil
	}
	used := map[string]bool{}
	filled := map[string]bool{}
	for _, slot := range plan.Pieces {
		for _, p := range room.Pieces {
			if !used[p.Thing] && p.Def == slot.Def && p.Rect == slot.Rect && p.Rot == slot.Rot {
				used[p.Thing], filled[slot.Slot] = true, true
				break
			}
		}
	}
	slots := append([]InteriorPiece(nil), plan.Pieces...)
	sort.Slice(slots, func(i, j int) bool { return slots[i].Slot < slots[j].Slot })
	var moves []TidyMove
	for _, slot := range slots {
		if filled[slot.Slot] {
			continue
		}
		best := -1
		var bestD int32
		for i, p := range room.Pieces {
			if used[p.Thing] || tidied[p.Thing] || p.Def != slot.Def || p.Size != slot.Size {
				continue
			}
			d := absInt32(p.Rect.X-slot.Rect.X) + absInt32(p.Rect.Z-slot.Rect.Z)
			if best < 0 || d < bestD {
				best, bestD = i, d
			}
		}
		if best < 0 {
			continue
		}
		p := room.Pieces[best]
		used[p.Thing] = true
		moves = append(moves, TidyMove{Thing: p.Thing, Def: p.Def, Slot: slot.Slot, Size: p.Size, From: p.Rect, To: slot.Rect, Rot: slot.Rot})
	}
	return moves
}

// tidyFurnitureOrder orders the wanted moves so no piece moves onto a cell
// another piece still stands on: a move is emitted once every cell of its
// target is free, and requires the moves that vacated those cells (and the
// piece's own earlier leg). When every remaining move waits on another (a
// swap or a cycle), the first remaining piece is parked on a free cell off
// every slot that keeps the room walkable, and its slot move follows. A
// move whose target a piece outside the batch blocks is dropped. The batch
// is capped at tidyFurnitureCap moves, never leaving a piece parked.
func tidyFurnitureOrder(room TidyRoom, wanted []TidyMove) []TidyMove {
	occupant := map[domain.Cell]string{}
	rotation := map[string]domain.Rotation{}
	for _, p := range room.Pieces {
		rotation[p.Thing] = p.Rot
		for _, c := range rectCells(p.Rect) {
			occupant[c] = p.Thing
		}
	}
	slotCells := map[domain.Cell]bool{}
	for _, m := range wanted {
		for _, c := range rectCells(m.To) {
			slotCells[c] = true
		}
	}
	if plan, ok := PlanInterior(room.Room, InteriorPieceDef{}); ok {
		for _, p := range plan.Pieces {
			for _, c := range rectCells(p.Rect) {
				slotCells[c] = true
			}
		}
	}
	vacatedBy := map[domain.Cell]int{}
	lastMove := map[string]int{}
	position := map[string]Rectangle{}
	for _, m := range wanted {
		position[m.Thing] = m.From
	}
	var out []TidyMove
	emit := func(m TidyMove) {
		m.From = position[m.Thing]
		deps := map[int]bool{}
		for _, c := range rectCells(m.To) {
			if i, ok := vacatedBy[c]; ok {
				deps[i] = true
			}
		}
		if i, ok := lastMove[m.Thing]; ok {
			deps[i] = true
		}
		m.After = nil
		for i := range deps {
			m.After = append(m.After, i)
		}
		sort.Ints(m.After)
		i := len(out)
		for _, c := range rectCells(m.From) {
			delete(occupant, c)
			vacatedBy[c] = i
		}
		for _, c := range rectCells(m.To) {
			occupant[c] = m.Thing
			delete(vacatedBy, c)
		}
		lastMove[m.Thing], position[m.Thing] = i, m.To
		out = append(out, m)
	}
	pending := append([]TidyMove(nil), wanted...)
	moving := func(thing string) bool {
		for _, m := range pending {
			if m.Thing == thing {
				return true
			}
		}
		return false
	}
	for len(pending) > 0 {
		progressed := false
		for i := 0; i < len(pending); i++ {
			m := pending[i]
			ready, feasible := true, true
			for _, c := range rectCells(m.To) {
				if o, ok := occupant[c]; ok && o != m.Thing {
					ready = false
					feasible = feasible && moving(o)
				}
			}
			if ready || !feasible {
				if ready {
					emit(m)
				}
				pending = append(pending[:i], pending[i+1:]...)
				i--
				progressed = true
			}
		}
		if progressed {
			continue
		}
		// Every remaining move waits on another: park the first piece.
		m := pending[0]
		temp, ok := tidyFurnitureTemp(room.Room, occupant, slotCells, position[m.Thing], m.Thing)
		if !ok {
			pending = pending[1:]
			continue
		}
		emit(TidyMove{Thing: m.Thing, Def: m.Def, Size: m.Size, To: temp, Rot: rotation[m.Thing], Temp: true})
	}
	// Cap the batch at a point where no piece is left parked.
	end := min(len(out), tidyFurnitureCap)
	for ; end > 0; end-- {
		parked := map[string]bool{}
		for _, m := range out[:end] {
			parked[m.Thing] = m.Temp
		}
		clean := true
		for _, p := range parked {
			clean = clean && !p
		}
		if clean {
			break
		}
	}
	return out[:end]
}

// tidyFurnitureTemp finds a free footprint of the piece's current shape, in
// cell order, off every plan slot, that keeps the room walkable.
func tidyFurnitureTemp(room InteriorRoom, occupant map[domain.Cell]string, slotCells map[domain.Cell]bool, from Rectangle, thing string) (Rectangle, bool) {
	blocked := map[domain.Cell]bool{}
	for c, o := range occupant {
		if o != thing {
			blocked[c] = true
		}
	}
	r := room.Interior
	for z := r.Z; z+from.Height <= r.Z+r.Height; z++ {
		for x := r.X; x+from.Width <= r.X+r.Width; x++ {
			rect := Rectangle{X: x, Z: z, Width: from.Width, Height: from.Height}
			cells := rectCells(rect)
			free := true
			for _, c := range cells {
				if blocked[c] || slotCells[c] {
					free = false
					break
				}
			}
			if free && InteriorPlacementWalkable(room, blocked, cells) {
				return rect, true
			}
		}
	}
	return Rectangle{}, false
}
