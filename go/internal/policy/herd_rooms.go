package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The barn as a real room. Each ReserveBarn reservation is a walled room: its
// interior is the reservation without the ring, its door faces the colony
// core; a pen beside it joins it through an animal flap in the wall they share
// (herd_pen.go). The room is derived from the reservation, never stored
// (HerdRooms). The animal-bed planner (NextHerdStep) reconciles each room: its
// ring and door, then animal sleeping spots in the barn, up to one per kept
// animal, nearest the door first, through the build-side reconciler
// (ReconcileRoom); then each pen's fence, gate and marker. A herd the rooms
// cannot hold gets another reservation (PlanHerdSites); a placed room never
// moves or shrinks. The barn is the bedroom of every animal, bonded ones
// included; a sick animal is treated in the hospital's animal beds
// (herd_ward.go).

const (
	// PlannedBarn is the barn's plan role.
	PlannedBarn PlannedRole = "barn"
	// PlannedPen is the plan role of a pen reservation: an outdoor room, a
	// fence and a gate round grazing ground, its barn's wall on the shared side.
	PlannedPen PlannedRole = "pen"
)

// herdBarnMinBeds is the fewest animals a barn is sized for.
const herdBarnMinBeds = 8

// herdSizeSide bounds the interior side a herd room searches.
const herdSizeSide = 24

// herdHeaterSlot is the barn's climate slot: the heater the
// temperature planner counts as conditioning the room and the power planner
// connects like any other unpowered consumer. It is no bed.
const herdHeaterSlot = "climate.heater"

// herdGridBeds is the 1x1 beds an interior w by h holds whichever wall its
// door is in: the columns beside the barn aisle times its bed rows.
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
	case ReservePen:
		return PlannedPen, true
	}
	return "", false
}

// HerdRooms are the plan's rooms of role (PlannedBarn, PlannedPen), one per
// reservation in plan order.
func (p LayoutPlan) HerdRooms(role PlannedRole) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Reservations {
		if want, ok := herdRole(r.Kind); ok && want == role {
			out = append(out, p.herdRoom(r.Area.Bounds(), role))
		}
	}
	return out
}

// roomsWithHerd is AllRooms and the barns the reservations hold: the rooms
// whose doors and links a ring is matched against.
func (p LayoutPlan) roomsWithHerd() []PlannedRoom {
	return append(slices.Clone(p.Rooms), p.HerdRooms(PlannedBarn)...)
}

