package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The wall lamp (#2948, #2951): the standing lamp's rival, priced in the same watts.

func TestWallLampPriceAndReachComeFromTheDefs(t *testing.T) {
	p := testLightPrice()
	if want := 30 + BuildW(DefaultSiteTypeWeights(), 28.5, 1.9); math.Abs(p.WallLampW-want) > 1e-6 {
		t.Errorf("wall lamp %v W, want %v", p.WallLampW, want)
	}
	if p.WallLampW >= p.LampW {
		t.Errorf("a wall lamp (15 steel) should cost less than a standing one (20): %v vs %v W", p.WallLampW, p.LampW)
	}
	if want := glowReach(11, 214); p.WallReach != want || p.LampReach != glowReach(12, 214) || p.WallReach >= p.LampReach {
		t.Errorf("reach %v / %v, want the glow solved per lamp (%v wall)", p.LampReach, p.WallReach, want)
	}
	// Without the wall lamp's def, radius or colour the option is absent and the
	// price is still valid; without the standing lamp's glow nothing is priced.
	for name, model := range map[string]LightModel{
		"no wall lamp":        {Standing: LampModel{30, 38, 12, 214}},
		"no wall radius":      {Standing: LampModel{30, 38, 12, 214}, Wall: LampModel{PowerW: 30, Build: 28.5, Brightness: 214}},
		"no wall colour":      {Standing: LampModel{30, 38, 12, 214}, Wall: LampModel{PowerW: 30, Build: 28.5, Radius: 11}},
		"wall lamp unpowered": {Standing: LampModel{30, 38, 12, 214}, Wall: LampModel{Build: 28.5, Radius: 11, Brightness: 214}},
	} {
		q := NewLightPrice(LightPriceInput{Model: model, Climate: testClimate(), SteelValue: 1.9, UpkeepPerDay: 1.6, UnitNutrition: 0.055})
		if !q.Valid() || q.WallLampW != 0 || q.WallReach != 0 {
			t.Errorf("%s: %+v", name, q)
		}
	}
	for name, standing := range map[string]LampModel{
		"no standing radius": {PowerW: 30, Build: 38, Brightness: 214},
		"no standing colour": {PowerW: 30, Build: 38, Radius: 12},
	} {
		q := NewLightPrice(LightPriceInput{Model: LightModel{Standing: standing, Wall: LampModel{30, 28.5, 11, 214}}, Climate: testClimate(), SteelValue: 1.9, UpkeepPerDay: 1.6, UnitNutrition: 0.055})
		if q.Valid() {
			t.Errorf("%s priced: %+v", name, q)
		}
	}
}

// litCells floods a lamp in open ground the way ComputeGlowGridsJob does
// (octile path cost 100 and 141, the lamp's own cell at 100, stopping past the
// radius) and returns the path distance, in cells, of the farthest lit cell on
// the axis and the smallest Euclidean distance of an unlit
// cell inside the flood. Ground glow is the brightest channel over 255 times 3.6,
// lit above 0.3 (GlowGrid.GroundGlowAt, PsychGlowAtGlow).
func litCells(radius float64, channel int) (axis int, unlitEuclid float64) {
	unlitEuclid = math.Inf(1)
	lit := func(dx, dz int) bool {
		lo, hi := min(dx, dz), max(dx, dz)
		x := float64(141*lo+100*(hi-lo)+100) / 100
		if x > radius {
			return false
		}
		f := float32(0.6*(1-x/radius) + 0.4/(x*x)) // Mathf.Lerp(1-d/r, 1/d^2, 0.4)
		r := int(float32(channel) * f)
		return float32(r)/255*3.6 > 0.3
	}
	for dx := 0; dx <= 14; dx++ {
		for dz := 0; dz <= dx; dz++ {
			if lit(dx, dz) {
				if dz == 0 {
					axis = max(axis, dx)
				}
				continue
			}
			if x := float64(141*dz+100*(dx-dz)+100) / 100; x <= radius+1 {
				unlitEuclid = min(unlitEuclid, math.Hypot(float64(dx), float64(dz)))
			}
		}
	}
	return axis, unlitEuclid
}

// The reach is the game's own falloff solved for LitGlow, checked against a flood
// of the glow grid: every cell inside it is lit, and it is within a cell of the
// nearest unlit one. The wiki's wall lamp page gives the same: about 9 cells from
// a standing lamp and 8 from a wall lamp along an axis, and 7.54 as the wall
// lamp's radius in the infobox.
func TestGlowReachMatchesTheGlowFlood(t *testing.T) {
	for _, tc := range []struct {
		name         string
		radius       float64
		wantAxis     int
		wantEuclidIn float64
	}{{"standing lamp", 12, 9, 8.35}, {"wall lamp", 11, 8, 7.59}} {
		reach := glowReach(tc.radius, 214)
		axis, unlit := litCells(tc.radius, 214)
		if axis != tc.wantAxis {
			t.Errorf("%s: flood lights %d cells along an axis, want %d", tc.name, axis, tc.wantAxis)
		}
		if math.Abs(reach-tc.wantEuclidIn) > 0.01 {
			t.Errorf("%s: reach %.3f, want %.2f", tc.name, reach, tc.wantEuclidIn)
		}
		if reach > unlit {
			t.Errorf("%s: reach %.3f includes an unlit cell at %.3f", tc.name, reach, unlit)
		}
		if reach < float64(axis)/math.Sqrt(1+0.41*0.41)-1e-9 || reach > float64(axis)+1 {
			t.Errorf("%s: reach %.3f is not the axis reach %d over the octile stretch", tc.name, reach, axis)
		}
	}
	// Unknown glow is no reach, and a lamp too dim to light its neighbour either.
	for name, got := range map[string]float64{"no radius": glowReach(0, 214), "no colour": glowReach(12, 0), "dim": glowReach(12, 10)} {
		if got != 0 {
			t.Errorf("%s: reach %v", name, got)
		}
	}
}

func lampsAt(p LightPrice, spot domain.Cell, isWall func(domain.Cell) bool) []lampCand {
	return append(p.standingCands([]domain.Cell{spot}), wallLampCands([]domain.Cell{spot}, isWall, p)...)
}

func wallEverywhere(domain.Cell) bool { return true }

// Both lamps can stand on the cell; the cheaper cover is the wall lamp where its
// shorter reach still lights the dark, the standing lamp where only it reaches.
func TestPlanLampsPicksTheCheaperCover(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 0, Z: 0}
	w := p.WorkCellW()
	for _, tc := range []struct {
		name string
		dark domain.Cell
		def  string
	}{
		{"within both reaches", domain.Cell{X: 4, Z: 0}, lightTable.WallLampDef},
		{"only the standing lamp reaches", domain.Cell{X: 8, Z: 0}, lightTable.LampDef},
	} {
		got, left := planLamps([]DarkCell{{Cell: tc.dark, W: w}}, lampsAt(p, spot, wallEverywhere))
		if len(got) != 1 || got[0].Def != tc.def || left != 0 {
			t.Errorf("%s: %+v, left %v; want one %s", tc.name, got, left, tc.def)
		}
	}
	// Out of both reaches nothing pays.
	if got, left := planLamps([]DarkCell{{Cell: domain.Cell{X: 9, Z: 0}, W: w}}, lampsAt(p, spot, wallEverywhere)); len(got) != 0 || left != w {
		t.Errorf("out of reach: %+v, left %v", got, left)
	}
	// A wall lamp dearer than the dark it lights, with no standing lamp, is not
	// placed.
	dear := p
	dear.WallLampW = w * 2
	cands := wallLampCands([]domain.Cell{spot}, wallEverywhere, dear)
	if got, _ := planLamps([]DarkCell{{Cell: domain.Cell{X: 4, Z: 0}, W: w}}, cands); len(got) != 0 {
		t.Errorf("a wall lamp dearer than the dark was placed: %+v", got)
	}
}

