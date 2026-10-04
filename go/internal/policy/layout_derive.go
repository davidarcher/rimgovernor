package policy

import (
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Derive and replan v2 (#783, B1). The plan is built from the survey once
// and afterwards only grown: a replan re-zones the map, drops rooms the
// terrain no longer carries, and calls Grow. The core candidates are a
// planning input, not part of the saved plan.

// layoutUtilities is what a fresh plan reserves beside its core; the pens,
// barn and vet room are sized by the herd plan's target herd (#1633).
var layoutUtilities = UtilityWants{TurbinePairs: 1, Solar: 1}

// DeriveLayoutPlan lays a fresh v2 plan over the survey for pawns
// colonists, with a geothermal enclosure on each reported steam geyser
// (#834) and pens, a barn and a vet room for a herd of animals
// (HerdPlan.PenAnimals). Unknown when the survey holds no room for a core.
func DeriveLayoutPlan(s MapSurvey, pawns int, tier BuildTier, geysers []PowerGeyser, animals int) domain.Fact[LayoutPlan] {
	zones := Zone(s)
	footprints := geyserFootprints(geysers)
	plan := SiteCore(LayoutPlan{Zones: coreWithout(zones, geothermalCells(footprints))}, s, pawns, 1, tier)
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan]()
	}
	plan.Zones = zones
	want := layoutUtilities
	want.Geysers, want.PenAnimals, want.ThickRoof = footprints, animals, ThickRoofCells(s)
	plan = PlanBaitRoom(PlanMountainPockets(PlanPerimeter(PlanUtilities(plan, want), s), s), s)
	return domain.Known(withoutCore(plan))
}

// ReplanLayout grows plan for pawns colonists and tombs tomb rooms over a
// fresh survey, with Grow's suites: rooms now on no-go ground are dropped,
// the rest never move.
// With the rooms unchanged the perimeter alone is replanned (#954), which
// changes the plan when the ground on or near the ring did (ground a
// moisture pump dried, a mined-out ring cell). It reports whether the plan
// changed. An emptied Retiring wing (emptied, keyed by its corridor's
// hallway cell) is dropped only when its ground lets Grow place a room or
// wing it otherwise could not (dropEmptiedWings); else it stays as spare
// beds.
func ReplanLayout(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier BuildTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool) {
	next, changed, _ := ReplanLayoutWithRooms(plan, s, RoomGrowth{}, 0, pawns, tombs, tier, geysers, emptied, suites...)
	return next, changed
}

// RoomGrowth is the rooms the plan is asked to add beyond the core: a throne
// room of at least ThroneArea cells (#1601; 0 asks for none), the Child
// rooms (#1680) the plan lacks and the gear rooms Gear asks for (#1773).
type RoomGrowth struct {
	ThroneArea int
	// ThroneMin is the title's minimum throne room area, 0 when nobody is
	// owed a throne room: smaller throne rooms retire once a room holding it
	// exists (#1825).
	ThroneMin int
	Child     []ChildRoomShape
	Demand    RoomDemand
	// Incinerator is the site of the incinerator the plan lacks (#1814).
	Incinerator IncineratorSite
	// Shapes are every resolved child-room need and Built the planned rooms
	// standing: the duplicates of one role reduce to one (#1823) once Built is
	// set (the census is known).
	Shapes []ChildRoomShape
	Built  map[Rectangle]bool
	// Ended are the roles whose need is gone and InUse the planned rooms
	// standing or furnished: an ended role's rooms outside InUse leave the
	// plan (#1824). InUse is set only when an ended role has a room.
	Ended []ModuleRole
	InUse map[Rectangle]bool
	// Fixed are the interiors of every planned room with anything of ours
	// on it (FixedRooms, #1943), set on every replan once the census is
	// known. Grow never moves a room, fixed or not.
	Fixed map[Rectangle]bool
}

