package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The barn and the vet room as real rooms (#1633). Each ReserveBarn and
// ReserveVetRoom reservation is a walled room: its interior is the
// reservation without the ring, its door faces the pen (barn) or the colony
// core (vet room). The room is derived from the reservation, never stored
// (HerdRooms). The animal-bed planner (NextHerdStep) shells each room, then
// places animal sleeping spots in the barn, up to one per kept animal, and
// animal beds in the vet room, VetBeds per herd, nearest the door first. A
// herd the rooms cannot hold gets another reservation of the same kind
// (PlanHerdSites); a placed room never moves or shrinks.
//
// The vet room is kept out of animals' reach by its absence from
// LayoutPlan.AnimalAreas, not by its door: a plain door is no barrier to an
// animal, so an animal enters only when colonists carry it in to be treated.
// Each standing vet room bed is then flagged medical (a BedUse patch; any bed
// can carry the flag natively), so colonists treat animals in it.

const (
	// PlannedBarn is the barn's plan role, PlannedVetRoom the vet room's.
	PlannedBarn    PlannedRole = "barn"
	PlannedVetRoom PlannedRole = "vet_room"
	// RoomRoleVetRoom is the vet room's interior-template role. It is the
	// controller's own, not a RoomRoleDef: the game scores a room of animal
	// beds Barn, whichever of the two it is.
	RoomRoleVetRoom RoomRole = "VetRoom"
)

// herdBarnMinBeds is the fewest animals a barn is sized for.
const herdBarnMinBeds = 8

// herdSizeSide bounds the interior side a herd room searches.
const herdSizeSide = 24

func init() {
	RegisterInteriorTemplate(RoomRoleBarn, InteriorTemplate{Name: "barn", Plan: planBarn})
	RegisterInteriorTemplate(RoomRoleVetRoom, InteriorTemplate{Name: "vet room", Plan: planBedGrid})
}

// planBedGrid is the barn and vet room template: a 1-cell aisle straight in
// from the door, and on both sides of it rows of beds alternating with free
// rows (the first free row is the door's), so every bed has a free cell
// beside it from which a colonist tends the animal. Slots run nearest the
// door first.
func planBedGrid(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	size := piece.Size
	if piece.Def == "" || size.X < 1 || size.Z < 1 {
		return nil, false
	}
	var out []InteriorPiece
	for row, v := 0, size.Z; v+size.Z <= f.Depth; row, v = row+1, v+2*size.Z {
		col := 0
		for u := f.Entrance - size.X; u >= 0; u -= size.X {
			out = append(out, NewInteriorPiece(fmt.Sprintf("bed.%d.w%d", row+1, col+1), piece.Def, size, domain.North, domain.Cell{X: u, Z: v}))
			col++
		}
		col = 0
		for u := f.Entrance + 1; u+size.X <= f.Width; u += size.X {
			out = append(out, NewInteriorPiece(fmt.Sprintf("bed.%d.e%d", row+1, col+1), piece.Def, size, domain.North, domain.Cell{X: u, Z: v}))
			col++
		}
	}
	return out, len(out) > 0
}

// herdHeaterSlot is the barn's climate slot (#1867): the heater the
// temperature planner counts as conditioning the room and the power planner
// connects like any other unpowered consumer. It is no bed.
const herdHeaterSlot = "climate.heater"

// planBarn is planBedGrid plus the climate slot: the catalog's heater
// (RoomFurniture.Heater) on the free floor furthest from the door where it
// keeps the room walkable and leaves every bed a free neighbour to be tended
// from. A room with no such cell, or a catalog with no heater shape, has no
// climate slot.
func planBarn(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	beds, ok := planBedGrid(f, piece)
	if !ok {
		return nil, false
	}
	heater, found := f.Shapes.Get(f.Shapes.Furniture.Heater)
	if !found || heater.Size.X != 1 || heater.Size.Z != 1 {
		return beds, true
	}
	blocked := map[domain.Cell]bool{}
	for _, b := range beds {
		for _, c := range rectCells(b.Rect) {
			blocked[c] = true
		}
	}
	room := InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors}
	for v := f.Depth - 1; v >= 0; v-- {
		for _, u := range herdHeaterColumns(f) {
			cell := domain.Cell{X: u, Z: v}
			if blocked[cell] || !herdBedsStayTended(f, blocked, cell) || !InteriorPlacementWalkable(room, blocked, []domain.Cell{cell}) {
				continue
			}
			return append(beds, NewInteriorPiece(herdHeaterSlot, heater.Def, heater.Size, domain.North, cell)), true
		}
	}
	return beds, true
}