// herdRoom is the room a barn reservation holds: the door in the middle of the
// wall facing the colony core (the barn's centre when the plan has no core).
func (p LayoutPlan) herdRoom(area Rectangle, role PlannedRole) PlannedRoom {
	in := Rectangle{X: area.X + 1, Z: area.Z + 1, Width: area.Width - 2, Height: area.Height - 2}
	room := PlannedRoom{Role: role, Region: RectRegion(in), Outdoor: role.IsOutdoor()}
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
	for _, r := range p.Reserved() {
		a := r.Area
		if a.Equal(RectRegion(own)) {
			continue
		}
		if a.Contains(out) {
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
	plan, ok := PlanRoomInterior(in, def)
	if !ok {
		return 0
	}
	return len(herdBedPieces(plan.Pieces, room.Door))
}

// herdBedPieces are the pieces of a barn plan that are beds: all but the
// climate slot, nearest the door first (a barn short of animals fills from the
// door).
func herdBedPieces(pieces []InteriorPiece, door domain.Cell) []InteriorPiece {
	var out []InteriorPiece
	for _, p := range pieces {
		if p.Slot != herdHeaterSlot {
			out = append(out, p)
		}
	}
	at := Rectangle{X: door.X, Z: door.Z, Width: 1, Height: 1}
	sort.SliceStable(out, func(i, j int) bool { return centreDistance(out[i].Rect, at) < centreDistance(out[j].Rect, at) })
	return out
}

// HerdFurniture are the native shapes of the two animal bed definitions.
type HerdFurniture struct {
	// Spot is the barn's sleeping spot, Bed the hospital ward's animal bed.
	Spot, Bed InteriorPieceDef
	// Heater is the barn's climate piece; the zero value while its research
	// is unfinished or its size unread, which plans no heater step (the beds
	// never wait on it).
	Heater InteriorPieceDef
	// Marker is the pen's PenMarker; the zero value while it is not buildable,
	// which plans no new marker (a standing one is kept).
	Marker InteriorPieceDef
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
	// HerdMedical: flag Bed, a standing hospital ward bed, medical.
	HerdMedical HerdStepKind = "medical"
)

// HerdStep is one bounded step towards the barn and the hospital's ward.
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

// NextHerdStep picks the next barn or ward step for a herd of animals kept
// animals and ward animal beds (WardBeds): the barn holds one sleeping spot per
// animal, filled room by room in plan order; the hospital, once it stands,
// holds the ward beds in the free floor beside its own template. A barn is
// reconciled while its ring or doors differ from the plan (a lost wall is
// rebuilt the same way as a first shell) or a bed of its quota or its heater
// is missing; each ward bed is flagged medical (sleeping's Medical fact says
// which are) first. The hospital's ring is the hospital planner's, so the ward
// step waits for the room to stand. None for no animals and no ward demand, or
// once every room matches and every bed stands and is flagged.
func NextHerdStep(plan LayoutPlan, rooms RoomObservation, ground GroundCensus, built []CurrentBuilding, sleeping []SleepingBed, animals, ward int, f HerdFurniture) HerdStep {
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	want := animals
	for _, room := range plan.HerdRooms(PlannedBarn) {
		if want <= 0 {
			break
		}
		in, ok := InteriorRoomFromLayout(room, rooms.Shapes)
		if !ok {
			continue
		}
		layout, ok := PlanRoomInterior(in, f.Spot)
		if !ok {
			continue
		}
		beds := herdBedPieces(layout.Pieces, room.Door)
		quota := min(want, len(beds))
		want -= quota
		template, absent := herdTemplate(PlannedBarn, f.Spot, f.Heater, layout.Pieces, quota, room, built, taken)
		if absent || !plan.GroundMatches(room, ground) {
			return HerdStep{Kind: HerdReconcile, Role: PlannedBarn, Room: room, Template: template}
		}
	}
	for _, room := range plan.HerdRooms(PlannedPen) {
		piece, marked := penMarkerPiece(room, f.Marker, built, taken)
		var template []WantedPiece
		if marked {
			template = []WantedPiece{piece}
		}
		if marked && !PenMarkerStanding(room, built) || !plan.GroundMatches(room, ground) {
			return HerdStep{Kind: HerdReconcile, Role: PlannedPen, Room: room, Template: template}
		}
	}
	if ward <= 0 || f.Bed.Def == "" {
		return HerdStep{}
	}
	for _, room := range plan.hospitals() {
		if _, standing := CensusRoomIn(room, rooms); !standing {
			continue
		}
		if bed, due := unflaggedWardBed(room, built, sleeping, f.Bed.Def); due {
			return HerdStep{Kind: HerdMedical, Role: PlannedHospital, Room: room, Bed: bed}
		}
		template, absent := wardTemplate(room, rooms.Shapes, f.Bed, ward, built, taken)
		if absent {
			return HerdStep{Kind: HerdReconcile, Role: PlannedHospital, Room: room, Template: template}
		}
		ward -= min(ward, len(template))
		if ward <= 0 {
			break
		}
	}
	return HerdStep{}
}

// herdTemplate is a barn's wanted furniture: the beds of def
// standing in the room (up to quota) where they stand, the missing ones in
// the first free planned bed slots, and the barn's heater (standing, else its
// planned slot when free). absent reports a planned piece the room lacks. A
// heater that is not buildable yet (no def) is no part of the template.
func herdTemplate(role PlannedRole, def, heater InteriorPieceDef, pieces []InteriorPiece, quota int, room PlannedRoom, built []CurrentBuilding, taken map[domain.Cell]bool) (template []WantedPiece, absent bool) {
	inside := func(b CurrentBuilding, name string) bool {
		return b.Building.Definition() == name && len(b.Cells) > 0 && rectInside(room.Region.Bounds(), cellsRectangle(b.Cells))
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
	for _, p := range herdBedPieces(pieces, room.Door) {
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
