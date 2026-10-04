package policy

import (
	"reflect"
	"runtime"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// courtyardSurvey is bare ground with a rich patch at the map centre: the
// synthetic courtyard.
func courtyardSurvey() MapSurvey {
	return zoningSurvey(100, func(x, z int32) SurveyCell {
		if x >= 44 && x < 56 && z >= 44 && z < 56 {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 0.5}
	})
}

// richValleySurvey is rich soil everywhere: no ground avoids it.
func richValleySurvey() MapSurvey {
	return zoningSurvey(100, func(x, z int32) SurveyCell {
		return SurveyCell{Walkable: true, Fertility: 1.4}
	})
}

// linksKeepWall fails when a planned Link door is not in the wall between
// two rooms.
func linksKeepWall(t *testing.T, p LayoutPlan) {
	t.Helper()
	rooms := p.AllRooms()
	for _, r := range rooms {
		if r.Link == nil {
			continue
		}
		shared := false
		for _, o := range rooms {
			if o.Interior != r.Interior && inWall(r.Interior, *r.Link) && inWall(o.Interior, *r.Link) && sharesWall(r.Interior, o.Interior) {
				shared = true
			}
		}
		if !shared {
			t.Fatalf("%s link %v is not in a wall shared with another room", r.Role, *r.Link)
		}
	}
}

func TestAffinityClustersFollowTheTripTable(t *testing.T) {
	clusters := affinityClusters(coreBaseRooms)
	var kitchen, store, hospital *roleCluster
	for i, c := range clusters {
		for _, r := range c.roles {
			switch r {
			case ModuleKitchen:
				kitchen = &clusters[i]
			case ModuleStorage:
				store = &clusters[i]
			case ModuleHospital:
				hospital = &clusters[i]
			}
		}
	}
	if kitchen == nil || store == nil || hospital == nil {
		t.Fatal("clusters", clusters)
	}
	if !reflect.DeepEqual(kitchen.roles, []ModuleRole{ModuleKitchen, ModuleFreezer, ModuleDining, ModuleButchery}) {
		t.Fatal("kitchen cluster", kitchen.roles)
	}
	if !reflect.DeepEqual(store.roles, []ModuleRole{ModuleWorkshop, ModuleStorage}) {
		t.Fatal("storage cluster", store.roles)
	}
	if len(hospital.roles) != 1 {
		t.Fatal("hospital stands alone", hospital.roles)
	}
	if clusters[0].roles[0] != ModuleKitchen {
		t.Fatal("the kitchen cluster is the heaviest, so first", clusters[0].roles)
	}
	n := 0
	for _, c := range clusters {
		n += len(c.roles)
	}
	if n != len(coreBaseRooms) {
		t.Fatal("every base room is in one cluster", n)
	}
}

func TestCoreObstaclesByLevel(t *testing.T) {
	s := zoningSurvey(60, func(x, z int32) SurveyCell {
		switch {
		case x >= 20 && x < 26 && z >= 20 && z < 26:
			return SurveyCell{Walkable: true, Fertility: 1.4}
		case x >= 30 && x < 36 && z >= 20 && z < 26:
			return SurveyCell{Walkable: true, Fertility: 1}
		case x >= 40 && x < 44 && z >= 20 && z < 26:
			return SurveyCell{Rock: true, Ore: true}
		}
		return SurveyCell{Walkable: true}
	})
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	rich, plain, ore, bare := domain.Cell{X: 22, Z: 22}, domain.Cell{X: 32, Z: 22}, domain.Cell{X: 42, Z: 22}, domain.Cell{X: 12, Z: 22}
	all := g.coreObstacles(zones, obstacleAll)
	if !all[rich] || !all[plain] || !all[ore] || all[bare] {
		t.Fatal("all level", all[rich], all[plain], all[ore], all[bare])
	}
	richOnly := g.coreObstacles(zones, obstacleRich)
	if !richOnly[rich] || richOnly[plain] || !richOnly[ore] || richOnly[bare] {
		t.Fatal("rich level", richOnly[rich], richOnly[plain], richOnly[ore], richOnly[bare])
	}
	if len(g.coreObstacles(zones, obstacleNone)) != 0 {
		t.Fatal("the last level has no obstacle")
	}
	carved := g.withObstacles(all)
	if carved.core[rich] || !g.core[rich] || !carved.core[bare] {
		t.Fatal("withObstacles takes obstacles out of a copy of the core")
	}
}

// TestSiteCoreWrapsRichCourtyard: the sited plan places every base room and
// stands no room or hallway cell on the rich patch.
func TestSiteCoreWrapsRichCourtyard(t *testing.T) {
	s := courtyardSurvey()
	zones := Zone(s)
	plan := SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
	sc := Score(plan, s)
	if len(sc.Missing) != 0 || sc.RoutesErr != "" || sc.RichCells != 0 {
		t.Fatal("courtyard plan", sc)
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	linksKeepWall(t, plan)
}

// TestGenerateHopsRichPatchFromTheEdge seeds the generator on the patch's
// edge, where the old generator builds over it: with the patch an obstacle
// no room or hallway cell lands on it and every base room is still placed.
func TestGenerateHopsRichPatchFromTheEdge(t *testing.T) {
	s := courtyardSurvey()
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	lg := g.withObstacles(g.coreObstacles(zones, obstacleRich))
	scorer := newPlanScorer(zones, nil, s)
	over := 0
	for _, seed := range []domain.Cell{{X: 42, Z: 50}, {X: 57, Z: 50}, {X: 30, Z: 50}} {
		if !lg.column(seed.X, seed.Z) {
			continue
		}
		p := lg.generate(LayoutPlan{Zones: zones}, seed, 3, 1, BuildTierCamp)
		sc := scorer.core(p)
		if sc.RichCells != 0 || len(sc.Missing) != 0 || sc.RoutesErr != "" {
			t.Fatalf("seed %v: %v", seed, sc)
		}
		linksKeepWall(t, p)
		old := growPlan(LayoutPlan{Zones: zones, Spine: []SpineSegment{{From: seed, To: seed}}}, 3, 1, BuildTierCamp)
		over += scorer.core(old).RichCells
	}
	if over == 0 {
		t.Fatal("the seeds should be ones the old generator builds over the patch from")
	}
}

// TestSiteCoreAllRichValleyPlacesEveryRoomAtCost: no ground avoids rich
// soil, so the last level keeps the cost fallback.
func TestSiteCoreAllRichValleyPlacesEveryRoomAtCost(t *testing.T) {
	s := richValleySurvey()
	plan := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, BuildTierCamp)
	sc := Score(plan, s)
	if len(sc.Missing) != 0 || sc.RoutesErr != "" {
		t.Fatal("all-rich plan", sc)
	}
	if sc.RichCells == 0 || sc.Soil >= 0 {
		t.Fatal("rooms stand on rich soil at cost", sc)
	}
}

func TestSiteCoreBaselineKeepsLinksAndRoutes(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	plan := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, BuildTierCamp)
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	linksKeepWall(t, plan)
	if sc := Score(plan, s); len(sc.Missing) != 0 {
		t.Fatal("missing", sc.Missing)
	}
}

func TestSiteCoreSameOnAnyThreadCount(t *testing.T) {
	s := courtyardSurvey()
	zones := Zone(s)
	many := SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
	prev := runtime.GOMAXPROCS(1)
	one := SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
	runtime.GOMAXPROCS(prev)
	if !reflect.DeepEqual(many, one) {
		t.Fatal("one thread gave a different plan")
	}
}
