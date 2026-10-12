package policy

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Layout plans grow from a survey. Replanning re-zones the map, drops rooms
// unsupported by terrain and sites missing rooms. Core candidates are derived
// planning input and are not saved.

// DeriveLayoutPlan lays a fresh plan over the survey from the station chain:
// the facts (a first plan's, StationFacts.Derive) go through the graph, the
// clustering and the embedding, and the tiler (TileEmbedding) turns the
// embedding into the rooms, doors, links, roofs and lamps. What the tiler does
// not plan is laid over its rooms: a barn for a herd of animals
// (HerdPlan.PenAnimals), the yards, the perimeter and its mountain pockets. The
// pens are sited by the replan, once the pasture is read. A geothermal
// enclosure is reserved on each reported steam geyser and rooms stay off it;
// the turbines and solar plots the planned demand needs (derivePowerSites) are
// placed beside the rooms by the site embedding and stored with their wind
// lanes, and the tiler routes the conduit net to them;
// the outskirts are rooms of the chain itself. The advice is returned beside
// the plan for the caller to journal. Unknown when the survey holds no room
// for a core; the error is the chain's (ErrStationFacts for a fact the colony
// has not read and the chain needs).
func DeriveLayoutPlan(s MapSurvey, f StationFacts, geysers []PowerGeyser, animals int) (domain.Fact[LayoutPlan], StationAdvice, error) {
	zones := Zone(s)
	footprints := geyserFootprints(geysers)
	f.Derive, f.Built, f.Survey = true, map[int32]bool{}, s
	f.Plan = LayoutPlan{Zones: coreWithout(zones, geothermalCells(footprints)), Cold: s.Cold, Hot: s.Hot, Reservations: geothermalReservations(zones, footprints)}
	advice, err := TileStations(f)
	if err != nil {
		return domain.Unknown[LayoutPlan](), advice, err
	}
	plan := advice.Tiled.Plan
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan](), advice, nil
	}
	// The survey's zones less its field patches, then the fields the tiler placed.
	plan.Zones = append(withFieldZones(zones, nil), plan.Zones[len(f.Plan.Zones):]...)
	plan.Cold, plan.Hot = s.Cold, s.Hot
	plan = PlanHerdSites(plan, animals)
	plan = PlanYardSites(plan, YardRoomsWanted(RoomDemand{}))
	plan = PlanMountainPockets(PlanPerimeter(plan, s), s)
	return domain.Known(withoutCore(plan).WithRoomIDs()), advice, nil
}

