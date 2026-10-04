package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// edgeOf is the edge term's cost for plan p over ground.
func edgeOf(p LayoutPlan, b Bounds, ground siteGround) int {
	return planScorer{s: MapSurvey{Bounds: b}, ground: ground}.edgeCost(p.AllRooms(), nil)
}

func plainScoreSurvey() MapSurvey {
	return zoningSurvey(140, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
}

func scoredPlan(t *testing.T) (LayoutPlan, MapSurvey) {
	t.Helper()
	s := plainScoreSurvey()
	p := corePlan(Zone(s), 3, BuildTierCamp)
	if len(p.Rooms) == 0 {
		t.Fatal("no rooms")
	}
	return p, s
}

func TestScoreDeterministicAndPasses(t *testing.T) {
	p, s := scoredPlan(t)
	a := Score(p, s)
	if !a.Passes() || !a.Walled {
		t.Fatal("grown plan on plain ground fails the hard tier:", a)
	}
	if !reflect.DeepEqual(a, Score(p, s)) {
		t.Fatal("same input scored differently")
	}
}

func TestScoreHardTierRanksBelowAnyPass(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	p, s := scoredPlan(t)
	good := Score(p, s)

	missing := p
	missing.Rooms = nil
	for _, r := range p.Rooms {
		if r.Role != coreBaseRooms[0] {
			missing.Rooms = append(missing.Rooms, r)
		}
	}
	if m := Score(missing, s); m.Passes() || len(m.Missing) == 0 || !good.Better(m) || m.Better(good) {
		t.Fatal("a plan missing a base room is not in the lower tier:", m)
	}

	noEntrance := p
	noEntrance.Entrances = nil
	if m := Score(noEntrance, s); m.Passes() || m.RoutesErr == "" || !good.Better(m) {
		t.Fatal("a plan with invalid routes is not in the lower tier:", m)
	}

	// A thoroughfare: a bedroom's interior placed over the only way into
	// dining makes the routes invalid or a path cross it; either way the
	// plan fails. Cut every hallway: no trip has a route.
	cut := p
	cut.Spine = nil
	if m := Score(cut, s); m.Passes() || good.Better(m) == false {
		t.Fatal("a plan without hallways is not in the lower tier:", m)
	}

	// Rich soil under a room: the same plan over a survey whose cells
	// under one room are rich.
	room := p.Rooms[0].Interior
	rich := zoningSurvey(140, func(x, z int32) SurveyCell {
		if contains(room, domain.Cell{X: x, Z: z}) {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	r := Score(p, rich)
	if r.Passes() || r.RichCells == 0 || !good.Better(r) || r.Soil >= good.Soil {
		t.Fatal("a room on rich soil is not in the lower tier:", r)
	}
	// When every plan stands on rich soil they share the lower tier and the
	// soft terms still rank them: fewer rich cells is better.
	more := zoningSurvey(140, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1.4} })
	if m := Score(p, more); m.Passes() || r.Better(m) == false {
		t.Fatal("less rich soil does not rank above more inside the lower tier:", r.Total(), m.Total())
	}
}

// handPlan is a straight hallway along z=10 with a kitchen and a freezer
// below it, the freezer at fx, each opening onto the hallway.
func handPlan(fx int32) LayoutPlan {
	room := func(role ModuleRole, x int32) LayoutRoom {
		return LayoutRoom{Role: role, Interior: Rectangle{X: x, Z: 13, Width: 3, Height: 3}, Door: domain.Cell{X: x + 1, Z: 12}}
	}
	spine := []SpineSegment{{From: domain.Cell{X: 0, Z: 10}, To: domain.Cell{X: 60, Z: 10}}}
	return LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Rooms: []LayoutRoom{room(ModuleKitchen, 5), room(ModuleFreezer, fx)}}
}

func TestScoreWalkRisesAsRoutesShorten(t *testing.T) {
	far, near := planWalk(handPlan(50)), planWalk(handPlan(12))
	if near >= far || near == 0 {
		t.Fatal("walk cost: freezer near the kitchen", near, "far", far)
	}
	// The cost is a negative contribution to the score.
	p := handPlan(50)
	s := plainScoreSurvey()
	sc := planScorer{g: newCoreGrid(nil, nil).withSoil(s), s: s, ground: newSiteGround(s)}
	defer func(w int) { planWeights.Walk = w }(planWeights.Walk)
	planWeights.Walk = 1
	if a, b := sc.core(handPlan(12)).Walk, sc.core(p).Walk; a <= b {
		t.Fatal("walk term: near", a, "far", b)
	}
}

func TestScoreWallAndFootprintShrinkWithTheRing(t *testing.T) {
	p, s := scoredPlan(t)
	base := Score(p, s)
	spread := p
	spread.Rooms = append(append([]LayoutRoom{}, p.Rooms...), LayoutRoom{Role: ModuleStorage, Interior: Rectangle{X: p.Rooms[0].Interior.X + 40, Z: p.Rooms[0].Interior.Z + 40, Width: 3, Height: 3}})
	wide := Score(spread, s)
	if wide.Wall >= base.Wall || wide.Footprint >= base.Footprint {
		t.Fatal("a longer ring should cost more: wall", base.Wall, wide.Wall, "footprint", base.Footprint, wide.Footprint)
	}
}

func TestScoreSoilExpansionAndDefenseTerms(t *testing.T) {
	p, s := scoredPlan(t)
	base := Score(p, s)
	if base.Defense <= 0 {
		t.Fatal("defense term on open ground:", base)
	}
	// Expansion: take the free ground next to the plan out of the core.
	sc := newPlanScorer(p.Zones, p.Reservations, s)
	free := sc.core(p).Expansion
	if free <= 0 {
		t.Fatal("no expansion room on open ground")
	}
	for c := range sc.g.core {
		delete(sc.g.core, c)
	}
	if boxed := sc.core(p).Expansion; boxed >= free {
		t.Fatal("expansion room did not fall with the free ground gone:", free, boxed)
	}
	// Soil and rock under the plan are costs.
	onRock := newPlanScorer(p.Zones, p.Reservations, s)
	onRock.g.rock[domain.Cell{X: p.Rooms[0].Interior.X, Z: p.Rooms[0].Interior.Z}] = true
	if got := onRock.core(p).Soil; got >= sc.core(p).Soil {
		t.Fatal("rock under a room did not lower the soil term:", got)
	}
}

// A dug cell is dearer than a plain-soil one: rooms are not sited into a
// mountain for being cheap while the colony waits on the excavation.
func TestRockCostsMoreThanPlainSoil(t *testing.T) {
	if planWeights.Rock <= planWeights.Soil*soilCostNormal {
		t.Fatalf("rock %d per cell does not exceed plain soil %d", planWeights.Rock, planWeights.Soil*soilCostNormal)
	}
}

// A room dug into rock is as far from the edge as the walk to its entrance:
// opened to the near side it pays the edge term, opened to the far side it
// does not.
func TestEdgeWalkRunsThroughDugRock(t *testing.T) {
	s := zoningSurvey(60, func(x, z int32) SurveyCell {
		if x >= 20 && x < 40 && z >= 20 && z < 40 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	g := newSiteGround(s)
	cell := domain.Cell{X: 30, Z: 30}
	if g.walkDist(cell) != -1 {
		t.Fatal("sealed rock walks:", g.walkDist(cell))
	}
	dug := map[domain.Cell]bool{}
	for z := int32(30); z < 40; z++ {
		dug[domain.Cell{X: 30, Z: z}] = true
	}
	if d := g.edgeWalk(dug)[cell.Z*g.w+cell.X]; d < 0 {
		t.Fatal("rock dug to open ground does not walk")
	}
}
