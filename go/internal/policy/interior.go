package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Interior plans (#798, #800) lay furniture out inside one rectangular room.
// A plan is derived, never persisted: PlanInterior is a pure function of the
// room's role, interior rectangle and door cells, so two rooms of the same
// role and size get the same layout turned to face their doors.
//
// Templates are written in a canonical frame: the room is Width cells along
// its entrance wall and Depth cells away from it, u runs along the entrance
// wall, v away from it, the entrance door stands at (Entrance, -1) and the
// entrance sits on the left half of the wall (a room whose door is right of
// centre is mirrored). Canonical North is "away from the entrance". A
// template for a role is one file that calls RegisterInteriorTemplate from
// init; interior_helpers.go holds the shared geometry.

// InteriorRoom is the input to a plan: the role whose template applies, the
// floor (walls excluded) and the doors in the walls around it.
type InteriorRoom struct {
	Role     RoomRole
	Interior Rectangle
	Doors    []domain.Cell
	// InnerDoors are the doors among Doors that open onto another room;
	// the frame faces a door onto a hallway or outdoors when it has one.
	InnerDoors []domain.Cell
	// Standing are the definitions of the player buildings standing on the
	// floor, sorted; a bench row's pitch widens to the widest standing
	// member of its family.
	Standing []string
}

// InteriorFrame is the canonical view of a room a template plans in.
type InteriorFrame struct {
	Width, Depth int32
	// Entrance is the u of the door the frame faces; its inside cell is
	// (Entrance, 0).
	Entrance int32
	// Standing are the shapes of the room's standing definitions.
	Standing []InteriorPieceDef
	// Doors are every door of the room in canonical cells, the entrance
	// included; each lies one cell outside the frame.
	Doors []domain.Cell
}

// InteriorPiece is one planned building. In a template's output Rect is
// canonical; in InteriorPlan.Pieces it is the world footprint.
type InteriorPiece struct {
	// Slot names the piece within its template ("bed", "bench.2").
	Slot string
	Def  string
	// Size is the definition's native size at rotation North (x, z).
	Size domain.Cell
	Rot  domain.Rotation
	Rect Rectangle
	// InteractionOffset is the definition's interaction cell offset at
	// rotation North, from the anchor; nil for pieces without one.
	InteractionOffset *domain.Cell
	// Row groups the pieces of one wall-band row; Pair names a mirrored
	// pair; Centred marks a piece on the room's centre line. The
	// regularity checker holds the plan to them.
	Row, Pair string
	Centred   bool
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

// InteriorTemplate plans one role's room for the piece being placed (#820):
// a template whose family holds the piece plans its slots for that
// definition's footprint, and any other piece gets the template's default
// layout. Plan returns false when the frame does not fit the template.
type InteriorTemplate struct {
	Name string
	Plan func(InteriorFrame, InteriorPieceDef) ([]InteriorPiece, bool)
}

var interiorTemplates = map[RoomRole]InteriorTemplate{}

// RegisterInteriorTemplate installs the template for a role; call it from
// init in the template's own file. One template per role.
func RegisterInteriorTemplate(role RoomRole, t InteriorTemplate) {
	if role == "" || t.Name == "" || t.Plan == nil {
		panic("invalid interior template")
	}
	if _, dup := interiorTemplates[role]; dup {
		panic(fmt.Sprintf("interior template for %s registered twice", role))
	}
	interiorTemplates[role] = t
}

// InteriorTemplateFor returns the registered template for a role.
func InteriorTemplateFor(role RoomRole) (InteriorTemplate, bool) {
	t, ok := interiorTemplates[role]
	return t, ok
}

// InteriorPlan is a room's derived layout.
type InteriorPlan struct {
	Template  string
	Room      InteriorRoom
	Frame     InteriorFrame
	Canonical []InteriorPiece
	// Pieces are Canonical turned into world footprints, in the same order.
	Pieces []InteriorPiece
}

// PlanInterior derives the room's plan from its role's template for the
// piece being placed (InteriorPieceDefFor). It is false when no template is
// registered, the room has no door, the template does not fit, or the
// template's output is malformed.
func PlanInterior(room InteriorRoom, piece InteriorPieceDef) (InteriorPlan, bool) {
	t, ok := InteriorTemplateFor(room.Role)
	if !ok {
		return InteriorPlan{}, false
	}
	x, ok := newInteriorTransform(room)
	if !ok {
		return InteriorPlan{}, false
	}
	frame := x.frame()
	for _, d := range room.Standing {
		frame.Standing = append(frame.Standing, InteriorPieceDefFor(d))
	}
	pieces, ok := t.Plan(frame, piece)
	if !ok || ValidateInteriorPieces(frame, pieces) != nil {
		return InteriorPlan{}, false
	}
	plan := InteriorPlan{Template: t.Name, Room: room, Frame: frame, Canonical: pieces}
	for _, p := range pieces {
		plan.Pieces = append(plan.Pieces, x.piece(p))
	}
	if InteriorPlanWalkable(plan) != nil {
		return InteriorPlan{}, false
	}
	return plan, true
}

// ValidateInteriorPieces checks a template's canonical output: named unique
// slots, a definition, a footprint matching the rotated size, inside the
// frame and overlapping nothing.
func ValidateInteriorPieces(f InteriorFrame, pieces []InteriorPiece) error {
	slots := map[string]bool{}
	used := map[domain.Cell]string{}
	for _, p := range pieces {
		if p.Slot == "" || slots[p.Slot] || p.Def == "" {
			return fmt.Errorf("interior piece %q: missing or duplicate slot or definition", p.Slot)
		}
		slots[p.Slot] = true
		w, h, ok := rotatedSize(p.Size, p.Rot)
		if !ok || p.Rect.Width != w || p.Rect.Height != h {
			return fmt.Errorf("interior piece %s: footprint %+v does not match size %+v at %s", p.Slot, p.Rect, p.Size, p.Rot)
		}
		if !f.Contains(p.Rect) {
			return fmt.Errorf("interior piece %s: footprint %+v outside the %dx%d frame", p.Slot, p.Rect, f.Width, f.Depth)
		}
		for _, c := range rectCells(p.Rect) {
			if other, taken := used[c]; taken {
				return fmt.Errorf("interior pieces %s and %s overlap at %v", other, p.Slot, c)
			}
			used[c] = p.Slot
		}
	}
	return nil
}

// InteriorRoomFromCensus reads a census room as a plan input under the
// given role: only a room whose cells fill their bounding rectangle has an
// interior, and its doors are the doorways beside that rectangle's sides.
func InteriorRoomFromCensus(room Room, role RoomRole, doorways []domain.Cell) (InteriorRoom, bool) {
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
	out := InteriorRoom{Role: role, Interior: rect}
	for _, d := range doorways {
		if _, ok := doorSide(rect, d); ok {
			out.Doors = append(out.Doors, d)
		}
	}
	sort.Slice(out.Doors, func(i, j int) bool { return cellLess(out.Doors[i], out.Doors[j]) })
	return out, len(out.Doors) > 0
}

// moduleRoomRoles maps a v2 layout module to the census role its
// furniture gives it.
var moduleRoomRoles = map[ModuleRole]RoomRole{
	ModuleBedroom:  RoomRoleBedroom,
	ModuleSuite:    RoomRoleSuite,
	ModuleBarracks: RoomRoleBarracks,
	ModuleDining:   RoomRoleDiningRoom,
	ModuleRec:      RoomRoleRecRoom,
	ModuleLab:      RoomRoleLaboratory,
	ModuleTomb:     RoomRoleTomb,
	ModuleHospital: RoomRoleHospital,
	ModuleKitchen:  RoomRoleKitchen,
	ModuleWorkshop: RoomRoleWorkshop,
	ModuleStorage:  RoomRoleStoreroom,
	ModulePrison:   RoomRolePrisonCell,
}

// InteriorRoomFromLayout reads a v2 layout room as a plan input.
func InteriorRoomFromLayout(r LayoutRoom) (InteriorRoom, bool) {
	role, ok := moduleRoomRoles[r.Role]
	if !ok || r.Interior.Width <= 0 || r.Interior.Height <= 0 {
		return InteriorRoom{}, false
	}
	if _, ok := doorSide(r.Interior, r.Door); !ok {
		return InteriorRoom{}, false
	}
	return InteriorRoom{Role: role, Interior: r.Interior, Doors: []domain.Cell{r.Door}}, true
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

// interiorTransform maps a room's canonical frame to the world.
type interiorTransform struct {
	room   InteriorRoom
	away   domain.Rotation // world direction of canonical North
	mirror bool
	width  int32
	depth  int32
}

// newInteriorTransform faces the frame to the room's entrance and mirrors
// it so the entrance sits on the wall's left half. The entrance is the
// first door in cell order that opens onto a hallway or outdoors (#820),
// or the first door when every door leads into another room; the frame
// lists it first, the other doors after it in cell order.
func newInteriorTransform(room InteriorRoom) (interiorTransform, bool) {
	r := room.Interior
	if r.Width <= 0 || r.Height <= 0 || len(room.Doors) == 0 {
		return interiorTransform{}, false
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
		return interiorTransform{}, false
	}
	x := interiorTransform{room: room, away: rotateCW(side, 2), width: r.Width, depth: r.Height}
	if side == domain.East || side == domain.West {
		x.width, x.depth = r.Height, r.Width
	}
	x.room.Doors = doors
	if u := x.toCanonical(doors[0]).X; u > x.width-1-u {
		x.mirror = true
	}
	return x, true
}

func (x interiorTransform) toWorld(c domain.Cell) domain.Cell {
	u, v, r := c.X, c.Z, x.room.Interior
	if x.mirror {
		u = x.width - 1 - u
	}
	switch x.away {
	case domain.South:
		return domain.Cell{X: r.X + r.Width - 1 - u, Z: r.Z + r.Height - 1 - v}
	case domain.East:
		return domain.Cell{X: r.X + v, Z: r.Z + r.Height - 1 - u}
	case domain.West:
		return domain.Cell{X: r.X + r.Width - 1 - v, Z: r.Z + u}
	}
	return domain.Cell{X: r.X + u, Z: r.Z + v}
}

func (x interiorTransform) toCanonical(c domain.Cell) domain.Cell {
	r := x.room.Interior
	var u, v int32
	switch x.away {
	case domain.South:
		u, v = r.X+r.Width-1-c.X, r.Z+r.Height-1-c.Z
	case domain.East:
		u, v = r.Z+r.Height-1-c.Z, c.X-r.X
	case domain.West:
		u, v = c.Z-r.Z, r.X+r.Width-1-c.X
	default:
		u, v = c.X-r.X, c.Z-r.Z
	}
	if x.mirror {
		u = x.width - 1 - u
	}
	return domain.Cell{X: u, Z: v}
}

func (x interiorTransform) frame() InteriorFrame {
	f := InteriorFrame{Width: x.width, Depth: x.depth}
	for _, d := range x.room.Doors {
		f.Doors = append(f.Doors, x.toCanonical(d))
	}
	f.Entrance = f.Doors[0].X
	return f
}

func (x interiorTransform) rotation(r domain.Rotation) domain.Rotation {
	if x.mirror && (r == domain.East || r == domain.West) {
		r = rotateCW(r, 2)
	}
	steps, _ := rotationIndex(x.away)
	return rotateCW(r, steps)
}

func (x interiorTransform) rect(r Rectangle) Rectangle {
	a := x.toWorld(domain.Cell{X: r.X, Z: r.Z})
	b := x.toWorld(domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1})
	return Rectangle{X: min(a.X, b.X), Z: min(a.Z, b.Z), Width: max(a.X, b.X) - min(a.X, b.X) + 1, Height: max(a.Z, b.Z) - min(a.Z, b.Z) + 1}
}

func (x interiorTransform) piece(p InteriorPiece) InteriorPiece {
	p.Rect, p.Rot = x.rect(p.Rect), x.rotation(p.Rot)
	return p
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
		if d, known := c.PlayerEdifice.Value(); known && d != "" {
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
		r, ok := InteriorRoomFromCensus(room, role, doorways)
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
		out = append(out, r)
	}
	return out
}

// InteriorSnapAnchors are the cells the fallback search tries before any
// other free cell of the room (#798): the wall bands, the centre lines and
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