func TestWallLampsHangOnPlainWallOnly(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 5, Z: 5}
	// No neighbour is a wall: no wall lamp.
	if got := wallLampCands([]domain.Cell{spot}, func(domain.Cell) bool { return false }, p); len(got) != 0 {
		t.Fatalf("wall lamp with no wall: %+v", got)
	}
	// Only the south neighbour is.
	south := addCell(spot, directions[2])
	got := wallLampCands([]domain.Cell{spot}, func(c domain.Cell) bool { return c == south }, p)
	if len(got) != 1 || got[0].Facing != rotationOf(directions[2]) || got[0].Def != lightTable.WallLampDef {
		t.Fatalf("wall lamp %+v, want one facing the wall to the %v", got, directions[2])
	}
}

// A wall cell a vent holds (or a door, a link) prices the wall lamp out of that
// spot and the standing lamp answers; the dark is still lit, nothing is refused.
func TestContestedWallCellFallsBackToTheStandingLamp(t *testing.T) {
	p := testLightPrice()
	spot := domain.Cell{X: 2, Z: 2}
	dark := []DarkCell{{Cell: spot, W: p.WorkCellW()}}
	got, _ := planLamps(dark, lampsAt(p, spot, wallEverywhere))
	if len(got) != 1 || got[0].Def != lightTable.WallLampDef {
		t.Fatalf("free wall: %+v", got)
	}
	got, left := planLamps(dark, lampsAt(p, spot, func(domain.Cell) bool { return false }))
	if len(got) != 1 || got[0].Def != lightTable.LampDef || left != 0 {
		t.Fatalf("held wall: %+v, left %v; want the standing lamp", got, left)
	}
}