// herdHeaterColumns are the frame's columns furthest from the aisle first.
func herdHeaterColumns(f InteriorFrame) []int32 {
	var out []int32
	for u := int32(0); u < f.Width; u++ {
		out = append(out, u)
	}
	sort.SliceStable(out, func(i, j int) bool {
		return abs64(float64(out[i]-f.Entrance)) > abs64(float64(out[j]-f.Entrance))
	})
	return out
}

// herdBedsStayTended reports whether every bed next to cell keeps a free
// neighbour other than cell to be tended from.
func herdBedsStayTended(f InteriorFrame, blocked map[domain.Cell]bool, cell domain.Cell) bool {
	around := func(c domain.Cell) []domain.Cell {
		return []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}}
	}
	for _, bed := range around(cell) {
		if !blocked[bed] {
			continue
		}
		free := false
		for _, n := range around(bed) {
			free = free || n != cell && !blocked[n] && n.X >= 0 && n.Z >= 0 && n.X < f.Width && n.Z < f.Depth
		}
		if !free {
			return false
		}
	}
	return true
}

// herdGridBeds is the 1x1 beds an interior w by h holds whichever wall its
// door is in: planBedGrid's columns beside the aisle times its bed rows.
func herdGridBeds(w, h int32) int {
	return int(min((w-1)*(h/2), (h-1)*(w/2)))
}

// herdSide is the near-square interior holding at least beds 1x1 beds, the
// fewest cells first.
func herdSide(beds int) (w, h int32) {
	best := int32(0)
	for a := int32(3); a <= herdSizeSide; a++ {
		for b := a; b <= herdSizeSide; b++ {
			if herdGridBeds(a, b) < beds || herdGridBeds(b, a) < beds {
				continue
			}
			if cells := a * b; best == 0 || cells < best || cells == best && b-a < h-w {
				best, w, h = cells, a, b
			}
		}
	}
	if best == 0 {
		return herdSizeSide, herdSizeSide
	}
	return w, h
}

// herdRole is the plan role of a herd reservation kind.
func herdRole(kind ReservationKind) (PlannedRole, bool) {
	switch kind {
	case ReserveBarn:
		return PlannedBarn, true
	case ReserveVetRoom:
		return PlannedVetRoom, true
	}
	return "", false
}

// HerdRooms are the plan's rooms of role (PlannedBarn or PlannedVetRoom), one
// per reservation in plan order.
func (p LayoutPlan) HerdRooms(role PlannedRole) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Reservations {
		if want, ok := herdRole(r.Kind); ok && want == role {
			out = append(out, p.herdRoom(r.Area, role))
		}
	}
	return out
}

// herdRoom is the room a barn or vet room reservation holds: the door in
// the middle of the wall facing the first pen (barn) or the colony core
// (vet room; the barn's centre when the plan has no core).
func (p LayoutPlan) herdRoom(area Rectangle, role PlannedRole) PlannedRoom {
	in := Rectangle{X: area.X + 1, Z: area.Z + 1, Width: area.Width - 2, Height: area.Height - 2}
	room := PlannedRoom{Role: role, Interior: in}
	cx, cz := float64(in.X)+float64(in.Width)/2, float64(in.Z)+float64(in.Height)/2
	tx, tz, ok := p.herdTarget(role)
	if !ok {
		tx, tz = cx, cz-1
	}
	dx, dz := tx-cx, tz-cz
	east := herdDoor{domain.Cell{X: in.X + in.Width, Z: in.Z + in.Height/2}, domain.East, 1, 0, dx}
	west := herdDoor{domain.Cell{X: in.X - 1, Z: in.Z + in.Height/2}, domain.West, -1, 0, -dx}
	north := herdDoor{domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height}, domain.North, 0, 1, dz}
	south := herdDoor{domain.Cell{X: in.X + in.Width/2, Z: in.Z - 1}, domain.South, 0, -1, -dz}
	var first herdDoor
	switch {
	case abs64(dx) > abs64(dz) && dx > 0:
		first = east
	case abs64(dx) > abs64(dz):
		first = west
	case dz > 0:
		first = north
	default:
		first = south
	}
	// A vet room standing against a barn's wall opens into it through a
	// door in the shared wall, besides its own door outside.
	if role == PlannedVetRoom {
		for _, r := range p.Reservations {
			if r.Kind != ReserveBarn || r.Area == area {
				continue
			}
			if link, ok := sharedWallLink(area, r.Area); ok {
				room.Link = &link
				break
			}
		}
	}
	// The door faces its target unless another reservation's walls stand
	// right outside it; then the best side that opens on free ground.
	room.Door, room.DoorRot = first.cell, first.rot
	if !p.doorBlocked(first, area) {
		return room
	}
	best, found := herdDoor{}, false
	for _, d := range []herdDoor{east, west, north, south} {
		if d.rot == first.rot || p.doorBlocked(d, area) || found && d.score <= best.score {
			continue
		}
		best, found = d, true
	}
	if found {
		room.Door, room.DoorRot = best.cell, best.rot
	}
	return room
}

