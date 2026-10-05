package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The incinerator (#1814, epic #1640): a walled, unroofed 3x3 room beside
// the dumps. Rotten food, rotting animal corpses and worn gear haul into it
// from the Low dumps (a higher priority zone over its floor, IncineratorSite)
// and it is burned whole. It is a reservation like the barn (herd_rooms.go),
// and its room is derived from it, never stored: the walls and door are
// non-flammable (observation FireproofStuff) so the fire stays inside, and
// the room has no roof so it does not heat up and kill the pawns who clean
// the ash. Unlike every other room it is permanent: clothing always wears
// and corpses always happen, so the plan never drops or moves it.

const (
	// ReserveIncinerator is the incinerator's walls and interior together.
	ReserveIncinerator ReservationKind = "incinerator"
	// PlannedIncinerator is the incinerator's plan role.
	PlannedIncinerator PlannedRole = "incinerator"
)

// incineratorSide is the interior side; the walled outline is two more.
const incineratorSide int32 = 3

// incineratorCandidates bounds the nearest outlines searched for barren ground.
const incineratorCandidates = 400

// IncineratorSite is where the incinerator stands: the walled outline (Area)
// and the side its door faces (Facing). The zero value is no site.
type IncineratorSite struct {
	Area   Rectangle
	Facing domain.Rotation
}

// Reservation is the layout reservation holding the site.
func (s IncineratorSite) Reservation() LayoutReservation {
	return LayoutReservation{Kind: ReserveIncinerator, Area: s.Area, Facing: s.Facing}
}

// Room is the walled room the site holds: the interior inside the outline,
// the door in the middle of the Facing wall.
func (s IncineratorSite) Room() PlannedRoom {
	a := s.Area
	in := Rectangle{X: a.X + 1, Z: a.Z + 1, Width: a.Width - 2, Height: a.Height - 2}
	room := PlannedRoom{Role: PlannedIncinerator, Interior: in, DoorRot: s.Facing}
	switch s.Facing {
	case domain.East:
		room.Door = domain.Cell{X: in.X + in.Width, Z: in.Z + in.Height/2}
	case domain.West:
		room.Door = domain.Cell{X: in.X - 1, Z: in.Z + in.Height/2}
	case domain.South:
		room.Door = domain.Cell{X: in.X + in.Width/2, Z: in.Z - 1}
	default:
		room.Door, room.DoorRot = domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height}, domain.North
	}
	return room
}

// IncineratorRooms are the plan's incinerator rooms, in plan order.
func (p LayoutPlan) IncineratorRooms() []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Reservations {
		if r.Kind == ReserveIncinerator {
			out = append(out, IncineratorSite{Area: r.Area, Facing: r.Facing}.Room())
		}
	}
	return out
}

// IncineratorWanted reports something waiting for the rotten or worn dump:
// the need that owes the plan its incinerator.
func IncineratorWanted(needs map[string]int) bool {
	return needs[domain.RottenDumpRole] > 0 || needs[domain.WornDumpRole] > 0
}

// IncineratorOwed is the site the plan should gain, zero when it holds an
// incinerator, nothing waits for the dumps or no ground fits one.
func (r StorageRequest) incineratorOwed() IncineratorSite {
	d := r.Dumps
	if d == nil || r.Layout == nil || !IncineratorWanted(d.Needs) || len(r.Layout.IncineratorRooms()) > 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return IncineratorSite{}
	}
	anchor := r.dumpAnchor()
	toward := anchor
	for _, z := range r.Zones {
		if (z.Role == domain.RottenDumpRole || z.Role == domain.WornDumpRole) && len(z.Cells) > 0 {
			toward = stockpileSorted(z.Cells)[0]
			break
		}
	}
	site, _ := SiteIncinerator(IncineratorSiteRequest{Plan: *r.Layout, Bounds: r.Bounds, Cells: r.Cells, Rooms: d.Rooms, Protected: r.Protected, Anchor: anchor, Toward: toward})
	return site
}

// IncineratorSiteRequest is the ground an incinerator is sited on.
type IncineratorSiteRequest struct {
	Plan      LayoutPlan
	Bounds    Bounds
	Cells     []SiteCell
	Rooms     []Room
	Protected []domain.Cell
	// Anchor sites the incinerator nearest it; Toward is the point its door
	// faces, the dump patch (the anchor when zero).
	Anchor, Toward domain.Cell
}

// SiteIncinerator finds the walled outline nearest the anchor on free
// outdoor ground clear of living rooms (the dumps' six cells), off the
// plan's rooms, hallways and reservations. The door faces the dump patch.
func SiteIncinerator(r IncineratorSiteRequest) (IncineratorSite, bool) {
	side := incineratorSide + 2
	protected := append([]domain.Cell(nil), r.Protected...)
	for _, room := range r.Plan.AllRooms() {
		protected = append(protected, RectangleCells(roomWalls(room))...)
	}
	for _, h := range r.Plan.Hallways() {
		x0, x1 := min(h.From.X, h.To.X), max(h.From.X, h.To.X)
		z0, z1 := min(h.From.Z, h.To.Z), max(h.From.Z, h.To.Z)
		protected = append(protected, RectangleCells(Rectangle{X: x0 - SpineWidth/2, Z: z0 - SpineWidth/2, Width: x1 - x0 + SpineWidth, Height: z1 - z0 + SpineWidth})...)
	}
	for _, res := range r.Plan.Reservations {
		protected = append(protected, RectangleCells(res.Area)...)
	}
	patches, err := OutdoorDumpSites(OutdoorDumpRequest{Bounds: r.Bounds, Anchor: r.Anchor, Cells: r.Cells, Rooms: r.Rooms, Protected: protected, Width: side, Height: side, Limit: incineratorCandidates})
	if err != nil || len(patches) == 0 {
		return IncineratorSite{}, false
	}
	best := patches[0]
	toward := r.Toward
	if toward == (domain.Cell{}) {
		toward = r.Anchor
	}
	return IncineratorSite{Area: best, Facing: facingToward(best, toward)}, true
}

// growIncinerator reserves site in plan unless the plan holds an incinerator
// already; an existing one never moves. It reports whether it added one.
func growIncinerator(plan LayoutPlan, site IncineratorSite) (LayoutPlan, bool) {
	if site == (IncineratorSite{}) || len(plan.IncineratorRooms()) > 0 {
		return plan, false
	}
	plan.Reservations = append(append([]LayoutReservation(nil), plan.Reservations...), site.Reservation())
	return plan, true
}

// facingToward is the side of area whose wall faces target.
func facingToward(area Rectangle, target domain.Cell) domain.Rotation {
	dx, dz := float64(target.X)-(float64(area.X)+float64(area.Width)/2), float64(target.Z)-(float64(area.Z)+float64(area.Height)/2)
	switch {
	case abs64(dx) > abs64(dz) && dx > 0:
		return domain.East
	case abs64(dx) > abs64(dz):
		return domain.West
	case dz > 0:
		return domain.North
	}
	return domain.South
}

// incineratorSites is the incinerator zone once its walls stand: the whole
// interior, taking rotten and worn dump items at a priority above the Low
// dumps so they haul in from them.
func (r StorageRequest) incineratorSites() []StockpileSite {
	if r.Dumps == nil || r.Dumps.Incinerator == nil {
		return nil
	}
	cells := stockpileSorted(RectangleCells(r.Dumps.Incinerator.Interior))
	return []StockpileSite{{Role: domain.IncineratorRole, Room: cells, Filter: domain.IncineratorFilter(), Priority: domain.PreferredPriority, Keyed: true, Candidates: [][]domain.Cell{cells}}}
}
