package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LayoutPlan combines hallway spines, concrete rooms, whole-map zones and
// reserved infrastructure sites.

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

// RoomLink is a door in the wall two rooms share: A and B are their room ids
// (A below B) and Cell the wall cell, which lies on both rooms' walls.
type RoomLink struct {
	A, B int32
	Cell domain.Cell
}

// PlannedRoom is one planned room.
type PlannedRoom struct {
	// ID names the room for as long as the plan lives: handed out by
	// LayoutPlan.NextRoomID, never reused, kept through reshape, shrink and
	// replans that pin the room. Never zero in a plan a store holds.
	ID int32
	// Replaces is the ID of the room this one supersedes; 0 for none.
	Replaces int32 `json:",omitempty"`
	Role     PlannedRole
	// Region is the room's floor, walls excluded.
	Region Region
	// Door is the primary door: the one furniture frames and rock planning
	// use.
	Door    domain.Cell
	DoorRot domain.Rotation
	// Doors are further boundary doors of a room that touches a second
	// hallway. Nil for a single-door room, never empty.
	Doors []Door `json:",omitempty"`
	// Link, when set, is a second door in a wall shared with a neighbour:
	// the freezer's door into the kitchen. Plans saved before it
	// have none.
	Link *domain.Cell
	// Dug is a room mined out of natural rock.
	Dug bool
	// Outdoor is an open room: a fence and a gate ring (RingDefs, the pen) or none (Unfenced), no roof and no
	// floor owed (the animal pen).
	Outdoor bool `json:",omitempty"`
	// Facing is the direction an unfenced training range's dummies lie from its
	// stands (Unfenced); unset for every other room.
	Facing domain.Rotation `json:",omitempty"`
	// Markers is the marker count of a sparring ring (RingSlot orders the
	// slots); 0 for every other room.
	Markers int `json:",omitempty"`
	// Airlock is a cold-map shelter's vestibule and outer door beyond its
	// door (layout_airlock.go); nil for every other room.
	Airlock *Airlock `json:",omitempty"`
	// Porch is a cold-map shelter's roofed lean-to holding the first
	// batteries (layout_porch.go); nil for every other room.
	Porch *Porch `json:",omitempty"`
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
	// Ore marks the mining zone over ore-bearing rock.
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
	Area Region
	Pair int32 `json:",omitempty"`
	// Facing is the side a walled reservation's door faces (the incinerator).
	Facing domain.Rotation `json:",omitempty"`
	// Herd is the race of the herd whose unit a pen, barn or vet room belongs
	// to; empty for the misc unit and for plans saved before it.
	Herd string `json:",omitempty"`
}

// LayoutPlan is the v2 colony layout.
type LayoutPlan struct {
	Spine []SpineSegment
	// Entrances are the cells traffic enters the base from: hallway cells on a
	// spine plan, the outside cell beside each exterior door on a tiled plan
	// (layout_tile.go). Empty until the plan is grown.
	Entrances []domain.Cell `json:",omitempty"`
	// Links is the door graph between rooms: one door in the wall two rooms
	// share, by room id. Only a tiled plan holds it; a spine plan keeps its
	// freezer and butchery doors on PlannedRoom.Link.
	Links []RoomLink `json:",omitempty"`
	// Rooms are the rooms hung off the spine; a wing holds its own.
	Rooms        []PlannedRoom
	Wings        []Wing `json:",omitempty"`
	Zones        []LayoutZone
	Reservations []LayoutReservation
	// RetiredGround is the walled footprint of each room the plan retired
	// while its building stands: planned-ground clearance demolishes
	// it, then the entry is dropped.
	RetiredGround []Region `json:",omitempty"`
	// Cold latches the map's climate when the plan is derived: the
	// seasonal curve dips below ColdMapBelowC. The shelter is sized for it,
	// so a replan never resizes or re-sites the room.
	Cold bool `json:",omitempty"`
	// Hot latches the other end of the climate the same way: the
	// seasonal curve peaks above HotEnter, so the shelter holds a floor slot
	// for a passive cooler.
	Hot bool `json:",omitempty"`
	// YardCells latches the grazing the wall's yard is sized for when the
	// plan is derived (YardCells(herd)); 0 keeps the floor, perimeterGap.
	// A replan never resizes the yard.
	YardCells int32 `json:",omitempty"`
	// NextRoomID is the counter room ids come from: the next id handed out,
	// latched in the plan so an id is never reused after its room leaves.
	NextRoomID int32 `json:",omitempty"`
}

// WithRoomIDs hands every room without an id the next one from NextRoomID,
// in plan order (rooms, then each wing's rooms). The counter first moves
// past every id the plan already holds, so ids stay unique and a dropped
// room's id is not handed out again.
func (p LayoutPlan) WithRoomIDs() LayoutPlan {
	next := max(p.NextRoomID, 1)
	for _, r := range p.AllRooms() {
		next = max(next, r.ID+1, r.Replaces+1)
	}
	p.Rooms = slices.Clone(p.Rooms)
	for i := range p.Rooms {
		if p.Rooms[i].ID == 0 {
			p.Rooms[i].ID, next = next, next+1
		}
	}
	if len(p.Wings) > 0 {
		p.Wings = slices.Clone(p.Wings)
		for i := range p.Wings {
			p.Wings[i].Rooms = slices.Clone(p.Wings[i].Rooms)
			for j := range p.Wings[i].Rooms {
				if p.Wings[i].Rooms[j].ID == 0 {
					p.Wings[i].Rooms[j].ID, next = next, next+1
				}
			}
		}
	}
	p.NextRoomID = next
	return p
}