// herdDoor is a candidate door: its wall cell, the side it faces, the step
// to the cell outside it and how well it faces the room's target.
type herdDoor struct {
	cell   domain.Cell
	rot    domain.Rotation
	sx, sz int32
	score  float64
}

// doorBlocked reports the cell outside d inside another reservation's area
// (its walls), where the door would open on a wall nobody can walk through.
func (p LayoutPlan) doorBlocked(d herdDoor, own Rectangle) bool {
	out := domain.Cell{X: d.cell.X + d.sx, Z: d.cell.Z + d.sz}
	for _, r := range p.Reservations {
		a := r.Area
		if a == own {
			continue
		}
		if out.X >= a.X && out.X < a.X+a.Width && out.Z >= a.Z && out.Z < a.Z+a.Height {
			return true
		}
	}
	return false
}

func abs64(v float64) float64 {
	if v < 0 {
		return -v
	}
	return v
}

// herdTarget is the point a herd room's door faces.
func (p LayoutPlan) herdTarget(role PlannedRole) (x, z float64, ok bool) {
	centre := func(r Rectangle) (float64, float64, bool) {
		return float64(r.X) + float64(r.Width)/2, float64(r.Z) + float64(r.Height)/2, true
	}
	if role == PlannedVetRoom {
		if c, found := p.Core(); found {
			return float64(c.X), float64(c.Z), true
		}
	}
	want := ReservePen
	if role == PlannedVetRoom {
		want = ReserveBarn
	}
	for _, r := range p.Reservations {
		if r.Kind == want {
			return centre(r.Area)
		}
	}
	return 0, 0, false
}

// herdCapacity is the beds the plan's rooms of role hold, counting 1x1
// beds: the sizes of the real definitions are read natively when a room is
// furnished.
func (p LayoutPlan) herdCapacity(role PlannedRole) int {
	n := 0
	for _, room := range p.HerdRooms(role) {
		n += herdRoomBeds(room, PieceShapes{}, InteriorPieceDef{Def: "bed", Size: domain.Cell{X: 1, Z: 1}})
	}
	return n
}

// herdRoomBeds is the beds def's template holds in room.
func herdRoomBeds(room PlannedRoom, shapes PieceShapes, def InteriorPieceDef) int {
	in, ok := InteriorRoomFromLayout(room, shapes)
	if !ok {
		return 0
	}
	plan, ok := PlanInterior(in, def)
	if !ok {
		return 0
	}
	return len(herdBedPieces(plan.Pieces))
}

// herdBedPieces are the pieces of a barn or vet room plan that are beds: all
// but the climate slot.
func herdBedPieces(pieces []InteriorPiece) []InteriorPiece {
	var out []InteriorPiece
	for _, p := range pieces {
		if p.Slot != herdHeaterSlot {
			out = append(out, p)
		}
	}
	return out
}

// HerdFurniture are the native shapes of the two animal bed definitions.
type HerdFurniture struct {
	Spot, Bed InteriorPieceDef
	// Heater is the barn's climate piece; the zero value while its research
	// is unfinished or its size unread, which plans no heater step (the beds
	// never wait on it).
	Heater InteriorPieceDef
}

// HerdStepKind is the next animal-bed step.
type HerdStepKind string

const (
	// HerdNone: nothing is due, or a fact is unknown.
	HerdNone HerdStepKind = ""
	// HerdShell: raise the walls and door of Room.
	HerdShell HerdStepKind = "shell"
	// HerdPlace: place Piece, a bed or the barn's heater, in Room.
	HerdPlace HerdStepKind = "place"
	// HerdMedical: flag Bed, a standing vet room bed, medical.
	HerdMedical HerdStepKind = "medical"
)

