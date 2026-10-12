package policy

import (
	"fmt"
	"hash/fnv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The region interior is the live furnishing entry (#2956): a room's role says
// what its floor should hold (RegionWants), and the region placer seats it,
// behind the room's doors, on any cell set; the seeded interior search then
// improves the layout (#2869). It replaces the role templates, which fit one
// rectangle in a canonical frame. A role with no entry here has no interior.
//
// What a template read as an input is expressed as a want or a placer option:
// the shelter's Occupants, Campfires, Coolers and Capabilities choose how many
// of each piece to want, Required (the throne's title pieces) are wants, and
// Reserved cells go to the placer. Standing widens no row: pieces carry no
// uniform pitch here, and a standing piece is matched to a slot by its
// definition's footprint (InteriorPiece.Accepts), not by where the slot lies.
//
// Slot names are the ones the reconcilers and runtime consumers read; a want
// names its pieces (RegionWant.Names) where a consumer reads the name.

// InteriorTable is the region interior's one judgment table, registered in
// DefTables so a renamed def fails the dangling-reference gate. ShelfDef is
// the input shelf beside a workshop bench and LampDef the standing lamp of a
// bedroom, throne room and dining room; KitchenStoves is the most stoves a
// kitchen wants: two stoves feed a mid-sized colony, and a third only adds
// floor the Space stat counts against the room.
type InteriorTable struct {
	ShelfDef, LampDef string
	KitchenStoves     int
}

var interiorTable = InteriorTable{ShelfDef: workshopShelfDef, LampDef: standingLampDef, KitchenStoves: 2}

// InteriorDefs is the region interior's judgment table.
func InteriorDefs() InteriorTable { return interiorTable }

// Floor is the room's floor: its Region when set, else the Interior
// rectangle.
func (r InteriorRoom) Floor() Region {
	if !r.Region.Empty() {
		return r.Region
	}
	return RectRegion(r.Interior)
}

// regionFrame is the room's floor seen from its entrance: Width cells along the
// entrance wall, Depth away from it, the entrance at column Entrance. A floor
// that is not a rectangle is measured by its bounding rectangle, so it never
// wants less than a rectangle of its bounds would hold; the placer then seats
// what its cells fit.
type regionFrame struct {
	Width, Depth, Entrance int32
}

func (r InteriorRoom) regionFrame() regionFrame {
	probe := r
	probe.Interior, probe.Region = r.Floor().Bounds(), Region{}
	if x, ok := newInteriorTransform(probe); ok {
		f := x.frame()
		return regionFrame{Width: f.Width, Depth: f.Depth, Entrance: f.Entrance}
	}
	b := probe.Interior
	return regionFrame{Width: max(b.Width, b.Height), Depth: min(b.Width, b.Height), Entrance: max(b.Width, b.Height) / 2}
}

// regionRole says what a role's floor should hold: the wants, in the order they
// place, and false when the room cannot be furnished for the piece being
// placed.
type regionRole func(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool)

// regionRoles are the roles the region placer furnishes.
var regionRoles = map[RoomRole]regionRole{
	RoomRoleWorkshop:   workshopWants,
	RoomRoleLaboratory: laboratoryWants,
	RoomRoleKitchen:    kitchenWants,
	RoomRoleTomb:       tombWants,
	RoomRolePrisonCell: jailWants,
	RoomRoleHospital:   hospitalWants,
	RoomRoleBedroom:    bedroomWants,
	RoomRoleSuite:      bedroomWants,
	RoomRoleThroneRoom: throneWants,
	RoomRoleShelter:    shelterWants,
	RoomRoleBarn:       barnWants,
	RoomRoleDiningRoom: diningWants(false),
	RoomRoleRecRoom:    diningWants(true),

	RoomRoleNursery: childRoomWants, RoomRolePlayroom: childRoomWants, RoomRoleClassroom: childRoomWants,
	RoomRoleWorshipRoom: childRoomWants, RoomRoleDeathrestChamber: childRoomWants,
	RoomRoleContainmentCell: childRoomWants, RoomRoleIsolationRoom: childRoomWants,
}

// regionRequest is the room's placement: its role's wants on its floor behind
// its doors.
func regionRequest(room InteriorRoom, piece InteriorPieceDef) (RegionPlacement, bool) {
	role, ok := regionRoles[room.Role]
	if !ok || len(room.Doors) == 0 {
		return RegionPlacement{}, false
	}
	wants, ok := role(room, piece, room.regionFrame())
	if !ok || len(wants) == 0 {
		return RegionPlacement{}, false
	}
	wants = unbuilt(wants, room.Built)
	if len(wants) == 0 && len(room.Built) == 0 {
		return RegionPlacement{}, false
	}
	reserved := append([]domain.Cell(nil), room.Reserved...)
	for _, p := range room.Built {
		reserved = append(append(reserved, rectCells(p.Rect)...), p.Reserves()...)
	}
	return RegionPlacement{Region: room.Floor(), Doors: room.Doors, Reserved: reserved, Wants: wants}, true
}

// unbuilt is the wants less what stands: a built piece takes one piece of the
// first want of its definition that still has one, and the names of the
// pieces it took go with it.
func unbuilt(wants []RegionWant, built []InteriorPiece) []RegionWant {
	taken := map[string]int{}
	for _, p := range built {
		taken[p.Def]++
	}
	var out []RegionWant
	for _, w := range wants {
		n := min(taken[w.Def], w.Count)
		taken[w.Def] -= n
		w.Count, w.Min = w.Count-n, max(w.Min-n, 0)
		if n < len(w.Names) {
			w.Names = w.Names[n:]
		} else {
			w.Names = nil
		}
		if w.Count > 0 {
			out = append(out, w)
		}
	}
	return out
}

// regionSeed seeds the room's search from what the room is, so equal rooms
// furnish alike and no wall time enters.
func regionSeed(room InteriorRoom) uint64 {
	h := fnv.New64a()
	fmt.Fprint(h, room.Role, room.Floor().Runs, room.Doors)
	return h.Sum64()
}

// PlanRegionInterior furnishes the room's floor from its role's wants with the
// greedy placer, improved by the seeded interior search. It is false when the
// role is not furnished by the region placer, the room has no door, or the
// floor does not fit what the role requires. The plan carries world footprints
// only: Frame and Canonical belong to the templates.
func PlanRegionInterior(room InteriorRoom, piece InteriorPieceDef) (InteriorPlan, bool) {
	return planRegion(room, piece, true)
}

func planRegion(room InteriorRoom, piece InteriorPieceDef, search bool) (InteriorPlan, bool) {
	req, ok := regionRequest(room, piece)
	if !ok {
		return InteriorPlan{}, false
	}
	if search {
		req.Search = &InteriorSearch{Candidates: req.Doors, Shapes: room.Shapes, Dining: room.Dining, Pinned: room.Built, Seed: regionSeed(room)}
	}
	pieces, err := PlaceInRegion(req)
	if err != nil {
		return InteriorPlan{}, false
	}
	return InteriorPlan{Template: "region", Room: room, Pieces: pieces}, true
}

// PlanRoomInterior is the live interior entry: the region placer's plan, false
// for a role with no entry in regionRoles. The role templates and PlanInterior
// remain only until they are deleted (#2957).
func PlanRoomInterior(room InteriorRoom, piece InteriorPieceDef) (InteriorPlan, bool) {
	return PlanRegionInterior(room, piece)
}

// RoomHasInterior reports that the room has a plan, without the search: the
// existence check of a caller that reads no piece.
func RoomHasInterior(room InteriorRoom, piece InteriorPieceDef) bool {
	_, ok := planRegion(room, piece, false)
	return ok
}

// benchDef is the definition a family's bench row plans: the piece being
// placed when the family holds it, else the family's default bench
// (RoomFurniture.Bench).
func (r InteriorRoom) benchDef(piece InteriorPieceDef, family RoomRole) (InteriorPieceDef, bool) {
	if piece.Family == family {
		return piece, true
	}
	return r.Shapes.Get(r.Shapes.Furniture.BenchFor(family))
}

// shapeWant is a want for count pieces of a shape.
func shapeWant(slot string, d InteriorPieceDef, count, min int, names func(i int) string) RegionWant {
	w := RegionWant{Slot: slot, Def: d.Def, Size: d.Size, Interaction: d.Interaction, Count: count, Min: min}
	for i := range count {
		w.Names = append(w.Names, names(i))
	}
	return w
}

func numbered(prefix string, from int) func(i int) string {
	return func(i int) string { return fmt.Sprintf("%s.%d", prefix, i+from) }
}

// benchRowCapacity is how many benches of a footprint one wall of the room
// holds with a free cell between them.
func benchRowCapacity(f regionFrame, d InteriorPieceDef, gap int32) int {
	if !frontInteraction(d) || f.Depth < d.Size.Z+2 {
		return 0
	}
	return int(RowCapacity(f.Width, d.Size.X, gap))
}