// ReplanLayoutWithRooms is ReplanLayout that also keeps the rooms of growth,
// added after the rest of the rooms and before the perimeter is replanned
// around them. A herd of
// animals (HerdPlan.PenAnimals; 0 leaves the herd sites as they are) gets
// the pens, barn and vet room it lacks (PlanHerdSites, #1633). The error
// joins the rooms growth asked for that no core slot took (#1799); the plan
// returned is still the best one, so the caller reports it and carries on.
func ReplanLayoutWithRooms(plan LayoutPlan, s MapSurvey, growth RoomGrowth, animals, pawns, tombs int, tier BuildTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool, error) {
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
	plan, sited := topUpHerdSites(plan, coreWithout(zones, vents), animals)
	var kept []LayoutRoom
	for _, r := range plan.Rooms {
		if !rectHits(roomWalls(r), noGo) && !rectHits(roomWalls(r), vents) {
			kept = append(kept, r)
		}
	}
	wings := keepWingRooms(plan.Wings, func(r LayoutRoom) bool { return !rectHits(roomWalls(r), noGo) && !rectHits(roomWalls(r), vents) })
	next := plan
	next.Rooms, next.Wings, next.Zones = kept, wings, coreWithout(zones, vents)
	dropped := len(next.AllRooms()) != len(plan.AllRooms())
	next, freed := dropEmptiedWings(next, emptied, pawns, tombs, tier, suites...)
	dropped = dropped || freed
	if growth.Built != nil {
		var retired bool
		next, retired = retireSurplusRooms(next, growth, growth.Built)
		dropped = dropped || retired
	}
	next, retired := retireAddOnRooms(next, growth)
	dropped = dropped || retired
	var unplaced []error
	next, throne, err := growThroneRoom(next, growth.ThroneArea)
	dropped = dropped || throne
	unplaced = append(unplaced, err)
	for _, shape := range growth.Child {
		var grown bool
		next, grown, err = growChildRoom(next, shape)
		dropped = dropped || grown
		unplaced = append(unplaced, err)
	}
	next, gear, err := growGearRooms(next, growth.Demand)
	dropped = dropped || gear
	unplaced = append(unplaced, err)
	next, storage, err := growStorageRooms(next, growth.Demand)
	dropped = dropped || storage
	unplaced = append(unplaced, err)
	next, incinerator := growIncinerator(next, growth.Incinerator)
	dropped = dropped || incinerator
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
func topUpHerdSites(plan LayoutPlan, core []LayoutZone, animals int) (LayoutPlan, bool) {
	if animals <= 0 {
		return plan, false
	}
	var inner []LayoutReservation
	for _, r := range plan.Reservations {
		if !perimeterKinds[r.Kind] {
			inner = append(inner, r)
		}
	}
	sitePlan := plan
	sitePlan.Zones, sitePlan.Reservations = core, inner
	topped := PlanHerdSites(sitePlan, animals)
	if len(topped.Reservations) == len(inner) {
		return plan, false
	}
	plan.Reservations = append(slices.Clone(plan.Reservations), topped.Reservations[len(inner):]...)
	return plan, true
}

// dropEmptiedWings grows plan, first dropping each emptied Retiring wing
// whose ground lets a counterfactual Grow place more core rooms or active
// wing rooms than the real Grow (#1249); the cleared wing's ground is then
// clearance's to demolish. Rooms outside a dropped wing never move. It
// reports whether a wing was dropped.
func dropEmptiedWings(plan LayoutPlan, emptied map[domain.Cell]bool, pawns, tombs int, tier BuildTier, suites ...float64) (LayoutPlan, bool) {
	grown := Grow(plan, pawns, tombs, tier, suites...)
	dropped := false
	for i := 0; i < len(plan.Wings); i++ {
		w := plan.Wings[i]
		if !emptied[w.Corridor.From] || retireWings([]Wing{w}, tier)[0].Purpose != WingBedroomsRetiring {
			continue
		}
		without := plan
		without.Wings = append(append([]Wing(nil), plan.Wings[:i]...), plan.Wings[i+1:]...)
		cf := Grow(without, pawns, tombs, tier, suites...)
		if len(cf.Rooms) > len(grown.Rooms) || activeWingRooms(cf) > activeWingRooms(grown) {
			plan, grown, dropped = without, cf, true
			i--
		}
	}
	return grown, dropped
}

// activeWingRooms is the rooms in p's wings that are not Retiring.
func activeWingRooms(p LayoutPlan) int {
	n := 0
	for _, w := range p.Wings {
		if w.Purpose != WingBedroomsRetiring {
			n += len(w.Rooms)
		}
	}
	return n
}

// sameInteriors reports whether a and b hold the same rooms in order: a
// grown suite (#1218) changes an interior without adding a room.
func sameInteriors(a, b []LayoutRoom) bool {
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
// needed (#1950); wings never grow.
func (p LayoutPlan) LayoutOutgrown(pawns int) bool {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == ModuleBedroom {
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