// HerdStep is one bounded step towards the barn and vet room.
type HerdStep struct {
	Kind  HerdStepKind
	Role  PlannedRole
	Room  PlannedRoom
	Piece InteriorPiece
	// Bed is the census id of the bed a HerdMedical step flags.
	Bed string
}

// Owed reports whether the planner can act on the step now.
func (s HerdStep) Owed() bool {
	return s.Kind == HerdShell || s.Kind == HerdPlace || s.Kind == HerdMedical
}

// NextHerdStep picks the next barn or vet room step for a herd of animals
// kept animals: the barn holds one sleeping spot per animal and the vet
// room VetBeds of them, filled room by room in plan order, each room walled
// before it is furnished and each vet bed flagged medical (sleeping's Medical
// fact says which are) before the next is placed. None for no animals or
// once every bed stands and is flagged.
func NextHerdStep(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, sleeping []SleepingBed, animals int, f HerdFurniture) HerdStep {
	if animals <= 0 {
		return HerdStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	for _, site := range []struct {
		role PlannedRole
		def  InteriorPieceDef
		beds int
	}{{PlannedBarn, f.Spot, animals}, {PlannedVetRoom, f.Bed, VetBeds(animals)}} {
		want := site.beds
		for _, room := range plan.HerdRooms(site.role) {
			if want <= 0 {
				break
			}
			in, ok := InteriorRoomFromLayout(room, rooms.Shapes)
			if !ok {
				continue
			}
			layout, ok := PlanInterior(in, site.def)
			if !ok {
				continue
			}
			beds := herdBedPieces(layout.Pieces)
			quota := min(want, len(beds))
			want -= quota
			if _, standing := PlannedRoomStanding(room, rooms); !standing {
				return HerdStep{Kind: HerdShell, Role: site.role, Room: room}
			}
			if site.role == PlannedVetRoom {
				if bed, due := unflaggedVetBed(room, built, sleeping, site.def.Def); due {
					return HerdStep{Kind: HerdMedical, Role: site.role, Room: room, Bed: bed}
				}
			}
			have := 0
			for _, b := range built {
				if b.Building.Definition() == site.def.Def && len(b.Cells) > 0 && rectInside(room.Interior, cellsRectangle(b.Cells)) {
					have++
				}
			}
			if have >= quota {
				if p, due := barnHeater(site.role, f.Heater, layout.Pieces, room, built, taken); due {
					return HerdStep{Kind: HerdPlace, Role: site.role, Room: room, Piece: p}
				}
				continue
			}
			for _, p := range beds {
				free := true
				for _, c := range rectCells(p.Rect) {
					free = free && !taken[c]
				}
				if free {
					return HerdStep{Kind: HerdPlace, Role: site.role, Room: room, Piece: p}
				}
			}
		}
	}
	return HerdStep{}
}

// barnHeater is the climate piece a barn owes once its beds stand: the
// planned heater slot while no heater of def stands in the room and its cells
// are free. A catalog heater that is not buildable yet owes nothing.
func barnHeater(role PlannedRole, def InteriorPieceDef, pieces []InteriorPiece, room PlannedRoom, built []CurrentBuilding, taken map[domain.Cell]bool) (InteriorPiece, bool) {
	if role != PlannedBarn || def.Def == "" {
		return InteriorPiece{}, false
	}
	for _, b := range built {
		if b.Building.Definition() == def.Def && len(b.Cells) > 0 && rectInside(room.Interior, cellsRectangle(b.Cells)) {
			return InteriorPiece{}, false
		}
	}
	for _, p := range pieces {
		if p.Slot != herdHeaterSlot || p.Def != def.Def {
			continue
		}
		for _, c := range rectCells(p.Rect) {
			if taken[c] {
				return InteriorPiece{}, false
			}
		}
		return p, true
	}
	return InteriorPiece{}, false
}

// unflaggedVetBed is the first standing bed of def in room whose census row
// reads not medical; a bed the census lacks or whose flag is unread is not
// due yet.
func unflaggedVetBed(room PlannedRoom, built []CurrentBuilding, sleeping []SleepingBed, def string) (string, bool) {
	for _, b := range built {
		if b.Building.Definition() != def || len(b.Cells) == 0 || !rectInside(room.Interior, cellsRectangle(b.Cells)) {
			continue
		}
		for _, s := range sleeping {
			if medical, known := s.Medical.Value(); s.ID == b.ID && known && !medical {
				return b.ID, true
			}
		}
	}
	return "", false
}
