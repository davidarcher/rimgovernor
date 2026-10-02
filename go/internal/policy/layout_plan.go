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

// LayoutRoom is one planned room.
type LayoutRoom struct {
	Role ModuleRole
	// Interior is the room's floor, walls excluded.
	Interior Rectangle
	Door     domain.Cell
	DoorRot  domain.Rotation
	// Link, when set, is a second door in a wall shared with a neighbour:
	// the freezer's door into the kitchen (#819). Plans saved before it
	// have none.
	Link *domain.Cell
	// Dug is a room mined out of natural rock.
	Dug bool
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
}

// LayoutPlan is the v2 colony layout.
type LayoutPlan struct {
	Spine []SpineSegment
	// Rooms are the rooms hung off the spine; a wing holds its own (#1213).
	Rooms        []LayoutRoom
	Wings        []Wing `json:",omitempty"`
	Zones        []LayoutZone
	Reservations []LayoutReservation
}

// Anchor is the interior centre of the
// first room for want that free accepts (every one when free is nil), in
// plan order (the planner lists rooms nearest the spine's start first). A
// full role falls back to reserve rooms; false means the plan holds no
// slot.
func (p LayoutPlan) Anchor(want ModuleRole, free func(room Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []ModuleRole{want, ModuleReserve} {
		for _, r := range p.AllRooms() {
			if r.Role == role && (free == nil || free(r.Interior)) {
				return domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// Core is the cell the base clusters around: the first storeroom's
// interior centre, else the main spine's midpoint (#1534). Stockpiles and
// the cooking campfire anchor here rather than on the colonists' centroid,
// which on a fresh landing is the map centre. False means an empty plan.
func (p LayoutPlan) Core() (domain.Cell, bool) {
	for _, r := range p.AllRooms() {
		if r.Role == ModuleStorage {
			return domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}, true
		}
	}
	if len(p.Spine) > 0 {
		s := p.Spine[0]
		return domain.Cell{X: (s.From.X + s.To.X) / 2, Z: (s.From.Z + s.To.Z) / 2}, true
	}
	return domain.Cell{}, false
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
