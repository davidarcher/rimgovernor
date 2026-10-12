package policy

import (
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
// the turbines and solar plots (FirstPowerWants) are placed beside the rooms by
// the site embedding and stored with their wind lanes, and the tiler routes the
// conduit net to them;
// the outskirts are rooms of the chain itself. The advice is returned beside
// the plan for the caller to journal. Unknown when the survey holds no room
// for a core; the error is the chain's (ErrStationFacts for a fact the colony
// has not read and the chain needs).
func DeriveLayoutPlan(s MapSurvey, f StationFacts, geysers []PowerGeyser, animals int) (domain.Fact[LayoutPlan], StationAdvice, error) {
	zones := Zone(s)
	footprints := geyserFootprints(geysers)
	f.Derive, f.Built, f.Survey, f.Power = true, map[int32]bool{}, s, FirstPowerWants()
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
	// ThroneMin is the title's minimum throne room area, 0 when nobody is
	// owed a throne room: smaller throne rooms retire once a room holding it
	// exists.
	ThroneMin int
	Child     []ChildRoomShape
	Demand    RoomDemand
	// Core are the demand-grown core rooms (demandCoreRooms) the colony now
	// needs: each the plan lacks is sited (layout_demand_rooms.go).
	Core []PlannedRole
	// Shapes are every resolved child-room need and Built the planned rooms
	// standing: the duplicates of one role reduce to one once Built is
	// set (the census is known).
	Shapes []ChildRoomShape
	Built  map[int32]bool
	// Ended are the roles whose need is gone and InUse the planned rooms
	// standing or furnished: an ended role's rooms outside InUse leave the
	// plan. InUse is set only when an ended role has a room.
	Ended []PlannedRole
	InUse map[int32]bool
	// Fixed are the interiors of every planned room with anything of ours
	// on it (FixedRooms), set on every replan once the census is
	// known (nil while it is not: every room is then kept). A replan
	// re-sites only the rooms outside it.
	Fixed map[int32]bool
	// Occupied are the cells of ours a new or re-sited room must stay off
	// (the census, sites, claims and in-flight rooms' walls).
	Occupied map[domain.Cell]bool
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
	// Outskirts is the outline (width, height) of the off-core cluster the
	// plan is asked to hold (OutskirtsSize); zero asks for none, and a
	// cluster the plan holds never moves. Its tomb and morgue are placed in
	// their slots.
	Outskirts [2]int32
	// Successor is the room the station chain asks to replace an outgrown
	// one (OutgrownSuccession); the zero value asks for none. A replan sites
	// it ahead of the re-siting, keeps both rooms pinned and retires the old
	// room once the successor stands (Built).
	Successor Successor
	// Slept are the superseded bedrooms a pawn still owns a bed in
	// (SleptBedrooms): they stay in the plan after their successor stands.
	Slept map[int32]bool
	// Perimeter is what the defense record holds of the ring's nodes: a ring
	// traced again over a held one is laid as a replacement beside it, and
	// not at all while a rebuild is in flight (layout_perimeter_replace.go).
	Perimeter PerimeterHold
	// Fields are the field blocks the station chain placed at this review
	// (StationAdvice.Embedding.Fields), each sized by FieldCapacityTarget; they
	// are the plan's field zones. With none (the chain did not run, or the soil
	// held none) the plan's own field zones stay as they are
	// (layout_field_replan.go).
	Fields []Rectangle
}

// ReplanLayoutWithRooms grows plan for pawns colonists and tombs tomb rooms
// over a fresh survey, with the suites: rooms now on no-go ground are
// dropped, the plan grows what it lacks, and the unbuilt rooms are sited
// again when that scores clearly better (layout_replan.go); a room
// with anything of ours on it never moves. With the rooms unchanged the
// perimeter alone is replanned, which changes the plan when the
// ground on or near the ring did (ground a moisture pump dried, a mined-out
// ring cell). It reports whether the plan changed. An emptied Retiring wing
// (emptied, keyed by its corridor's hallway cell) is dropped only when a
// beds. It also keeps the rooms of growth, added after the rest of the
// rooms and before the perimeter is replanned around them. A herd of
// animals (HerdPlan.PenAnimals; 0 leaves the herd sites as they are) gets
// animals (HerdPlan.PenAnimals; 0 leaves the herd sites as they are) gets
// the barn it lacks (PlanHerdSites). The error
// joins the rooms growth asked for that no core slot took; the plan
// returned is still the best one, so the caller reports it and carries on.
func ReplanLayoutWithRooms(plan LayoutPlan, s MapSurvey, growth RoomGrowth, animals, pawns, tombs int, tier TechTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool, error) {
	plan = plan.WithRoomIDs()
	next, changed, err := replanLayout(plan, s, growth, animals, pawns, tombs, tier, geysers, emptied, suites...)
	return next.WithRoomIDs(), changed, err
}

func replanLayout(plan LayoutPlan, s MapSurvey, growth RoomGrowth, animals, pawns, tombs int, tier TechTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool, error) {
	zones := Zone(s)
	vents := geothermalCells(geyserFootprints(geysers))
	noGo := map[domain.Cell]bool{}
	for _, z := range zones {
		if z.Kind != ZoneNoGo {
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				noGo[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	fields := replanFieldZones(plan, growth, s)
	zoned := withFieldZones(zones, fields)
	plan, sited := topUpHerdSites(plan, coreWithout(zones, vents), animals, growth.HerdUnits, growth.Pens, growth.Grazing)
	plan, yards := topUpYards(plan, coreWithout(zones, vents), YardRoomsWanted(growth.Demand))
	sited = sited || yards
	var kept []PlannedRoom
	for _, r := range plan.Rooms {
		if !rectHits(roomWalls(r), noGo) && !rectHits(roomWalls(r), vents) {
			kept = append(kept, r)
		}
	}
	wings := keepWingRooms(plan.Wings, func(r PlannedRoom) bool { return !rectHits(roomWalls(r), noGo) && !rectHits(roomWalls(r), vents) })
	next := plan
	next.Rooms, next.Wings, next.Zones = kept, wings, coreWithout(zoned, vents)
	dropped := len(next.AllRooms()) != len(plan.AllRooms())
	if growth.Built != nil {
		var retired bool
		next, retired = retireSurplusRooms(next, growth, growth.Built)
		dropped = dropped || retired
	}
	next, retired := retireAddOnRooms(next, growth)
	dropped = dropped || retired
	next, retired = retireShelter(next, growth)
	dropped = dropped || retired
	// The successor of an outgrown room joins the plan before the replan pins
	// it; one that cannot be sited is reported and nothing is built.
	// A built room that has columns free beside it is enlarged in place
	// instead (layout_reshape.go), and one rebuild runs at a time.
	var unplaced []error
	var successorScorer *planScorer
	if growth.Successor.Any() || growth.Built != nil {
		sc := newPlanScorer(next.Zones, next.Reserved(), s)
		successorScorer = &sc
	}
	asked := growth.Successor
	next, reshaped := reshapeOutgrown(next, asked, growth, blockedCells(next, growth.Occupied))
	if reshaped || rebuildInFlight(growth) {
		asked = Successor{}
	}
	dropped = dropped || reshaped
	next, successor, err := growSuccessor(next, asked, successorScorer, blockedCells(next, growth.Occupied))
	dropped = dropped || successor
	unplaced = append(unplaced, err)
	// A built room that is clearly better moved (rich soil under it, or a
	// score gain past the switching threshold) gets a successor too.
	if !reshaped && !successor && successorScorer != nil {
		var relocated bool
		next, relocated = relocateBuilt(next, *successorScorer, growth, blockedCells(next, growth.Occupied))
		dropped = dropped || relocated
	}
	// Growth, then the unbuilt rooms sited again where that scores clearly
	// better (layout_replan.go); an emptied Retiring wing is among them.
	rp := newReplanner(next, s, blockedCells(next, growth.Occupied), pawns, tombs, tier, suites)
	next, resited := rp.replanCore(next, ReplanPins(next, growth.Fixed, emptied, tier))
	dropped = dropped || resited
	scorer := newPlanScorer(next.Zones, next.Reserved(), s)
	next, throne, err := growThroneRoom(next, growth.ThroneArea, &scorer)
	dropped = dropped || throne
	unplaced = append(unplaced, err)
	for _, shape := range growth.Child {
		var grown bool
		next, grown, err = growChildRoom(next, shape, &scorer)
		dropped = dropped || grown
		unplaced = append(unplaced, err)
	}
	next, gear, err := growGearRooms(next, growth.Demand, &scorer)
	dropped = dropped || gear
	unplaced = append(unplaced, err)
	next, storage, err := growStorageRooms(next, growth.Demand, &scorer)
	dropped = dropped || storage
	unplaced = append(unplaced, err)
	next, core, err := growDemandRooms(next, s, &scorer, growth.Core)
	dropped = dropped || core
	unplaced = append(unplaced, err)
	next, outskirts := growOutskirts(next, growth.Outskirts)
	dropped = dropped || outskirts
	if growth.Outskirts != [2]int32{} {
		var rooms bool
		next, rooms = growOutskirtsRooms(next, ThickRoofCells(s))
		dropped = dropped || rooms
	}
	next, graves := growGraveyards(next, growth.Demand)
	dropped = dropped || graves
	next, ranges := growRanges(next, growth.Demand)
	dropped = dropped || ranges
	next, rings := growRings(next, growth.Demand)
	dropped = dropped || rings
	next.Zones = zoned
	unplacedRooms := errors.Join(unplaced...)
	if !dropped && sameInteriors(plan.AllRooms(), next.AllRooms()) {
		fresh := layPerimeter(plan, plan, s, growth.Perimeter)
		// Rooms unchanged, fields resized or moved: the plan changed.
		moved := !sameFieldZones(plan.Zones, fields)
		if moved {
			fresh.Zones = withFieldZones(fresh.Zones, fields)
		}
		return fresh, sited || moved || !sameSites(plan, fresh), unplacedRooms
	}
	if len(next.AllRooms()) == 0 {
		return plan, false, unplacedRooms
	}
	return layPerimeter(plan, next, s, growth.Perimeter), true, unplacedRooms
}

// topUpHerdSites adds the herd sites animals needs to plan, sited over the
// fresh core candidates and off the current perimeter (which is laid again
// around them). It reports whether it added any.
func topUpHerdSites(plan LayoutPlan, core []LayoutZone, animals int, herds []HerdCeiling, pens []PenWant, grazing map[domain.Cell]bool) (LayoutPlan, bool) {
	if animals <= 0 && len(herds) == 0 && PensOwed(plan, pens) == 0 {
		return plan, false
	}
	return topUpSites(plan, core, func(p LayoutPlan) LayoutPlan {
		return PlanPenSites(PlanHerdSites(p, animals, herds...), grazing, pens...)
	})
}

// topUpSites runs add over plan with the fresh core candidates and no site
// nodes (the perimeter is laid again afterwards), and keeps the reservations
// it added. It reports whether it added or keyed any.
func topUpSites(plan LayoutPlan, core []LayoutZone, add func(LayoutPlan) LayoutPlan) (LayoutPlan, bool) {
	sitePlan := plan
	sitePlan.Zones, sitePlan.Sites = core, nil
	topped := add(sitePlan)
	// A top-up also keys the units of a plan saved without keys.
	plan.Reservations = slices.Clone(plan.Reservations)
	changed := false
	for i := range plan.Reservations {
		if plan.Reservations[i].Herd != topped.Reservations[i].Herd {
			plan.Reservations[i].Herd, changed = topped.Reservations[i].Herd, true
		}
	}
	n := len(plan.Reservations)
	plan.Reservations = append(plan.Reservations, topped.Reservations[n:]...)
	return plan, changed || len(topped.Reservations) > n
}

// blockedCells is the occupied cells a replan's rooms must stay off: all but
// those under the walls of the plan's own rooms, which the plan already
// keeps (an unfixed room has none, so a room sited again never lands on
// something built).
func blockedCells(plan LayoutPlan, occupied map[domain.Cell]bool) map[domain.Cell]bool {
	if len(occupied) == 0 {
		return nil
	}
	own := map[domain.Cell]bool{}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			own[c] = true
		}
	}
	blocked := map[domain.Cell]bool{}
	for c := range occupied {
		if !own[c] {
			blocked[c] = true
		}
	}
	return blocked
}

// sameInteriors reports whether a and b hold the same rooms in order: a
// changed interior is not the same plan.
func sameInteriors(a, b []PlannedRoom) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i].Region.Bounds() != b[i].Region.Bounds() {
			return false
		}
	}
	return true
}

// LayoutOutgrown reports fewer bedrooms than colonists: another wing is
// needed; wings never grow.
func (p LayoutPlan) LayoutOutgrown(pawns int) bool {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == PlannedBedroom {
			n++
		}
	}
	return n < pawns
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
