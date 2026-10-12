package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LayoutPlan combines concrete rooms, whole-map zones and reserved
// infrastructure sites.

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
	// Crossing is what walking through the room costs, read from the station
	// group that became it (RoomCrossing); nil for a room that
	// is not a station group, which keeps its role's noThroughfare rule.
	Crossing *RoomCrossing `json:",omitempty"`
	// Through marks a room the plan lets pawns walk through to reach other
	// rooms although crossing it costs something (MayBeThrough): CheckRoutes
	// accepts a path across it and trip scoring prices the extra steps.
	Through bool `json:",omitempty"`
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
	// Parent is the ID of the room this one is attached to: an airlock or
	// porch child (layout_airlock.go, layout_porch.go); 0 for every room that
	// stands on its own. A child owns no ring (Unfenced): its parent's ring
	// takes its walls and doors. See ChildrenOf.
	Parent int32 `json:",omitempty"`
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
	ReserveGeothermal  ReservationKind = "geothermal"
)

// LayoutReservation is one reserved site.
type LayoutReservation struct {
	Kind ReservationKind
	Area Region
	// Facing is the side a walled reservation's door faces (the incinerator).
	Facing domain.Rotation `json:",omitempty"`
	// Herd is the race of the herd whose unit a pen, barn or vet room belongs
	// to; empty for the misc unit and for plans saved before it.
	Herd string `json:",omitempty"`
}

// LayoutPlan is the v2 colony layout.
type LayoutPlan struct {
	// Entrances are the cells traffic enters the base from: the outside cell beside each exterior door on a tiled plan
	// (layout_tile.go). Empty until the plan is grown.
	Entrances []domain.Cell `json:",omitempty"`
	// Links is the door graph between rooms: one door in the wall two rooms
	// share, by room id.
	Links []RoomLink `json:",omitempty"`
	// Conduits are the power conduit paths the planner lays, one per node joined
	// to the net (layout_conduit.go).
	Conduits []ConduitLink `json:",omitempty"`
	// Owed is the room each station group owes the plan, one role per group,
	// set only by a tiled plan (layout_tile.go). The hard tier's Missing term
	// counts a room per entry, so two groups of one role need two rooms; empty
	// keeps defaultOwedRooms, once each.
	Owed []PlannedRole `json:",omitempty"`
	// Trips are the pawn trips between this plan's rooms with their daily
	// frequency (the station graph's flows, layout_trips.go). When set, plan
	// scoring walks them instead of the role-pair trip table.
	Trips []RoomTrip `json:",omitempty"`
	// OutsideTrips are the rooms' trips to the outside, walked to the nearest
	// entrance (exteriorDoorMovements); scoring does not read them.
	OutsideTrips []OutsideTrip `json:",omitempty"`
	Rooms        []PlannedRoom
	Zones        []LayoutZone
	Reservations []LayoutReservation
	// Sites are the defense nodes PlanPerimeter traces: ring, gates, killbox,
	// approach, fence, pumps, mortar and cover-clear band (layout_perimeter_sites.go).
	Sites []PlannedSite `json:",omitempty"`
	// RetiredGround is the walled footprint of each room the plan retired
	// while its building stands: planned-ground clearance demolishes
	// it, then the entry is dropped.
	RetiredGround []Region `json:",omitempty"`
	// Roofed is the cells the plan roofs on a map whose outdoor temperature is
	// not good for pawns: every room except the outdoor roles
	// (layout_roof.go). Only a tiled plan holds it; the climate envelope
	// round roofs it (MaintainClimateEnvelope).
	Roofed Region `json:",omitzero"`
	// Lamps are the lamps the plan places in each lit room by room id
	// (layout_light.go placeLamps): standing or wall, priced against the dark
	// they light. The lighting round builds them where a bench reads dark; a room
	// the plan found darkness cheaper for has none. Only a tiled plan holds them.
	Lamps map[int32][]PlannedLamp `json:",omitempty"`
	// Cold latches the map's climate when the plan is derived: the
	// seasonal curve dips below ColdMapBelowC. The shelter is sized for it,
	// so a replan never resizes or re-sites the room.
	Cold bool `json:",omitempty"`
	// Hot latches the other end of the climate the same way: the
	// seasonal curve peaks above HotEnter, so the shelter holds a floor slot
	// for a passive cooler.
	Hot bool `json:",omitempty"`
	// NextRoomID is the counter room ids come from: the next id handed out,
	// latched in the plan so an id is never reused after its room leaves.
	NextRoomID int32 `json:",omitempty"`
}

// WithRoomIDs hands every room without an id the next one from NextRoomID,
// in plan order. The counter first moves
// past every id the plan already holds, so ids stay unique and a dropped
// room's id is not handed out again.
func (p LayoutPlan) WithRoomIDs() LayoutPlan {
	next := max(p.NextRoomID, 1)
	for _, r := range p.Rooms {
		next = max(next, r.ID+1, r.Replaces+1)
	}
	p.Rooms = slices.Clone(p.Rooms)
	next = idRooms(p.Rooms, next)
	p.NextRoomID = next
	return p
}

