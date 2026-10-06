package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Layout plan v2 (#771, A1) replaces the module grid with a spine of
// hallways, concrete rooms hung off it, whole-map zones and reserved
// infrastructure sites.

// SpineWidth is a spine hallway's width in cells.
const SpineWidth int32 = 3

// SpineSegment is one straight hallway run SpineWidth wide: From and To
// are the centre line's end cells (same X or same Z).
type SpineSegment struct {
	From, To domain.Cell
}

// Door is one boundary door of a room: its wall cell and the side it faces.
type Door struct {
	Cell domain.Cell
	Rot  domain.Rotation
}

// PlannedRoom is one planned room.
type PlannedRoom struct {
	Role PlannedRole
	// Interior is the room's floor, walls excluded.
	Interior Rectangle
	// Door is the primary door: the one furniture frames and rock planning
	// use.
	Door    domain.Cell
	DoorRot domain.Rotation
	// Doors are further boundary doors of a room that touches a second
	// hallway. Nil for a single-door room, never empty.
	Doors []Door `json:",omitempty"`
	// Link, when set, is a second door in a wall shared with a neighbour:
	// the freezer's door into the kitchen (#819). Plans saved before it
	// have none.
	Link *domain.Cell
	// Dug is a room mined out of natural rock.
	Dug bool
	// Outdoor is an open ring: a fence and a gate (RingDefs), no roof and no
	// floor owed (the animal pen, #2120).
	Outdoor bool `json:",omitempty"`
}

// ZoneKind is a whole-map zone's use.
type ZoneKind string

const (
	ZoneField   ZoneKind = "field"
	ZonePasture ZoneKind = "pasture"
	ZoneMining  ZoneKind = "mining"
	ZoneWood    ZoneKind = "wood"
	ZoneNoGo    ZoneKind = "no_go"
)

// RowRun is the cells X..X+Length-1 on row Z.
type RowRun struct {
	Z, X, Length int32
}

// LayoutZone is one zone as row runs over the whole map.
type LayoutZone struct {
	Kind ZoneKind
	Runs []RowRun
	// Ore marks the mining zone over ore-bearing rock (#837).
	Ore bool
}

// ReservationKind is an infrastructure site the plan holds.
type ReservationKind string

const (
	ReserveBatteryRoom ReservationKind = "battery_room"
	ReserveTurbine     ReservationKind = "turbine"
	ReserveTurbineLane ReservationKind = "turbine_lane"
	ReserveSolar       ReservationKind = "solar"
	ReserveGeothermal  ReservationKind = "geothermal"
	ReservePerimeter   ReservationKind = "perimeter_wall"
	ReserveGate        ReservationKind = "gate"
	ReserveKillbox     ReservationKind = "killbox"
	ReserveMortar      ReservationKind = "mortar"
	ReserveCoverClear  ReservationKind = "cover_clear"
)

// LayoutReservation is one reserved site; Pair groups a turbine pair with
// its lanes (0 for every other kind).
type LayoutReservation struct {
	Kind ReservationKind
	Area Rectangle
	Pair int32 `json:",omitempty"`
	// Facing is the side a walled reservation's door faces (the incinerator).
	Facing domain.Rotation `json:",omitempty"`
	// Herd is the race of the herd whose unit a pen, barn or vet room belongs
	// to (#2226); empty for the misc unit and for plans saved before it.
	Herd string `json:",omitempty"`
}

// LayoutPlan is the v2 colony layout.
type LayoutPlan struct {
	Spine []SpineSegment
	// Entrances are the hallway cells traffic enters the base from; empty
	// until the plan is grown (#1946).
	Entrances []domain.Cell `json:",omitempty"`
	// Rooms are the rooms hung off the spine; a wing holds its own (#1213).
	Rooms        []PlannedRoom
	Wings        []Wing `json:",omitempty"`
	Zones        []LayoutZone
	Reservations []LayoutReservation
	// RetiredGround is the walled footprint of each room the plan retired
	// while its building stands (#2075): planned-ground clearance demolishes
	// it, then the entry is dropped.
	RetiredGround []Rectangle `json:",omitempty"`
	// Cold latches the map's climate when the plan is derived (#2044): the
	// seasonal curve dips below ColdMapBelowC. The shelter is sized for it,
	// so a replan never resizes or re-sites the room.
	Cold bool `json:",omitempty"`
	// Hot latches the other end of the climate the same way (#2044): the
	// seasonal curve peaks above HotEnter, so the shelter holds a floor slot
	// for a passive cooler.
	Hot bool `json:",omitempty"`
}

// Anchor is the interior centre of the
// first room for want that free accepts (every one when free is nil), in
// plan order (the planner lists rooms nearest the spine's start first). A
// full role falls back to reserve rooms; false means the plan holds no
// slot.
func (p LayoutPlan) Anchor(want PlannedRole, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []PlannedRole{want, PlannedReserve} {
		for _, r := range p.AllRooms() {
			if r.Role == role && (free == nil || free(r.Interior)) {
				return domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// Core is the cell the base clusters around: the first storeroom's
// interior centre, else the centre of the footprint, every hallway and room
// wall (#1534, #1947). Stockpiles and
// the cooking campfire anchor here rather than on the colonists' centroid,
// which on a fresh landing is the map centre. False means an empty plan.
func (p LayoutPlan) Core() (domain.Cell, bool) {
	for _, r := range p.AllRooms() {
		if r.Role == PlannedStorage {
			return domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}, true
		}
	}
	return p.Center()
}

// Center is the middle of the plan's core: the bounding box of its hallways
// and room walls. False for a plan with neither.
func (p LayoutPlan) Center() (domain.Cell, bool) {
	fp, ok := p.CoreBounds()
	if !ok {
		return domain.Cell{}, false
	}
	return domain.Cell{X: fp.X + fp.Width/2, Z: fp.Z + fp.Height/2}, true
}

// CoreBounds is the bounding box of the plan's hallways and room walls (#2040);
// false for a plan with neither.
func (p LayoutPlan) CoreBounds() (Rectangle, bool) {
	var fp Rectangle
	for _, r := range spineRects(p.Hallways()) {
		fp = unionRect(fp, r)
	}
	for _, r := range p.AllRooms() {
		fp = unionRect(fp, roomWalls(r))
	}
	return fp, fp.Width != 0
}

// Valid reports a plan a store may persist: at least one room, straight
// spine segments, non-empty rectangles and runs, and a real door rotation.
func (p LayoutPlan) Valid() bool {
	if len(p.AllRooms()) == 0 {
		return false
	}
	for _, w := range p.Wings {
		if w.Purpose == "" || len(w.Rooms) == 0 {
			return false
		}
	}
	for _, s := range p.Hallways() {
		if s.From.X != s.To.X && s.From.Z != s.To.Z {
			return false
		}
	}
	halls := spineRects(p.Hallways())
	for _, c := range p.Entrances {
		on := false
		for _, h := range halls {
			on = on || contains(h, c)
		}
		if !on {
			return false
		}
	}
	for _, r := range p.AllRooms() {
		if r.Role == "" || r.Interior.Width < 1 || r.Interior.Height < 1 {
			return false
		}
		switch r.DoorRot {
		case domain.North, domain.East, domain.South, domain.West:
		default:
			return false
		}
	}
	for _, z := range p.Zones {
		if z.Kind == "" {
			return false
		}
		for _, run := range z.Runs {
			if run.Length < 1 {
				return false
			}
		}
	}
	for _, g := range p.RetiredGround {
		if g.Width < 1 || g.Height < 1 {
			return false
		}
	}
	for _, r := range p.Reservations {
		if r.Kind == "" || r.Area.Width < 1 || r.Area.Height < 1 {
			return false
		}
	}
	return true
}

// Extent is the bounding rectangle of what the plan builds: every room
// with its walls, every hallway and every reserved site (#1282). The
// whole-map zones are left out; false means the plan holds none of these.
func (p LayoutPlan) Extent() (Rectangle, bool) {
	var rects []Rectangle
	for _, r := range p.AllRooms() {
		rects = append(rects, Rectangle{X: r.Interior.X - 1, Z: r.Interior.Z - 1, Width: r.Interior.Width + 2, Height: r.Interior.Height + 2})
	}
	rects = append(rects, spineRects(p.Hallways())...)
	for _, r := range p.Reservations {
		rects = append(rects, r.Area)
	}
	return RectUnion(rects...)
}

// RectUnion is the bounding rectangle of the non-empty rects; false when
// there are none.
func RectUnion(rects ...Rectangle) (Rectangle, bool) {
	var out Rectangle
	found := false
	for _, r := range rects {
		if r.Width < 1 || r.Height < 1 {
			continue
		}
		if !found {
			out, found = r, true
			continue
		}
		minX, minZ := min(out.X, r.X), min(out.Z, r.Z)
		maxX, maxZ := max(out.X+out.Width, r.X+r.Width), max(out.Z+out.Height, r.Z+r.Height)
		out = Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ}
	}
	return out, found
}

// Same reports whether two rooms are identical, the comparison PlannedRoom's
// slice of doors rules out for ==.
func (r PlannedRoom) Same(o PlannedRoom) bool {
	if (r.Link == nil) != (o.Link == nil) || r.Link != nil && *r.Link != *o.Link || len(r.Doors) != len(o.Doors) {
		return false
	}
	for i := range r.Doors {
		if r.Doors[i] != o.Doors[i] {
			return false
		}
	}
	return r.Role == o.Role && r.Interior == o.Interior && r.Door == o.Door && r.DoorRot == o.DoorRot && r.Dug == o.Dug && r.Outdoor == o.Outdoor
}

// NearestAnchor is the interior centre of the free planned room of role want
// nearest to point to (squared distance, plan order on ties); free filters
// out occupied rooms (every room when nil). With no free room of that role it
// falls back to the nearest free reserve room; false means the plan holds no
// slot.
func (p LayoutPlan) NearestAnchor(want PlannedRole, to domain.Cell, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []PlannedRole{want, PlannedReserve} {
		best, bestD, found := domain.Cell{}, int64(0), false
		for _, r := range p.AllRooms() {
			if r.Role != role || free != nil && !free(r.Interior) {
				continue
			}
			c := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
			dx, dz := int64(c.X-to.X), int64(c.Z-to.Z)
			if d := dx*dx + dz*dz; !found || d < bestD {
				best, bestD, found = c, d, true
			}
		}
		if found {
			return best, true
		}
	}
	return domain.Cell{}, false
}

// FieldAnchor is the middle of the first field-zone run whose middle cell
// free accepts (every run when nil); false means no such run.
func (p LayoutPlan) FieldAnchor(free func(Rectangle) bool) (domain.Cell, bool) {
	for _, z := range p.Zones {
		if z.Kind != ZoneField {
			continue
		}
		for _, run := range z.Runs {
			c := domain.Cell{X: run.X + run.Length/2, Z: run.Z}
			if free == nil || free(Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}) {
				return c, true
			}
		}
	}
	return domain.Cell{}, false
}