// geothermalReservations are the enclosures of the geysers whose cells all lie
// on core ground; a site that does not fit is left out.
func geothermalReservations(zones []LayoutZone, footprints []Rectangle) []LayoutReservation {
	core := map[domain.Cell]bool{}
	for _, z := range zones {
		if z.Kind != ZoneCore {
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				core[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	var out []LayoutReservation
	for _, g := range footprints {
		area := geothermalArea(g)
		if !slices.ContainsFunc(RectangleCells(area), func(c domain.Cell) bool { return !core[c] }) {
			out = append(out, LayoutReservation{Kind: ReserveGeothermal, Area: RectRegion(area)})
		}
	}
	return out
}

// RoomGrowth is the rooms the plan is asked to add beyond the core: a throne
// room of at least ThroneArea cells (0 asks for none), the Child
// rooms the plan lacks and the gear rooms Gear asks for.
type RoomGrowth struct {
	ThroneArea int
	Demand     RoomDemand
	// InUse are the planned rooms of an ended role still standing or furnished;
	// the replan holds them where they stand.
	InUse map[int32]bool
	// Fixed are the interiors of every planned room with anything of ours
	// on it (FixedRooms), set on every replan once the census is
	// known (nil while it is not: every room is then kept). A replan
	// re-sites only the rooms outside it.
	Fixed map[int32]bool
	// RetireShelter is ShelterRetirable at this review; InFlight are
	// the planned rooms an open journal plan works on, which a retirement
	// leaves in the plan.
	RetireShelter bool
	InFlight      map[int32]bool
	// HerdUnits are the herds' units (HerdPlan.HerdUnits): each gets a
	// barn of its own beside the misc unit's.
	HerdUnits []HerdCeiling
	// Pens are the pens the herd asks for (HerdPlan.PenWants) and Grazing the
	// survey cells they may stand on (GrazingGround): each unit short of its
	// cells gets a pen beside its barn (PlanPenSites). Both are nil while the
	// animal census or the pasture table is unread, which asks for none.
	Pens    []PenWant
	Grazing map[domain.Cell]bool
	// Slept are the superseded bedrooms a pawn still owns a bed in
	// (SleptBedrooms): they stay in the plan after their successor stands.
	Slept map[int32]bool
	// Perimeter is what the defense record holds of the ring's nodes: a ring
	// traced again over a held one is laid as a replacement beside it, and
	// not at all while a rebuild is in flight (layout_perimeter_replace.go).
	Perimeter PerimeterHold
}

// ErrReplanEmpty is a replan whose tiled plan holds no room: the colony keeps
// the plan it has.
var ErrReplanEmpty = errors.New("the tiled plan holds no room")

// ReplanLayout is the next plan of a review (#3005, J4 of #2965): the tiled plan
// of the station chain, wholesale, over the plan prev the colony holds. f are the
// facts of the review (the plan's Built rooms, the chain's inputs): the tiler
// keeps every standing room it has a node for, pins the rooms growth holds
// (TilePinned) and sites the rest again. A standing room with no node in the new
// embedding leaves the plan and its ground is retired (RetiredGround) for
// clearance; the embedder's switching price (#2877) decides what moves. The
// rooms the tiler does not plan are laid over its plan as DeriveLayoutPlan lays
// them: the barns, pens and yards of prev that clear the new rooms stay and the
// herd's and the yard's shortfall is sited, then the perimeter is traced around
// what the defense record holds of it (LayPerimeter). The error is the chain's
// or the tiler's (ErrStationFacts for a fact the colony has not read, ErrTile*
// for a plan it cannot tile, ErrReplanEmpty); prev is returned with it, so the
// caller keeps its plan and journals the error.
func ReplanLayout(prev LayoutPlan, f StationFacts, s MapSurvey, geysers []PowerGeyser, growth RoomGrowth, animals int) (LayoutPlan, StationAdvice, error) {
	prev = prev.WithRoomIDs()
	f.Plan, f.Pinned = prev, TilePinned(prev, growth)
	f = ReplanTileFacts(f, s, geysers)
	advice, err := TileStations(f)
	if err != nil {
		return prev, advice, err
	}
	plan := advice.Tiled.Plan
	if len(plan.AllRooms()) == 0 {
		return prev, advice, ErrReplanEmpty
	}
	// The survey's zones less its field patches, then the fields the tiler placed.
	plan.Zones = append(withFieldZones(Zone(s), nil), plan.Zones[len(f.Plan.Zones):]...)
	plan.Cold, plan.Hot = s.Cold, s.Hot
	tiled := plan
	plan = keepHeld(prev, plan, growth)
	var joined []ConduitDecision
	plan.Conduits, joined = rejoinHeld(prev, tiled, plan, s)
	advice.Tiled.ConduitDecisions = append(advice.Tiled.ConduitDecisions, joined...)
	plan.RetiredGround = retiredGround(prev, plan, f.Built)
	plan.Reservations = append(slices.Clone(plan.Reservations), carriedSites(prev, plan)...)
	plan = PlanHerdSites(plan, animals, growth.HerdUnits...)
	plan = PlanPenSites(plan, growth.Grazing, growth.Pens...)
	plan = PlanYardSites(plan, YardRoomsWanted(growth.Demand))
	return LayPerimeter(prev, plan, s, growth.Perimeter).WithRoomIDs(), advice, nil
}

// keepHeld puts back the rooms of prev the colony holds (heldRooms) that the
// tiled plan left out, with their attached rooms, where they stood: a node-less
// room an open plan works on, a pawn sleeps in or a role that ended still uses
// does not leave the plan under it, and nothing demolishes it. A successor that
// stands keeps the Replaces the tiler dropped, so a bedroom a pawn still sleeps
// in stays the one the migration moves its sleepers out of.
func keepHeld(prev, next LayoutPlan, growth RoomGrowth) LayoutPlan {
	held := heldRooms(prev, growth)
	have := map[int32]bool{}
	for _, r := range next.AllRooms() {
		have[r.ID] = true
	}
	before := map[int32]PlannedRoom{}
	for _, r := range prev.AllRooms() {
		before[r.ID] = r
	}
	next.Rooms = slices.Clone(next.Rooms)
	for i, r := range next.Rooms {
		if old := before[r.ID]; r.Replaces == 0 && old.Replaces != 0 && held[old.Replaces] {
			next.Rooms[i].Replaces = old.Replaces
		}
	}
	for _, r := range prev.AllRooms() {
		if held[r.ID] && !have[r.ID] && !r.attached() {
			next.Rooms = append(next.Rooms, r)
			next.Rooms = append(next.Rooms, prev.ChildrenOf(r.ID)...)
		}
	}
	return next
}

// rejoinHeld joins the rooms keepHeld put back into the tiled plan's conduit
// net (#2966). The tiler routes only the rooms it planned; a held room that
// drew power through a link in prev (or its predecessor's) keeps that link where
// it still holds, else it routes to the net the new plan lays. The conduits a
// replan moves are laid before the old ones are given up: nothing ever removes
// a built conduit (clearance takes down walls and doors, not conduits), so the
// old path stays live until the new one carries the load and a link the plan
// drops is simply not laid again.
func rejoinHeld(prev, tiled, plan LayoutPlan, s MapSurvey) ([]ConduitLink, []ConduitDecision) {
	planned := map[int32]bool{}
	for _, r := range tiled.AllRooms() {
		planned[r.ID] = true
	}
	held := heldLinks(prev.Conduits, plan)
	var consumers []conduitNode
	for _, r := range plan.AllRooms() {
		if planned[r.ID] || r.attached() {
			continue
		}
		if _, drew := held[RoomRef(r.ID)]; drew {
			cells := r.Region.Cells()
			sortCells(cells)
			consumers = append(consumers, conduitNode{ref: RoomRef(r.ID), cells: cells})
		}
	}
	if len(consumers) == 0 {
		return plan.Conduits, nil
	}
	return JoinConduits(plan, consumers, s, prev.Conduits)
}

// retiredGround is the retired ground prev holds that clearance has not cleared,
// and the ground (ring and attached children) of each standing room of prev the
// plan next no longer holds: the building comes down through planned-ground
// clearance.
func retiredGround(prev, next LayoutPlan, built map[int32]bool) []Region {
	out := slices.Clone(prev.RetiredGround)
	held := map[int32]bool{}
	for _, r := range next.AllRooms() {
		held[r.ID] = true
	}
	for _, r := range prev.AllRooms() {
		if r.attached() || held[r.ID] || !built[r.ID] {
			continue
		}
		out = append(out, RectRegion(roomGround(r.Region.Bounds())))
		for _, c := range prev.ChildrenOf(r.ID) {
			out = append(out, RectRegion(c.attachedGround()))
		}
	}
	return out
}

// carriedSites are the barns, pens and yards of prev that stand clear of the
// rooms and the reserved ground of next: the tiler does not see them, so a new
// room may land on one, which then goes and is sited again. A site that clears
// stays where it is, so a replan does not move a barn or a yard that was built.
func carriedSites(prev, next LayoutPlan) []LayoutReservation {
	var taken []Rectangle
	for _, r := range next.AllRooms() {
		taken = append(taken, roomGround(r.Region.Bounds()))
	}
	for _, r := range next.Reserved() {
		taken = append(taken, r.Area.Bounds())
	}
	var out []LayoutReservation
	for _, r := range prev.Reservations {
		if r.Kind != ReserveBarn && r.Kind != ReservePen && r.Kind != ReserveYard {
			continue
		}
		if !slices.ContainsFunc(taken, func(t Rectangle) bool { return rectsOverlap(t, r.Area.Bounds()) }) {
			out = append(out, r)
		}
	}
	return out
}

// SamePlan reports whether a and b are the same plan as it is saved.
func SamePlan(a, b LayoutPlan) bool {
	x, xerr := json.Marshal(a)
	y, yerr := json.Marshal(b)
	return xerr == nil && yerr == nil && bytes.Equal(x, y)
}

// Summary is the plan's service-log line: rooms by role, zones by kind,
// reservations by kind, and the route check (busiest hallway cell's path
// count, or the rejection).
func (p LayoutPlan) Summary() string {
	rooms, zones, reserved := map[string]int{}, map[string]int{}, map[string]int{}
	for _, r := range p.AllRooms() {
		rooms[string(r.Role)]++
	}
	for _, z := range p.Zones {
		zones[string(z.Kind)]++
	}
	for _, r := range p.Reservations {
		reserved[string(r.Kind)]++
	}
	for _, s := range p.Sites {
		reserved[string(s.Kind)]++
	}
	route := "ok"
	traffic, err := CheckRoutes(p)
	if err != nil {
		route = err.Error()
	} else {
		busiest := 0
		for _, n := range traffic {
			busiest = max(busiest, n)
		}
		route = fmt.Sprintf("ok busiest=%d", busiest)
	}
	return fmt.Sprintf("rooms=[%s] zones=[%s] reservations=[%s] routes=%s", counts(rooms), counts(zones), counts(reserved), route)
}

func counts(m map[string]int) string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	parts := make([]string, len(keys))
	for i, k := range keys {
		parts[i] = fmt.Sprintf("%s:%d", k, m[k])
	}
	return strings.Join(parts, " ")
}

func withoutCore(p LayoutPlan) LayoutPlan {
	zones := make([]LayoutZone, 0, len(p.Zones))
	for _, z := range p.Zones {
		if z.Kind != ZoneCore {
			zones = append(zones, z)
		}
	}
	p.Zones = zones
	return p
}

func rectHits(r Rectangle, cells map[domain.Cell]bool) bool {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if cells[domain.Cell{X: x, Z: z}] {
				return true
			}
		}
	}
	return false
}

