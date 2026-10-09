package policy

import (
	"testing"
)

const outskirtsW, outskirtsH int32 = 16, 12

// outskirtsPlan is a core plan on a 220 map whose rock starts at rockX, so the
// side toward the rock holds the least core ground.
func outskirtsPlan(rockX int32) LayoutPlan {
	zones := Zone(zoningSurvey(220, func(x, z int32) SurveyCell {
		if x >= rockX {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}))
	return corePlan(zones, 6, TechTierCamp)
}

func TestOutskirtsSitedOffTheCoreOnTheSideWithLeastGround(t *testing.T) {
	plan := outskirtsPlan(150)
	site, ok := SiteOutskirts(plan, outskirtsW, outskirtsH)
	if !ok {
		t.Fatal("no site")
	}
	ext, _ := plan.CoreBounds()
	u := newUtilityGrid(plan)
	lines := growthLines(plan, u)
	side, _, beyond := sideOf(ext, site)
	if !beyond {
		t.Fatal("site overlaps the core extent", site, ext)
	}
	for _, l := range lines {
		if rectsOverlap(site, l) {
			t.Fatal("site on a growth line", site, l)
		}
	}
	for _, r := range plan.AllRooms() {
		if rectsOverlap(site, pad(roomWalls(r), outskirtsGap-1)) {
			t.Fatal("site within the gap of a room", site, roomWalls(r))
		}
	}
	for _, c := range RectangleCells(site) {
		if !u.ok[c.Z*u.w+c.X] {
			t.Fatal("site off core ground", c)
		}
	}
	cands, _, _, _ := outskirtsCandidates(plan, outskirtsW, outskirtsH)
	chosen := u.sideGround(ext, side, lines)
	if len(cands) < 2 {
		t.Fatal("want sites on more than one side to rank", len(cands))
	}
	for other := range cands {
		if u.sideGround(ext, other, lines) < chosen {
			t.Fatal("a side with less core ground fits", other, chosen)
		}
	}
	if again, _ := SiteOutskirts(plan, outskirtsW, outskirtsH); again != site {
		t.Fatal("not deterministic", site, again)
	}
}

func TestOutskirtsGrowOnceAndNeverMove(t *testing.T) {
	plan := outskirtsPlan(150)
	grown, ok := growOutskirts(plan, [2]int32{outskirtsW, outskirtsH})
	if !ok {
		t.Fatal("not grown")
	}
	area, has := grown.OutskirtsArea()
	if !has {
		t.Fatal("no reservation")
	}
	if _, again := growOutskirts(grown, [2]int32{outskirtsW + 4, outskirtsH}); again {
		t.Fatal("a second cluster")
	}
	if _, none := growOutskirts(plan, [2]int32{}); none {
		t.Fatal("zero size asks for none")
	}
	if len(plan.Reservations) == len(grown.Reservations) {
		t.Fatal("the input plan was changed or nothing added")
	}
	if got, _ := grown.OutskirtsArea(); got != area {
		t.Fatal("moved")
	}
}

func TestOutskirtsStayWholeWhenTheCoreGrowsLater(t *testing.T) {
	plan, _ := growOutskirts(outskirtsPlan(150), [2]int32{outskirtsW, outskirtsH})
	area, _ := plan.OutskirtsArea()
	late, err := WithCoreRooms(plan, PlannedBattery, PlannedWorship)
	if err != nil {
		t.Fatal(err)
	}
	if len(late.AllRooms()) <= len(plan.AllRooms()) {
		t.Fatal("no room grew")
	}
	for _, r := range late.AllRooms() {
		if rectsOverlap(roomWalls(r), area) {
			t.Fatal("a late room cut into the cluster", r.Role, area)
		}
	}
	if got, _ := late.OutskirtsArea(); got != area {
		t.Fatal("cluster changed", got, area)
	}
}

func TestOutskirtsNeedWalkablePath(t *testing.T) {
	plan := outskirtsPlan(150)
	site, _ := SiteOutskirts(plan, outskirtsW, outskirtsH)
	// A rock moat round the site leaves no way in: the next best site is used.
	moat := LayoutZone{Kind: ZoneMining}
	for _, c := range RectangleCells(pad(site, 2)) {
		if !contains(site, c) {
			moat.Runs = append(moat.Runs, RowRun{Z: c.Z, X: c.X, Length: 1})
		}
	}
	walled := plan
	walled.Zones = append(append([]LayoutZone(nil), plan.Zones...), moat)
	next, ok := SiteOutskirts(walled, outskirtsW, outskirtsH)
	if ok && rectsOverlap(next, pad(site, 2)) && next == site {
		t.Fatal("sited behind a rock moat", next)
	}
}

func TestOutskirtsReplanGateUnchangedWithoutCluster(t *testing.T) {
	plan := outskirtsPlan(150)
	if _, ok := growOutskirts(plan, [2]int32{}); ok {
		t.Fatal("a plan that asks for none grew one")
	}
}

// The outskirts stand close to the core: the core ring walls them in, so the
// base stays one compact block (no far cluster for the outer ring to reach).
func TestOutskirtsStandCloseToTheCore(t *testing.T) {
	plan := outskirtsPlan(150)
	site, ok := SiteOutskirts(plan, outskirtsW, outskirtsH)
	if !ok {
		t.Fatal("no site")
	}
	ext, _ := plan.CoreBounds()
	if _, dist, _ := sideOf(ext, site); dist > outskirtsGap+4 {
		t.Fatal("site far from the core extent", dist, site, ext)
	}
}
