package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The barn and the vet room as real rooms. Each ReserveBarn and
// ReserveVetRoom reservation is a walled room: its interior is the
// reservation without the ring, its door faces the colony core. The barn also
// takes an animal flap beside its door, into the paddock. The room is derived from the reservation, never stored
// (HerdRooms). The animal-bed planner (NextHerdStep) reconciles each room: its
// ring and door, then animal sleeping spots in the barn, up to one per kept animal, and
// animal beds in the vet room, VetBeds per herd, nearest the door first, through
// the build-side reconciler (ReconcileRoom). A
// herd the rooms cannot hold gets another reservation of the same kind
// (PlanHerdSites); a placed room never moves or shrinks.
//
// The vet room is kept out of animals' reach by its not being a
// barn reservation (animals roam only the barns), not by its door: a plain door is no barrier to an
// animal, so an animal enters only when colonists carry it in to be treated.
// Each standing vet room bed is then flagged medical (a BedUse patch; any bed
// can carry the flag natively), so colonists treat animals in it.

const (
	// PlannedBarn is the barn's plan role, PlannedVetRoom the vet room's.
	PlannedBarn    PlannedRole = "barn"
	PlannedVetRoom PlannedRole = "vet_room"
	// PlannedPen is the paddock marker's plan role: the outdoor room the
	// build side reconciles the one PenMarker through. It is no part of
	// roomsWithHerd and no reservation holds one.
	PlannedPen PlannedRole = "pen"
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

// herdHeaterSlot is the barn's climate slot: the heater the
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

// HerdRooms are the plan's rooms of role (PlannedBarn or PlannedVetRoom),
// one per reservation in plan order.
func (p LayoutPlan) HerdRooms(role PlannedRole) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Reservations {
		if want, ok := herdRole(r.Kind); ok && want == role {
			out = append(out, p.herdRoom(r.Area, role))
		}
	}
	return out
}

// roomsWithHerd is AllRooms and the barns and vet rooms the reservations hold:
// the rooms whose doors and links a ring is matched against.
func (p LayoutPlan) roomsWithHerd() []PlannedRoom {
	return append(append(slices.Clone(p.AllRooms()), p.HerdRooms(PlannedBarn)...), p.HerdRooms(PlannedVetRoom)...)
}

// barnFlap is the cell of a barn's ring that takes its animal flap:
// beside the barn's own door, so it opens onto the same ground, the paddock.
// The flap lets animals through but no colonist; the door is the colonists'.
// The cell is on the wall's straight run (never a corner), no other
// reservation stands right outside it.
func (p LayoutPlan) barnFlap(area Rectangle) (domain.Cell, bool) {
	room := p.herdRoom(area, PlannedBarn)
	var step, along domain.Cell
	switch room.DoorRot {
	case domain.East:
		step, along = domain.Cell{X: 1}, domain.Cell{Z: 1}
	case domain.West:
		step, along = domain.Cell{X: -1}, domain.Cell{Z: 1}
	case domain.North:
		step, along = domain.Cell{Z: 1}, domain.Cell{X: 1}
	default:
		step, along = domain.Cell{Z: -1}, domain.Cell{X: 1}
	}
	in := room.Interior
	for _, sign := range []int32{1, -1} {
		c := domain.Cell{X: room.Door.X + sign*along.X, Z: room.Door.Z + sign*along.Z}
		if along.X != 0 && (c.X < in.X || c.X >= in.X+in.Width) || along.Z != 0 && (c.Z < in.Z || c.Z >= in.Z+in.Height) {
			continue
		}
		if p.doorBlocked(herdDoor{cell: c, sx: step.X, sz: step.Z}, area) {
			continue
		}
		return c, true
	}
	return domain.Cell{}, false
}

// flaps are the animal flap cells of every barn, in plan order.
func (p LayoutPlan) flaps() []domain.Cell {
	var out []domain.Cell
	for _, r := range p.Reservations {
		if r.Kind != ReserveBarn {
			continue
		}
		if c, ok := p.barnFlap(r.Area); ok {
			out = append(out, c)
		}
	}
	return out
}

// FlapCells are the cells of r's ring that take an animal flap: a barn's flap
// beside its door, none for any other room.
func (p LayoutPlan) FlapCells(r PlannedRoom) []domain.Cell {
	return p.ringCells(r, p.flaps())
}

func (p LayoutPlan) ringCells(r PlannedRoom, cells []domain.Cell) []domain.Cell {
	if r.Outdoor {
		return nil
	}
	ring := roomWalls(r)
	var out []domain.Cell
	for _, c := range cells {
		if onRing(c, ring) {
			out = append(out, c)
		}
	}
	return out
}