// geyserFootprints is each reported geyser's bounding rectangle.
func geyserFootprints(geysers []PowerGeyser) []Rectangle {
	var out []Rectangle
	for _, g := range geysers {
		if len(g.Cells) == 0 {
			continue
		}
		var r Rectangle
		for _, c := range g.Cells {
			r = unionRect(r, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
		}
		out = append(out, r)
	}
	return out
}

// geothermalArea is the enclosure PlanUtilities reserves over a geyser: the
// generator centred on it and its shell.
func geothermalArea(geyser Rectangle) Rectangle {
	cx, cz := geyser.X+geyser.Width/2, geyser.Z+geyser.Height/2
	side := geothermalSide + 2*geothermalShell
	return Rectangle{X: cx - side/2, Z: cz - side/2, Width: side, Height: side}
}

// geothermalCells is every cell of the geysers' enclosures: rooms stay off
// them so each geyser keeps its generator site.
func geothermalCells(footprints []Rectangle) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, f := range footprints {
		for _, c := range RectangleCells(geothermalArea(f)) {
			out[c] = true
		}
	}
	return out
}

// coreWithout drops cells from the core candidates.
func coreWithout(zones []LayoutZone, cells map[domain.Cell]bool) []LayoutZone {
	if len(cells) == 0 {
		return zones
	}
	out := make([]LayoutZone, 0, len(zones))
	for _, z := range zones {
		if z.Kind != ZoneCore {
			out = append(out, z)
			continue
		}
		var runs []RowRun
		for _, r := range z.Runs {
			start := r.X
			for x := r.X; x <= r.X+r.Length; x++ {
				if x < r.X+r.Length && !cells[domain.Cell{X: x, Z: r.Z}] {
					continue
				}
				if x > start {
					runs = append(runs, RowRun{Z: r.Z, X: start, Length: x - start})
				}
				start = x + 1
			}
		}
		z.Runs = runs
		out = append(out, z)
	}
	return out
}
