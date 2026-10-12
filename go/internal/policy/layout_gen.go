package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Layout generation: clusters,
// packing, housing blocks and corridors. It grows a fresh plan from one
// seed over a grid whose obstacle cells are already out of the core
// (obstacles in layout_build_ground.go), placing the base rooms by affinity cluster
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

// siteCandidates bounds the number of seeds evaluated during fresh siting.
var siteCandidates = 1000

// siteScore is one candidate's result, scored by Score's terms.
type siteScore struct {
	seed  domain.Cell
	score PlanScore
	plan  LayoutPlan
}

// SiteCore sites a fresh plan for pawns colonists and tombs tomb rooms: grown
// from each candidate seed over s, keeping the best-scoring result.
func SiteCore(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier TechTier) LayoutPlan {
	return siteCore(plan, s, pawns, tombs, tier, siteSearchIters)
}

// siteCore is SiteCore with searchIters local-search operators per searched
// site (0 skips the search: the replay harness's unsearched baseline).
func siteCore(plan LayoutPlan, s MapSurvey, pawns, tombs int, tier TechTier, searchIters int) LayoutPlan {
	g := newCoreGrid(plan.Zones, plan.Reserved()).withSoil(s)
	if len(g.core) == 0 {
		return plan
	}
	scorer := planScorer{g: g.buildGround, s: s, ground: newSiteGround(s)}
	var scores []siteScore
	var lg coreGrid
	// Each obstacle level sites every seed over the ground left once its
	// obstacles are out; a level holds only if some plan places every base
	// room, routes and houses every colonist. The strictest holding level is
	// kept, and a looser one (fields built on) replaces it only when its best
	// plan beats the kept one by fieldGain: farmland is worth a core a little
	// off the best ground, not a core crammed against the map border because
	// the middle is all field. The last level has no obstacle (rich soil at
	// cost) and runs only when no level held.
	held := false
	for _, level := range obstacleLevels {
		if held && level == obstacleNone {
			break
		}
		ls, llg := siteLevel(g, scorer, plan, level, pawns, tombs, tier)
		if level == obstacleNone {
			scores, lg = ls, llg
			break
		}
		var ok []siteScore
		for _, sc := range ls {
			if sc.score.Passes() && bedroomCount(sc.plan) >= pawns {
				ok = append(ok, sc)
			}
		}
		if len(ok) == 0 {
			continue
		}
		if !held || ok[0].score.Total() > scores[0].score.Total()+planWeights.FieldGain {
			scores, lg, held = ok, llg, true
		}
	}
	if len(scores) == 0 {
		return plan
	}
	// The wall terms need PlanPerimeter, far dearer than generate, so
	// only the best siteWallCandidates by core score are walled and reranked.
	// The best sites by core score are searched (layout_gen_search.go); the
	// searched plans join the walled pool beside the unsearched ones, so the
	// winner never scores below what the search started from.
	walled := append([]siteScore(nil), scores[:min(siteWallCandidates, len(scores))]...)
	if searchIters > 0 {
		start := scores[:min(siteSearchTop, len(scores))]
		searched := make([]siteScore, len(start))
		eachIndex(len(start), func(i int) {
			seed := start[i].seed
			p := lg.generateBase(plan, seed, pawns, tombs, tier)
			p = lg.finish(lg.search(scorer, p, seed, searchIters))
			searched[i] = siteScore{seed: seed, score: scorer.core(p), plan: p}
		})
		walled = append(walled, searched...)
	}
	eachIndex(len(walled), func(i int) {
		walled[i].score = scorer.walled(walled[i].plan, walled[i].score)
	})
	rankSites(walled)
	return walled[0].plan
}

// siteLevel generates and scores a plan from each seed of the ground g
// leaves at level, best first, and returns that ground. A level whose
// obstacles leave no seed returns none.
func siteLevel(g coreGrid, scorer planScorer, plan LayoutPlan, level obstacleLevel, pawns, tombs int, tier TechTier) ([]siteScore, coreGrid) {
	lg := g.withObstacles(g.coreObstacles(plan.Zones, level))
	if len(lg.core) == 0 {
		return nil, lg
	}
	seeds := lg.siteSeeds(siteCandidates)
	scores := make([]siteScore, len(seeds))
	eachIndex(len(seeds), func(i int) {
		p := lg.generate(plan, seeds[i], pawns, tombs, tier)
		scores[i] = siteScore{seed: seeds[i], score: scorer.core(p), plan: p}
	})
	rankSites(scores)
	return scores, lg
}

// siteWallCandidates bounds perimeter construction and rescoring for the
// candidates with the best core scores.
const siteWallCandidates = 16

