package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Test entry points to the generator: tests build plans through the same
// generate production sites with (layout_gen.go), without the site search.

// corePlan generates a plan from the core candidates' centroid seed in
// zones, for pawns colonists and one tomb.
func corePlan(zones []LayoutZone, pawns int, tier TechTier) LayoutPlan {
	g := newCoreGrid(zones, nil)
	seed, ok := g.seed()
	if !ok {
		return LayoutPlan{Zones: zones}
	}
	return g.generate(LayoutPlan{Zones: zones}, seed, pawns, 0, tier)
}

// growPlan generates plan again over its zones: the rooms it holds stay and
// the rooms it lacks for pawns colonists, tombs tomb rooms and the suites
// are sited.
func growPlan(plan LayoutPlan, pawns, tombs int, tier TechTier, suites ...float64) LayoutPlan {
	g := newCoreGrid(plan.Zones, plan.Reservations)
	seed := domain.Cell{}
	if len(plan.Spine) > 0 {
		seed = plan.Spine[0].From
	} else if c, ok := g.seed(); ok {
		seed = c
	} else {
		return plan
	}
	return g.generate(plan, seed, pawns, tombs, tier, suites...)
}

// coreSeed is the siting seed of zones' core candidates.
func coreSeed(zones []LayoutZone) (domain.Cell, bool) {
	return newBuildGround(zones, nil).seed()
}

// generateAt generates plan from seed over zones' core candidates.
func generateAt(zones []LayoutZone, plan LayoutPlan, seed domain.Cell, pawns, tombs int, tier TechTier, suites ...float64) LayoutPlan {
	return newCoreGrid(zones, nil).generate(plan, seed, pawns, tombs, tier, suites...)
}

// generateClear generates a plan from the centroid seed of the ground left
// once level's obstacles are out of zones' core candidates.
func generateClear(zones []LayoutZone, s MapSurvey, level obstacleLevel, pawns, tombs int, suites ...float64) (LayoutPlan, bool) {
	g := newCoreGrid(zones, nil).withSoil(s)
	lg := g.withObstacles(g.coreObstacles(zones, level))
	seed, ok := lg.seed()
	if !ok {
		return LayoutPlan{}, false
	}
	return lg.generate(LayoutPlan{Zones: zones}, seed, pawns, tombs, TechTierCamp, suites...), true
}

// coldCorePlan is corePlan for three colonists on a cold or temperate map.
func coldCorePlan(cold bool) LayoutPlan {
	zones := coreTestZones()
	seed, _ := coreSeed(zones)
	return generateAt(zones, LayoutPlan{Zones: zones, Cold: cold}, seed, 3, 0, TechTierCamp).WithRoomIDs()
}

// withRooms grows the demand-grown core rooms wanted onto plan.
func withRooms(plan LayoutPlan, wanted ...PlannedRole) LayoutPlan {
	if len(plan.Hallways()) == 0 {
		return plan
	}
	have := map[PlannedRole]bool{}
	for _, r := range plan.AllRooms() {
		have[r.Role] = true
	}
	grew := false
	for _, role := range wanted {
		if have[role] {
			continue
		}
		var added bool
		if plan, added, _ = SiteRoom(plan, nil, role, coreRoomSize[role]); added {
			grew = true
		}
	}
	if grew {
		u := newUtilityGrid(plan)
		u.thick = ThickRoofCells(MapSurvey{})
		reserveExhausts(u, &plan)
	}
	return plan
}

// wingOf is the index of the wing for purpose, or -1.
func wingOf(wings []Wing, purpose WingPurpose) int {
	for i, w := range wings {
		if w.Purpose == purpose {
			return i
		}
	}
	return -1
}
