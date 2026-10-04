package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Test entry points to the generator: tests build plans through the same
// generate production sites with (layout_gen.go), without the site search.

// corePlan generates a plan from the core candidates' centroid seed in
// zones, for pawns colonists and one tomb.
func corePlan(zones []LayoutZone, pawns int, tier BuildTier) LayoutPlan {
	g := newCoreGrid(zones, nil)
	seed, ok := g.seed()
	if !ok {
		return LayoutPlan{Zones: zones}
	}
	return g.generate(LayoutPlan{Zones: zones}, seed, pawns, 1, tier)
}

// growPlan generates plan again over its zones: the rooms it holds stay and
// the rooms it lacks for pawns colonists, tombs tomb rooms and the suites
// are sited.
func growPlan(plan LayoutPlan, pawns, tombs int, tier BuildTier, suites ...float64) LayoutPlan {
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

// wingOf is the index of the wing for purpose, or -1.
func wingOf(wings []Wing, purpose WingPurpose) int {
	for i, w := range wings {
		if w.Purpose == purpose {
			return i
		}
	}
	return -1
}

// replanTest is ReplanLayoutWithRooms with no growth asked.
func replanTest(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier BuildTier, geysers []PowerGeyser, emptied map[domain.Cell]bool, suites ...float64) (LayoutPlan, bool) {
	next, changed, _ := ReplanLayoutWithRooms(plan, s, RoomGrowth{}, 0, pawns, tombs, tier, geysers, emptied, suites...)
	return next, changed
}