// herdRoom is the room a barn or vet room reservation holds: the door in
// the middle of the wall facing the first pen (barn) or the colony core
// (vet room; the barn's centre when the plan has no core).
func (p LayoutPlan) herdRoom(area Rectangle, role PlannedRole) PlannedRoom {
	in := Rectangle{X: area.X + 1, Z: area.Z + 1, Width: area.Width - 2, Height: area.Height - 2}
	room := PlannedRoom{Role: role, Interior: in, Outdoor: role.IsOutdoor()}
	cx, cz := float64(in.X)+float64(in.Width)/2, float64(in.Z)+float64(in.Height)/2
	tx, tz, ok := p.herdTarget()
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

// herdTarget is the point a herd room's door faces: the colony core.
func (p LayoutPlan) herdTarget() (x, z float64, ok bool) {
	if c, found := p.Core(); found {
		return float64(c.X), float64(c.Z), true
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
	// HerdReconcile: Room differs from the plan or from Template (the animal
	// beds, and the barn's heater): the build side reconciles it
	// (ReconcileRoom). The room's state is whatever the diff leaves; there is
	// no shell or place step.
	HerdReconcile HerdStepKind = "reconcile"
	// HerdMedical: flag Bed, a standing vet room bed, medical.
	HerdMedical HerdStepKind = "medical"
)

// HerdStep is one bounded step towards the barn and vet room.
type HerdStep struct {
	Kind HerdStepKind
	Role PlannedRole
	Room PlannedRoom
	// Template is a HerdReconcile's wanted furniture: standing beds where
	// they stand, the missing ones in their template slots, the barn's heater.
	Template []WantedPiece
	// Bed is the census id of the bed a HerdMedical step flags.
	Bed string
}

// Owed reports whether the planner can act on the step now.
func (s HerdStep) Owed() bool {
	return s.Kind == HerdReconcile || s.Kind == HerdMedical
}

// NextHerdStep picks the next barn or vet room step for a herd of animals
// kept animals: the barn holds one sleeping spot per animal and the vet
// room VetBeds of them, filled room by room in plan order. A room is
// reconciled while its ring or doors differ from the plan (a lost wall is
// rebuilt the same way as a first shell) or a bed of its quota or the barn's
// heater is missing; each vet bed is flagged medical (sleeping's Medical
// fact says which are) first. None for no animals or once every room matches
// and every bed stands and is flagged.
func NextHerdStep(plan LayoutPlan, rooms RoomObservation, ground GroundCensus, built []CurrentBuilding, sleeping []SleepingBed, animals int, f HerdFurniture) HerdStep {
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
			if site.role == PlannedVetRoom {
				if bed, due := unflaggedVetBed(room, built, sleeping, site.def.Def); due {
					return HerdStep{Kind: HerdMedical, Role: site.role, Room: room, Bed: bed}
				}
			}
			template, absent := herdTemplate(site.role, site.def, f.Heater, layout.Pieces, quota, room, built, taken)
			if absent || !plan.GroundMatches(room, ground) {
				return HerdStep{Kind: HerdReconcile, Role: site.role, Room: room, Template: template}
			}
		}
	}
	return HerdStep{}
}

// herdTemplate is a barn or vet room's wanted furniture: the beds of def
// standing in the room (up to quota) where they stand, the missing ones in
// the first free planned bed slots, and the barn's heater (standing, else its
// planned slot when free). absent reports a planned piece the room lacks. A
// heater that is not buildable yet (no def) is no part of the template.
func herdTemplate(role PlannedRole, def, heater InteriorPieceDef, pieces []InteriorPiece, quota int, room PlannedRoom, built []CurrentBuilding, taken map[domain.Cell]bool) (template []WantedPiece, absent bool) {
	inside := func(b CurrentBuilding, name string) bool {
		return b.Building.Definition() == name && len(b.Cells) > 0 && rectInside(room.Interior, cellsRectangle(b.Cells))
	}
	standing := func(b CurrentBuilding) WantedPiece {
		r := cellsRectangle(b.Cells)
		return WantedPiece{DefName: b.Building.Definition(), Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}}
	}
	planned := func(p InteriorPiece) WantedPiece {
		return WantedPiece{DefName: p.Def, Minimum: domain.Cell{X: p.Rect.X, Z: p.Rect.Z}, Maximum: domain.Cell{X: p.Rect.X + p.Rect.Width - 1, Z: p.Rect.Z + p.Rect.Height - 1}, Slot: p.Slot, Size: p.Size, Rot: p.Rot}
	}
	free := func(p InteriorPiece) bool {
		for _, c := range rectCells(p.Rect) {
			if taken[c] {
				return false
			}
		}
		return true
	}
	have := 0
	for _, b := range built {
		if have < quota && inside(b, def.Def) {
			template = append(template, standing(b))
			have++
		}
	}
	for _, p := range herdBedPieces(pieces) {
		if have < quota && free(p) {
			template = append(template, planned(p))
			absent = true
			have++
		}
	}
	if role != PlannedBarn || heater.Def == "" {
		return template, absent
	}
	for _, b := range built {
		if inside(b, heater.Def) {
			return append(template, standing(b)), absent
		}
	}
	for _, p := range pieces {
		if p.Slot == herdHeaterSlot && p.Def == heater.Def && free(p) {
			return append(template, planned(p)), true
		}
	}
	return template, absent
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