// A wall lamp shares its cell with furniture (Placeworker_AttachedToWall refuses
// only a Full-fill building there; the lamp is Standable and not an edifice), so
// a cell a bench holds offers it and a standing lamp does not; a Full-fill piece
// (a cooler) holds the cell against both.
func TestWallLampSharesACellWithFurnitureButNotWithAFullFillPiece(t *testing.T) {
	p := testLightPrice()
	bench, worker, cooler, free := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 1}, domain.Cell{X: 3, Z: 1}, domain.Cell{X: 4, Z: 1}
	floor := []domain.Cell{free, cooler, worker, bench}
	held := map[domain.Cell]bool{bench: true, worker: true, cooler: true}
	cands := p.roomCands(floor, held, map[domain.Cell]bool{worker: true}, map[domain.Cell]bool{cooler: true}, wallEverywhere)
	standing, wall := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range cands {
		if c.Def == lightTable.LampDef {
			standing[c.Cell] = true
		} else {
			wall[c.Cell] = true
		}
	}
	if len(standing) != 1 || !standing[free] {
		t.Errorf("standing lamps on %v, want only the free cell", standing)
	}
	if len(wall) != 3 || !wall[free] || !wall[bench] || !wall[worker] || wall[cooler] {
		t.Errorf("wall lamps on %v, want every cell but the cooler's", wall)
	}
	// Chosen when cheaper: a bench cell's dark is lit by a wall lamp on the bench.
	got, left := planLamps([]DarkCell{{Cell: bench, W: p.WorkCellW()}}, p.roomCands([]domain.Cell{bench}, map[domain.Cell]bool{bench: true}, nil, nil, wallEverywhere))
	if len(got) != 1 || got[0].Cell != bench || got[0].Def != lightTable.WallLampDef || left != 0 {
		t.Errorf("bench cell: %+v, left %v; want a wall lamp on the bench", got, left)
	}
}

// A fenced indoor room's ring is walled; a door, a
// link, an entrance or a vent, an outdoor room's fence and open ground are not
// walls. Natural rock is.
func TestPlainWallsAreTheFencedRingLessWhatOtherBuildingsHold(t *testing.T) {
	plan := roofPlan() // kitchen (10..13, 14..17), door (11,13); pen outdoors
	rock := map[domain.Cell]bool{{X: 9, Z: 11}: true}
	isWall := plainWalls(plan, rock)
	for _, tc := range []struct {
		cell domain.Cell
		want bool
		why  string
	}{
		{domain.Cell{X: 12, Z: 13}, true, "kitchen north wall"},
		{domain.Cell{X: 11, Z: 13}, false, "kitchen door"},
		{domain.Cell{X: 9, Z: 11}, true, "natural rock"},
		{domain.Cell{X: 27, Z: 11}, false, "the pen's gate"},
		{domain.Cell{X: 28, Z: 8}, false, "the pen's fence"},
		{domain.Cell{X: 22, Z: 9}, false, "open ground"},
	} {
		if got := isWall(tc.cell); got != tc.want {
			t.Errorf("%s %v: plain wall %v, want %v", tc.why, tc.cell, got, tc.want)
		}
	}
}

func TestTilerWallLampsHangOnWallsBesideTheirFloorCell(t *testing.T) {
	in := lampTileInput(t)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	isWall := plainWalls(res.Plan, surveyRock(in.Survey))
	walls := 0
	for id, lamps := range res.Lamps {
		for _, l := range lamps {
			if l.Def == lightTable.LampDef {
				if l.Facing != "" {
					t.Errorf("room %d standing lamp faces %q", id, l.Facing)
				}
				continue
			}
			walls++
			if l.Def != lightTable.WallLampDef {
				t.Errorf("room %d lamp %+v is neither lamp def", id, l)
			}
			if support := addCell(l.Cell, directionOf(l.Facing)); !isWall(support) {
				t.Errorf("room %d wall lamp %+v hangs on %v, which is no plain wall", id, l, support)
			}
		}
	}
	if walls == 0 {
		t.Errorf("no room took a wall lamp, which is cheaper than a standing one: %+v", res.Lamps)
	}
}

// The vents the tiler placed (#2915) hold their wall cells: no wall lamp may
// count them as a plain wall.
func TestPlacedVentsHoldTheirWallCell(t *testing.T) {
	res := ventTiling(t, seaIce, ventYes, furnaceHeat)
	vents := ventsOf(res)
	if len(vents) == 0 {
		t.Fatal("no vent was placed to hold a wall cell")
	}
	isWall := plainWalls(res.Plan, nil)
	for _, v := range vents {
		for _, c := range v.Footprint.Cells() {
			if isWall(c) {
				t.Errorf("vent cell %v counted as a plain wall", c)
			}
		}
	}
}

func TestTilerWithoutAWallLampPlacesStandingLampsOnly(t *testing.T) {
	in := lampTileInput(t)
	in.Light.WallLampW, in.Light.WallReach = 0, 0
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Lamps) == 0 {
		t.Fatal("no lamps")
	}
	for id, lamps := range res.Lamps {
		for _, l := range lamps {
			if l.Def != lightTable.LampDef {
				t.Errorf("room %d lamp %+v is not a standing lamp", id, l)
			}
		}
	}
}