// rankSites orders scores best first, ties by seed (Z, X).
func rankSites(scores []siteScore) {
	sort.SliceStable(scores, func(i, j int) bool {
		a, b := scores[i], scores[j]
		if a.score.Better(b.score) != b.score.Better(a.score) {
			return a.score.Better(b.score)
		}
		return a.seed.Z < b.seed.Z || a.seed.Z == b.seed.Z && a.seed.X < b.seed.X
	})
}

// siteShelter adds the shelter to a base plan's rooms unless it holds one.
// The preferred ground is shelterGap cells clear of every other room; where
// that fits nowhere the gap shrinks a cell at a time, and last the shelter
// takes an ordinary slot on a hallway (packRoom), so the first roof is never
// delayed by a site that cannot be had. g is the core ground the rest of the
// plan left; seed is the base's centre, the shelter's door faces it.
func (g coreGrid) siteShelter(spine []SpineSegment, rooms []PlannedRoom, wings []Wing, seed domain.Cell, colonists int, cold, hot bool) ([]SpineSegment, []PlannedRoom) {
	if g.noShelter {
		return spine, rooms
	}
	for _, r := range rooms {
		if r.Role == PlannedShelter {
			return spine, rooms
		}
	}
	sizes := ShelterSizes(colonists, ShelterCampfires(cold), ShelterCoolers(hot))
	all := append([]PlannedRoom(nil), rooms...)
	for _, w := range wings {
		all = append(all, w.Rooms...)
	}
	halls := spineRects(LayoutPlan{Spine: spine, Wings: wings}.Hallways())
	// The old generator's cold-map shelter gets an airlock where the ground has
	// room for one; where it fits nowhere it is sited without. The tiler prices
	// its airlocks instead (layout_tile_airlock.go).
	passes := []bool{false}
	if cold {
		passes = []bool{true, false}
	}
	for _, airlock := range passes {
		for gap := shelterGap; gap >= 1; gap-- {
			for _, size := range sizes {
				if room, ok := g.shelterOnOpenGround(all, halls, seed, size, gap, airlock, cold); ok {
					return spine, append(rooms, room...)
				}
			}
		}
	}
	for _, size := range sizes {
		if next, grown, placed, _ := g.packRoom(spine, rooms, wings, PlannedShelter, size); placed {
			return next, grown
		}
	}
	return spine, rooms
}

// shelterOnOpenGround is the best site whose walls sit on core ground, clear
// of the hallways and gap cells from every room: the fewest rich-soil cells,
// then the nearest to seed. Its door is at the middle of the wall facing seed.
func (g coreGrid) shelterOnOpenGround(rooms []PlannedRoom, halls []Rectangle, seed domain.Cell, size [2]int32, gap int32, airlock, porch bool) ([]PlannedRoom, bool) {
	w, d := size[0], size[1]
	var avoid []Rectangle
	for _, r := range rooms {
		avoid = append(avoid, pad(roomWalls(r), gap))
	}
	for _, h := range halls {
		avoid = append(avoid, pad(h, 1))
	}
	var best []PlannedRoom
	bestRich, bestDist, found := 0, int32(0), false
	for x := seed.X - shelterReach; x <= seed.X+shelterReach; x++ {
		for z := seed.Z - shelterReach; z <= seed.Z+shelterReach; z++ {
			in := Rectangle{X: x, Z: z, Width: w, Height: d}
			room := shelterWithChildren(in, seed, airlock, porch)
			footprint := pad(in, 1)
			for _, c := range room[1:] {
				footprint = unionRect(footprint, c.attachedGround())
			}
			if g.skip[in] || shelterBlocked(footprint, avoid) {
				continue
			}
			rich, ok := g.shelterGround(footprint)
			if !ok {
				continue
			}
			dist := distanceToRect(seed, in)
			if !found || rich < bestRich || rich == bestRich && dist < bestDist {
				best, bestRich, bestDist, found = room, rich, dist, true
			}
		}
	}
	return best, found
}

// shelterGround reports the rich-soil cells under walls and whether every
// cell is core ground.
func (g coreGrid) shelterGround(walls Rectangle) (int, bool) {
	rich := 0
	for x := walls.X; x < walls.X+walls.Width; x++ {
		for z := walls.Z; z < walls.Z+walls.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if !g.core[c] {
				return 0, false
			}
			if g.soil[c] == soilCostRich {
				rich++
			}
		}
	}
	return rich, true
}

// distanceToRect is the Chebyshev distance from c to r.
func distanceToRect(c domain.Cell, r Rectangle) int32 {
	dx := max(r.X-c.X, 0, c.X-(r.X+r.Width-1))
	dz := max(r.Z-c.Z, 0, c.Z-(r.Z+r.Height-1))
	return max(dx, dz)
}
