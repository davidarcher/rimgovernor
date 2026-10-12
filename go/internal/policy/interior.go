package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// InteriorRoom is the input to a plan: the role whose template applies, the
// floor (walls excluded) and the doors in the walls around it.
type InteriorRoom struct {
	Role     RoomRole
	Interior Rectangle
	// Region is the floor when it is not the whole Interior rectangle (an
	// L, a notch); the zero Region is the rectangle (Floor). Only the region
	// placer reads it, Interior is its bounding rectangle.
	Region Region
	Doors  []domain.Cell
	// InnerDoors are the doors among Doors that open onto another room;
	// the frame faces a door onto a hallway or outdoors when it has one.
	InnerDoors []domain.Cell
	// Standing are the definitions of the player buildings standing on the
	// floor, sorted; a bench row's pitch widens to the widest standing
	// member of its family.
	Standing []string
	// Dining is the furniture the dining and rec templates place, from the
	// catalog rows; the zero value for a room those templates never plan.
	Dining DiningFurniture
	// Shapes are the plannable shapes of the catalog's buildable
	// definitions; a plan needs them to size and sort every piece.
	Shapes PieceShapes
	// Required are the pieces the throne template plans besides the throne
	// (the title's braziers, columns and instrument), each with the
	// definition and shape the catalog gave it.
	Required []RequiredPiece
	// Occupants is how many sleepers a template that packs bunks plans for
	// (the shelter); 0 fills every bunk that fits. Derived and never
	// persisted, like the rest of the plan input.
	Occupants int
	// Campfires is how many campfire slots the shelter template plans:
	// two on a cold map, none elsewhere. Derived from the layout plan's
	// latched climate, never persisted.
	Campfires int
	// Coolers is how many passive cooler floor slots the shelter template
	// plans: one on a hot map, none elsewhere, from the plan's latch.
	Coolers int
	// Capabilities are what the room hosts; the shelter template plans a
	// piece (research bench, crafting spot, campfires) only for a capability
	// the room hosts. Derived from the planned role, never persisted.
	Capabilities []Capability
	// Reserved are floor cells (world) a template must keep clear: ground
	// another plan holds, like the starter stockpile patch or rock yet to be
	// mined. Cells outside the interior are ignored.
	Reserved []domain.Cell
	// Built are the pieces standing on the floor, as world footprints. The
	// region placer leaves them where they stand: they block their cells, count
	// toward a want of their definition and link to what is placed around
	// them, and the plan holds only the pieces still to stand. The templates
	// ignore them.
	Built []InteriorPiece
	// WorkChairs maps a bench definition to the work chair priced for it
	// (PlannedRoom.WorkChairs); the region placer stands one on each such bench's
	// interaction cell. Absent leaves the bench without a chair.
	WorkChairs map[string]string
}

// Piece is def's shape for a plan in this room; false when the catalog has
// no buildable row for it.
func (r InteriorRoom) Piece(def string) (InteriorPieceDef, bool) { return r.Shapes.Get(def) }

// InteriorPiece is one planned building; Rect is its world footprint.
type InteriorPiece struct {
	// Slot names the piece within its room ("bed", "bench.2").
	Slot string
	Def  string
	// Size is the definition's native size at rotation North (x, z).
	Size domain.Cell
	Rot  domain.Rotation
	Rect Rectangle
	// InteractionOffset is the definition's interaction cell offset at
	// rotation North, from the anchor; nil for pieces without one.
	InteractionOffset *domain.Cell
	// Row names the want the piece was placed for (RegionWant.Slot).
	Row string
	// Beside names the Row of the pieces this one must face and touch (a chair
	// at its table); Clear is the length of the straight lane kept open in
	// front of the piece, where its user stands (RegionWant.Beside, .Clear).
	Beside string
	Clear  int32
}

// Anchor is the cell a native building order names for this footprint.
func (p InteriorPiece) Anchor() domain.Cell { return AnchorForRect(p.Rect, p.Size, p.Rot) }

// Interaction is the cell the worker stands on, if the piece has one.
func (p InteriorPiece) Interaction() (domain.Cell, bool) {
	if p.InteractionOffset == nil {
		return domain.Cell{}, false
	}
	a, o := p.Anchor(), rotateOffset(*p.InteractionOffset, p.Rot)
	return domain.Cell{X: a.X + o.X, Z: a.Z + o.Z}, true
}

