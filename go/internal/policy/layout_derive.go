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

// layoutUtilities is what a fresh plan reserves beside its core; the pens,
// barn and vet room are sized by the herd plan's target herd.
var layoutUtilities = UtilityWants{TurbinePairs: 1, Solar: 1}

// DeriveLayoutPlan lays a fresh plan over the survey for pawns
// colonists, with a geothermal enclosure on each reported steam geyser
// and pens, a barn and a vet room for a herd of animals
// (HerdPlan.PenAnimals), and a wall yard that holds yardAnimals (HerdPlan.YardAnimals, 0 keeps the floor). Unknown when the survey holds no room for a core.
func DeriveLayoutPlan(s MapSurvey, pawns int, tier TechTier, geysers []PowerGeyser, animals, yardAnimals int) domain.Fact[LayoutPlan] {
	zones := Zone(s)
	footprints := geyserFootprints(geysers)
	plan := SiteCore(LayoutPlan{Zones: coreWithout(zones, geothermalCells(footprints)), Cold: s.Cold, Hot: s.Hot}, s, pawns, 0, tier)
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan]()
	}
	plan.Zones = zones
	plan.YardCells = YardCells(yardAnimals)
	want := layoutUtilities
	want.Geysers, want.PenAnimals, want.ThickRoof = footprints, animals, ThickRoofCells(s)
	scorer := newPlanScorer(plan.Zones, plan.Reservations, s)
	want.scorer = &scorer
	plan = PlanUtilities(plan, want)
	plan = PlanYardSites(plan, YardRoomsWanted(RoomDemand{}))
	plan, _ = growOutskirts(plan, OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, want.ThickRoof)
	plan = PlanBaitRoom(PlanMountainPockets(PlanPerimeter(plan, s), s), s)
	return domain.Known(withoutCore(plan))
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
	Built  map[Rectangle]bool
	// Ended are the roles whose need is gone and InUse the planned rooms
	// standing or furnished: an ended role's rooms outside InUse leave the
	// plan. InUse is set only when an ended role has a room.
	Ended []PlannedRole
	InUse map[Rectangle]bool
	// Fixed are the interiors of every planned room with anything of ours
	// on it (FixedRooms), set on every replan once the census is
	// known (nil while it is not: every room is then kept). A replan
	// re-sites only the rooms outside it.
	Fixed map[Rectangle]bool
	// Occupied are the cells of ours a new or re-sited room must stay off
	// (the census, sites, claims and in-flight rooms' walls).
	Occupied map[domain.Cell]bool
	// RetireShelter is ShelterRetirable at this review; InFlight are
	// the planned rooms an open journal plan works on, which a retirement
	// leaves in the plan.
	RetireShelter bool
	InFlight      map[Rectangle]bool
	// HerdUnits are the herds' units (HerdPlan.HerdUnits): each gets a
	// pen, barn and vet area of its own beside the misc unit's.
	HerdUnits []HerdCeiling
	// Outskirts is the outline (width, height) of the off-core cluster the
	// plan is asked to hold (OutskirtsSize); zero asks for none, and a
	// cluster the plan holds never moves. Its tomb and morgue are placed in
	// their slots.
	Outskirts [2]int32
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
// the pens, barn and vet room it lacks (PlanHerdSites). The error
// joins the rooms growth asked for that no core slot took; the plan
// returned is still the best one, so the caller reports it and carries on.
func ReplanLayoutWithRooms(plan LayoutPlan, s MapSurvey, growth RoomGrowth, animals, pawns, tombs int, tier TechTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool, error) {
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
	plan, sited := topUpHerdSites(plan, coreWithout(zones, vents), animals, growth.HerdUnits)
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
	next.Rooms, next.Wings, next.Zones = kept, wings, coreWithout(zones, vents)
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
	// Growth, then the unbuilt rooms sited again where that scores clearly
	// better (layout_replan.go); an emptied Retiring wing is among them.
	rp := newReplanner(next, s, blockedCells(next, growth.Occupied), pawns, tombs, tier, suites)
	next, resited := rp.replanCore(next, replanPins(next, growth.Fixed, emptied, tier))
	dropped = dropped || resited
	var unplaced []error
	scorer := newPlanScorer(next.Zones, next.Reservations, s)
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
	next.Zones = zones
	unplacedRooms := errors.Join(unplaced...)
	if !dropped && sameInteriors(plan.AllRooms(), next.AllRooms()) {
		fresh := withoutCore(PlanBaitRoom(PlanMountainPockets(PlanPerimeter(plan, s), s), s))
		return fresh, sited || !samePerimeter(plan, fresh), unplacedRooms
	}
	if len(next.AllRooms()) == 0 {
		return plan, false, unplacedRooms
	}
	return withoutCore(PlanBaitRoom(PlanMountainPockets(PlanPerimeter(next, s), s), s)), true, unplacedRooms
}

// topUpHerdSites adds the herd sites animals needs to plan, sited over the
// fresh core candidates and off the current perimeter (which is laid again
// around them). It reports whether it added any.
func topUpHerdSites(plan LayoutPlan, core []LayoutZone, animals int, herds []HerdCeiling) (LayoutPlan, bool) {
	if animals <= 0 && len(herds) == 0 {
		return plan, false
	}
	return topUpSites(plan, core, func(p LayoutPlan) LayoutPlan { return PlanHerdSites(p, animals, herds...) })
}

// topUpSites runs add over plan with the fresh core candidates and the
// reservations the perimeter does not own, and keeps the reservations it
// added. It reports whether it added or keyed any.
func topUpSites(plan LayoutPlan, core []LayoutZone, add func(LayoutPlan) LayoutPlan) (LayoutPlan, bool) {
	var inner []LayoutReservation
	for _, r := range plan.Reservations {
		if !perimeterKinds[r.Kind] {
			inner = append(inner, r)
		}
	}
	sitePlan := plan
	sitePlan.Zones, sitePlan.Reservations = core, inner
	topped := add(sitePlan)
	// A top-up also keys the units of a plan saved without keys.
	plan.Reservations = slices.Clone(plan.Reservations)
	changed := false
	for i, j := 0, 0; i < len(plan.Reservations); i++ {
		if perimeterKinds[plan.Reservations[i].Kind] {
			continue
		}
		if plan.Reservations[i].Herd != topped.Reservations[j].Herd {
			plan.Reservations[i].Herd, changed = topped.Reservations[j].Herd, true
		}
		j++
	}
	plan.Reservations = append(plan.Reservations, topped.Reservations[len(inner):]...)
	return plan, changed || len(topped.Reservations) > len(inner)
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
		if a[i].Interior != b[i].Interior {
			return false
		}
	}
	return true
}

// samePerimeter reports whether a and b hold the same perimeter
// reservations, in any order.
func samePerimeter(a, b LayoutPlan) bool {
	count := map[LayoutReservation]int{}
	for _, r := range a.Reservations {
		if perimeterKinds[r.Kind] {
			count[r]++
		}
	}
	for _, r := range b.Reservations {
		if perimeterKinds[r.Kind] {
			count[r]--
		}
	}
	for _, n := range count {
		if n != 0 {
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
