package policy

import (
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Derive and replan v2 (#783, B1). The plan is built from the survey once
// and afterwards only grown: a replan re-zones the map, drops rooms the
// terrain no longer carries, and calls Grow. The core candidates are a
// planning input, not part of the saved plan.

// layoutUtilities is what a fresh plan reserves beside its core.
var layoutUtilities = UtilityWants{TurbinePairs: 1, Solar: 1}

// DeriveLayoutPlan lays a fresh v2 plan over the survey for pawns
// colonists, with a geothermal enclosure on each reported steam geyser
// (#834). Unknown when the survey holds no room for a core.
func DeriveLayoutPlan(s MapSurvey, pawns int, tier BuildTier, geysers []PowerGeyser) domain.Fact[LayoutPlan] {
	plan := PlanCore(Zone(s), pawns, tier)
	if len(plan.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan]()
	}
	want := layoutUtilities
	for _, g := range geysers {
		if len(g.Cells) == 0 {
			continue
		}
		var r Rectangle
		for _, c := range g.Cells {
			r = unionRect(r, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
		}
		want.Geysers = append(want.Geysers, r)
	}
	plan = PlanBaitRoom(PlanMountainPockets(PlanPerimeter(PlanUtilities(plan, want), s), s), s)
	return domain.Known(withoutCore(plan))
}

// ReplanLayout grows plan for pawns colonists and tombs tomb rooms over a
// fresh survey, with Grow's suites: rooms now on no-go ground are dropped,
// the rest never move.
// With the rooms unchanged the perimeter alone is replanned (#954), which
// changes the plan when the ground on or near the ring did (ground a
// moisture pump dried, a mined-out ring cell). It reports whether the plan
// changed.
func ReplanLayout(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier BuildTier, suites ...float64) (LayoutPlan, bool) {
	zones := Zone(s)
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
	var kept []LayoutRoom
	for _, r := range plan.Rooms {
		if !rectHits(roomWalls(r), noGo) {
			kept = append(kept, r)
		}
	}
	wings := keepWingRooms(plan.Wings, func(r LayoutRoom) bool { return !rectHits(roomWalls(r), noGo) })
	next := plan
	next.Rooms, next.Wings, next.Zones = kept, wings, zones
	before := len(next.AllRooms())
	dropped := before != len(plan.AllRooms())
	next = Grow(next, pawns, tombs, tier, suites...)
	if !dropped && len(next.AllRooms()) == before {
		fresh := withoutCore(PlanBaitRoom(PlanMountainPockets(PlanPerimeter(plan, s), s), s))
		return fresh, !samePerimeter(plan, fresh)
	}
	if len(next.AllRooms()) == 0 {
		return plan, false
	}
	return withoutCore(PlanBaitRoom(PlanMountainPockets(PlanPerimeter(next, s), s), s)), true
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

// LayoutOutgrown reports fewer bedrooms than colonists.
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