// InteriorPlan is a room's derived layout.
type InteriorPlan struct {
	Room InteriorRoom
	// Pieces are the planned footprints, in world cells.
	Pieces []InteriorPiece
}

// entranceFrame is the room's floor seen from its entrance: Width cells along
// the entrance wall, Depth away from it, the entrance in column Entrance, on the
// left half of the wall. The entrance is the first door in cell order that
// opens onto a hallway or outdoors, or the first door when every door leads
// into another room. False when the room has no door in its walls.
func entranceFrame(room InteriorRoom) (regionFrame, bool) {
	r := room.Interior
	if r.Width <= 0 || r.Height <= 0 || len(room.Doors) == 0 {
		return regionFrame{}, false
	}
	inner := map[domain.Cell]bool{}
	for _, d := range room.InnerDoors {
		inner[d] = true
	}
	doors := append([]domain.Cell(nil), room.Doors...)
	sort.SliceStable(doors, func(i, j int) bool {
		if inner[doors[i]] != inner[doors[j]] {
			return !inner[doors[i]]
		}
		return cellLess(doors[i], doors[j])
	})
	side, ok := doorSide(r, doors[0])
	if !ok {
		return regionFrame{}, false
	}
	d := doors[0]
	f := regionFrame{Width: r.Width, Depth: r.Height}
	var u int32
	switch side {
	case domain.South:
		u = d.X - r.X
	case domain.North:
		u = r.X + r.Width - 1 - d.X
	case domain.West:
		f.Width, f.Depth = r.Height, r.Width
		u = r.Z + r.Height - 1 - d.Z
	default:
		f.Width, f.Depth = r.Height, r.Width
		u = d.Z - r.Z
	}
	f.Entrance = min(u, f.Width-1-u)
	return f, true
}

// InteriorRoomFromCensus reads a census room as a plan input under the
// given role: only a room whose cells fill their bounding rectangle has an
// interior, and its doors are the doorways beside that rectangle's sides.
func InteriorRoomFromCensus(room Room, role RoomRole, doorways []domain.Cell, shapes PieceShapes) (InteriorRoom, bool) {
	if len(room.Cells) == 0 {
		return InteriorRoom{}, false
	}
	rect := cellsRectangle(room.Cells)
	seen := map[domain.Cell]bool{}
	for _, c := range room.Cells {
		seen[c] = true
	}
	if int64(len(seen)) != int64(rect.Width)*int64(rect.Height) {
		return InteriorRoom{}, false
	}
	out := InteriorRoom{Role: role, Interior: rect, Shapes: shapes}
	for _, d := range doorways {
		if _, ok := doorSide(rect, d); ok {
			out.Doors = append(out.Doors, d)
		}
	}
	sort.Slice(out.Doors, func(i, j int) bool { return cellLess(out.Doors[i], out.Doors[j]) })
	return out, len(out.Doors) > 0
}

// moduleRoomRoles maps a layout module to the census role its
// furniture gives it.
var moduleRoomRoles = map[PlannedRole]RoomRole{
	PlannedBedroom:          RoomRoleBedroom,
	PlannedSuite:            RoomRoleSuite,
	PlannedShelter:          RoomRoleShelter,
	PlannedDining:           RoomRoleDiningRoom,
	PlannedRec:              RoomRoleRecRoom,
	PlannedLab:              RoomRoleLaboratory,
	PlannedTomb:             RoomRoleTomb,
	PlannedThrone:           RoomRoleThroneRoom,
	PlannedNursery:          RoomRoleNursery,
	PlannedPlayroom:         RoomRolePlayroom,
	PlannedClassroom:        RoomRoleClassroom,
	PlannedWorship:          RoomRoleWorshipRoom,
	PlannedDeathrestChamber: RoomRoleDeathrestChamber,
	PlannedContainmentCell:  RoomRoleContainmentCell,
	PlannedIsolationRoom:    RoomRoleIsolationRoom,
	PlannedHospital:         RoomRoleHospital,
	PlannedKitchen:          RoomRoleKitchen,
	PlannedWorkshop:         RoomRoleWorkshop,
	PlannedStorage:          RoomRoleStoreroom,
	PlannedArmory:           RoomRoleStoreroom,
	PlannedWardrobe:         RoomRoleStoreroom,
	PlannedPrison:           RoomRolePrisonCell,
	PlannedBarn:             RoomRoleBarn,
}

