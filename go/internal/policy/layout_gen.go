package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Layout generation: obstacles, clusters,
// packing, housing blocks and corridors. It grows a fresh plan from one
// seed over a grid whose obstacle cells are already out of the core
// (layout_gen_obstacles.go), placing the base rooms by affinity cluster
// (layout_gen_cluster.go), the housing as straight wings and suite blocks
// sited as units (layout_gen_wing.go), then finishing the hallway network:
// rings, second doors and entrances (layout_gen_corridor.go). SiteCore runs
// it from the seed set, level by level.

// generate grows plan from seed over g, for pawns colonists and tombs tomb
// rooms. g is not changed.
func (g coreGrid) generate(plan LayoutPlan, seed domain.Cell, pawns, tombs int, tier TechTier, suites ...float64) LayoutPlan {
	return g.finish(g.generateBase(plan, seed, pawns, tombs, tier, suites...))
}

// generateBase is generate before the hallway network is finished: rooms,
// housing blocks and the entrances. The local search (layout_gen_search.go)
// varies a base plan, then finish closes its rings.
func (g coreGrid) generateBase(plan LayoutPlan, seed domain.Cell, pawns, tombs int, tier TechTier, suites ...float64) LayoutPlan {
	open := g
	g, base := open.clone(), open.clone()
	spine := []SpineSegment{{From: seed, To: seed}}
	if len(plan.Spine) > 0 {
		// A replan keeps the hallways its fixed rooms open onto.
		spine = slices.Clone(plan.Spine)
	}
	rooms := append([]PlannedRoom(nil), plan.Rooms...)
	wings := retireWings(plan.Wings, tier)
	// Other rooms stay off the wings' ground.
	g.carveSuiteWings(wings)
	base.carveSuiteWings(wings)
	g.carveBedroomWings(wings)
	if len(spine) == 1 {
		if next, ok := g.growSpine(spine, rooms); ok {
			// The centre crossing is laid first so no room takes its column.
			spine = next
		}
	}
	have := map[PlannedRole]int{}
	for _, r := range rooms {
		have[r.Role]++
	}
	var want []PlannedRole
	for _, role := range coreBaseRooms {
		if have[role] == 0 {
			want = append(want, role)
		}
	}
	clusters := affinityClusters(want)
	for i := have[PlannedTomb]; i < tombs; i++ {
		clusters = append(clusters, roleCluster{roles: []PlannedRole{PlannedTomb}})
	}
	housed := false
	house := func() {
		if !housed {
			housed = true
			spine, wings = base.siteBedWings(spine, rooms, wings, pawns, tier)
			g.carveBedroomWings(wings)
		}
	}
	for _, c := range clusters {
		spine, rooms, _ = g.placeCluster(spine, rooms, wings, c)
		// The wing is sited right behind the storage room, so it takes the
		// ground beside it before the later clusters do.
		for _, role := range c.roles {
			if role == PlannedStorage {
				house()
			}
		}
	}
	house()
	if closet, ok := g.mealCloset(rooms); ok {
		trial := append(append([]PlannedRoom(nil), rooms...), closet)
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Rooms: trial, Wings: wings}); err == nil {
			rooms = trial
		}
	}
	sg := open.clone()
	sg.carveBedroomWings(wings)
	sg.carveSuiteWings(wings)
	spine, wings = sg.siteSuiteBlocks(spine, rooms, wings, suites)
	sg.carveSuiteWings(wings)
	sg.carveBedroomWings(wings)
	spine, rooms = sg.siteShelter(spine, rooms, wings, seed, pawns, plan.Cold, plan.Hot)
	plan.Spine, plan.Rooms, plan.Wings = spine, rooms, wings
	return routedEntrances(plan)
}

// routedEntrances sets plan's entrances to the hallway ends that reach out
// of the base, or, when that set strands a trip, to the end slabs of every
// hallway, which are always enough.
func routedEntrances(plan LayoutPlan) LayoutPlan {
	layoutCounters.routed.Add(1)
	plan.Entrances = hallEntrances(plan.Spine)
	if _, err := CheckRoutes(plan); err != nil {
		layoutCounters.spineFallbacks.Add(1)
		plan.Entrances = spineEntrances(plan.Spine)
	}
	return plan
}

// finish closes a base plan's hallway network: rings, then second doors.
func (g coreGrid) finish(plan LayoutPlan) LayoutPlan {
	return g.routeRings(plan).addSecondDoors(g.fixed)
}