// idRooms hands each room without an id the next from next, in place, and
// returns the counter. An attached child (airlock, porch) sited with no
// Parent is the parent's: the room listed just before it.
func idRooms(rooms []PlannedRoom, next int32) int32 {
	var last int32
	for i := range rooms {
		if rooms[i].ID == 0 {
			rooms[i].ID, next = next, next+1
		}
		if !rooms[i].attached() {
			last = rooms[i].ID
		} else if rooms[i].Parent == 0 {
			rooms[i].Parent = last
		}
	}
	return next
}

// attachedGround is the ground an attached child occupies beyond its parent's
// ring: the airlock's vestibule, walls and outer door, or the porch strip.
func (r PlannedRoom) attachedGround() Rectangle {
	if r.Role == PlannedAirlock {
		return r.AirlockGround()
	}
	return r.PorchGround()
}

// attached reports a child room that hangs off a parent: an airlock or porch.
func (r PlannedRoom) attached() bool { return r.Role == PlannedAirlock || r.Role == PlannedPorch }

// Anchor is the interior centre of the
// first room for want that free accepts (every one when free is nil), in
// plan order. A full role falls back to reserve rooms; false means the plan holds no
// slot.
func (p LayoutPlan) Anchor(want PlannedRole, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []PlannedRole{want, PlannedReserve} {
		for _, r := range p.Rooms {
			if r.Role == role && (free == nil || free(r.Region.Bounds())) {
				return domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// Core is the cell the base clusters around: the first storeroom's
// interior centre, else the centre of the footprint of every room
// wall. Stockpiles and
// the cooking campfire anchor here rather than on the colonists' centroid,
// which on a fresh landing is the map centre. False means an empty plan.
func (p LayoutPlan) Core() (domain.Cell, bool) {
	for _, r := range p.Rooms {
		if r.Role == PlannedStorage {
			return domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}, true
		}
	}
	return p.Center()
}

// Center is the middle of the plan's core: the bounding box of its room
// walls. False for a plan with no rooms.
func (p LayoutPlan) Center() (domain.Cell, bool) {
	fp, ok := p.CoreBounds()
	if !ok {
		return domain.Cell{}, false
	}
	return domain.Cell{X: fp.X + fp.Width/2, Z: fp.Z + fp.Height/2}, true
}

// CoreBounds is the bounding box of the plan's room walls; false for a plan
// with no rooms.
func (p LayoutPlan) CoreBounds() (Rectangle, bool) {
	var fp Rectangle
	for _, r := range p.Rooms {
		fp = unionRect(fp, roomWalls(r))
	}
	return fp, fp.Width != 0
}

// Valid reports a plan a store may persist: at least one room, non-empty
// rectangles and runs, and a real door rotation.
func (p LayoutPlan) Valid() bool {
	if len(p.Rooms) == 0 {
		return false
	}
	for _, c := range p.Entrances {
		if !p.besideExteriorDoor(c) {
			return false
		}
	}
	ids := map[int32]bool{}
	for _, r := range p.Rooms {
		if r.Role == "" || r.Region.Empty() {
			return false
		}
		if r.ID < 1 || ids[r.ID] || r.ID >= p.NextRoomID || r.Replaces == r.ID || r.Parent == r.ID {
			return false
		}
		if r.attached() != (r.Parent != 0) {
			return false
		}
		ids[r.ID] = true
		if r.Through && !r.MayBeThrough() {
			return false
		}
		switch r.DoorRot {
		case domain.North, domain.East, domain.South, domain.West:
		default:
			if !r.Unfenced() { // an unfenced room has no door
				return false
			}
		}
	}
	rooms := map[int32]Rectangle{}
	for _, r := range p.Rooms {
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
	if p.roofedOutdoors() {
		return false
	}
	if !p.validConduits(rooms) {
		return false
	}
	for _, t := range p.Trips {
		if _, ok := rooms[t.A]; !ok || t.A >= t.B || t.Weight <= 0 {
			return false
		}
		if _, ok := rooms[t.B]; !ok {
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
		if g.Empty() {
			return false
		}
	}
	for _, r := range p.Reservations {
		if r.Kind == "" || r.Area.Empty() {
			return false
		}
	}
	siteIDs := map[string]bool{}
	for _, s := range p.Sites {
		if s.Kind == "" || s.Node == "" || siteIDs[s.Node] || s.Footprint.Empty() || s.Kind == NodeRing && s.Ring == nil {
			return false
		}
		siteIDs[s.Node] = true
		if s.Kind == NodeVent && !p.validVent(s) {
			return false
		}
	}
	for _, s := range p.Sites {
		for _, id := range s.ReplacesSites {
			if !siteIDs[id] || id == s.Node {
				return false
			}
		}
	}
	return true
}

// validVent reports a vent that stands in a room's wall: one cell of a fenced
// room's wall ring, a corner excluded, that no door, link or entrance holds.
func (p LayoutPlan) validVent(s PlannedSite) bool {
	cells := s.Footprint.Cells()
	if len(cells) != 1 {
		return false
	}
	c := cells[0]
	if slices.Contains(p.Entrances, c) || slices.ContainsFunc(p.Links, func(l RoomLink) bool { return l.Cell == c }) {
		return false
	}
	inRing := false
	for _, r := range p.Rooms {
		if r.Unfenced() {
			continue
		}
		if slices.Contains(p.DoorsOf(r), c) {
			return false
		}
		inRing = inRing || inWall(r.Region.Bounds(), c)
	}
	return inRing
}

// besideExteriorDoor reports c as the outside cell of a room's own door: it
// touches the room's Door from outside the room.
func (p LayoutPlan) besideExteriorDoor(c domain.Cell) bool {
	for _, r := range p.Rooms {
		if r.Unfenced() || r.Region.Contains(c) {
			continue
		}
		if abs32(r.Door.X-c.X)+abs32(r.Door.Z-c.Z) == 1 {
			return true
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

// LinksOf are the cells of the link doors room r holds in walls it shares with
// a neighbour: the door graph's links (LinkCells). Herd rooms (barn, pen) are
// reservations without room ids and own no link, so they give none.
func (p LayoutPlan) LinksOf(r PlannedRoom) []domain.Cell {
	if r.ID == 0 {
		return nil
	}
	return p.LinkCells(r.ID)
}

// DoorsOf are the cells of every door of room r: Door, then its links
// (LinksOf), each cell once.
func (p LayoutPlan) DoorsOf(r PlannedRoom) []domain.Cell {
	out := []domain.Cell{r.Door}
	for _, c := range p.LinksOf(r) {
		if !slices.Contains(out, c) {
			out = append(out, c)
		}
	}
	return out
}

// linkInto is the cell of r's link door that opens into a room of role, else
// its first link; false when r holds none. The freezer's door into the kitchen
// and the butchery's into the freezer pick their link this way, where a tiled
// room may hold several.
func (p LayoutPlan) linkInto(r PlannedRoom, role PlannedRole) (domain.Cell, bool) {
	links := p.LinksOf(r)
	for _, c := range links {
		for _, o := range p.Rooms {
			if o.ID != r.ID && o.Role == role && inWall(o.Region.Bounds(), c) {
				return c, true
			}
		}
	}
	if len(links) == 0 {
		return domain.Cell{}, false
	}
	return links[0], true
}

// Extent is the bounding rectangle of what the plan builds: every room
// with its walls and every reserved site. The
// whole-map zones are left out; false means the plan holds none of these.
func (p LayoutPlan) Extent() (Rectangle, bool) {
	var rects []Rectangle
	for _, r := range p.Rooms {
		rects = append(rects, Rectangle{X: r.Region.Bounds().X - 1, Z: r.Region.Bounds().Z - 1, Width: r.Region.Bounds().Width + 2, Height: r.Region.Bounds().Height + 2})
	}
	for _, r := range p.Reservations {
		rects = append(rects, r.Area.Bounds())
	}
	for _, s := range p.Sites {
		rects = append(rects, s.Footprint.Bounds())
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
// Region and Crossing rule out for ==.
func (r PlannedRoom) Same(o PlannedRoom) bool {
	return r.ID == o.ID && r.Replaces == o.Replaces && r.Role == o.Role && r.Through == o.Through && (r.Crossing == nil) == (o.Crossing == nil) && (r.Crossing == nil || *r.Crossing == *o.Crossing) && r.Region.Bounds() == o.Region.Bounds() && r.Door == o.Door && r.DoorRot == o.DoorRot && r.Dug == o.Dug && r.Outdoor == o.Outdoor && r.Facing == o.Facing && r.Markers == o.Markers && r.Parent == o.Parent
}

// NearestAnchor is the interior centre of the free planned room of role want
// nearest to point to (squared distance, plan order on ties); free filters
// out occupied rooms (every room when nil). With no free room of that role it
// falls back to the nearest free reserve room; false means the plan holds no
// slot.
func (p LayoutPlan) NearestAnchor(want PlannedRole, to domain.Cell, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []PlannedRole{want, PlannedReserve} {
		best, bestD, found := domain.Cell{}, int64(0), false
		for _, r := range p.Rooms {
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
	return r.Kind == o.Kind && r.Facing == o.Facing && r.Herd == o.Herd && r.Area.Equal(o.Area)
}