// InteriorRoomFromLayout reads a layout room as a plan input.
func InteriorRoomFromLayout(r PlannedRoom, shapes PieceShapes) (InteriorRoom, bool) {
	role, ok := moduleRoomRoles[r.Role]
	if !ok || r.Region.Bounds().Width <= 0 || r.Region.Bounds().Height <= 0 {
		return InteriorRoom{}, false
	}
	if _, ok := doorSide(r.Region.Bounds(), r.Door); !ok {
		return InteriorRoom{}, false
	}
	out := InteriorRoom{Role: role, Interior: r.Region.Bounds(), Doors: []domain.Cell{r.Door}, Shapes: shapes, Capabilities: CapabilitiesOf(r.Role), WorkChairs: r.WorkChairs}
	if !r.Region.IsRect() {
		out.Region = r.Region
	}
	return out, true
}

// doorSide is the direction from the room to a door in one of its walls
// (corners excluded).
func doorSide(r Rectangle, d domain.Cell) (domain.Rotation, bool) {
	inX, inZ := d.X >= r.X && d.X < r.X+r.Width, d.Z >= r.Z && d.Z < r.Z+r.Height
	switch {
	case inX && d.Z == r.Z-1:
		return domain.South, true
	case inX && d.Z == r.Z+r.Height:
		return domain.North, true
	case inZ && d.X == r.X-1:
		return domain.West, true
	case inZ && d.X == r.X+r.Width:
		return domain.East, true
	}
	return "", false
}

var rotationOrder = []domain.Rotation{domain.North, domain.East, domain.South, domain.West}

func rotationIndex(r domain.Rotation) (int, bool) {
	for i, o := range rotationOrder {
		if o == r {
			return i, true
		}
	}
	return 0, false
}

// rotateCW turns r clockwise by steps quarter turns.
func rotateCW(r domain.Rotation, steps int) domain.Rotation {
	i, _ := rotationIndex(r)
	return rotationOrder[((i+steps)%4+4)%4]
}

// rotatedSize is a definition's footprint dimensions at a rotation.
func rotatedSize(size domain.Cell, rot domain.Rotation) (int32, int32, bool) {
	if size.X < 1 || size.Z < 1 {
		return 0, 0, false
	}
	switch rot {
	case domain.North, domain.South:
		return size.X, size.Z, true
	case domain.East, domain.West:
		return size.Z, size.X, true
	}
	return 0, 0, false
}

// OccupiedRect is RimWorld's GenAdj.OccupiedRect: the footprint a building
// of the given North size occupies when anchored at a cell.
func OccupiedRect(anchor, size domain.Cell, rot domain.Rotation) Rectangle {
	w, h, ok := rotatedSize(size, rot)
	if !ok {
		return Rectangle{}
	}
	cx, cz := anchor.X, anchor.Z
	if !(size.X == 1 && size.Z == 1) {
		switch rot {
		case domain.East:
			if h%2 == 0 {
				cz--
			}
		case domain.South:
			if w%2 == 0 {
				cx--
			}
			if h%2 == 0 {
				cz--
			}
		case domain.West:
			if w%2 == 0 {
				cx--
			}
		}
	}
	return Rectangle{X: cx - (w-1)/2, Z: cz - (h-1)/2, Width: w, Height: h}
}

// AnchorForRect inverts OccupiedRect.
func AnchorForRect(r Rectangle, size domain.Cell, rot domain.Rotation) domain.Cell {
	probe := OccupiedRect(domain.Cell{}, size, rot)
	return domain.Cell{X: r.X - probe.X, Z: r.Z - probe.Z}
}

// rotateOffset turns a North offset to a rotation (RimWorld's
// IntVec3.RotatedBy).
func rotateOffset(o domain.Cell, rot domain.Rotation) domain.Cell {
	switch rot {
	case domain.East:
		return domain.Cell{X: o.Z, Z: -o.X}
	case domain.South:
		return domain.Cell{X: -o.X, Z: -o.Z}
	case domain.West:
		return domain.Cell{X: -o.Z, Z: o.X}
	}
	return o
}

