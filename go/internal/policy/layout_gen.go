package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The new generator (#1955, #1956, epic #1938): obstacles, clusters,
// packing, housing blocks and corridors. It grows a fresh plan from one
// seed over a grid whose obstacle cells are already out of the core
// (layout_gen_obstacles.go), placing the base rooms by affinity cluster
// (layout_gen_cluster.go), the housing as straight wings and suite blocks
// sited as units (layout_gen_wing.go), then finishing the hallway network:
// rings, second doors and entrances (layout_gen_corridor.go). SiteCore runs
// it from the seed set, level by level.

// generate grows plan from seed over g, for pawns colonists and tombs tomb
// rooms. g is not changed.
func (g coreGrid) generate(plan LayoutPlan, seed domain.Cell, pawns, tombs int, tier BuildTier, suites ...float64) LayoutPlan {
	open := g
	g, base := open.clone(), open.clone()
	spine := []SpineSegment{{From: seed, To: seed}}
	rooms := append([]LayoutRoom(nil), plan.Rooms...)
	wings := retireWings(plan.Wings, tier)
	// Other rooms stay off the wings' ground.
	g.carveSuiteWings(wings)
	base.carveSuiteWings(wings)
	g.carveBedroomWings(wings)
	if next, ok := g.addCrossing(spine, rooms); ok {
		// The centre crossing is laid first so no room takes its column (#952).
		spine = next
	}
	have := map[ModuleRole]int{}
	for _, r := range rooms {
		have[r.Role]++
	}
	var want []ModuleRole
	for _, role := range coreBaseRooms {
		if have[role] == 0 {
			want = append(want, role)
		}
	}
	clusters := affinityClusters(want)
	for i := max(have[ModuleTomb], 1); i < tombs; i++ {
		clusters = append(clusters, roleCluster{roles: []ModuleRole{ModuleTomb}})
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
		// ground beside it before the later clusters do (#1178).
		for _, role := range c.roles {
			if role == ModuleStorage {
				house()
			}
		}
	}
	house()
	if closet, ok := g.mealCloset(rooms); ok {
		trial := append(append([]LayoutRoom(nil), rooms...), closet)
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Rooms: trial, Wings: wings}); err == nil {
			rooms = trial
		}
	}
	sg := open.clone()
	sg.carveBedroomWings(wings)
	sg.carveSuiteWings(wings)
	spine, wings = sg.siteSuiteBlocks(spine, rooms, wings, suites)
	plan.Spine, plan.Rooms, plan.Wings = spine, rooms, wings
	plan.Entrances = hallEntrances(spine)
	if _, err := CheckRoutes(plan); err != nil {
		// An entrance set that strands a trip: the end slabs of every
		// hallway are always enough.
		plan.Entrances = spineEntrances(spine)
		return plan
	}
	plan = open.routeRings(plan)
	return plan.addSecondDoors()
}