// Anchor is the interior centre of the
// first room for want that free accepts (every one when free is nil), in
// plan order (the planner lists rooms nearest the spine's start first). A
// full role falls back to reserve rooms; false means the plan holds no
// slot.
func (p LayoutPlan) Anchor(want PlannedRole, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []PlannedRole{want, PlannedReserve} {
		for _, r := range p.AllRooms() {
			if r.Role == role && (free == nil || free(r.Region.Bounds())) {
				return domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// Core is the cell the base clusters around: the first storeroom's
// interior centre, else the centre of the footprint, every hallway and room
// wall. Stockpiles and
// the cooking campfire anchor here rather than on the colonists' centroid,
// which on a fresh landing is the map centre. False means an empty plan.
func (p LayoutPlan) Core() (domain.Cell, bool) {
	for _, r := range p.AllRooms() {
		if r.Role == PlannedStorage {
			return domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}, true
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

// CoreBounds is the bounding box of the plan's hallways and room walls;
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
		on := p.besideExteriorDoor(c)
		for _, h := range halls {
			on = on || contains(h, c)
		}
		if !on {
			return false
		}
	}
	ids := map[int32]bool{}
	for _, r := range p.AllRooms() {
		if r.Role == "" || r.Region.Empty() {
			return false
		}
		if r.ID < 1 || ids[r.ID] || r.ID >= p.NextRoomID || r.Replaces == r.ID {
			return false
		}
		ids[r.ID] = true
		switch r.DoorRot {
		case domain.North, domain.East, domain.South, domain.West:
		default:
			if !r.Unfenced() { // an unfenced room has no door
				return false
			}
		}
	}
	rooms := map[int32]Rectangle{}
	for _, r := range p.AllRooms() {
		rooms[r.ID] = r.Region.Bounds()
	}
	seen := map[RoomLink]bool{}
	for _, l := range p.Links {
		a, okA := rooms[l.A]
		b, okB := rooms[l.B]
		if !okA || !okB || l.A >= l.B || seen[l] || !inWall(a, l.Cell) || !inWall(b, l.Cell) {
			return false
		}
		seen[l] = true
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
		if g.Empty() {
			return false
		}
	}
	for _, r := range p.Reservations {
		if r.Kind == "" || r.Area.Empty() {
			return false
		}
	}
	return true
}

// besideExteriorDoor reports c as the outside cell of a room's own door: it
// touches the room's Door or one of its Doors from outside the room.
func (p LayoutPlan) besideExteriorDoor(c domain.Cell) bool {
	for _, r := range p.AllRooms() {
		if r.Unfenced() || r.Region.Contains(c) {
			continue
		}
		doors := []domain.Cell{r.Door}
		for _, d := range r.Doors {
			doors = append(doors, d.Cell)
		}
		for _, d := range doors {
			if abs32(d.X-c.X)+abs32(d.Z-c.Z) == 1 {
				return true
			}
		}
	}
	return false
}

// LinkCells are the cells of the doors the door graph gives room id.
func (p LayoutPlan) LinkCells(id int32) []domain.Cell {
	var out []domain.Cell
	for _, l := range p.Links {
		if l.A == id || l.B == id {
			out = append(out, l.Cell)
		}
	}
	return out
}

// Extent is the bounding rectangle of what the plan builds: every room
// with its walls, every hallway and every reserved site. The
// whole-map zones are left out; false means the plan holds none of these.
func (p LayoutPlan) Extent() (Rectangle, bool) {
	var rects []Rectangle
	for _, r := range p.AllRooms() {
		rects = append(rects, Rectangle{X: r.Region.Bounds().X - 1, Z: r.Region.Bounds().Z - 1, Width: r.Region.Bounds().Width + 2, Height: r.Region.Bounds().Height + 2})
	}
	rects = append(rects, spineRects(p.Hallways())...)
	for _, r := range p.Reservations {
		rects = append(rects, r.Area.Bounds())
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
	return r.ID == o.ID && r.Replaces == o.Replaces && r.Role == o.Role && r.Region.Bounds() == o.Region.Bounds() && r.Door == o.Door && r.DoorRot == o.DoorRot && r.Dug == o.Dug && r.Outdoor == o.Outdoor && r.Facing == o.Facing && r.Markers == o.Markers && (r.Airlock == nil) == (o.Airlock == nil) && (r.Airlock == nil || *r.Airlock == *o.Airlock) && (r.Porch == nil) == (o.Porch == nil) && (r.Porch == nil || *r.Porch == *o.Porch)
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
			if r.Role != role || free != nil && !free(r.Region.Bounds()) {
				continue
			}
			c := domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}
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

// Equal reports whether both reservations are the same site.
func (r LayoutReservation) Equal(o LayoutReservation) bool {
	return r.Kind == o.Kind && r.Pair == o.Pair && r.Facing == o.Facing && r.Herd == o.Herd && r.Area.Equal(o.Area)
}