// passThroughRoles are the roles a door into counts as an entrance, like
// a hallway's: rooms colonists cross rather than rooms they work or sleep
// in.
var passThroughRoles = map[RoomRole]bool{RoomRoleNone: true, RoomRoleRoom: true, RoomRoleDiningRoom: true, RoomRoleRecRoom: true, RoomRoleStoreroom: true, RoomRoleThroneRoom: true, RoomRoleWorshipRoom: true}

// InteriorRoomsFor lists the rectangular rooms hosting a facility as plan
// inputs. A generic room is planned as the facility's own role, which is
// the role its new furniture gives it; any other room keeps its role.
func InteriorRoomsFor(f FacilityRequirement, rooms RoomObservation, cells []SiteCell) []InteriorRoom {
	var doorways []domain.Cell
	edifice := map[domain.Cell]string{}
	for _, c := range cells {
		if positive(c.Doorway) {
			doorways = append(doorways, c.Cell)
		}
		if d := c.PlayerEdifice(); d != "" {
			edifice[c.Cell] = d
		}
	}
	// A door leads into another room when the cell past it lies in an
	// enclosed private or work room; a hallway or an unfurnished room reads
	// None or Room, a pass-through room (dining, rec, storeroom, a hall)
	// is hallway-like, and outdoors is in no enclosed room. A kitchen is
	// the exception: RimWorld reads its freezer as a Storeroom, so a door
	// into one is the kitchen's inner (freezer) door, never its entrance.
	purposed := map[domain.Cell]string{}
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		enclosed, _ := room.Enclosed.Value()
		if known && enclosed && (!passThroughRoles[role] || f.Role == RoomRoleKitchen && role == RoomRoleStoreroom) {
			for _, c := range room.Cells {
				purposed[c] = room.ID
			}
		}
	}
	var out []InteriorRoom
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if !known || !f.Hosts(role) {
			continue
		}
		if role == RoomRoleRoom || role == RoomRoleNone {
			role = f.Role
		}
		r, ok := InteriorRoomFromCensus(room, role, doorways, rooms.Shapes)
		if !ok {
			continue
		}
		for _, d := range r.Doors {
			side, _ := doorSide(r.Interior, d)
			step := rotateOffset(domain.Cell{X: 0, Z: 1}, side)
			if id, ok := purposed[domain.Cell{X: d.X + step.X, Z: d.Z + step.Z}]; ok && id != room.ID {
				r.InnerDoors = append(r.InnerDoors, d)
			}
		}
		standing := map[string]bool{}
		for _, c := range room.Cells {
			if d, ok := edifice[c]; ok && !standing[d] {
				standing[d] = true
				r.Standing = append(r.Standing, d)
			}
		}
		sort.Strings(r.Standing)
		r.Dining = rooms.Dining
		out = append(out, r)
	}
	return out
}

// InteriorSnapAnchors are the cells the fallback search tries before any
// other free cell of the room: the wall bands, the centre lines and
// the mirror images of occupied cells across both centre lines.
func InteriorSnapAnchors(room InteriorRoom, occupied []domain.Cell) []domain.Cell {
	r := room.Interior
	inside := func(c domain.Cell) bool {
		return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
	}
	snap := map[domain.Cell]bool{}
	for _, c := range rectCells(r) {
		u, v := c.X-r.X, c.Z-r.Z
		band := u == 0 || v == 0 || u == r.Width-1 || v == r.Height-1
		centre := 2*u+1 == r.Width || 2*u+2 == r.Width || 2*v+1 == r.Height || 2*v+2 == r.Height
		snap[c] = band || centre
	}
	for _, c := range occupied {
		if !inside(c) {
			continue
		}
		snap[domain.Cell{X: 2*r.X + r.Width - 1 - c.X, Z: c.Z}] = true
		snap[domain.Cell{X: c.X, Z: 2*r.Z + r.Height - 1 - c.Z}] = true
	}
	var out []domain.Cell
	for c, ok := range snap {
		if ok {
			out = append(out, c)
		}
	}
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

// PlannedRoleOf is the layout module a census room role is planned as, the
// inverse of moduleRoomRoles; Storeroom is the plain storage module. False
// means no planned room carries the role.
func PlannedRoleOf(role RoomRole) (PlannedRole, bool) {
	if role == RoomRoleStoreroom {
		return PlannedStorage, true
	}
	for m, r := range moduleRoomRoles {
		if r == role {
			return m, true
		}
	}
	return "", false
}
